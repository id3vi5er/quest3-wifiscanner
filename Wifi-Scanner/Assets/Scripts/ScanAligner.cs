using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;

namespace WifiScanner
{
    /// <summary>
    /// "Scan ausrichten": snaps a loaded session onto the real environment.
    /// The Quest re-anchors its tracking origin on every start, so loaded
    /// world coordinates are shifted/rotated against reality. Two phases:
    ///
    ///   1. SelectWall — aim the right controller's ray at a wall of a loaded
    ///      (extruded) room; the wall is outlined, a GREEN sphere marks the
    ///      edge's first corner (the anchor). Trigger = select, B = deselect.
    ///   2. TraceWall — hold the LEFT controller where the green anchor corner
    ///      really is, point the RIGHT controller along the real wall, hold
    ///      both grips (preview line), release = apply. Only the direction and
    ///      the left anchor position enter the math — the controller distance
    ///      need not match the wall length.
    ///
    /// The resulting rigid transform (yaw + XZ translation + floor-height
    /// offset) is applied to the WHOLE session via
    /// SessionLoader.ApplySessionTransform. Created and wired by SceneBootstrap.
    /// </summary>
    public class ScanAligner : MonoBehaviour, IExclusiveTool
    {
        public enum Phase { SelectWall, TraceWall }

        [Header("Wired by SceneBootstrap")]
        public RoomTracer roomTracer;
        public SessionLoader sessionLoader;
        [Tooltip("Material ASSET (URP/Unlit) cloned + tinted for the visuals.")]
        public Material lineMaterial;

        [Tooltip("Minimum controller distance for a valid wall trace (m).")]
        public float minControllerDistance = 0.15f;
        [Tooltip("How far the preview line extends beyond each controller (m).")]
        public float lineExtension = 1.5f;

        public bool IsActive { get; private set; }
        public Phase CurrentPhase { get; private set; }

        private InputAction _posAction;   // right pointer pose
        private InputAction _rotAction;
        private InputAction _selectAction; // right trigger
        private InputAction _cancelAction; // right B
        private InputAction _leftGrip;
        private InputAction _rightGrip;
        private InputAction _leftPos;
        private InputAction _rightPos;

        private XROrigin _origin;
        private GameObject _root;
        private LineRenderer _wallOutline;
        private LineRenderer _traceLine;
        private Transform _anchorMarker;
        private Material _matCyan, _matOrange, _matGreen;

        // Selected wall (phase 2 input).
        private RoomOutline _selectedRoom;
        private Vector2 _edgeCorner;   // first corner of the selected edge (XZ)
        private Vector2 _edgeDir;      // unit direction along the edge (XZ)
        private bool _holding;

        private void Start()
        {
            _origin = FindFirstObjectByType<XROrigin>();

            if (lineMaterial != null)
            {
                _matCyan = MakeTinted(new Color(0f, 0.86f, 1f, 1f));
                _matOrange = MakeTinted(new Color(1f, 0.55f, 0.1f, 1f));
                _matGreen = MakeTinted(new Color(0.2f, 1f, 0.3f, 1f));
            }
            else
            {
                Debug.LogError("[ScanAligner] lineMaterial not assigned — assign " +
                               "the point material in SceneBootstrap.");
            }

            _root = new GameObject("ScanAlignerVisuals");
            _root.transform.SetParent(transform, false);

            var outlineGo = new GameObject("WallOutline");
            outlineGo.transform.SetParent(_root.transform, false);
            _wallOutline = CreateLine(outlineGo, 4, true);

            var traceGo = new GameObject("TraceLine");
            traceGo.transform.SetParent(_root.transform, false);
            _traceLine = CreateLine(traceGo, 2, false);

            var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = "AnchorMarker";
            Destroy(marker.GetComponent<Collider>());
            marker.transform.SetParent(_root.transform, false);
            marker.transform.localScale = Vector3.one * 0.08f;
            if (_matGreen != null)
                marker.GetComponent<Renderer>().sharedMaterial = _matGreen;
            _anchorMarker = marker.transform;

            _root.SetActive(false);
        }

