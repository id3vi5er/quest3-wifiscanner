using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace WifiScanner
{
    /// <summary>
    /// Start-up prompt: when an autosave from a previous run exists, a small
    /// world-space card asks "Letzte Session fortsetzen?" — Fortsetzen loads
    /// the autosave via <see cref="SessionLoader.LoadStem"/>, Verwerfen (or a
    /// 60 s timeout) dismisses it. The loaded world coordinates usually need a
    /// "Scan ausrichten" afterwards (the Quest re-anchors on every start); the
    /// card says so. Created and wired by SceneBootstrap; destroys itself.
    /// </summary>
    public class ResumePrompt : MonoBehaviour
    {
        public const string AutosaveStem = "wifi_autosave";

        [Header("Wired by SceneBootstrap")]
        public SessionLoader sessionLoader;

        [Tooltip("Distance in front of the user (m).")]
        public float summonDistance = 0.6f;
        [Tooltip("Vertical offset (m) — above the control panel's spawn spot.")]
        public float summonHeightOffset = 0.25f;
        [Tooltip("Seconds until the prompt dismisses itself.")]
        public float timeoutSeconds = 60f;

        private GameObject _root;
        private TextMeshProUGUI _title;
        private TextMeshProUGUI _info;
        private TextMeshProUGUI _resumeLabel;
        private TextMeshProUGUI _discardLabel;
        private System.DateTime _written;
        private InputAction _posAction;
        private InputAction _rotAction;
        private bool _placed;
        private float _age;

        private void Start()
        {
            if (sessionLoader == null ||
                !DataExporter.TryGetScanInfo(AutosaveStem, out System.DateTime written))
            {
                Destroy(this);
                return;
            }

            XrUiHover.EnsureXrUiInputModule();
            BuildCard(written);
        }

        private void OnEnable()
        {
            LocalizationManager.LanguageChanged += OnLanguageChanged;
            _posAction = new InputAction("ResumePos", InputActionType.Value,
                "<XRController>{RightHand}/pointerPosition", expectedControlType: "Vector3");
            _posAction.AddBinding("<XRController>{RightHand}/devicePosition"); // fallback
            _rotAction = new InputAction("ResumeRot", InputActionType.Value,
                "<XRController>{RightHand}/pointerRotation", expectedControlType: "Quaternion");
            _rotAction.AddBinding("<XRController>{RightHand}/deviceRotation"); // fallback
            _posAction.Enable();
            _rotAction.Enable();
        }

        private void OnDisable()
        {
            LocalizationManager.LanguageChanged -= OnLanguageChanged;
            _posAction?.Disable();
            _rotAction?.Disable();
            XrUiHover.Clear(this);
        }

        private void Update()
        {
            if (_root == null) return;

            // First placement waits for a live XR camera pose (like the panel).
            if (!_placed) PlaceInFrontOfUser();

            _age += Time.deltaTime;
            if (_age > timeoutSeconds)
            {
                Dismiss();
                return;
            }

            // Register hover so no tool trigger fires while clicking the card
            // (no tool is active at start-up, but stay consistent).
            Vector3 pos = _posAction.ReadValue<Vector3>();
            Quaternion rot = _rotAction.ReadValue<Quaternion>();
            var cam = Camera.main;
            Transform space = cam != null ? cam.transform.parent : null;
            Vector3 origin = space != null ? space.TransformPoint(pos) : pos;
            Vector3 dir = space != null
                ? space.rotation * rot * Vector3.forward
                : rot * Vector3.forward;
            XrUiHover.Set(this, XrUiHover.RayOverRect(origin, dir,
                (RectTransform)_root.transform, margin: 0.06f));
        }

        private void OnResumeClicked()
        {
            bool ok = sessionLoader.LoadStem(AutosaveStem);
            if (!ok && _title != null)
            {
                _title.text = "<color=#FF6B6B>" +
                    LocalizationManager.Get("resume.load_failed") + "</color>";
                _age = timeoutSeconds - 2f; // show briefly, then close
                return;
            }
            Haptics.TickRight();
            Dismiss();
        }

        private void Dismiss()
        {
            if (_root != null) Destroy(_root);
            Destroy(this);
        }

        private void PlaceInFrontOfUser()
        {
            var cam = Camera.main;
            if (cam == null) return;
            Vector3 fwd = cam.transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) return;
            fwd.Normalize();
            _root.transform.position = cam.transform.position
                + fwd * summonDistance + Vector3.up * summonHeightOffset;
            _root.transform.rotation = Quaternion.LookRotation(fwd);
            _placed = true;
        }

        private void BuildCard(System.DateTime written)
        {
            _written = written;
            _root = new GameObject("ResumePrompt", typeof(RectTransform), typeof(Canvas));
            _root.transform.SetParent(transform, false);
            var canvas = _root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.overrideSorting = true;
            canvas.sortingOrder = 220;
            var rt = (RectTransform)_root.transform;
            rt.sizeDelta = new Vector2(420f, 220f);
            _root.transform.localScale = Vector3.one * 0.0008f;
            _root.AddComponent<GraphicRaycaster>();
            _root.AddComponent<TrackedDeviceGraphicRaycaster>();

            var bg = new GameObject("Background", typeof(RectTransform)).AddComponent<Image>();
            bg.transform.SetParent(_root.transform, false);
            bg.color = new Color(0.05f, 0.05f, 0.08f, 0.96f);
            UiBuilder.Stretch(bg.rectTransform);

            var content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(_root.transform, false);
            UiBuilder.Stretch((RectTransform)content.transform);
            var layout = content.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(20, 20, 16, 16);
            layout.spacing = 10f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            _title = UiBuilder.AddText(content.transform,
                LocalizationManager.Get("resume.title"), 24f, FontStyles.Bold,
                TextAlignmentOptions.Center);
            _info = UiBuilder.AddText(content.transform,
                ResumeInfo(),
                17f, FontStyles.Normal, TextAlignmentOptions.Center);
            _info.color = new Color(1f, 1f, 1f, 0.7f);
            TextMeshProUGUI[] labels = UiBuilder.AddButtonRow(content.transform,
                (LocalizationManager.Get("resume.continue"), OnResumeClicked),
                (LocalizationManager.Get("resume.discard"), Dismiss));
            _resumeLabel = labels[0];
            _discardLabel = labels[1];
        }

        private string ResumeInfo()
        {
            string date = LocalizationManager.CurrentLanguage == AppLanguage.German
                ? _written.ToString("dd.MM. HH:mm")
                : _written.ToString("MM/dd h:mm tt",
                    System.Globalization.CultureInfo.GetCultureInfo("en-US"));
            return LocalizationManager.Format("resume.info", date);
        }

        private void OnLanguageChanged()
        {
            if (_title != null) _title.text = LocalizationManager.Get("resume.title");
            if (_info != null) _info.text = ResumeInfo();
            if (_resumeLabel != null)
                _resumeLabel.text = LocalizationManager.Get("resume.continue");
            if (_discardLabel != null)
                _discardLabel.text = LocalizationManager.Get("resume.discard");
        }
    }
}
