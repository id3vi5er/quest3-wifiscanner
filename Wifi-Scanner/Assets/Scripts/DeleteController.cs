using UnityEngine;
using UnityEngine.InputSystem;

namespace WifiScanner
{
    /// <summary>
    /// Universal delete mode ("Löschen" on the panel). While active, the right
    /// controller's pointer ray is tested against every deletable element —
    /// traced rooms, drill lines, distance measurements, spatial notes and ping
    /// nodes. The nearest target within hitRadius highlights; a DOUBLE trigger
    /// press on it deletes it (the first press arms with a "nochmal drücken"
    /// prompt, a safety confirmation). Hover is re-resolved every frame — indices
    /// shift after a delete. Created and wired by SceneBootstrap.
    /// </summary>
    public class DeleteController : MonoBehaviour, IExclusiveTool
    {
        private enum TargetKind { None, Room, DrillLine, Measurement, PingNode, Note }

        [Header("Wired by SceneBootstrap")]
        public RoomTracer roomTracer;
        public DrillPlanner drill;
        public DistanceMeasurer distanceMeasurer;
        public PingTester pingTester;
        public SpatialNoteManager spatialNotes;

        [Header("Deleting")]
        [Tooltip("Max distance between ray and target to count as a hit (m).")]
        public float hitRadius = 0.15f;
        [Tooltip("Seconds the first (arming) trigger press stays valid.")]
        public float confirmWindow = 2.5f;

        public bool IsActive { get; private set; }

        private InputAction _posAction;
        private InputAction _rotAction;
        private InputAction _deleteAction;

        private TargetKind _hoverKind = TargetKind.None;
        private int _hoverIndex = -1;

        private TargetKind _armedKind = TargetKind.None;
        private int _armedIndex = -1;
        private float _armedUntil;

        private GameObject _root;
        private WorldLabel _prompt;

        private void Start()
        {
            _root = new GameObject("DeleteControllerVisuals");
            _root.transform.SetParent(transform, false);
            _prompt = WorldLabel.Create(_root.transform);
            _prompt.SetColor(new Color(1f, 0.5f, 0.4f));
            _prompt.SetVisible(false);
        }

        private void OnEnable()
        {
            _posAction = new InputAction("DeletePos", InputActionType.Value,
                "<XRController>{RightHand}/pointerPosition", expectedControlType: "Vector3");
            _posAction.AddBinding("<XRController>{RightHand}/devicePosition"); // fallback
            _rotAction = new InputAction("DeleteRot", InputActionType.Value,
                "<XRController>{RightHand}/pointerRotation", expectedControlType: "Quaternion");
            _rotAction.AddBinding("<XRController>{RightHand}/deviceRotation"); // fallback
            _deleteAction = new InputAction("DeleteTrigger", InputActionType.Button,
                "<XRController>{RightHand}/triggerPressed");
            _posAction.Enable();
            _rotAction.Enable();
            _deleteAction.Enable();
            ExclusiveTools.Register(this);
        }

        private void OnDisable()
        {
            _posAction?.Disable();
            _rotAction?.Disable();
            _deleteAction?.Disable();
            ExclusiveTools.Unregister(this);
        }

        // ---- Mode control ----

        public void ToggleActive() => SetActive(!IsActive);

        public void SetActive(bool active)
        {
            if (IsActive == active) return;
            IsActive = active;
            if (active) ExclusiveTools.DeactivateOthers(this);
            else { ClearHover(); Disarm(); }
            Debug.Log($"[DeleteController] Löschmodus " +
                      $"{(IsActive ? "AKTIV" : "beendet")}.");
        }

        // ---- Main loop ----