        private void OnEnable()
        {
            _posAction = new InputAction("AlignPos", InputActionType.Value,
                "<XRController>{RightHand}/pointerPosition", expectedControlType: "Vector3");
            _posAction.AddBinding("<XRController>{RightHand}/devicePosition");
            _rotAction = new InputAction("AlignRot", InputActionType.Value,
                "<XRController>{RightHand}/pointerRotation", expectedControlType: "Quaternion");
            _rotAction.AddBinding("<XRController>{RightHand}/deviceRotation");
            _selectAction = new InputAction("AlignSelect", InputActionType.Button,
                "<XRController>{RightHand}/triggerPressed");
            _cancelAction = new InputAction("AlignCancel", InputActionType.Button,
                "<XRController>{RightHand}/secondaryButton");
            _leftGrip = new InputAction("AlignLGrip", InputActionType.Button,
                "<XRController>{LeftHand}/gripPressed");
            _rightGrip = new InputAction("AlignRGrip", InputActionType.Button,
                "<XRController>{RightHand}/gripPressed");
            _leftPos = new InputAction("AlignLPos", InputActionType.Value,
                "<XRController>{LeftHand}/devicePosition", expectedControlType: "Vector3");
            _rightPos = new InputAction("AlignRPos", InputActionType.Value,
                "<XRController>{RightHand}/devicePosition", expectedControlType: "Vector3");
            _posAction.Enable();
            _rotAction.Enable();
            _selectAction.Enable();
            _cancelAction.Enable();
            _leftGrip.Enable();
            _rightGrip.Enable();
            _leftPos.Enable();
            _rightPos.Enable();
            ExclusiveTools.Register(this);
        }

        private void OnDisable()
        {
            _posAction?.Disable();
            _rotAction?.Disable();
            _selectAction?.Disable();
            _cancelAction?.Disable();
            _leftGrip?.Disable();
            _rightGrip?.Disable();
            _leftPos?.Disable();
            _rightPos?.Disable();
            ExclusiveTools.Unregister(this);
        }

        public void ToggleActive() => SetActive(!IsActive);

        public void SetActive(bool active)
        {
            if (IsActive == active) return;
            IsActive = active;
            if (IsActive)
            {
                ExclusiveTools.DeactivateOthers(this);
                CurrentPhase = Phase.SelectWall;
                _selectedRoom = null;
                _holding = false;
            }
            if (_root != null) _root.SetActive(IsActive);
            if (!IsActive) HideAllVisuals();
            Debug.Log($"[ScanAligner] Ausrichten {(IsActive ? "AKTIV" : "beendet")}.");
        }

        private void Update()
        {
            if (!IsActive || roomTracer == null || sessionLoader == null) return;

            bool ui = ControlPanel.PointerOverPanel;

            if (CurrentPhase == Phase.SelectWall)
                UpdateSelectWall(ui);
            else
                UpdateTraceWall(ui);
        }

        // ---- Phase 1: pick a wall of the loaded plan ----

        private void UpdateSelectWall(bool ui)
        {
            GetPointer(out Vector3 origin, out Vector3 fwd);
            bool hit = roomTracer.TryRaycastWall(origin, fwd,
                out int roomIdx, out int edgeIdx, out _, out _);

            if (hit)
            {
                var room = roomTracer.Rooms[roomIdx];
                if (RoomTracer.TryGetEdge(room, edgeIdx,
                        out Vector2 a, out Vector2 dir, out float len))
                {
                    ShowWallOutline(room, a, dir, len, _matCyan);
                    if (!ui && _selectAction.WasPressedThisFrame())
                    {
                        _selectedRoom = room;
                        _edgeCorner = a;
                        _edgeDir = dir;
                        ShowWallOutline(room, a, dir, len, _matOrange);
                        CurrentPhase = Phase.TraceWall;
                        Haptics.TickRight();
                        Debug.Log($"[ScanAligner] Wand gewählt (Raum {roomIdx + 1}, " +
                                  $"Kante {edgeIdx}, {len:F2} m). Jetzt die reale " +
                                  "Wand nachziehen: linker Controller an die grüne Ecke.");
                        return;
                    }
                }
            }
            else
            {
                _wallOutline.gameObject.SetActive(false);
                _anchorMarker.gameObject.SetActive(false);
            }
        }

        // ---- Phase 2: trace the real wall with both grips ----

