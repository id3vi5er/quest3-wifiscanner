using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace WifiScanner
{
    /// <summary>One completed measurement: two world endpoints + length.</summary>
    [Serializable]
    public class Measurement
    {
        public float ax, ay, az;
        public float bx, by, bz;
        public float meters;
    }

    /// <summary>
    /// Laser distance measurer ("Distanz messen" on the panel). A cursor floats
    /// a fixed distance in front of the controller (like the floor-calibration
    /// cursor). Trigger #1 freezes the start point; as you move, a line connects
    /// it to the live cursor and a label above the line shows the current length
    /// every frame. Trigger #2 freezes the end point and completes the
    /// measurement — completed lines stay in the world; the next trigger starts a
    /// new one. "Messungen löschen" on the panel removes them all.
    ///
    /// Completed measurements are exported to {stem}_measure.json.
    /// Created and wired by SceneBootstrap.
    /// </summary>
    public class DistanceMeasurer : MonoBehaviour, IExclusiveTool
    {
        [Header("Wired by SceneBootstrap")]
        [Tooltip("Material ASSET (URP/Unlit) cloned + tinted for the lines.")]
        public Material visualMaterial;

        [Header("Measuring")]
        [Tooltip("Distance of the floating cursor in front of the controller (m).")]
        public float cursorDistance = 0.3f;
        public float cursorDiameter = 0.04f;
        public float lineWidth = 0.008f;

        public bool IsActive { get; private set; }
        public IReadOnlyList<Measurement> Measurements => _measurements;

        private class MeasureVisual
        {
            public GameObject root;
            public LineRenderer line;
            public Transform startMarker;
            public Transform endMarker;
            public WorldLabel label;
        }

        private readonly List<Measurement> _measurements = new List<Measurement>();
        private readonly List<MeasureVisual> _visuals = new List<MeasureVisual>();

        private InputAction _posAction;
        private InputAction _rotAction;
        private InputAction _placeAction;

        private GameObject _root;
        private Transform _cursor;
        private Material _matCyan, _matGreen, _matRed;
        private Mesh _sphereMesh;

        private bool _measuring;
        private Vector3 _start;
        private MeasureVisual _active; // the in-progress measurement

        private void Start()
        {
            var temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _sphereMesh = temp.GetComponent<MeshFilter>().sharedMesh;
            Destroy(temp);

            if (visualMaterial != null)
            {
                _matCyan = MakeTinted(new Color(0f, 0.86f, 1f, 1f));
                _matGreen = MakeTinted(new Color(0.2f, 1f, 0.3f, 1f));
                _matRed = MakeTinted(new Color(1f, 0.25f, 0.2f, 1f));
            }
            else
            {
                Debug.LogError("[DistanceMeasurer] visualMaterial not assigned — " +
                               "assign the point material in SceneBootstrap.");
            }

            _root = new GameObject("DistanceMeasurerVisuals");
            _root.transform.SetParent(transform, false);

            _cursor = CreateSphere(_root.transform, cursorDiameter, _matGreen);
            _cursor.gameObject.SetActive(false);
        }

        private Material MakeTinted(Color c)
        {
            var m = new Material(visualMaterial); // asset clone keeps its variant
            m.SetColor("_BaseColor", c);
            return m;
        }

        private void OnEnable()
        {
            _posAction = new InputAction("MeasurePos", InputActionType.Value,
                "<XRController>{RightHand}/pointerPosition", expectedControlType: "Vector3");
            _posAction.AddBinding("<XRController>{RightHand}/devicePosition"); // fallback
            _rotAction = new InputAction("MeasureRot", InputActionType.Value,
                "<XRController>{RightHand}/pointerRotation", expectedControlType: "Quaternion");
            _rotAction.AddBinding("<XRController>{RightHand}/deviceRotation"); // fallback
            _placeAction = new InputAction("MeasurePlace", InputActionType.Button,
                "<XRController>{RightHand}/triggerPressed");
            _posAction.Enable();
            _rotAction.Enable();
            _placeAction.Enable();
            ExclusiveTools.Register(this);
        }

        private void OnDisable()
        {
            _posAction?.Disable();
            _rotAction?.Disable();
            _placeAction?.Disable();
            ExclusiveTools.Unregister(this);
        }

        // ---- Mode control ----

        public void ToggleActive() => SetActive(!IsActive);

        public void SetActive(bool active)
        {
            if (IsActive == active) return;
            IsActive = active;
            if (active) ExclusiveTools.DeactivateOthers(this);
            if (_cursor != null) _cursor.gameObject.SetActive(active);
            if (!active) CancelInProgress();
            Debug.Log($"[DistanceMeasurer] Distanzmessung " +
                      $"{(IsActive ? "AKTIV" : "beendet")} ({_measurements.Count} Messungen).");
        }

        // ---- Main loop ----

        private void Update()
        {
            if (!IsActive) return;

            GetPointer(out Vector3 handPos, out Vector3 handFwd);
            Vector3 cursor = handPos + handFwd * cursorDistance;
            if (_cursor != null) _cursor.position = cursor;

            if (_measuring && _active != null)
            {
                _active.line.SetPosition(0, _start);
                _active.line.SetPosition(1, cursor);
                _active.endMarker.position = cursor;
                float meters = Vector3.Distance(_start, cursor);
                _active.label.SetWorldPosition(Vector3.Lerp(_start, cursor, 0.5f)
                    + Vector3.up * 0.03f);
                _active.label.SetText(FormatDistance(meters));
            }

            // Pointer on the panel = trigger belongs to the UI, don't measure.
            if (!ControlPanel.PointerOverPanel && _placeAction.WasPressedThisFrame())
            {
                if (!_measuring) BeginMeasurement(cursor);
                else FinishMeasurement(cursor);
            }
        }

        private void BeginMeasurement(Vector3 start)
        {
            _start = start;
            _measuring = true;
            _active = CreateVisual();
            _active.startMarker.position = start;
            _active.endMarker.position = start;
            _active.line.SetPosition(0, start);
            _active.line.SetPosition(1, start);
            _active.label.SetText("0.0 cm");
            Haptics.TickRight();
        }

        private void FinishMeasurement(Vector3 end)
        {
            if (_active == null) { _measuring = false; return; }
            float meters = Vector3.Distance(_start, end);
            _active.line.SetPosition(1, end);
            _active.endMarker.position = end;
            _active.label.SetWorldPosition(Vector3.Lerp(_start, end, 0.5f) + Vector3.up * 0.03f);
            _active.label.SetText(FormatDistance(meters));

            _measurements.Add(new Measurement
            {
                ax = _start.x, ay = _start.y, az = _start.z,
                bx = end.x, by = end.y, bz = end.z,
                meters = meters,
            });
            _visuals.Add(_active);
            _active = null;
            _measuring = false;
            Haptics.PlaceRight();
            Debug.Log($"[DistanceMeasurer] Messung {_measurements.Count}: {meters:F3} m.");
        }

        private void CancelInProgress()
        {
            if (_active != null) Destroy(_active.root);
            _active = null;
            _measuring = false;
        }

        public void ClearMeasurements()
        {
            CancelInProgress();
            foreach (var v in _visuals) Destroy(v.root);
            _visuals.Clear();
            _measurements.Clear();
        }

        /// <summary>Replaces all measurements (session load / re-alignment).
        /// Null or empty just clears.</summary>
        public void LoadMeasurements(IReadOnlyList<Measurement> loaded)
        {
            ClearMeasurements();
            if (loaded == null) return;
            foreach (var m in loaded)
            {
                var a = new Vector3(m.ax, m.ay, m.az);
                var b = new Vector3(m.bx, m.by, m.bz);
                var v = CreateVisual();
                v.startMarker.position = a;
                v.endMarker.position = b;
                v.line.SetPosition(0, a);
                v.line.SetPosition(1, b);
                v.label.SetWorldPosition(Vector3.Lerp(a, b, 0.5f) + Vector3.up * 0.03f);
                v.label.SetText(FormatDistance(m.meters));
                _measurements.Add(new Measurement
                {
                    ax = m.ax, ay = m.ay, az = m.az,
                    bx = m.bx, by = m.by, bz = m.bz,
                    meters = m.meters,
                });
                _visuals.Add(v);
            }
            Debug.Log($"[DistanceMeasurer] {_measurements.Count} Messung(en) geladen.");
        }

        // ---- Delete-mode support ----

        public bool TryGetSegment(int i, out Vector3 a, out Vector3 b)
        {
            a = b = Vector3.zero;
            if (i < 0 || i >= _measurements.Count) return false;
            var m = _measurements[i];
            a = new Vector3(m.ax, m.ay, m.az);
            b = new Vector3(m.bx, m.by, m.bz);
            return true;
        }

        public void SetHighlight(int i, bool on)
        {
            if (i < 0 || i >= _visuals.Count) return;
            var v = _visuals[i];
            Material m = on ? _matRed : _matCyan;
            if (v.line != null) v.line.sharedMaterial = m;
            if (v.startMarker != null)
                v.startMarker.GetComponent<Renderer>().sharedMaterial = m;
            if (v.endMarker != null)
                v.endMarker.GetComponent<Renderer>().sharedMaterial = m;
        }

        public void RemoveMeasurement(int i)
        {
            if (i < 0 || i >= _measurements.Count) return;
            var snapshot = UndoStack.CloneMeasurements(_measurements);
            UndoStack.Push("Messung gelöscht", () => LoadMeasurements(snapshot));
            Destroy(_visuals[i].root);
            _visuals.RemoveAt(i);
            _measurements.RemoveAt(i);
            Haptics.HeavyRight();
        }

        // ---- Formatting ----

        private static string FormatDistance(float meters)
        {
            return meters < 1f
                ? $"{meters * 100f:F1} cm"
                : $"{meters:F2} m";
        }

        // ---- Visuals ----

        private MeasureVisual CreateVisual()
        {
            var v = new MeasureVisual();
            v.root = new GameObject($"Measurement_{_measurements.Count + 1}");
            v.root.transform.SetParent(_root.transform, false);

            var lineGo = new GameObject("Line");
            lineGo.transform.SetParent(v.root.transform, false);
            v.line = lineGo.AddComponent<LineRenderer>();
            v.line.positionCount = 2;
            v.line.startWidth = v.line.endWidth = lineWidth;
            v.line.sharedMaterial = _matCyan;
            v.line.shadowCastingMode = ShadowCastingMode.Off;
            v.line.receiveShadows = false;

            v.startMarker = CreateSphere(v.root.transform, cursorDiameter, _matCyan);
            v.endMarker = CreateSphere(v.root.transform, cursorDiameter, _matCyan);
            v.label = WorldLabel.Create(v.root.transform);
            return v;
        }

        private Transform CreateSphere(Transform parent, float diameter, Material mat)
        {
            var go = new GameObject("MeasurePoint");
            go.transform.SetParent(parent, false);
            go.transform.localScale = Vector3.one * diameter;
            go.AddComponent<MeshFilter>().sharedMesh = _sphereMesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return go.transform;
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
