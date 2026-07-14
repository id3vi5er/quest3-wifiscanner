using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace WifiScanner
{
    /// <summary>One placed drill line: surface entry point + free direction.
    /// The rendered line runs from entry - direction*stub (protruding into
    /// the room so a real drill can be aligned against it) through the wall
    /// to entry + direction*depth (approximate exit point).</summary>
    [Serializable]
    public class DrillLine
    {
        public Vector3 entry;      // world-space hit on the room surface
        public Vector3 direction;  // normalized controller forward at placement
        public float depth;        // drill depth beyond the surface (m)
    }

    /// <summary>
    /// Drill planning mode ("Bohrplanung" on the panel). While active, the
    /// right controller's pointer ray is intersected with the surfaces of
    /// finished rooms (walls of extruded rooms, floor, ceiling — via
    /// RoomTracer.TryRaycastRoomSurfaces). A live preview shows the drill
    /// line snapped to the hit point, pointing in controller direction:
    /// an orange 10 cm stub into the room plus a cyan segment through the
    /// wall whose depth is adjusted with the right thumbstick (up/down).
    ///
    ///   Trigger = place the line (keeps its world pose permanently)
    ///   Stick Y = drill depth (persists across placements)
    ///   A       = remove the last placed line
    ///
    /// Lines are streamed to the PC viewer ("drill" packet, re-broadcast
    /// every 3 s like the rooms) and exported via SampleCollector.Export.
    /// Created and wired by SceneBootstrap.
    /// </summary>
    public class DrillPlanner : MonoBehaviour, IExclusiveTool
    {
        [Header("Wired by SceneBootstrap")]
        public RoomTracer roomTracer;
        [Tooltip("Material ASSET for lines/markers (cloned + tinted).")]
        public Material visualMaterial;
        public NetworkStreamer streamer;

        [Header("Drill line")]
        [Tooltip("How far the line protrudes into the room (m).")]
        public float stubLength = 0.10f;
        public float minDepth = 0.05f;
        public float maxDepth = 1.0f;
        [Tooltip("Depth change per second at full stick deflection (m/s).")]
        public float depthRate = 0.3f;
        public float stickDeadzone = 0.15f;
        public float lineWidth = 0.01f;
        public float markerDiameter = 0.025f;

        public bool IsActive { get; private set; }
        public IReadOnlyList<DrillLine> Lines => _lines;

        // Renderers of one placed line, needed for highlight + delete.
        private class LineVisual
        {
            public GameObject root;
            public LineRenderer stub;
            public LineRenderer drill;
            public Renderer entryMarker;
            public Renderer endMarker;
        }

        private readonly List<DrillLine> _lines = new List<DrillLine>();
        private readonly List<LineVisual> _visuals = new List<LineVisual>();

        private InputAction _posAction;
        private InputAction _rotAction;
        private InputAction _placeAction;
        private InputAction _removeAction;
        private InputAction _depthStick;

        private GameObject _root;
        private LineVisual _preview;
        private Material _matOrange;  // stub + entry marker
        private Material _matCyan;    // drill segment + end marker
        private Material _matRed;     // delete-mode highlight
        private Mesh _sphereMesh;
        private float _depth = 0.10f;
        private float _resendTimer;

        [Serializable]
        private class DrillPacket
        {
            public string type = "drill";
            public List<DrillLine> lines;
        }

        private void Start()
        {
            var temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _sphereMesh = temp.GetComponent<MeshFilter>().sharedMesh;
            Destroy(temp);

            if (visualMaterial != null)
            {
                _matOrange = MakeTinted(new Color(1f, 0.55f, 0.1f, 1f));
                _matCyan = MakeTinted(new Color(0f, 0.86f, 1f, 1f));
                _matRed = MakeTinted(new Color(1f, 0.2f, 0.2f, 1f));
            }
            else
            {
                Debug.LogError("[DrillPlanner] visualMaterial not assigned — " +
                               "assign the point material in SceneBootstrap.");
            }

            _root = new GameObject("DrillPlannerVisuals");
            _root.transform.SetParent(transform, false);

            _preview = CreateLineVisual("Preview");
            _preview.root.SetActive(false);

            _depth = Mathf.Clamp(_depth, minDepth, maxDepth);
        }

        private Material MakeTinted(Color c)
        {
            // Clones of a material ASSET keep their shader variant in the build.
            var m = new Material(visualMaterial);
            m.SetColor("_BaseColor", c);
            return m;
        }

        private void OnEnable()
        {
            _posAction = new InputAction("DrillPos", InputActionType.Value,
                "<XRController>{RightHand}/pointerPosition", expectedControlType: "Vector3");
            _posAction.AddBinding("<XRController>{RightHand}/devicePosition"); // fallback
            _rotAction = new InputAction("DrillRot", InputActionType.Value,
                "<XRController>{RightHand}/pointerRotation", expectedControlType: "Quaternion");
            _rotAction.AddBinding("<XRController>{RightHand}/deviceRotation"); // fallback
            _placeAction = new InputAction("DrillPlace", InputActionType.Button,
                "<XRController>{RightHand}/triggerPressed");
            _removeAction = new InputAction("DrillRemove", InputActionType.Button,
                "<XRController>{RightHand}/primaryButton");
            _depthStick = new InputAction("DrillDepth", InputActionType.Value,
                "<XRController>{RightHand}/primary2DAxis", expectedControlType: "Vector2");
            _posAction.Enable();
            _rotAction.Enable();
            _placeAction.Enable();
            _removeAction.Enable();
            _depthStick.Enable();
            ExclusiveTools.Register(this);
        }

        private void OnDisable()
        {
            _posAction?.Disable();
            _rotAction?.Disable();
            _placeAction?.Disable();
            _removeAction?.Disable();
            _depthStick?.Disable();
            ExclusiveTools.Unregister(this);
        }

        // ---- Mode control ----

        public void ToggleActive() => SetActive(!IsActive);

        public void SetActive(bool active)
        {
            if (IsActive == active) return;
            IsActive = active;
            if (active)
                ExclusiveTools.DeactivateOthers(this); // only one trigger owner
            if (!active && _preview != null) _preview.root.SetActive(false);
            Debug.Log($"[DrillPlanner] Bohrplanung " +
                      $"{(IsActive ? "AKTIV" : "beendet")} ({_lines.Count} Linien).");
        }

        // ---- Main loop ----

        private void Update()
        {
            // Periodic re-broadcast: a PC viewer started later still receives
            // the placed drill lines (same pattern as the rooms packet).
            if (streamer != null && _lines.Count > 0)
            {
                _resendTimer += Time.deltaTime;
                if (_resendTimer >= 3f)
                    SendDrillUpdate();
            }

            if (!IsActive) return;

            GetPointer(out Vector3 handPos, out Vector3 handFwd);

            // Depth adjust (rate-based) even while not pointing at a wall.
            float y = _depthStick.ReadValue<Vector2>().y;
            if (Mathf.Abs(y) > stickDeadzone)
                _depth = Mathf.Clamp(_depth + y * depthRate * Time.deltaTime,
                    minDepth, maxDepth);

            Vector3 hit = Vector3.zero;
            bool hasHit = roomTracer != null &&
                roomTracer.TryRaycastRoomSurfaces(handPos, handFwd, out hit);
            // When the pointer is on the control panel the trigger/A belong to
            // the UI (e.g. clicking this tool's own button off) — don't also act.
            bool ui = ControlPanel.PointerOverPanel;

            _preview.root.SetActive(hasHit);
            if (hasHit)
            {
                UpdateLineVisual(_preview, hit, handFwd.normalized, _depth);
                if (!ui && _placeAction.WasPressedThisFrame())
                    PlaceLine(hit, handFwd.normalized);
            }

            if (!ui && _removeAction.WasPressedThisFrame())
                RemoveLastLine();
        }

        private void PlaceLine(Vector3 entry, Vector3 dir)
        {
            var line = new DrillLine { entry = entry, direction = dir, depth = _depth };
            _lines.Add(line);
            var vis = CreateLineVisual($"DrillLine_{_lines.Count}");
            UpdateLineVisual(vis, entry, dir, _depth);
            _visuals.Add(vis);
            Haptics.PlaceRight();
            SendDrillUpdate();
            Debug.Log($"[DrillPlanner] Bohrlinie {_lines.Count} gesetzt " +
                      $"(Tiefe {_depth:F2} m).");
        }

        // ---- Public API (delete mode, export) ----

        public void RemoveLastLine() => RemoveLine(_lines.Count - 1);

        public void RemoveLine(int index)
        {
            if (index < 0 || index >= _lines.Count) return;
            var snapshot = UndoStack.CloneLines(_lines);
            UndoStack.Push(LocalizationManager.Get("drill.deleted_undo"),
                () => LoadLines(snapshot));
            Destroy(_visuals[index].root);
            _visuals.RemoveAt(index);
            _lines.RemoveAt(index);
            Haptics.HeavyRight();
            SendDrillUpdate();
            Debug.Log($"[DrillPlanner] Bohrlinie gelöscht ({_lines.Count} verbleibend).");
        }

        public void ClearLines()
        {
            foreach (var v in _visuals) Destroy(v.root);
            _visuals.Clear();
            _lines.Clear();
            SendDrillUpdate();
        }

        /// <summary>Replaces all drill lines (session load / re-alignment).
        /// Null or empty just clears.</summary>
        public void LoadLines(IReadOnlyList<DrillLine> loaded)
        {
            foreach (var v in _visuals) Destroy(v.root);
            _visuals.Clear();
            _lines.Clear();
            if (loaded != null)
                foreach (var l in loaded)
                {
                    var line = new DrillLine
                    {
                        entry = l.entry,
                        direction = l.direction,
                        depth = l.depth,
                    };
                    _lines.Add(line);
                    var vis = CreateLineVisual($"DrillLine_{_lines.Count}");
                    UpdateLineVisual(vis, line.entry, line.direction, line.depth);
                    _visuals.Add(vis);
                }
            SendDrillUpdate();
            Debug.Log($"[DrillPlanner] {_lines.Count} Bohrlinie(n) geladen.");
        }

        /// <summary>Full world-space segment of a placed line (stub end to exit).</summary>
        public void GetSegment(int index, out Vector3 start, out Vector3 end)
        {
            var l = _lines[index];
            start = l.entry - l.direction * stubLength;
            end = l.entry + l.direction * l.depth;
        }

        /// <summary>Tints a placed line red (delete-mode hover).</summary>
        public void SetLineHighlight(int index, bool on)
        {
            if (index < 0 || index >= _visuals.Count) return;
            var v = _visuals[index];
            v.stub.sharedMaterial = on ? _matRed : _matOrange;
            v.drill.sharedMaterial = on ? _matRed : _matCyan;
            v.entryMarker.sharedMaterial = on ? _matRed : _matOrange;
            v.endMarker.sharedMaterial = on ? _matRed : _matCyan;
        }

        // ---- Streaming ----

        private void SendDrillUpdate()
        {
            if (streamer == null) return;
            _resendTimer = 0f;
            var packet = new DrillPacket { lines = _lines };
            streamer.SendRaw(JsonUtility.ToJson(packet));
        }

        // ---- Visuals ----

        private LineVisual CreateLineVisual(string name)
        {
            var v = new LineVisual();
            v.root = new GameObject(name);
            v.root.transform.SetParent(_root.transform, false);

            var stubGo = new GameObject("Stub");
            stubGo.transform.SetParent(v.root.transform, false);
            v.stub = CreateLine(stubGo, _matOrange);
            var drillGo = new GameObject("Drill");
            drillGo.transform.SetParent(v.root.transform, false);
            v.drill = CreateLine(drillGo, _matCyan);

            v.entryMarker = CreateSphere(v.root.transform, _matOrange);
            v.endMarker = CreateSphere(v.root.transform, _matCyan);
            return v;
        }

        private void UpdateLineVisual(LineVisual v, Vector3 entry, Vector3 dir,
            float depth)
        {
            Vector3 start = entry - dir * stubLength;
            Vector3 end = entry + dir * depth;
            v.stub.SetPosition(0, start);
            v.stub.SetPosition(1, entry);
            v.drill.SetPosition(0, entry);
            v.drill.SetPosition(1, end);
            v.entryMarker.transform.position = entry;
            v.endMarker.transform.position = end;
        }

        private LineRenderer CreateLine(GameObject go, Material mat)
        {
            var line = go.AddComponent<LineRenderer>();
            line.positionCount = 2;
            line.startWidth = line.endWidth = lineWidth;
            line.sharedMaterial = mat;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            return line;
        }

        private Renderer CreateSphere(Transform parent, Material mat)
        {
            var go = new GameObject("DrillMarker");
            go.transform.SetParent(parent, false);
            go.transform.localScale = Vector3.one * markerDiameter;
            go.AddComponent<MeshFilter>().sharedMesh = _sphereMesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return mr;
        }

        private void GetPointer(out Vector3 worldPos, out Vector3 worldFwd)
        {
            Vector3 pos = _posAction.ReadValue<Vector3>();
            Quaternion rot = _rotAction.ReadValue<Quaternion>();
            var cam = Camera.main;
            Transform space = cam != null ? cam.transform.parent : null;
            if (space != null)
            {
                worldPos = space.TransformPoint(pos);
                worldFwd = space.rotation * rot * Vector3.forward;
            }
            else
            {
                worldPos = pos;
                worldFwd = rot * Vector3.forward;
            }
        }
    }
}