        private void Update()
        {
            if (!IsActive) return;

            GetPointer(out Vector3 origin, out Vector3 dir);

            // Nearest target of ANY kind within hitRadius of the ray. Re-resolved
            // every frame so deletes never act on a stale index.
            TargetKind kind = TargetKind.None;
            int index = -1;
            float best = hitRadius;

            if (drill != null)
                for (int i = 0; i < drill.Lines.Count; i++)
                {
                    drill.GetSegment(i, out Vector3 a, out Vector3 b);
                    Consider(RaySegmentDistance(origin, dir, a, b), TargetKind.DrillLine,
                        i, ref best, ref kind, ref index);
                }

            if (distanceMeasurer != null)
                for (int i = 0; i < distanceMeasurer.Measurements.Count; i++)
                    if (distanceMeasurer.TryGetSegment(i, out Vector3 a, out Vector3 b))
                        Consider(RaySegmentDistance(origin, dir, a, b),
                            TargetKind.Measurement, i, ref best, ref kind, ref index);

            if (pingTester != null)
                for (int i = 0; i < pingTester.NodeCount; i++)
                    if (pingTester.TryGetNodePos(i, out Vector3 p))
                        Consider(RayPointDistance(origin, dir, p), TargetKind.PingNode,
                            i, ref best, ref kind, ref index);

            if (spatialNotes != null)
                for (int i = 0; i < spatialNotes.Notes.Count; i++)
                    if (spatialNotes.TryGetNotePos(i, out Vector3 p))
                        Consider(RayPointDistance(origin, dir, p), TargetKind.Note,
                            i, ref best, ref kind, ref index);

            if (roomTracer != null)
            {
                var rooms = roomTracer.Rooms;
                for (int r = 0; r < rooms.Count; r++)
                {
                    var room = rooms[r];
                    int n = room.cornersXZ.Count;
                    float y0 = room.floorY;
                    float y1 = y0 + room.height;
                    bool extruded = room.height > 0.02f;
                    for (int i = 0; i < n; i++)
                    {
                        Vector2 ca = room.cornersXZ[i];
                        Vector2 cb = room.cornersXZ[(i + 1) % n];
                        var a = new Vector3(ca.x, y0, ca.y);
                        var b = new Vector3(cb.x, y0, cb.y);
                        float d = RaySegmentDistance(origin, dir, a, b);
                        if (extruded)
                        {
                            var top = new Vector3(ca.x, y1, ca.y);
                            d = Mathf.Min(d, RaySegmentDistance(origin, dir, a, top));
                        }
                        Consider(d, TargetKind.Room, r, ref best, ref kind, ref index);
                    }
                }
            }

            SetHover(kind, index);

            // Arming timeout.
            if (_armedKind != TargetKind.None && Time.unscaledTime > _armedUntil)
                Disarm();

            // Keep the confirm prompt above the armed target.
            if (_armedKind != TargetKind.None && TryGetTargetPos(_armedKind, _armedIndex,
                    out Vector3 armedPos))
            {
                _prompt.SetVisible(true);
                _prompt.SetWorldPosition(armedPos + Vector3.up * 0.12f);
                _prompt.SetText("Nochmal drücken\nzum Löschen");
            }
            else _prompt.SetVisible(false);

            if (_hoverKind == TargetKind.None || ControlPanel.PointerOverPanel ||
                !_deleteAction.WasPressedThisFrame())
                return;

            // Double-press confirmation: first press arms this target, a second
            // press on the SAME target within the window deletes it.
            if (_armedKind == _hoverKind && _armedIndex == _hoverIndex &&
                Time.unscaledTime <= _armedUntil)
            {
                TargetKind k = _hoverKind;
                int idx = _hoverIndex;
                ClearHover(); // indices shift after the delete
                Disarm();
                DeleteTarget(k, idx); // the Remove* methods pulse HeavyRight
            }
            else
            {
                _armedKind = _hoverKind;
                _armedIndex = _hoverIndex;
                _armedUntil = Time.unscaledTime + confirmWindow;
                Haptics.TickRight(); // armed — second press deletes
            }
        }

        private static void Consider(float dist, TargetKind kind, int index,
            ref float best, ref TargetKind bestKind, ref int bestIndex)
        {
            if (dist < best)
            {
                best = dist;
                bestKind = kind;
                bestIndex = index;
            }
        }

        private void DeleteTarget(TargetKind kind, int index)
        {
            switch (kind)
            {
                case TargetKind.Room: roomTracer?.RemoveRoom(index); break;
                case TargetKind.DrillLine: drill?.RemoveLine(index); break;
                case TargetKind.Measurement: distanceMeasurer?.RemoveMeasurement(index); break;
                case TargetKind.PingNode: pingTester?.RemoveNode(index); break;
                case TargetKind.Note: spatialNotes?.RemoveNote(index); break;
            }
        }

