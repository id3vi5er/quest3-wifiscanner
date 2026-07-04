using System;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace WifiScanner
{
    /// <summary>One traced room: floor-plan corners in world XZ.</summary>
    [Serializable]
    public class RoomOutline
    {
        public List<Vector2> cornersXZ = new List<Vector2>();
        public float floorY;
    }

    /// <summary>
    /// Manual room tracing, no Space Setup needed: a cursor dot floats in front
    /// of the RIGHT controller (distance configurable via SceneBootstrap) and
    /// is projected onto the known floor plane — corner heights are ignored,
    /// it's a top-view floor plan. Started/stopped from the panel button.
    ///
    /// While active:
    ///   Trigger = place the next corner (in order, cw or ccw; lines appear
    ///             from the second corner on, plus a preview line to the cursor)
    ///   B       = finish: connect last corner back to the first (needs >= 3)
    ///   A       = undo the last corner
    ///   Snap    = within snapRadius of the FIRST corner the cursor locks on
    ///             and turns green; Trigger then closes the room.
    /// After closing, the outline stays visible and the next Trigger press
    /// starts the next room. RecordingControls is suspended while active so
    /// A/B don't also toggle recording/export.
    ///
    /// Export: on every data export the traced rooms are written as a
    /// real-scale SVG (1 unit = 1 mm) plus raw JSON — see DataExporter.
    /// Created and wired by SceneBootstrap.
    /// </summary>
    public class RoomTracer : MonoBehaviour
    {
        [Header("Wired by SceneBootstrap")]
        [Tooltip("For grid-aligned SVG export (uses its gridYawDegrees).")]
        public FloorHeatmapRenderer floor;
        [Tooltip("Material ASSET for cursor/lines (cloned + tinted).")]
        public Material visualMaterial;

        [Header("Tracing")]
        [Tooltip("Distance of the floating cursor in front of the controller (m).")]
        public float cursorDistance = 0.3f;
        [Tooltip("Within this distance of the first corner the cursor snaps " +
                 "shut (m).")]
        public float snapRadius = 0.3f;
        public float lineWidth = 0.015f;
        [Tooltip("Lines/markers float this high above the floor (m).")]
        public float lineFloorOffset = 0.02f;

        public bool IsActive { get; private set; }
        public IReadOnlyList<RoomOutline> Rooms => _rooms;
        public int ActiveCornerCount => _activeCorners.Count;

        private readonly List<RoomOutline> _rooms = new List<RoomOutline>();
        private readonly List<Vector3> _activeCorners = new List<Vector3>();
        private readonly List<GameObject> _activeMarkers = new List<GameObject>();
        private readonly List<GameObject> _roomVisuals = new List<GameObject>();

        private InputAction _posAction;
        private InputAction _rotAction;
        private InputAction _placeAction;
        private InputAction _undoAction;
        private InputAction _finishAction;

        private GameObject _root;
        private Transform _cursor;
        private Renderer _cursorRenderer;
        private LineRenderer _activeLine;
        private Material _lineMat;    // cyan
        private Material _snapMat;    // green
        private Mesh _sphereMesh;
        private XROrigin _origin;
        private float _floorY;
        private bool _hasFloor;
        private bool _snapped;

        private void Start()
        {
            _origin = FindFirstObjectByType<XROrigin>();

            var temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _sphereMesh = temp.GetComponent<MeshFilter>().sharedMesh;
            Destroy(temp);

            if (visualMaterial != null)
            {
                // Clones of a material ASSET keep their shader variant in the
                // build (same rule as everywhere else in this project).
                _lineMat = new Material(visualMaterial);
                _lineMat.SetColor("_BaseColor", new Color(0f, 0.86f, 1f, 1f));
                _snapMat = new Material(visualMaterial);
                _snapMat.SetColor("_BaseColor", new Color(0.2f, 1f, 0.3f, 1f));
            }
            else
            {
                Debug.LogError("[RoomTracer] visualMaterial not assigned — " +
                               "assign the point material in SceneBootstrap.");
            }

            _root = new GameObject("RoomTracerVisuals");
            _root.transform.SetParent(transform, false);

            _cursor = CreateSphere(_root.transform, 0.05f);
            _cursorRenderer = _cursor.GetComponent<Renderer>();
            _cursor.gameObject.SetActive(false);

            var lineGo = new GameObject("ActiveOutline");
            lineGo.transform.SetParent(_root.transform, false);
            _activeLine = CreateLine(lineGo);
            _activeLine.enabled = false;
        }

        private void OnEnable()
        {
            _posAction = new InputAction("TracePos", InputActionType.Value,
                "<XRController>{RightHand}/devicePosition", expectedControlType: "Vector3");
            _rotAction = new InputAction("TraceRot", InputActionType.Value,
                "<XRController>{RightHand}/deviceRotation", expectedControlType: "Quaternion");
            _placeAction = new InputAction("TracePlace", InputActionType.Button,
                "<XRController>{RightHand}/triggerPressed");
            _undoAction = new InputAction("TraceUndo", InputActionType.Button,
                "<XRController>{RightHand}/primaryButton");
            _finishAction = new InputAction("TraceFinish", InputActionType.Button,
                "<XRController>{RightHand}/secondaryButton");
            _posAction.Enable();
            _rotAction.Enable();
            _placeAction.Enable();
            _undoAction.Enable();
            _finishAction.Enable();
        }

        private void OnDisable()
        {
            _posAction?.Disable();
            _rotAction?.Disable();
            _placeAction?.Disable();
            _undoAction?.Disable();
            _finishAction?.Disable();
        }

        public void ToggleActive()
        {
            IsActive = !IsActive;
            if (IsActive && !_hasFloor &&
                FloorHeatmapRenderer.TryGetTrackedFloorY(_origin, out float y))
            {
                _floorY = y;
                _hasFloor = true;
            }
            if (_cursor != null) _cursor.gameObject.SetActive(IsActive);
            UpdateActiveLine(Vector3.zero, false);
            Debug.Log($"[RoomTracer] Raumzeichnen {(IsActive ? "AKTIV" : "beendet")} " +
                      $"({_rooms.Count} Räume, {_activeCorners.Count} offene Ecken).");
        }

        private void Update()
        {
            if (!IsActive || _cursor == null) return;

            Vector3 cursor = ComputeCursorPoint();

            // Snap onto the first corner to close the loop.
            _snapped = false;
            if (_activeCorners.Count >= 3)
            {
                Vector3 first = _activeCorners[0];
                if ((cursor - first).sqrMagnitude <= snapRadius * snapRadius)
                {
                    cursor = first;
                    _snapped = true;
                }
            }

            _cursor.position = cursor;
            if (_cursorRenderer != null && _lineMat != null)
                _cursorRenderer.sharedMaterial = _snapped ? _snapMat : _lineMat;

            UpdateActiveLine(cursor, true);

            if (_placeAction.WasPressedThisFrame())
            {
                if (_snapped) CloseRoom();
                else PlaceCorner(cursor);
            }
            if (_undoAction.WasPressedThisFrame())
                UndoCorner();
            if (_finishAction.WasPressedThisFrame())
                CloseRoom();
        }

        // Controller tip point projected onto the floor plane (top view — the
        // pointed height is deliberately ignored once the floor is known).
        private Vector3 ComputeCursorPoint()
        {
            Vector3 pos = _posAction.ReadValue<Vector3>();
            Quaternion rot = _rotAction.ReadValue<Quaternion>();
            var cam = Camera.main;
            Transform space = cam != null ? cam.transform.parent : null;
            Vector3 world;
            Vector3 fwd;
            if (space != null)
            {
                world = space.TransformPoint(pos);
                fwd = space.rotation * rot * Vector3.forward;
            }
            else
            {
                world = pos;
                fwd = rot * Vector3.forward;
            }
            Vector3 p = world + fwd * cursorDistance;
            if (_hasFloor) p.y = _floorY + lineFloorOffset;
            return p;
        }

        private void PlaceCorner(Vector3 point)
        {
            if (!_hasFloor)
            {
                // No floor-based tracking origin (Editor): the first corner
                // defines the floor plane.
                _floorY = point.y - lineFloorOffset;
                _hasFloor = true;
            }
            _activeCorners.Add(point);
            var marker = CreateSphere(_root.transform, 0.035f);
            marker.position = point;
            _activeMarkers.Add(marker.gameObject);
        }

        public void UndoCorner()
        {
            if (_activeCorners.Count == 0) return;
            _activeCorners.RemoveAt(_activeCorners.Count - 1);
            var marker = _activeMarkers[_activeMarkers.Count - 1];
            _activeMarkers.RemoveAt(_activeMarkers.Count - 1);
            Destroy(marker);
        }

        /// <summary>Connects the last corner back to the first (needs >= 3).</summary>
        public void CloseRoom()
        {
            if (_activeCorners.Count < 3)
            {
                Debug.LogWarning("[RoomTracer] Mindestens 3 Ecken nötig.");
                return;
            }

            var room = new RoomOutline { floorY = _floorY };
            foreach (var c in _activeCorners)
                room.cornersXZ.Add(new Vector2(c.x, c.z));
            _rooms.Add(room);

            // Persistent closed outline; corner markers stay as part of it.
            var roomGo = new GameObject($"Room_{_rooms.Count}");
            roomGo.transform.SetParent(_root.transform, false);
            var line = CreateLine(roomGo);
            line.loop = true;
            line.positionCount = _activeCorners.Count;
            for (int i = 0; i < _activeCorners.Count; i++)
                line.SetPosition(i, _activeCorners[i]);
            foreach (var marker in _activeMarkers)
                marker.transform.SetParent(roomGo.transform, true);
            _roomVisuals.Add(roomGo);

            _activeCorners.Clear();
            _activeMarkers.Clear();
            UpdateActiveLine(Vector3.zero, false);
            Debug.Log($"[RoomTracer] Raum {_rooms.Count} geschlossen " +
                      $"({room.cornersXZ.Count} Ecken).");
        }

        public void ClearRooms()
        {
            _rooms.Clear();
            _activeCorners.Clear();
            foreach (var m in _activeMarkers) Destroy(m);
            _activeMarkers.Clear();
            foreach (var v in _roomVisuals) Destroy(v);
            _roomVisuals.Clear();
            UpdateActiveLine(Vector3.zero, false);
        }

        /// <summary>SVG (+ raw JSON) alongside the other export files.</summary>
        public void ExportSvg(string stem)
        {
            if (_rooms.Count == 0) return;
            float gridYaw = floor != null ? floor.gridYawDegrees : 0f;
            DataExporter.ExportRoomsSvg(_rooms, gridYaw, stem);
        }

        // Polyline through the placed corners plus a preview segment to the
        // cursor while tracing.
        private void UpdateActiveLine(Vector3 cursor, bool withCursor)
        {
            if (_activeLine == null) return;
            int n = _activeCorners.Count;
            if (n == 0 || !IsActive)
            {
                _activeLine.enabled = false;
                return;
            }
            _activeLine.enabled = true;
            _activeLine.loop = false;
            _activeLine.positionCount = withCursor ? n + 1 : n;
            for (int i = 0; i < n; i++)
                _activeLine.SetPosition(i, _activeCorners[i]);
            if (withCursor)
                _activeLine.SetPosition(n, cursor);
        }

        private Transform CreateSphere(Transform parent, float diameter)
        {
            var go = new GameObject("TracePoint");
            go.transform.SetParent(parent, false);
            go.transform.localScale = Vector3.one * diameter;
            go.AddComponent<MeshFilter>().sharedMesh = _sphereMesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _lineMat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return go.transform;
        }

        private LineRenderer CreateLine(GameObject go)
        {
            var line = go.AddComponent<LineRenderer>();
            line.startWidth = line.endWidth = lineWidth;
            line.sharedMaterial = _lineMat;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            return line;
        }
    }
}
