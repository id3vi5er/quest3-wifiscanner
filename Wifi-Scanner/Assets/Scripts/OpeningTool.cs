using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace WifiScanner
{
    /// <summary>
    /// Doors &amp; windows ("Türen/Fenster" on the panel). While active, the right
    /// controller's pointer ray is intersected with the walls of finished,
    /// extruded rooms (RoomTracer.TryRaycastWall). Two trigger clicks on the SAME
    /// wall define an opening:
    ///   Tür    (door):   1. click = the top corner, 2. click = the width.
    ///                    Height runs automatically down to the floor.
    ///   Fenster (window): 1. + 2. click = two opposite corners of a free
    ///                    rectangle (full control over size).
    ///   A = switch door/window,  B = remove the last opening.
    ///
    /// Openings are stored on the room (RoomTracer.WallOpening), streamed with the
    /// rooms packet and cut into the OBJ / drawn into the SVG on export.
    /// Created and wired by SceneBootstrap.
    /// </summary>
    public class OpeningTool : MonoBehaviour, IExclusiveTool
    {
        [Header("Wired by SceneBootstrap")]
        public RoomTracer roomTracer;
        [Tooltip("Material ASSET (URP/Unlit) cloned + tinted for the preview.")]
        public Material visualMaterial;

        [Header("Openings")]
        public float cursorDiameter = 0.05f;
        public float lineWidth = 0.015f;

        public bool IsActive { get; private set; }

        private InputAction _posAction;
        private InputAction _rotAction;
        private InputAction _placeAction;
        private InputAction _typeAction;
        private InputAction _removeAction;

        private GameObject _root;
        private Transform _cursor;
        private Renderer _cursorRenderer;
        private Transform _hingeMarker; // marks the door hinge (first click)
        private LineRenderer _preview;
        private Material _matCyan, _matOrange, _matYellow, _matGreen;
        private Mesh _sphereMesh;

        private bool _isDoor = true;
        // First-click anchor while placing.
        private bool _placing;
        private int _anchorRoom = -1, _anchorEdge = -1;
        private float _anchorAlong, _anchorY;

        private void Start()
        {
            var temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _sphereMesh = temp.GetComponent<MeshFilter>().sharedMesh;
            Destroy(temp);

            if (visualMaterial != null)
            {
                _matCyan = MakeTinted(new Color(0f, 0.86f, 1f, 1f));
                _matOrange = MakeTinted(new Color(1f, 0.55f, 0.1f, 1f));   // door
                _matYellow = MakeTinted(new Color(1f, 0.85f, 0.1f, 1f));   // window
                _matGreen = MakeTinted(new Color(0.2f, 1f, 0.3f, 1f));     // hinge
            }
            else
            {
                Debug.LogError("[OpeningTool] visualMaterial not assigned — assign the " +
                               "point material in SceneBootstrap.");
            }

            _root = new GameObject("OpeningToolVisuals");
            _root.transform.SetParent(transform, false);

            _cursor = CreateSphere(_root.transform, cursorDiameter, _matCyan);
            _cursorRenderer = _cursor.GetComponent<Renderer>();
            _cursor.gameObject.SetActive(false);

            _hingeMarker = CreateSphere(_root.transform, cursorDiameter * 1.25f, _matGreen);
            _hingeMarker.gameObject.SetActive(false);

            var lineGo = new GameObject("Preview");
            lineGo.transform.SetParent(_root.transform, false);
            _preview = lineGo.AddComponent<LineRenderer>();
            _preview.loop = true;
            _preview.positionCount = 4;
            _preview.startWidth = _preview.endWidth = lineWidth;
            _preview.sharedMaterial = _matOrange;
            _preview.shadowCastingMode = ShadowCastingMode.Off;
            _preview.receiveShadows = false;
            _preview.enabled = false;
        }

        private Material MakeTinted(Color c)
        {
            var m = new Material(visualMaterial); // asset clone keeps its variant
            m.SetColor("_BaseColor", c);
            return m;
        }

        private void OnEnable()
        {
            _posAction = new InputAction("OpeningPos", InputActionType.Value,
                "<XRController>{RightHand}/pointerPosition", expectedControlType: "Vector3");
            _posAction.AddBinding("<XRController>{RightHand}/devicePosition"); // fallback
            _rotAction = new InputAction("OpeningRot", InputActionType.Value,
                "<XRController>{RightHand}/pointerRotation", expectedControlType: "Quaternion");
            _rotAction.AddBinding("<XRController>{RightHand}/deviceRotation"); // fallback
            _placeAction = new InputAction("OpeningPlace", InputActionType.Button,
                "<XRController>{RightHand}/triggerPressed");
            _typeAction = new InputAction("OpeningType", InputActionType.Button,
                "<XRController>{RightHand}/primaryButton");
            _removeAction = new InputAction("OpeningRemove", InputActionType.Button,
                "<XRController>{RightHand}/secondaryButton");
            _posAction.Enable();
            _rotAction.Enable();
            _placeAction.Enable();
            _typeAction.Enable();
            _removeAction.Enable();
            ExclusiveTools.Register(this);
        }

        private void OnDisable()
        {
            _posAction?.Disable();
            _rotAction?.Disable();
            _placeAction?.Disable();
            _typeAction?.Disable();
            _removeAction?.Disable();
            ExclusiveTools.Unregister(this);
        }

        // ---- Mode control ----

        public void ToggleActive() => SetActive(!IsActive);
        public bool IsDoorMode => _isDoor;

        public void SetActive(bool active)
        {
            if (IsActive == active) return;
            IsActive = active;
            if (active) ExclusiveTools.DeactivateOthers(this);
            _placing = false;
            if (_cursor != null) _cursor.gameObject.SetActive(active);
            if (_hingeMarker != null) _hingeMarker.gameObject.SetActive(false);
            if (_preview != null) _preview.enabled = false;
            Debug.Log($"[OpeningTool] Türen/Fenster {(IsActive ? "AKTIV" : "beendet")}.");
        }

        // ---- Main loop ----

        private void Update()
        {
            if (!IsActive) return;

            bool ui = ControlPanel.PointerOverPanel;
            if (!ui && _typeAction.WasPressedThisFrame())
                _isDoor = !_isDoor;
            if (!ui && _removeAction.WasPressedThisFrame())
            {
                if (roomTracer != null) roomTracer.RemoveLastOpening();
                _placing = false;
                _preview.enabled = false;
                return;
            }

            GetPointer(out Vector3 handPos, out Vector3 handFwd);
            int roomIdx = -1, edgeIdx = -1;
            float along = 0f, y = 0f;
            bool hit = roomTracer != null &&
                roomTracer.TryRaycastWall(handPos, handFwd, out roomIdx,
                    out edgeIdx, out along, out y);

            if (_cursor != null)
            {
                _cursor.gameObject.SetActive(hit);
                if (hit)
                {
                    Vector3 c = WallPointToWorld(roomIdx, edgeIdx, along, y);
                    _cursor.position = c;
                    SetCursorMat(_isDoor ? _matOrange : _matYellow);
                }
            }

            // Live preview of the pending opening on the anchor's wall.
            if (_placing && hit && roomIdx == _anchorRoom && edgeIdx == _anchorEdge)
                ShowPreview(_anchorRoom, _anchorEdge, along, y);
            else if (!_placing)
                _preview.enabled = false;

            // Green dot on the door hinge (Anschlagseite) = the first click.
            bool showHinge = _placing && _isDoor;
            _hingeMarker.gameObject.SetActive(showHinge);
            if (showHinge)
                _hingeMarker.position =
                    WallPointToWorld(_anchorRoom, _anchorEdge, _anchorAlong, _anchorY);

            if (ui || !_placeAction.WasPressedThisFrame()) return;

            if (!hit)
            {
                // Clicking off a wall cancels an in-progress placement.
                _placing = false;
                _preview.enabled = false;
                return;
            }

            if (!_placing)
            {
                _placing = true;
                _anchorRoom = roomIdx;
                _anchorEdge = edgeIdx;
                _anchorAlong = along;
                _anchorY = y;
                Haptics.TickRight();
            }
            else if (roomIdx == _anchorRoom && edgeIdx == _anchorEdge)
            {
                Finish(along, y);
            }
            // A second click on a different wall is ignored (keep the anchor).
        }

        private void Finish(float along2, float y2)
        {
            var room = roomTracer.Rooms[_anchorRoom];
            float start = Mathf.Min(_anchorAlong, along2);
            float end = Mathf.Max(_anchorAlong, along2);
            if (end - start < 0.02f) { _placing = false; _preview.enabled = false; return; }

            var op = new WallOpening
            {
                edgeIndex = _anchorEdge,
                startAlong = start,
                endAlong = end,
                isDoor = _isDoor,
                hingeAlong = _anchorAlong, // first click = hinge (Anschlagseite)
            };
            if (_isDoor)
            {
                // First click = top corner on the hinge side; height runs down
                // to the floor.
                op.bottomY = room.floorY;
                op.topY = Mathf.Max(_anchorY, room.floorY + 0.05f);
            }
            else
            {
                op.bottomY = Mathf.Min(_anchorY, y2);
                op.topY = Mathf.Max(_anchorY, y2);
            }

            roomTracer.AddOpening(_anchorRoom, op);
            _placing = false;
            _preview.enabled = false;
            Haptics.PlaceRight();
        }

        // ---- Preview ----

        private void ShowPreview(int roomIdx, int edgeIdx, float along2, float y2)
        {
            var room = roomTracer.Rooms[roomIdx];
            float start = Mathf.Min(_anchorAlong, along2);
            float end = Mathf.Max(_anchorAlong, along2);
            float bottomY, topY;
            if (_isDoor) { bottomY = room.floorY; topY = _anchorY; }
            else { bottomY = Mathf.Min(_anchorY, y2); topY = Mathf.Max(_anchorY, y2); }

            if (!RoomTracer.TryGetEdge(room, edgeIdx, out Vector2 a, out Vector2 dir, out _))
            { _preview.enabled = false; return; }
            Vector2 c0 = a + dir * start;
            Vector2 c1 = a + dir * end;
            _preview.sharedMaterial = _isDoor ? _matOrange : _matYellow;
            _preview.enabled = true;
            _preview.SetPosition(0, new Vector3(c0.x, bottomY, c0.y));
            _preview.SetPosition(1, new Vector3(c1.x, bottomY, c1.y));
            _preview.SetPosition(2, new Vector3(c1.x, topY, c1.y));
            _preview.SetPosition(3, new Vector3(c0.x, topY, c0.y));
        }

        private Vector3 WallPointToWorld(int roomIdx, int edgeIdx, float along, float y)
        {
            var room = roomTracer.Rooms[roomIdx];
            if (!RoomTracer.TryGetEdge(room, edgeIdx, out Vector2 a, out Vector2 dir, out _))
                return new Vector3(0f, y, 0f);
            Vector2 p = a + dir * along;
            return new Vector3(p.x, y, p.y);
        }

        // ---- Helpers ----

        private void SetCursorMat(Material m)
        {
            if (_cursorRenderer != null && m != null) _cursorRenderer.sharedMaterial = m;
        }

        private Transform CreateSphere(Transform parent, float diameter, Material mat)
        {
            var go = new GameObject("OpeningCursor");
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