        // ---- Hover highlight ----

        private void SetHover(TargetKind kind, int index)
        {
            if (kind == _hoverKind && index == _hoverIndex) return;
            ClearHover();
            _hoverKind = kind;
            _hoverIndex = index;
            Highlight(kind, index, true);
        }

        private void ClearHover()
        {
            Highlight(_hoverKind, _hoverIndex, false);
            _hoverKind = TargetKind.None;
            _hoverIndex = -1;
        }

        private void Highlight(TargetKind kind, int index, bool on)
        {
            switch (kind)
            {
                case TargetKind.Room: roomTracer?.SetRoomHighlight(index, on); break;
                case TargetKind.DrillLine: drill?.SetLineHighlight(index, on); break;
                case TargetKind.Measurement: distanceMeasurer?.SetHighlight(index, on); break;
                case TargetKind.PingNode: pingTester?.SetHighlight(index, on); break;
                case TargetKind.Note: spatialNotes?.SetHighlight(index, on); break;
            }
        }

        private void Disarm()
        {
            _armedKind = TargetKind.None;
            _armedIndex = -1;
            if (_prompt != null) _prompt.SetVisible(false);
        }

        private bool TryGetTargetPos(TargetKind kind, int index, out Vector3 pos)
        {
            pos = Vector3.zero;
            switch (kind)
            {
                case TargetKind.Room:
                    if (roomTracer == null || index < 0 || index >= roomTracer.Rooms.Count)
                        return false;
                    var room = roomTracer.Rooms[index];
                    Vector2 c = Vector2.zero;
                    foreach (var cn in room.cornersXZ) c += cn;
                    if (room.cornersXZ.Count > 0) c /= room.cornersXZ.Count;
                    pos = new Vector3(c.x, room.floorY + room.height * 0.5f, c.y);
                    return true;
                case TargetKind.DrillLine:
                    if (drill == null || index < 0 || index >= drill.Lines.Count) return false;
                    drill.GetSegment(index, out Vector3 a, out Vector3 b);
                    pos = (a + b) * 0.5f;
                    return true;
                case TargetKind.Measurement:
                    if (distanceMeasurer == null ||
                        !distanceMeasurer.TryGetSegment(index, out Vector3 ma, out Vector3 mb))
                        return false;
                    pos = (ma + mb) * 0.5f;
                    return true;
                case TargetKind.PingNode:
                    return pingTester != null && pingTester.TryGetNodePos(index, out pos);
                case TargetKind.Note:
                    return spatialNotes != null && spatialNotes.TryGetNotePos(index, out pos);
            }
            return false;
        }

        // ---- Math ----

        /// <summary>Distance between a ray (origin, dir) and a point.</summary>
        private static float RayPointDistance(Vector3 origin, Vector3 dir, Vector3 p)
        {
            float t = Mathf.Max(0f, Vector3.Dot(p - origin, dir));
            return Vector3.Distance(origin + dir * t, p);
        }

        /// <summary>Closest distance between a ray (origin, dir) and a segment
        /// [a, b]; standard two-line closest-point solve, both parameters
        /// clamped to their valid ranges.</summary>
        private static float RaySegmentDistance(Vector3 origin, Vector3 dir,
            Vector3 a, Vector3 b)
        {
            Vector3 seg = b - a;
            Vector3 w = a - origin;
            float aa = Vector3.Dot(dir, dir);
            float bb = Vector3.Dot(dir, seg);
            float cc = Vector3.Dot(seg, seg);
            float dd = Vector3.Dot(dir, w);
            float ee = Vector3.Dot(seg, w);
            float denom = aa * cc - bb * bb;

            float s;
            float t;
            if (denom > 1e-6f)
                s = Mathf.Clamp01((aa * ee - bb * dd) / denom);
            else
                s = 0f;
            t = Mathf.Max(0f, (dd + bb * s) / aa);
            s = cc > 1e-6f
                ? Mathf.Clamp01(Vector3.Dot(dir * t - w, seg) / cc)
                : 0f;

            Vector3 pRay = origin + dir * t;
            Vector3 pSeg = a + seg * s;
            return Vector3.Distance(pRay, pSeg);
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
