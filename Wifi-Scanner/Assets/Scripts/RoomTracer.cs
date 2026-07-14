using System;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace WifiScanner
{
    /// <summary>A door/window cut into one wall of a room. Position is stored in
    /// wall space: the edge it sits on plus the horizontal span along that edge
    /// (metres from the edge's first corner) and the absolute vertical range.
    /// Doors run down to the floor; windows are a free rectangle.</summary>
    [Serializable]
    public class WallOpening
    {
        public int edgeIndex;     // polygon edge corner[edgeIndex] -> next
        public float startAlong;  // metres along the edge (startAlong < endAlong)
        public float endAlong;
        public float bottomY;     // absolute world Y
        public float topY;
        public bool isDoor;       // true = door (to floor), false = window
        public float hingeAlong;  // door hinge side = first-clicked jamb (metres along edge)
    }

    /// <summary>One traced room: floor-plan corners in world XZ + pulled-up
    /// height + any door/window openings cut into its walls.</summary>
    [Serializable]
    public class RoomOutline
    {
        public List<Vector2> cornersXZ = new List<Vector2>();
        public float floorY;
        public float height; // 0 = flat outline, >0 = extruded (OBJ export)
        public List<WallOpening> openings = new List<WallOpening>();
        public string name = ""; // user label ("Wohnzimmer"), shown + exported
    }

    /// <summary>
    /// Manual room tracing, no Space Setup needed. A cursor dot floats in
    /// front of the RIGHT controller (distance + visibility configurable via
    /// SceneBootstrap) and is projected onto the floor plane. Started/stopped
    /// from the panel button.
    ///
    /// While active (idle = no open polygon):
    ///   Trigger  = place the first corner of a new room — UNLESS the cursor
    ///              hovers a finished room's corner (yellow: grab + drag to
    ///              move it) or the pointer ray hits inside a finished room
    ///              (orange marker: hold trigger and pull UP to set the room
    ///              height like a pointing stick — the ceiling follows).
    ///   While drawing: Trigger = next corner, B = finish (close last→first),
    ///              A = undo, snap onto corner 1 (green) closes the loop.
    /// Rooms with height export as extruded OBJ next to SVG/JSON.
    ///
    /// Floor calibration ("Boden kalibrieren" on the panel): the cursor stays
    /// UNprojected — hold it onto the real floor and pull the trigger; the
    /// measured height becomes the work plane for tracing AND corrects the
    /// ACTIVE storey's base (FloorHeatmapRenderer.SetFloorOverride). Finished
    /// rooms keep their own floorY — storeys stack, they never shift along.
    /// With a FloorManager the work plane follows the active storey.
    ///
    /// RecordingControls is suspended while tracing so A/B don't also toggle
    /// recording/export. Created and wired by SceneBootstrap.
    /// </summary>
    public class RoomTracer : MonoBehaviour, IExclusiveTool
    {
        private enum DragMode { None, Height, Corner }

        [Tooltip("Optional: multi-storey manager — new rooms are drawn on the " +
                 "ACTIVE storey's plane; rooms of other storeys are ignored by " +
                 "corner/height grabs. Wired by SceneBootstrap.")]
        public FloorManager floorManager;

        [Header("Wired by SceneBootstrap")]
        [Tooltip("For grid-aligned SVG export and the shared floor plane.")]
        public FloorHeatmapRenderer floor;
        [Tooltip("Material ASSET for cursor/lines (cloned + tinted).")]
        public Material visualMaterial;
        [Tooltip("Optional: live-streams the traced rooms to the PC viewer.")]
        public NetworkStreamer streamer;

        [Header("Tracing")]
        [Tooltip("Distance of the floating cursor in front of the controller (m).")]
        public float cursorDistance = 0.3f;
        [Tooltip("Snap distance for closing a room and for grabbing corners (m).")]
        public float snapRadius = 0.3f;
        [Tooltip("Show the floating cursor dot (SceneBootstrap setting).")]
        public bool cursorVisible = true;
        [Tooltip("Diameter of the cursor dot (m).")]
        public float cursorDiameter = 0.05f;
        public float lineWidth = 0.015f;
        [Tooltip("Lines/markers float this high above the floor (m).")]
        public float lineFloorOffset = 0.02f;
        [Tooltip("Maximum pull-up room height (m).")]
        public float maxRoomHeight = 6f;

        public bool IsActive { get; private set; }
        /// <summary>Floor calibration armed: next trigger sets the floor height.</summary>
        public bool IsCalibratingFloor { get; private set; }
        public bool IsNamingRoom { get; private set; }
        public IReadOnlyList<RoomOutline> Rooms => _rooms;
        public int ActiveCornerCount => _activeCorners.Count;

        // One entry per finished room: data + all visuals for live editing.
        private class RoomVisual
        {
            public RoomOutline outline;
            public GameObject root;
            public LineRenderer baseLine;
            public LineRenderer topLine;
            public readonly List<LineRenderer> verticalLines = new List<LineRenderer>();
            public readonly List<LineRenderer> openingLines = new List<LineRenderer>();
            public readonly List<Transform> markers = new List<Transform>();
            public WorldLabel nameLabel; // floating room name at the centroid
        }

        private readonly List<RoomOutline> _rooms = new List<RoomOutline>();
        private readonly List<RoomVisual> _visuals = new List<RoomVisual>();
        private readonly List<Vector3> _activeCorners = new List<Vector3>();
        private readonly List<GameObject> _activeMarkers = new List<GameObject>();

        private InputAction _posAction;
        private InputAction _rotAction;
        private InputAction _placeAction;
        private InputAction _undoAction;
        private InputAction _finishAction;

        private GameObject _root;
        private Transform _cursor;
        private Renderer _cursorRenderer;
        private LineRenderer _activeLine;
        private Material _matCyan;    // default lines/cursor
        private Material _matGreen;   // snap-to-close
        private Material _matYellow;  // corner grab
        private Material _matOrange;  // height pull
        private Mesh _sphereMesh;
        private XROrigin _origin;
        private float _floorY;
        private bool _hasFloor;

        private DragMode _drag = DragMode.None;
        private int _dragRoom = -1;
        private int _dragCorner = -1;
        private TouchScreenKeyboard _nameKeyboard;
        private RoomVisual _namingVisual;
        private Vector3 _heightAnchor;
        private float _roomsResendTimer;

        [Serializable]
        private class RoomsPacket
        {
            public string type = "rooms";
            public float gridYawDegrees;
            public List<RoomOutline> rooms;
            // Storey base heights + labels so viewers can map each room
            // (via its floorY) to a storey and filter per floor.
            public List<float> floorBases;
            public List<string> floorLabels;
        }

        private void Start()
        {
            _origin = FindFirstObjectByType<XROrigin>();

            var temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _sphereMesh = temp.GetComponent<MeshFilter>().sharedMesh;
            Destroy(temp);

            if (visualMaterial != null)
            {
                _matCyan = MakeTinted(new Color(0f, 0.86f, 1f, 1f));
                _matGreen = MakeTinted(new Color(0.2f, 1f, 0.3f, 1f));
                _matYellow = MakeTinted(new Color(1f, 0.85f, 0.1f, 1f));
                _matOrange = MakeTinted(new Color(1f, 0.55f, 0.1f, 1f));
            }
            else
            {
                Debug.LogError("[RoomTracer] visualMaterial not assigned — " +
                               "assign the point material in SceneBootstrap.");
            }

            _root = new GameObject("RoomTracerVisuals");
            _root.transform.SetParent(transform, false);

            _cursor = CreateSphere(_root.transform, cursorDiameter);
            _cursorRenderer = _cursor.GetComponent<Renderer>();
            _cursor.gameObject.SetActive(false);

            var lineGo = new GameObject("ActiveOutline");
            lineGo.transform.SetParent(_root.transform, false);
            _activeLine = CreateLine(lineGo);
            _activeLine.enabled = false;
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
            // Aim/pointer pose — the same pose the menu rays use, pointing
            // forward out of the controller (the grip/device pose points
            // ~60° upward on Quest and felt wrong for aiming).
            _posAction = new InputAction("TracePos", InputActionType.Value,
                "<XRController>{RightHand}/pointerPosition", expectedControlType: "Vector3");
            _posAction.AddBinding("<XRController>{RightHand}/devicePosition"); // fallback
            _rotAction = new InputAction("TraceRot", InputActionType.Value,
                "<XRController>{RightHand}/pointerRotation", expectedControlType: "Quaternion");
            _rotAction.AddBinding("<XRController>{RightHand}/deviceRotation"); // fallback
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
            ExclusiveTools.Register(this);
        }

        private void OnDisable()
        {
            _posAction?.Disable();
            _rotAction?.Disable();
            _placeAction?.Disable();
            _undoAction?.Disable();
            _finishAction?.Disable();
            ExclusiveTools.Unregister(this);
        }

        // ---- Mode control ----

        public void ToggleActive() => SetActive(!IsActive);

        public void SetActive(bool active)
        {
            if (IsActive == active) return;
            IsActive = active;
            IsCalibratingFloor = false;
            if (IsActive)
            {
                ExclusiveTools.DeactivateOthers(this); // only one trigger owner
                ResolveFloor();
            }
            RefreshCursorVisible();
            UpdateActiveLine(Vector3.zero, false);
            _drag = DragMode.None;
            Debug.Log($"[RoomTracer] Raumzeichnen {(IsActive ? "AKTIV" : "beendet")} " +
                      $"({_rooms.Count} Räume, {_activeCorners.Count} offene Ecken).");
        }

        /// <summary>Arms the manual floor calibration (panel button); the next
        /// trigger press takes the cursor's height as the floor plane.</summary>
        public void ToggleFloorCalibration()
        {
            IsCalibratingFloor = !IsCalibratingFloor;
            IsNamingRoom = false;
            RefreshCursorVisible();
        }

        /// <summary>Arms room naming (panel button "Raum benennen"): aim the
        /// cursor into a finished room on the work plane, trigger opens the
        /// Quest keyboard for its name.</summary>
        public void ToggleRoomNaming()
        {
            IsNamingRoom = !IsNamingRoom;
            IsCalibratingFloor = false;
            if (!IsNamingRoom)
            {
                _nameKeyboard = null;
                _namingVisual = null;
            }
            RefreshCursorVisible();
        }

        private void RefreshCursorVisible()
        {
            if (_cursor != null)
                _cursor.gameObject.SetActive(cursorVisible &&
                    (IsActive || IsCalibratingFloor || IsNamingRoom));
        }

        private void ResolveFloor()
        {
            if (_hasFloor) return;
            if (floor != null && floor.TryGetFloorY(out float y))
            {
                _floorY = y;
                _hasFloor = true;
            }
            else if (FloorHeatmapRenderer.TryGetTrackedFloorY(_origin, out y))
            {
                _floorY = y;
                _hasFloor = true;
            }
        }

        /// <summary>
        /// The work plane follows the ACTIVE storey (auto-switched on stairs or
        /// picked in the panel dropdown) — frozen while a polygon is open or a
        /// drag runs, so in-progress geometry stays coplanar.
        /// </summary>
        private void SyncWorkPlane()
        {
            if (floorManager == null || floorManager.Floors.Count == 0) return;
            if (_activeCorners.Count > 0 || _drag != DragMode.None) return;
            _floorY = floorManager.ActiveBaseY;
            _hasFloor = true;
        }

        // ---- Main loop ----

        private void Update()
        {
            // Periodic room re-broadcast (runs in every mode): a PC viewer
            // started later still receives the current floor plan.
            if (streamer != null && _rooms.Count > 0)
            {
                _roomsResendTimer += Time.deltaTime;
                if (_roomsResendTimer >= 3f)
                    SendRoomsUpdate();
            }

            PollNameKeyboard();

            if (!IsActive && !IsCalibratingFloor && !IsNamingRoom) return;

            SyncWorkPlane();

            GetPointer(out Vector3 handPos, out Vector3 handFwd);
            Vector3 rawCursor = handPos + handFwd * cursorDistance;

            // When the pointer is on the control panel the trigger/A/B belong to
            // the UI (e.g. clicking this tool's own button off) — don't also act.
            bool ui = ControlPanel.PointerOverPanel;

            // -- Floor calibration: unprojected cursor, trigger = take height --
            if (IsCalibratingFloor)
            {
                if (_cursor != null)
                {
                    _cursor.position = rawCursor;
                    SetCursorMat(_matOrange);
                }
                if (!ui && _placeAction.WasPressedThisFrame())
                    ApplyFloorCalibration(rawCursor.y);
                return;
            }

            Vector3 cursor = rawCursor;
            if (_hasFloor) cursor.y = _floorY + lineFloorOffset;

            // -- Room naming: aim into a room on the work plane, trigger opens
            //    the keyboard for its name --
            if (IsNamingRoom)
            {
                bool overRoom = FindRoomContaining(cursor, out int nameRoom);
                if (_cursor != null)
                {
                    _cursor.position = cursor;
                    SetCursorMat(overRoom ? _matGreen : _matYellow);
                }
                if (!ui && overRoom && _nameKeyboard == null &&
                    _placeAction.WasPressedThisFrame())
                {
                    _namingVisual = _visuals[nameRoom];
                    _nameKeyboard = TouchScreenKeyboard.Open(
                        _namingVisual.outline.name ?? "",
                        TouchScreenKeyboardType.Default);
                    Haptics.TickRight();
                }
                return;
            }

            // -- Ongoing drags --
            if (_drag == DragMode.Height) { UpdateHeightDrag(handPos, handFwd); return; }
            if (_drag == DragMode.Corner) { UpdateCornerDrag(cursor); return; }

            // -- Snap onto corner 1 of the open polygon (close the loop) --
            bool snapClose = false;
            if (_activeCorners.Count >= 3)
            {
                Vector3 first = _activeCorners[0];
                if ((cursor - first).sqrMagnitude <= snapRadius * snapRadius)
                {
                    cursor = first;
                    snapClose = true;
                }
            }

            // -- Idle: corner grab / height pull candidates --
            int hoverRoom = -1, hoverCorner = -1;
            bool hoverHeight = false;
            Vector3 heightHit = Vector3.zero;
            if (_activeCorners.Count == 0 && _hasFloor)
            {
                FindNearestRoomCorner(cursor, out hoverRoom, out hoverCorner);
                if (hoverRoom < 0 &&
                    TryRayFloorHit(handPos, handFwd, out heightHit) &&
                    FindRoomContaining(heightHit, out int hitRoom))
                {
                    hoverHeight = true;
                    hoverRoom = hitRoom;
                    cursor = heightHit + Vector3.up * lineFloorOffset;
                }
            }

            if (_cursor != null)
            {
                _cursor.position = cursor;
                SetCursorMat(snapClose ? _matGreen
                    : hoverCorner >= 0 ? _matYellow
                    : hoverHeight ? _matOrange
                    : _matCyan);
            }
            UpdateActiveLine(cursor, true);

            // -- Actions --
            if (!ui && _placeAction.WasPressedThisFrame())
            {
                if (snapClose) CloseRoom();
                else if (hoverCorner >= 0)
                {
                    _drag = DragMode.Corner;
                    _dragRoom = hoverRoom;
                    _dragCorner = hoverCorner;
                }
                else if (hoverHeight)
                {
                    _drag = DragMode.Height;
                    _dragRoom = hoverRoom;
                    _heightAnchor = new Vector3(heightHit.x, _floorY, heightHit.z);
                }
                else PlaceCorner(cursor);
            }
            if (!ui && _undoAction.WasPressedThisFrame())
                UndoCorner();
            if (!ui && _finishAction.WasPressedThisFrame())
                CloseRoom();
        }

        // ---- Room naming ----

        // Applies the Quest keyboard's text live to the room being named.
        private void PollNameKeyboard()
        {
            if (_nameKeyboard == null || _namingVisual == null) return;
            if (_nameKeyboard.status == TouchScreenKeyboard.Status.Visible)
            {
                _namingVisual.outline.name = _nameKeyboard.text ?? "";
                RefreshNameLabel(_namingVisual);
                return;
            }
            // Done / Canceled / LostFocus — finalise and leave the mode.
            if (_nameKeyboard.status == TouchScreenKeyboard.Status.Done)
            {
                _namingVisual.outline.name = _nameKeyboard.text ?? "";
                Haptics.PlaceRight();
            }
            RefreshNameLabel(_namingVisual);
            SendRoomsUpdate();
            _nameKeyboard = null;
            _namingVisual = null;
            IsNamingRoom = false;
            RefreshCursorVisible();
            Debug.Log("[RoomTracer] Raumname gesetzt.");
        }

        // ---- Floor calibration ----

        private void ApplyFloorCalibration(float newFloorY)
        {
            float old = _floorY;
            _floorY = newFloorY;
            _hasFloor = true;
            IsCalibratingFloor = false;
            RefreshCursorVisible();

            // Share the plane with the heatmap (corrects the ACTIVE storey's
            // base with a FloorManager present). Finished rooms deliberately
            // keep their own floorY — with stacked storeys, recalibrating one
            // floor must never drag other floors' rooms along.
            if (floor != null) floor.SetFloorOverride(newFloorY);
            for (int i = 0; i < _activeCorners.Count; i++)
            {
                Vector3 c = _activeCorners[i];
                c.y = newFloorY + lineFloorOffset;
                _activeCorners[i] = c;
                _activeMarkers[i].transform.position = c;
            }
            SendRoomsUpdate();
            Debug.Log($"[RoomTracer] Boden kalibriert: y = {newFloorY:F3} m " +
                      $"(vorher {old:F3}). Räume bleiben auf ihren Etagen.");
        }

        // ---- Height pull ----

        private void UpdateHeightDrag(Vector3 rayOrigin, Vector3 rayDir)
        {
            var v = _visuals[_dragRoom];
            // Closest point between the controller ray and the vertical line
            // through the anchor — the ceiling follows the pointing stick.
            Vector3 up = Vector3.up;
            Vector3 r = _heightAnchor - rayOrigin;
            float b = Vector3.Dot(up, rayDir);
            float c = Vector3.Dot(rayDir, rayDir);
            float d = Vector3.Dot(up, r);
            float e = Vector3.Dot(rayDir, r);
            float denom = c - b * b; // a = up·up = 1
            float s = denom > 1e-5f ? (b * e - c * d) / denom : 0f;
            float height = Mathf.Clamp(s, 0f, maxRoomHeight);

            v.outline.height = height;
            UpdateHeightVisual(v);
            if (_cursor != null)
            {
                _cursor.position = _heightAnchor + Vector3.up * height;
                SetCursorMat(_matOrange);
            }

            if (!_placeAction.IsPressed())
            {
                _drag = DragMode.None;
                SendRoomsUpdate();
                Debug.Log($"[RoomTracer] Raumhöhe: {height:F2} m.");
            }
        }

        // ---- Corner editing ----

        private void UpdateCornerDrag(Vector3 cursor)
        {
            var v = _visuals[_dragRoom];
            v.outline.cornersXZ[_dragCorner] = new Vector2(cursor.x, cursor.z);
            RebuildRoomVisual(v);
            if (_cursor != null)
            {
                _cursor.position = cursor;
                SetCursorMat(_matYellow);
            }
            if (!_placeAction.IsPressed())
            {
                _drag = DragMode.None;
                SendRoomsUpdate();
            }
        }

        private void FindNearestRoomCorner(Vector3 cursor, out int roomIdx, out int cornerIdx)
        {
            roomIdx = -1;
            cornerIdx = -1;
            float bestSqr = snapRadius * snapRadius;
            var p = new Vector2(cursor.x, cursor.z);
            for (int r = 0; r < _visuals.Count; r++)
            {
                // Stacked storeys: only rooms on the current work plane are
                // grabbable — a corner one floor up must not react.
                if (Mathf.Abs(_visuals[r].outline.floorY - _floorY) > 1f) continue;
                var corners = _visuals[r].outline.cornersXZ;
                for (int i = 0; i < corners.Count; i++)
                {
                    float sqr = (corners[i] - p).sqrMagnitude;
                    if (sqr < bestSqr)
                    {
                        bestSqr = sqr;
                        roomIdx = r;
                        cornerIdx = i;
                    }
                }
            }
        }

        private bool TryRayFloorHit(Vector3 origin, Vector3 dir, out Vector3 hit)
        {
            hit = Vector3.zero;
            float planeY = _floorY;
            if (Mathf.Abs(dir.y) < 1e-4f) return false;
            float t = (planeY - origin.y) / dir.y;
            if (t < 0.05f || t > 20f) return false;
            hit = origin + dir * t;
            return true;
        }

        private bool FindRoomContaining(Vector3 point, out int roomIdx)
        {
            var p = new Vector2(point.x, point.z);
            for (int r = 0; r < _visuals.Count; r++)
            {
                // Height pull only targets rooms on the current work plane.
                if (Mathf.Abs(_visuals[r].outline.floorY - _floorY) > 1f) continue;
                if (PointInPolygon(p, _visuals[r].outline.cornersXZ))
                {
                    roomIdx = r;
                    return true;
                }
            }
            roomIdx = -1;
            return false;
        }

        private static bool PointInPolygon(Vector2 p, List<Vector2> poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                if ((poly[i].y > p.y) != (poly[j].y > p.y) &&
                    p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y)
                        / (poly[j].y - poly[i].y) + poly[i].x)
                    inside = !inside;
            }
            return inside;
        }

        // ---- Drawing ----

        private void PlaceCorner(Vector3 point)
        {
            if (!_hasFloor)
            {
                // No floor source at all (Editor): first corner defines it.
                _floorY = point.y - lineFloorOffset;
                _hasFloor = true;
            }
            _activeCorners.Add(point);
            var marker = CreateSphere(_root.transform, 0.035f);
            marker.position = point;
            _activeMarkers.Add(marker.gameObject);
            Haptics.PlaceRight();
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

            var vis = new RoomVisual { outline = room };
            vis.root = new GameObject($"Room_{_rooms.Count}");
            vis.root.transform.SetParent(_root.transform, false);
            var baseGo = new GameObject("Base");
            baseGo.transform.SetParent(vis.root.transform, false);
            vis.baseLine = CreateLine(baseGo);
            vis.baseLine.loop = true;
            foreach (var marker in _activeMarkers)
            {
                marker.transform.SetParent(vis.root.transform, true);
                vis.markers.Add(marker.transform);
            }
            _visuals.Add(vis);
            RebuildRoomVisual(vis);

            _activeCorners.Clear();
            _activeMarkers.Clear();
            UpdateActiveLine(Vector3.zero, false);
            Haptics.PlaceRight();
            SendRoomsUpdate();
            Debug.Log($"[RoomTracer] Raum {_rooms.Count} geschlossen " +
                      $"({room.cornersXZ.Count} Ecken).");
        }

        /// <summary>Deletes one finished room (delete mode).</summary>
        public void RemoveRoom(int index)
        {
            if (index < 0 || index >= _visuals.Count) return;
            var snapshot = UndoStack.CloneRooms(_rooms);
            UndoStack.Push(LocalizationManager.Get("room.deleted_undo"),
                () => LoadRooms(snapshot));
            Destroy(_visuals[index].root);
            _visuals.RemoveAt(index);
            _rooms.RemoveAt(index);
            Haptics.HeavyRight();
            SendRoomsUpdate();
            Debug.Log($"[RoomTracer] Raum {index + 1} gelöscht " +
                      $"({_rooms.Count} verbleibend).");
        }

        /// <summary>Tints a finished room's visuals yellow (delete-mode hover).</summary>
        public void SetRoomHighlight(int index, bool on)
        {
            if (index < 0 || index >= _visuals.Count) return;
            var v = _visuals[index];
            Material m = on ? _matYellow : _matCyan;
            if (m == null) return;
            if (v.baseLine != null) v.baseLine.sharedMaterial = m;
            if (v.topLine != null) v.topLine.sharedMaterial = m;
            foreach (var line in v.verticalLines)
                if (line != null) line.sharedMaterial = m;
            foreach (var marker in v.markers)
            {
                var r = marker != null ? marker.GetComponent<Renderer>() : null;
                if (r != null) r.sharedMaterial = m;
            }
        }

        /// <summary>
        /// Intersects a ray with the surfaces of all finished rooms: wall
        /// quads (extruded outline edges, rooms with height), floor polygon
        /// and ceiling polygon. Returns the nearest hit (drill planning).
        /// </summary>
        public bool TryRaycastRoomSurfaces(Vector3 origin, Vector3 dir,
            out Vector3 hit)
        {
            hit = Vector3.zero;
            float bestT = float.MaxValue;
            const float tMin = 0.05f, tMax = 20f;
            var origin2 = new Vector2(origin.x, origin.z);

            foreach (var room in _rooms)
            {
                var corners = room.cornersXZ;
                int n = corners.Count;
                if (n < 3) continue;
                float y0 = room.floorY;
                bool extruded = room.height > 0.02f;
                float y1 = y0 + room.height;

                // Floor (always) and ceiling (extruded rooms only).
                if (Mathf.Abs(dir.y) > 1e-4f)
                {
                    float t = (y0 - origin.y) / dir.y;
                    if (t > tMin && t < tMax && t < bestT)
                    {
                        Vector3 p = origin + dir * t;
                        if (PointInPolygon(new Vector2(p.x, p.z), corners))
                        { bestT = t; hit = p; }
                    }
                    if (extruded)
                    {
                        t = (y1 - origin.y) / dir.y;
                        if (t > tMin && t < tMax && t < bestT)
                        {
                            Vector3 p = origin + dir * t;
                            if (PointInPolygon(new Vector2(p.x, p.z), corners))
                            { bestT = t; hit = p; }
                        }
                    }
                }

                // Walls: vertical quad over each outline edge.
                if (!extruded) continue;
                for (int i = 0; i < n; i++)
                {
                    Vector2 a = corners[i];
                    Vector2 e = corners[(i + 1) % n] - a;
                    float lenSqr = e.sqrMagnitude;
                    if (lenSqr < 1e-6f) continue;
                    var nrm = new Vector2(e.y, -e.x); // horizontal wall normal
                    float denom = nrm.x * dir.x + nrm.y * dir.z;
                    if (Mathf.Abs(denom) < 1e-5f) continue; // parallel
                    float t = (nrm.x * (a.x - origin2.x)
                             + nrm.y * (a.y - origin2.y)) / denom;
                    if (t <= tMin || t >= tMax || t >= bestT) continue;
                    Vector3 p = origin + dir * t;
                    if (p.y < y0 || p.y > y1) continue;
                    float s = ((p.x - a.x) * e.x + (p.z - a.y) * e.y) / lenSqr;
                    if (s < 0f || s > 1f) continue;
                    bestT = t;
                    hit = p;
                }
            }
            return bestT < float.MaxValue;
        }

        /// <summary>Edge <paramref name="edgeIndex"/> of a room: first corner
        /// (XZ), unit direction (XZ) and length in metres. False if invalid.</summary>
        public static bool TryGetEdge(RoomOutline room, int edgeIndex,
            out Vector2 corner, out Vector2 dir, out float length)
        {
            corner = dir = Vector2.zero;
            length = 0f;
            int n = room.cornersXZ.Count;
            if (n < 2 || edgeIndex < 0 || edgeIndex >= n) return false;
            corner = room.cornersXZ[edgeIndex];
            Vector2 e = room.cornersXZ[(edgeIndex + 1) % n] - corner;
            length = e.magnitude;
            if (length < 1e-4f) return false;
            dir = e / length;
            return true;
        }

        /// <summary>
        /// Intersects a ray with the vertical WALL quads of all finished,
        /// extruded rooms and returns the nearest hit's wall identity plus where
        /// on it the ray landed: room index, edge index, metres along that edge
        /// from its first corner, and the absolute world Y. Used by the opening
        /// tool to place doors/windows.
        /// </summary>
        public bool TryRaycastWall(Vector3 origin, Vector3 dir, out int roomIdx,
            out int edgeIdx, out float along, out float y)
        {
            roomIdx = edgeIdx = -1;
            along = y = 0f;
            float bestT = float.MaxValue;
            const float tMin = 0.05f, tMax = 20f;
            var origin2 = new Vector2(origin.x, origin.z);

            for (int r = 0; r < _rooms.Count; r++)
            {
                var room = _rooms[r];
                var corners = room.cornersXZ;
                int n = corners.Count;
                if (n < 3 || room.height <= 0.02f) continue;
                float y0 = room.floorY;
                float y1 = y0 + room.height;
                for (int i = 0; i < n; i++)
                {
                    Vector2 a = corners[i];
                    Vector2 e = corners[(i + 1) % n] - a;
                    float lenSqr = e.sqrMagnitude;
                    if (lenSqr < 1e-6f) continue;
                    var nrm = new Vector2(e.y, -e.x); // horizontal wall normal
                    float denom = nrm.x * dir.x + nrm.y * dir.z;
                    if (Mathf.Abs(denom) < 1e-5f) continue;
                    float t = (nrm.x * (a.x - origin2.x)
                             + nrm.y * (a.y - origin2.y)) / denom;
                    if (t <= tMin || t >= tMax || t >= bestT) continue;
                    Vector3 p = origin + dir * t;
                    if (p.y < y0 || p.y > y1) continue;
                    float s = ((p.x - a.x) * e.x + (p.z - a.y) * e.y) / lenSqr;
                    if (s < 0f || s > 1f) continue;
                    bestT = t;
                    roomIdx = r;
                    edgeIdx = i;
                    along = s * Mathf.Sqrt(lenSqr);
                    y = p.y;
                }
            }
            return bestT < float.MaxValue;
        }

        /// <summary>Adds a door/window to a room and refreshes its visuals.</summary>
        public void AddOpening(int roomIdx, WallOpening opening)
        {
            if (roomIdx < 0 || roomIdx >= _visuals.Count) return;
            _rooms[roomIdx].openings.Add(opening);
            RebuildRoomVisual(_visuals[roomIdx]);
            SendRoomsUpdate();
            Debug.Log($"[RoomTracer] {(opening.isDoor ? "Tür" : "Fenster")} " +
                      $"in Raum {roomIdx + 1} gesetzt " +
                      $"({_rooms[roomIdx].openings.Count} Öffnung(en)).");
        }

        /// <summary>Removes the most recently added opening across all rooms
        /// (undo for the opening tool). Returns false if there were none.</summary>
        public bool RemoveLastOpening()
        {
            for (int r = _rooms.Count - 1; r >= 0; r--)
            {
                var ops = _rooms[r].openings;
                if (ops.Count == 0) continue;
                var snapshot = UndoStack.CloneRooms(_rooms);
                UndoStack.Push(LocalizationManager.Get("opening.removed_undo"),
                    () => LoadRooms(snapshot));
                ops.RemoveAt(ops.Count - 1);
                RebuildRoomVisual(_visuals[r]);
                SendRoomsUpdate();
                return true;
            }
            return false;
        }

        /// <summary>
        /// Replaces all finished rooms with loaded outlines (session load /
        /// re-alignment) and rebuilds their visuals. Also clears any open
        /// polygon via ClearRooms.
        /// </summary>
        public void LoadRooms(IReadOnlyList<RoomOutline> rooms)
        {
            if (_root == null)
            {
                Debug.LogWarning("[RoomTracer] Noch nicht initialisiert.");
                return;
            }
            ClearRooms();
            foreach (var outline in rooms)
            {
                if (outline?.cornersXZ == null || outline.cornersXZ.Count < 3)
                    continue;
                if (outline.openings == null)
                    outline.openings = new List<WallOpening>();
                _rooms.Add(outline);

                var vis = new RoomVisual { outline = outline };
                vis.root = new GameObject($"Room_{_rooms.Count}");
                vis.root.transform.SetParent(_root.transform, false);
                var baseGo = new GameObject("Base");
                baseGo.transform.SetParent(vis.root.transform, false);
                vis.baseLine = CreateLine(baseGo);
                vis.baseLine.loop = true;
                for (int i = 0; i < outline.cornersXZ.Count; i++)
                    vis.markers.Add(CreateSphere(vis.root.transform, 0.035f));
                _visuals.Add(vis);
                RebuildRoomVisual(vis);
            }
            if (_rooms.Count > 0)
            {
                // Continue tracing/height-dragging on the loaded floor plane.
                _floorY = _rooms[_rooms.Count - 1].floorY;
                _hasFloor = true;
            }
            SendRoomsUpdate();
            Debug.Log($"[RoomTracer] {_rooms.Count} Raum/Räume geladen.");
        }

        public void ClearRooms()
        {
            _rooms.Clear();
            foreach (var v in _visuals) Destroy(v.root);
            _visuals.Clear();
            _activeCorners.Clear();
            foreach (var m in _activeMarkers) Destroy(m);
            _activeMarkers.Clear();
            UpdateActiveLine(Vector3.zero, false);
            SendRoomsUpdate();
        }

        /// <summary>Clears geometry plus transient drawing/calibration state and
        /// adopts the freshly reset storey's tracked floor.</summary>
        public void ResetForNewSession()
        {
            ClearRooms();
            _drag = DragMode.None;
            _dragRoom = _dragCorner = -1;
            IsCalibratingFloor = false;
            IsNamingRoom = false;
            _nameKeyboard = null;
            _namingVisual = null;
            _hasFloor = false;
            if (floorManager != null && floorManager.Floors.Count > 0)
            {
                _floorY = floorManager.ActiveBaseY;
                _hasFloor = true;
            }
            else
                ResolveFloor();
            RefreshCursorVisible();
        }

        /// <summary>Pushes the current floor plan to the PC viewer (UDP).</summary>
        private void SendRoomsUpdate()
        {
            if (streamer == null) return;
            _roomsResendTimer = 0f;
            var packet = new RoomsPacket
            {
                gridYawDegrees = floor != null ? floor.gridYawDegrees : 0f,
                rooms = _rooms,
                floorBases = new List<float>(),
                floorLabels = new List<string>(),
            };
            if (floorManager != null)
                foreach (var f in floorManager.Floors)
                {
                    packet.floorBases.Add(f.baseY);
                    packet.floorLabels.Add(f.label);
                }
            streamer.SendRaw(JsonUtility.ToJson(packet));
        }

        // ---- Export ----

        /// <summary>SVG + raw JSON + extruded OBJ alongside the other exports.
        /// Optional drill lines are drawn into the SVG as entry-point crosses.</summary>
        public void Export(string stem, IReadOnlyList<DrillLine> drills = null)
        {
            if (_rooms.Count == 0) return;
            float gridYaw = floor != null ? floor.gridYawDegrees : 0f;

            // One floor plan PER STOREY — mixing storeys in one 2D plan is
            // unreadable. Single storey keeps the plain _rooms.svg name.
            if (floorManager != null && floorManager.Floors.Count > 1)
            {
                DataExporter.ExportRoomsJson(_rooms, gridYaw, stem); // all rooms once
                foreach (var level in floorManager.Floors)
                {
                    var floorRooms = new List<RoomOutline>();
                    foreach (var room in _rooms)
                        if (Mathf.Abs(room.floorY - level.baseY) <= 1f)
                            floorRooms.Add(room);
                    if (floorRooms.Count == 0) continue;
                    var floorDrills = FilterDrills(drills, level.baseY);
                    DataExporter.ExportRoomsSvg(floorRooms, gridYaw, stem,
                        floorDrills, "_" + level.label, writeJson: false);
                }
            }
            else
            {
                DataExporter.ExportRoomsSvg(_rooms, gridYaw, stem, drills);
            }
            DataExporter.ExportRoomsObj(_rooms, stem); // 3D: all storeys stacked
        }

        // Drill lines whose entry sits on the given storey (base .. base+3.2 m).
        private static List<DrillLine> FilterDrills(
            IReadOnlyList<DrillLine> drills, float baseY)
        {
            if (drills == null) return null;
            var result = new List<DrillLine>();
            foreach (var d in drills)
                if (d.entry.y >= baseY - 0.3f && d.entry.y <= baseY + 3.2f)
                    result.Add(d);
            return result.Count > 0 ? result : null;
        }

        // ---- Visuals ----

        // Rebuilds base outline, corner markers and (if height > 0) the top
        // loop + vertical edges of one room.
        private void RebuildRoomVisual(RoomVisual v)
        {
            var corners = v.outline.cornersXZ;
            int n = corners.Count;
            float y0 = v.outline.floorY + lineFloorOffset;

            v.baseLine.positionCount = n;
            for (int i = 0; i < n; i++)
                v.baseLine.SetPosition(i, new Vector3(corners[i].x, y0, corners[i].y));

            for (int i = 0; i < v.markers.Count && i < n; i++)
                v.markers[i].position = new Vector3(corners[i].x, y0, corners[i].y);

            UpdateHeightVisual(v);
            UpdateOpeningVisual(v);
            RefreshNameLabel(v);
        }

        // Floating room name above the floor centroid (hidden while unnamed).
        private void RefreshNameLabel(RoomVisual v)
        {
            bool hasName = !string.IsNullOrEmpty(v.outline.name);
            if (v.nameLabel == null)
            {
                if (!hasName) return;
                v.nameLabel = WorldLabel.Create(v.root.transform, 0.0022f, 30f);
                v.nameLabel.SetColor(new Color(0.55f, 0.9f, 1f));
            }
            var corners = v.outline.cornersXZ;
            Vector2 centroid = Vector2.zero;
            foreach (var c in corners) centroid += c;
            if (corners.Count > 0) centroid /= corners.Count;
            v.nameLabel.SetWorldPosition(new Vector3(
                centroid.x, v.outline.floorY + 1.3f, centroid.y));
            v.nameLabel.SetText(v.outline.name);
            v.nameLabel.SetVisible(hasName);
        }

        // Draws each door/window as a rectangle loop on its wall (doors orange,
        // windows yellow), following the current corners so edits move them too.
        private void UpdateOpeningVisual(RoomVisual v)
        {
            var room = v.outline;
            int need = room.openings.Count;
            while (v.openingLines.Count < need)
            {
                var go = new GameObject("Opening");
                go.transform.SetParent(v.root.transform, false);
                var line = CreateLine(go);
                line.loop = true;
                line.positionCount = 4;
                v.openingLines.Add(line);
            }
            for (int i = 0; i < v.openingLines.Count; i++)
            {
                bool used = i < need;
                v.openingLines[i].enabled = used;
                if (!used) continue;
                var op = room.openings[i];
                if (!TryGetEdge(room, op.edgeIndex, out Vector2 a, out Vector2 dir, out _))
                {
                    v.openingLines[i].enabled = false;
                    continue;
                }
                Vector2 c0 = a + dir * op.startAlong;
                Vector2 c1 = a + dir * op.endAlong;
                var line = v.openingLines[i];
                line.SetPosition(0, new Vector3(c0.x, op.bottomY, c0.y));
                line.SetPosition(1, new Vector3(c1.x, op.bottomY, c1.y));
                line.SetPosition(2, new Vector3(c1.x, op.topY, c1.y));
                line.SetPosition(3, new Vector3(c0.x, op.topY, c0.y));
                line.sharedMaterial = op.isDoor ? _matOrange : _matYellow;
            }
        }

        private void UpdateHeightVisual(RoomVisual v)
        {
            var corners = v.outline.cornersXZ;
            int n = corners.Count;
            bool hasHeight = v.outline.height > 0.02f;
            float y0 = v.outline.floorY + lineFloorOffset;
            float y1 = v.outline.floorY + v.outline.height;

            if (hasHeight && v.topLine == null)
            {
                var topGo = new GameObject("Top");
                topGo.transform.SetParent(v.root.transform, false);
                v.topLine = CreateLine(topGo);
                v.topLine.loop = true;
            }
            if (v.topLine != null)
            {
                v.topLine.enabled = hasHeight;
                if (hasHeight)
                {
                    v.topLine.positionCount = n;
                    for (int i = 0; i < n; i++)
                        v.topLine.SetPosition(i,
                            new Vector3(corners[i].x, y1, corners[i].y));
                }
            }

            while (v.verticalLines.Count < (hasHeight ? n : 0))
            {
                var go = new GameObject("Edge");
                go.transform.SetParent(v.root.transform, false);
                var line = CreateLine(go);
                line.positionCount = 2;
                v.verticalLines.Add(line);
            }
            for (int i = 0; i < v.verticalLines.Count; i++)
            {
                bool used = hasHeight && i < n;
                v.verticalLines[i].enabled = used;
                if (used)
                {
                    v.verticalLines[i].SetPosition(0,
                        new Vector3(corners[i].x, y0, corners[i].y));
                    v.verticalLines[i].SetPosition(1,
                        new Vector3(corners[i].x, y1, corners[i].y));
                }
            }
        }

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

        private void SetCursorMat(Material m)
        {
            if (_cursorRenderer != null && m != null)
                _cursorRenderer.sharedMaterial = m;
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

        private Transform CreateSphere(Transform parent, float diameter)
        {
            var go = new GameObject("TracePoint");
            go.transform.SetParent(parent, false);
            go.transform.localScale = Vector3.one * diameter;
            go.AddComponent<MeshFilter>().sharedMesh = _sphereMesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _matCyan;
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
            line.sharedMaterial = _matCyan;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            return line;
        }
    }
}