        private void UpdateTraceWall(bool ui)
        {
            if (!ui && _cancelAction.WasPressedThisFrame())
            {
                _selectedRoom = null;
                _holding = false;
                _traceLine.gameObject.SetActive(false);
                CurrentPhase = Phase.SelectWall;
                return;
            }

            bool both = _leftGrip.IsPressed() && _rightGrip.IsPressed();
            if (both)
            {
                _holding = true;
                ShowTraceLine(LeftWorld(), RightWorld());
            }
            else if (_holding)
            {
                _holding = false;
                _traceLine.gameObject.SetActive(false);
                ApplyAlignment(LeftWorld(), RightWorld());
            }
        }

        private void ApplyAlignment(Vector3 left, Vector3 right)
        {
            Vector3 g = right - left;
            g.y = 0f;
            if (g.magnitude < minControllerDistance)
            {
                Debug.LogWarning("[ScanAligner] Controller zu nah beieinander — " +
                                 "nochmal nachziehen.");
                return; // stay in TraceWall
            }
            if (_selectedRoom == null) return;

            float gestureYaw = Mathf.Atan2(g.x, g.z) * Mathf.Rad2Deg;
            float edgeYaw = Mathf.Atan2(_edgeDir.x, _edgeDir.y) * Mathf.Rad2Deg;
            float deltaYaw = Mathf.DeltaAngle(edgeYaw, gestureYaw);

            // Height offset from the CURRENT tracked floor — a manual floor
            // override belongs to the old, misaligned session.
            float dy = 0f;
            if (FloorHeatmapRenderer.TryGetTrackedFloorY(_origin, out float fy))
                dy = fy - _selectedRoom.floorY;

            Quaternion r = Quaternion.Euler(0f, deltaYaw, 0f);
            Vector3 p0 = r * new Vector3(_edgeCorner.x, 0f, _edgeCorner.y);
            var t = new Vector3(left.x - p0.x, dy, left.z - p0.z);

            sessionLoader.ApplySessionTransform(deltaYaw, t);
            Haptics.PlaceRight();
            SetActive(false);
        }

        // ---- Visuals ----

        private void ShowWallOutline(RoomOutline room, Vector2 a, Vector2 dir,
            float len, Material mat)
        {
            float y0 = room.floorY;
            float y1 = room.floorY + room.height;
            Vector2 b = a + dir * len;
            _wallOutline.SetPosition(0, new Vector3(a.x, y0, a.y));
            _wallOutline.SetPosition(1, new Vector3(b.x, y0, b.y));
            _wallOutline.SetPosition(2, new Vector3(b.x, y1, b.y));
            _wallOutline.SetPosition(3, new Vector3(a.x, y1, a.y));
            if (mat != null) _wallOutline.sharedMaterial = mat;
            _wallOutline.gameObject.SetActive(true);

            _anchorMarker.position = new Vector3(a.x, (y0 + y1) * 0.5f, a.y);
            _anchorMarker.gameObject.SetActive(true);
        }

        private void ShowTraceLine(Vector3 left, Vector3 right)
        {
            Vector3 dir = (right - left).normalized;
            _traceLine.SetPosition(0, left - dir * lineExtension);
            _traceLine.SetPosition(1, right + dir * lineExtension);
            _traceLine.gameObject.SetActive(true);
        }

        private void HideAllVisuals()
        {
            if (_wallOutline != null) _wallOutline.gameObject.SetActive(false);
            if (_traceLine != null) _traceLine.gameObject.SetActive(false);
            if (_anchorMarker != null) _anchorMarker.gameObject.SetActive(false);
        }

        private LineRenderer CreateLine(GameObject go, int positions, bool loop)
        {
            var line = go.AddComponent<LineRenderer>();
            line.positionCount = positions;
            line.loop = loop;
            line.startWidth = line.endWidth = 0.012f;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            if (_matCyan != null) line.sharedMaterial = _matCyan;
            go.SetActive(false);
            return line;
        }

        private Material MakeTinted(Color c)
        {
            // Clones of a material ASSET keep their shader variant in the build.
            var m = new Material(lineMaterial);
            m.SetColor("_BaseColor", c);
            return m;
        }

        // ---- Poses ----

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

        private Vector3 LeftWorld() => ToWorld(_leftPos.ReadValue<Vector3>());
        private Vector3 RightWorld() => ToWorld(_rightPos.ReadValue<Vector3>());

        private static Vector3 ToWorld(Vector3 trackingSpacePos)
        {
            var cam = Camera.main;
            if (cam != null && cam.transform.parent != null)
                return cam.transform.parent.TransformPoint(trackingSpacePos);
            return trackingSpacePos;
        }
    }
}
