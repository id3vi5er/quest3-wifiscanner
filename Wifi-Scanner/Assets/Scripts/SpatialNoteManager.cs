using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace WifiScanner
{
    /// <summary>One placed spatial note: world position + type + free text.</summary>
    [Serializable]
    public class SpatialNote
    {
        public float posX, posY, posZ;
        public string type; // NoteType name: VirtualAp / Interference / Text
        public string text;
    }

    /// <summary>
    /// Spatial notes ("Notizen" on the panel). While active, the trigger drops a
    /// permanent 3D anchor right at the controller position (no aiming at walls).
    /// The A button cycles the active note type; a small label at the controller
    /// shows the current one:
    ///   Virtueller AP (cyan) · Störquelle (rot) · Text (weiß)
    /// A "Text" note opens the Quest system keyboard so you can type its label.
    ///
    /// Notes are streamed to the PC viewer ("notes" packet) and exported to
    /// {stem}_notes.json. Created and wired by SceneBootstrap.
    /// </summary>
    public class SpatialNoteManager : MonoBehaviour, IExclusiveTool
    {
        private enum NoteType { VirtualAp, Interference, Text }

        [Header("Wired by SceneBootstrap")]
        [Tooltip("Material ASSET (URP/Unlit) cloned + tinted for note markers.")]
        public Material visualMaterial;
        public NetworkStreamer streamer;

        [Header("Notes")]
        public float markerDiameter = 0.06f;

        public bool IsActive { get; private set; }
        public IReadOnlyList<SpatialNote> Notes => _notes;

        private class NoteVisual
        {
            public SpatialNote note;
            public Transform marker;
            public WorldLabel label;
        }

        private readonly List<SpatialNote> _notes = new List<SpatialNote>();
        private readonly List<NoteVisual> _visuals = new List<NoteVisual>();

        private InputAction _posAction;
        private InputAction _rotAction;
        private InputAction _placeAction;
        private InputAction _cycleAction;

        private GameObject _root;
        private Transform _cursor;
        private Renderer _cursorRenderer;
        private WorldLabel _typeLabel;
        private Material _matAp, _matInterference, _matText, _matRed;
        private Mesh _sphereMesh;
        private NoteType _activeType = NoteType.VirtualAp;
        private float _resendTimer;

        private TouchScreenKeyboard _keyboard;
        private NoteVisual _editing;

        [Serializable]
        private class NotesPacket
        {
            public string type = "notes";
            public List<SpatialNote> notes;
        }

        private void Start()
        {
            var temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _sphereMesh = temp.GetComponent<MeshFilter>().sharedMesh;
            Destroy(temp);

            if (visualMaterial != null)
            {
                _matAp = MakeTinted(new Color(0f, 0.86f, 1f, 1f));
                _matInterference = MakeTinted(new Color(1f, 0.25f, 0.2f, 1f));
                _matText = MakeTinted(new Color(0.95f, 0.95f, 0.95f, 1f));
                _matRed = MakeTinted(new Color(1f, 0.15f, 0.1f, 1f));
            }
            else
            {
                Debug.LogError("[SpatialNoteManager] visualMaterial not assigned — " +
                               "assign the point material in SceneBootstrap.");
            }

            _root = new GameObject("SpatialNoteVisuals");
            _root.transform.SetParent(transform, false);

            _cursor = CreateSphere(_root.transform, markerDiameter, _matAp);
            _cursorRenderer = _cursor.GetComponent<Renderer>();
            _cursor.gameObject.SetActive(false);

            _typeLabel = WorldLabel.Create(_root.transform);
            _typeLabel.SetVisible(false);
        }

        private Material MakeTinted(Color c)
        {
            var m = new Material(visualMaterial); // asset clone keeps its variant
            m.SetColor("_BaseColor", c);
            return m;
        }

        private void OnEnable()
        {
            _posAction = new InputAction("NotePos", InputActionType.Value,
                "<XRController>{RightHand}/pointerPosition", expectedControlType: "Vector3");
            _posAction.AddBinding("<XRController>{RightHand}/devicePosition"); // fallback
            _rotAction = new InputAction("NoteRot", InputActionType.Value,
                "<XRController>{RightHand}/pointerRotation", expectedControlType: "Quaternion");
            _rotAction.AddBinding("<XRController>{RightHand}/deviceRotation"); // fallback
            _placeAction = new InputAction("NotePlace", InputActionType.Button,
                "<XRController>{RightHand}/triggerPressed");
            _cycleAction = new InputAction("NoteCycle", InputActionType.Button,
                "<XRController>{RightHand}/primaryButton");
            _posAction.Enable();
            _rotAction.Enable();
            _placeAction.Enable();
            _cycleAction.Enable();
            ExclusiveTools.Register(this);
        }

        private void OnDisable()
        {
            _posAction?.Disable();
            _rotAction?.Disable();
            _placeAction?.Disable();
            _cycleAction?.Disable();
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
            if (_typeLabel != null) _typeLabel.SetVisible(active);
            Debug.Log($"[SpatialNoteManager] Notizen {(IsActive ? "AKTIV" : "beendet")} " +
                      $"({_notes.Count} Notizen).");
        }

        // ---- Main loop ----

        private void Update()
        {
            // Keep note labels billboarded + poll a running keyboard edit.
            foreach (var v in _visuals)
                if (v.label != null)
                    v.label.SetWorldPosition(v.note != null
                        ? new Vector3(v.note.posX, v.note.posY, v.note.posZ)
                          + Vector3.up * (markerDiameter + 0.03f)
                        : v.marker.position);
            PollKeyboard();

            if (streamer != null && _notes.Count > 0)
            {
                _resendTimer += Time.deltaTime;
                if (_resendTimer >= 3f) SendNotesUpdate();
            }

            if (!IsActive) return;

            GetPointer(out Vector3 handPos, out Vector3 _);
            if (_cursor != null)
            {
                _cursor.position = handPos;
                SetCursorMat(MaterialFor(_activeType));
            }
            if (_typeLabel != null)
            {
                _typeLabel.SetWorldPosition(handPos + Vector3.up * (markerDiameter + 0.04f));
                _typeLabel.SetText($"Typ: {LabelFor(_activeType)}\n(A = wechseln)");
                _typeLabel.SetColor(ColorFor(_activeType));
            }

            // Pointer on the panel = trigger/A belong to the UI, don't act.
            if (ControlPanel.PointerOverPanel) return;

            if (_cycleAction.WasPressedThisFrame())
            {
                _activeType = (NoteType)(((int)_activeType + 1) % 3);
                Haptics.TickRight();
            }

            if (_placeAction.WasPressedThisFrame())
                PlaceNote(handPos, _activeType);
        }

        private void PlaceNote(Vector3 pos, NoteType type)
        {
            var note = new SpatialNote
            {
                posX = pos.x, posY = pos.y, posZ = pos.z,
                type = type.ToString(),
                text = LabelFor(type),
            };
            var v = new NoteVisual { note = note };
            v.marker = CreateSphere(_root.transform, markerDiameter, MaterialFor(type));
            v.marker.position = pos;
            v.label = WorldLabel.Create(_root.transform);
            v.label.SetText(note.text);
            v.label.SetColor(ColorFor(type));
            _notes.Add(note);
            _visuals.Add(v);

            if (type == NoteType.Text)
            {
                // Open the Quest system keyboard to type the note's label.
                _editing = v;
                _keyboard = TouchScreenKeyboard.Open("", TouchScreenKeyboardType.Default);
            }

            Haptics.PlaceRight();
            SendNotesUpdate();
            Debug.Log($"[SpatialNoteManager] Notiz {_notes.Count} ({type}) gesetzt.");
        }

        private void PollKeyboard()
        {
            if (_keyboard == null || _editing == null) return;
            if (_keyboard.status == TouchScreenKeyboard.Status.Visible)
            {
                string live = _keyboard.text;
                if (!string.IsNullOrEmpty(live))
                {
                    _editing.note.text = live;
                    _editing.label.SetText(live);
                }
                return;
            }
            // Done / Canceled / LostFocus — finalise.
            if (_keyboard.status == TouchScreenKeyboard.Status.Done &&
                !string.IsNullOrEmpty(_keyboard.text))
            {
                _editing.note.text = _keyboard.text;
                _editing.label.SetText(_keyboard.text);
                SendNotesUpdate();
            }
            _keyboard = null;
            _editing = null;
        }

        public void ClearNotes()
        {
            foreach (var v in _visuals)
            {
                if (v.marker != null) Destroy(v.marker.gameObject);
                if (v.label != null) Destroy(v.label.gameObject);
            }
            _visuals.Clear();
            _notes.Clear();
            SendNotesUpdate();
        }

        /// <summary>Replaces all notes (session load / re-alignment). Null or
        /// empty just clears.</summary>
        public void LoadNotes(IReadOnlyList<SpatialNote> loaded)
        {
            foreach (var v in _visuals)
            {
                if (v.marker != null) Destroy(v.marker.gameObject);
                if (v.label != null) Destroy(v.label.gameObject);
            }
            _visuals.Clear();
            _notes.Clear();
            if (loaded != null)
                foreach (var n in loaded)
                {
                    NoteType type = TypeForName(n.type);
                    var note = new SpatialNote
                    {
                        posX = n.posX, posY = n.posY, posZ = n.posZ,
                        type = type.ToString(),
                        text = string.IsNullOrEmpty(n.text) ? LabelFor(type) : n.text,
                    };
                    var v = new NoteVisual { note = note };
                    v.marker = CreateSphere(_root.transform, markerDiameter,
                        MaterialFor(type));
                    v.marker.position = new Vector3(note.posX, note.posY, note.posZ);
                    v.label = WorldLabel.Create(_root.transform);
                    v.label.SetText(note.text);
                    v.label.SetColor(ColorFor(type));
                    _notes.Add(note);
                    _visuals.Add(v);
                }
            SendNotesUpdate();
            Debug.Log($"[SpatialNoteManager] {_notes.Count} Notiz(en) geladen.");
        }

        // ---- Delete-mode support ----

        public bool TryGetNotePos(int i, out Vector3 p)
        {
            p = Vector3.zero;
            if (i < 0 || i >= _notes.Count) return false;
            var n = _notes[i];
            p = new Vector3(n.posX, n.posY, n.posZ);
            return true;
        }

        public void SetHighlight(int i, bool on)
        {
            if (i < 0 || i >= _visuals.Count) return;
            var v = _visuals[i];
            if (v.marker == null) return;
            var r = v.marker.GetComponent<Renderer>();
            if (r != null) r.sharedMaterial = on ? _matRed : MaterialForName(v.note.type);
        }

        public void RemoveNote(int i)
        {
            if (i < 0 || i >= _notes.Count) return;
            var snapshot = UndoStack.CloneNotes(_notes);
            UndoStack.Push("Notiz gelöscht", () => LoadNotes(snapshot));
            if (_visuals[i].marker != null) Destroy(_visuals[i].marker.gameObject);
            if (_visuals[i].label != null) Destroy(_visuals[i].label.gameObject);
            _visuals.RemoveAt(i);
            _notes.RemoveAt(i);
            Haptics.HeavyRight();
            SendNotesUpdate();
        }

        private Material MaterialForName(string typeName) =>
            typeName == NoteType.VirtualAp.ToString() ? _matAp
            : typeName == NoteType.Interference.ToString() ? _matInterference
            : _matText;

        // ---- Streaming ----

        private void SendNotesUpdate()
        {
            if (streamer == null) return;
            _resendTimer = 0f;
            var packet = new NotesPacket { notes = _notes };
            streamer.SendRaw(JsonUtility.ToJson(packet));
        }

        // ---- Type helpers ----

        private Material MaterialFor(NoteType t) =>
            t == NoteType.VirtualAp ? _matAp
            : t == NoteType.Interference ? _matInterference
            : _matText;

        private static Color ColorFor(NoteType t) =>
            t == NoteType.VirtualAp ? new Color(0f, 0.86f, 1f)
            : t == NoteType.Interference ? new Color(1f, 0.35f, 0.3f)
            : Color.white;

        private static string LabelFor(NoteType t) =>
            t == NoteType.VirtualAp ? "Virtueller AP"
            : t == NoteType.Interference ? "Störquelle"
            : "Text";

        // Stored type name (from a sidecar JSON) back to the enum; unknown
        // names fall back to Text so foreign files still load.
        private static NoteType TypeForName(string name) =>
            Enum.TryParse(name, out NoteType t) ? t : NoteType.Text;

        // ---- Helpers ----

        private void SetCursorMat(Material m)
        {
            if (_cursorRenderer != null && m != null) _cursorRenderer.sharedMaterial = m;
        }

        private Transform CreateSphere(Transform parent, float diameter, Material mat)
        {
            var go = new GameObject("NoteMarker");
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
