using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace WifiScanner
{
    /// <summary>
    /// First-run tutorial: a floating card walks through the core workflow
    /// (floor calibration → room tracing → recording → export). Steps with an
    /// observable outcome (room drawn, recording running) advance
    /// automatically; the rest via "Weiter". "Überspringen" ends it any time.
    /// Shows once (PlayerPrefs "wifi_onboarded"); "Tutorial starten" on the
    /// panel's System tab re-runs it. Created and wired by SceneBootstrap.
    /// </summary>
    public class OnboardingGuide : MonoBehaviour
    {
        private const string PrefsKey = "wifi_onboarded";

        [Header("Wired by SceneBootstrap")]
        public SampleCollector collector;
        public RoomTracer roomTracer; // optional

        [Tooltip("Distance in front of the user (m).")]
        public float summonDistance = 0.9f;
        [Tooltip("Vertical offset relative to eye height (m).")]
        public float summonHeightOffset = 0.15f;

        private class Step
        {
            public string title;
            public string text;
            public Func<bool> autoAdvance; // null = only the Weiter button
        }

        private Step[] _steps;
        private int _index = -1;
        private GameObject _root;
        private TextMeshProUGUI _title;
        private TextMeshProUGUI _text;
        private TextMeshProUGUI _nextLabel;
        private TextMeshProUGUI _skipLabel;
        private UnityEngine.InputSystem.InputAction _posAction;
        private UnityEngine.InputSystem.InputAction _rotAction;

        private void Start()
        {
            BuildSteps();
            if (PlayerPrefs.GetInt(PrefsKey, 0) == 0)
                StartTutorial();
        }

        /// <summary>(Re)starts the tutorial from the first step.</summary>
        public void StartTutorial()
        {
            if (_root == null) BuildCard();
            _index = -1;
            _root.SetActive(true);
            Advance();
        }

        public bool IsRunning => _root != null && _root.activeSelf;

        private void BuildSteps()
        {
            _steps = new[]
            {
                new Step
                {
                    title = LocalizationManager.Get("tutorial.welcome.title"),
                    text = LocalizationManager.Get("tutorial.welcome.text"),
                },
                new Step
                {
                    title = LocalizationManager.Get("tutorial.floor.title"),
                    text = LocalizationManager.Get("tutorial.floor.text"),
                },
                new Step
                {
                    title = LocalizationManager.Get("tutorial.room.title"),
                    text = LocalizationManager.Get("tutorial.room.text"),
                    autoAdvance = () => roomTracer != null && roomTracer.Rooms.Count > 0,
                },
                new Step
                {
                    title = LocalizationManager.Get("tutorial.record.title"),
                    text = LocalizationManager.Get("tutorial.record.text"),
                    autoAdvance = () => collector != null &&
                        collector.IsRecording && collector.SampleCount > 20,
                },
                new Step
                {
                    title = LocalizationManager.Get("tutorial.view.title"),
                    text = LocalizationManager.Get("tutorial.view.text"),
                },
                new Step
                {
                    title = LocalizationManager.Get("tutorial.export.title"),
                    text = LocalizationManager.Get("tutorial.export.text"),
                },
            };
        }

        private void OnEnable()
        {
            LocalizationManager.LanguageChanged += OnLanguageChanged;
            _posAction = new UnityEngine.InputSystem.InputAction("GuidePos",
                UnityEngine.InputSystem.InputActionType.Value,
                "<XRController>{RightHand}/pointerPosition", expectedControlType: "Vector3");
            _posAction.AddBinding("<XRController>{RightHand}/devicePosition"); // fallback
            _rotAction = new UnityEngine.InputSystem.InputAction("GuideRot",
                UnityEngine.InputSystem.InputActionType.Value,
                "<XRController>{RightHand}/pointerRotation", expectedControlType: "Quaternion");
            _rotAction.AddBinding("<XRController>{RightHand}/deviceRotation"); // fallback
            _posAction.Enable();
            _rotAction.Enable();
        }

        private void Update()
        {
            if (!IsRunning) return;

            // Hover guard: an active tool (e.g. the room tracer during its own
            // tutorial step) must not fire when the user clicks "Weiter".
            UpdateHover();

            if (_index < 0 || _index >= _steps.Length) return;
            var step = _steps[_index];
            if (step.autoAdvance != null && step.autoAdvance())
            {
                Haptics.TickRight();
                Advance();
            }
        }

        private void UpdateHover()
        {
            if (_root == null) return;
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

        private void Advance()
        {
            _index++;
            if (_index >= _steps.Length)
            {
                Finish();
                return;
            }
            RefreshCurrentStep();
            PlaceInFrontOfUser();
        }

        private void OnLanguageChanged()
        {
            BuildSteps();
            RefreshCurrentStep();
            if (_skipLabel != null)
                _skipLabel.text = LocalizationManager.Get("tutorial.skip");
        }

        private void RefreshCurrentStep()
        {
            if (_index < 0 || _index >= _steps.Length || _title == null) return;
            Step step = _steps[_index];
            _title.text = $"{step.title}  <color=#8a93a6>({_index + 1}/{_steps.Length})</color>";
            _text.text = step.text;
            if (_nextLabel != null)
                _nextLabel.text = LocalizationManager.Get(
                    _index == _steps.Length - 1 ? "tutorial.finish" : "tutorial.next");
        }

        private void Finish()
        {
            PlayerPrefs.SetInt(PrefsKey, 1);
            PlayerPrefs.Save();
            if (_root != null) _root.SetActive(false);
            XrUiHover.Clear(this);
        }

        private void OnDisable()
        {
            LocalizationManager.LanguageChanged -= OnLanguageChanged;
            _posAction?.Disable();
            _rotAction?.Disable();
            XrUiHover.Clear(this);
        }

        private void PlaceInFrontOfUser()
        {
            var cam = Camera.main;
            if (cam == null || _root == null) return;
            Vector3 fwd = cam.transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) return;
            fwd.Normalize();
            _root.transform.position = cam.transform.position
                + fwd * summonDistance + Vector3.up * summonHeightOffset;
            _root.transform.rotation = Quaternion.LookRotation(fwd);
        }

        private void BuildCard()
        {
            XrUiHover.EnsureXrUiInputModule();

            _root = new GameObject("OnboardingCard", typeof(RectTransform), typeof(Canvas));
            _root.transform.SetParent(transform, false);
            var canvas = _root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.overrideSorting = true;
            canvas.sortingOrder = 220;
            var rt = (RectTransform)_root.transform;
            rt.sizeDelta = new Vector2(460f, 360f);
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
            layout.padding = new RectOffset(22, 22, 16, 16);
            layout.spacing = 10f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            _title = UiBuilder.AddText(content.transform, "", 25f,
                FontStyles.Bold, TextAlignmentOptions.Left);
            _text = UiBuilder.AddText(content.transform, "", 19f,
                FontStyles.Normal, TextAlignmentOptions.TopLeft);
            _text.gameObject.AddComponent<LayoutElement>().preferredHeight = 200f;
            var buttons = UiBuilder.AddButtonRow(content.transform,
                (LocalizationManager.Get("tutorial.next"),
                    () => { Haptics.TickRight(); Advance(); }),
                (LocalizationManager.Get("tutorial.skip"), Finish));
            _nextLabel = buttons[0];
            _skipLabel = buttons[1];
        }
    }
}
