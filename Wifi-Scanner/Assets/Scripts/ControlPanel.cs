using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace WifiScanner
{
    /// <summary>
    /// Interactive world-space control panel, operated with the XR ray
    /// interactors of the XRI rig (point at it and pull the trigger).
    ///
    /// Runtime-built like the wrist HUD — no scene wiring needed:
    ///  - Buttons: record pause/resume, export, clear, view mode, room scan,
    ///    room-mesh visibility.
    ///  - Sliders: opacity, RSSI range filter (min/max, dBm).
    ///  - Readout: live stats (points, RSSI now/min/max/avg, duration, SSID)
    ///    plus the colour-scale legend.
    ///
    /// The MENU button on the left controller toggles the panel and re-places
    /// it in front of the user. The controller shortcuts (RecordingControls,
    /// OpacityControls) keep working in parallel.
    /// </summary>
    public class ControlPanel : MonoBehaviour
    {
        [Header("Wired by SceneBootstrap")]
        public SampleCollector collector;
        public PointCloudRenderer cloud;
        public FloorHeatmapRenderer floor;
        public VisualizationModeController modeController;
        public RoomMeshController roomMesh;
        public DynamicOcclusionController occlusion;
        public MinimapController minimap;
        public GridCalibrator gridCalibrator;

        [Header("Placement")]
        [Tooltip("Distance in front of the user when (re)summoned (m).")]
        public float summonDistance = 0.75f;
        [Tooltip("Vertical offset relative to eye height when summoned (m).")]
        public float summonHeightOffset = -0.1f;
        public bool visibleAtStart = true;

        // dBm range covered by the filter sliders.
        private const int FilterRangeMin = -100;
        private const int FilterRangeMax = -30;

        private GameObject _root;
        private TextMeshProUGUI _statsText;
        private TextMeshProUGUI _recLabel;
        private TextMeshProUGUI _modeLabel;
        private TextMeshProUGUI _meshLabel;
        private TextMeshProUGUI _occlusionLabel;
        private TextMeshProUGUI _minimapLabel;
        private TextMeshProUGUI _gridLabel;
        private TextMeshProUGUI _filterLabel;
        private int _filterMin = FilterRangeMin;
        private int _filterMax = FilterRangeMax;
        private InputAction _menuAction;
        private float _statsTimer;
        private bool _placed;

        private void Start()
        {
            EnsureXrUiInputModule();
            BuildPanel();
            _root.SetActive(visibleAtStart);
        }

        private void OnEnable()
        {
            _menuAction = new InputAction("TogglePanel", InputActionType.Button,
                "<XRController>{LeftHand}/menuButton");
            _menuAction.Enable();
        }

        private void OnDisable()
        {
            _menuAction?.Disable();
        }

        private void Update()
        {
            if (_root == null) return;

            if (_menuAction.WasPressedThisFrame())
            {
                bool show = !_root.activeSelf;
                _root.SetActive(show);
                if (show) PlaceInFrontOfUser();
            }

            if (!_root.activeSelf) return;

            // First placement has to wait until the XR camera pose is live.
            if (!_placed) PlaceInFrontOfUser();

            _statsTimer += Time.deltaTime;
            if (_statsTimer >= 0.25f)
            {
                _statsTimer = 0f;
                RefreshStats();
            }
        }

        // ---- Actions ----

        private void OnRecordClicked()
        {
            if (collector == null) return;
            if (collector.IsRecording) collector.StopRecording();
            else collector.StartRecording();
            RefreshStats();
        }

        private void OnExportClicked()
        {
            if (collector == null) return;
            string dir = collector.Export();
            Debug.Log($"[ControlPanel] Export -> {dir}");
        }

        private void OnClearClicked() => collector?.ClearAll();

        private void OnModeClicked() => modeController?.CycleMode();

        private void OnScanClicked() => roomMesh?.RequestSceneCapture();

        private void OnMeshToggleClicked()
        {
            if (roomMesh == null) return;
            roomMesh.CycleMode(); // aus -> Geist -> verdeckt (statischer Occluder)
            _meshLabel.text = $"Mesh: {roomMesh.ModeLabel}";
        }

        private void OnOcclusionClicked()
        {
            if (occlusion == null) return;
            occlusion.ToggleOcclusion(); // dynamische Tiefensensor-Occlusion
            _occlusionLabel.text = occlusion.OcclusionEnabled
                ? "Occlusion: an" : "Occlusion: aus";
        }

        private void OnMinimapClicked()
        {
            if (minimap == null) return;
            minimap.Toggle();
            _minimapLabel.text = minimap.IsVisible ? "Minimap: an" : "Minimap: aus";
        }

        private void OnGridCalibrateClicked() => gridCalibrator?.ToggleArmed();

        private void OnOpacityChanged(float value01)
        {
            cloud?.SetOpacity(value01);
            floor?.SetOpacity(value01);
        }

        private void OnFilterMinChanged(float v)
        {
            _filterMin = Mathf.Min(Mathf.RoundToInt(v), _filterMax - 1);
            ApplyFilter();
        }

        private void OnFilterMaxChanged(float v)
        {
            _filterMax = Mathf.Max(Mathf.RoundToInt(v), _filterMin + 1);
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            // At the slider extremes, open the range fully so no edge value is cut.
            int min = _filterMin <= FilterRangeMin ? -127 : _filterMin;
            int max = _filterMax >= FilterRangeMax ? 0 : _filterMax;
            cloud?.SetRssiFilter(min, max);
            floor?.SetRssiFilter(min, max);
            _filterLabel.text = $"Filter: {_filterMin} … {_filterMax} dBm";
        }

        private void RefreshStats()
        {
            if (_statsText == null || collector == null) return;

            string rec = collector.IsRecording
                ? "<color=#7CFC7C>● REC</color>"
                : "<color=#FF6B6B>■ PAUSE</color>";
            int secs = Mathf.RoundToInt(collector.RecordingSeconds);
            string stats = collector.TryGetRssiStats(out int min, out int max, out float avg)
                ? $"Min {min} / Max {max} / Ø {avg:F0} dBm"
                : "Min – / Max – / Ø – dBm";

            _statsText.text =
                $"{rec}   {secs / 60:D2}:{secs % 60:D2}   " +
                $"Ansicht: {(modeController != null ? modeController.ModeLabel : "-")}\n" +
                $"Punkte: {collector.SampleCount}    " +
                $"RSSI: {collector.LastRssi} dBm\n" +
                $"{stats}\n" +
                $"SSID: {collector.LastSsid}";

            if (_modeLabel != null && modeController != null)
                _modeLabel.text = $"Ansicht: {modeController.ModeLabel}";
            if (_recLabel != null)
                _recLabel.text = collector.IsRecording ? "❚❚ Pause" : "● Aufnahme";
            if (_meshLabel != null && roomMesh != null)
                _meshLabel.text = $"Mesh: {roomMesh.ModeLabel}";
            if (_occlusionLabel != null && occlusion != null)
                _occlusionLabel.text = occlusion.OcclusionEnabled
                    ? "Occlusion: an" : "Occlusion: aus";
            if (_minimapLabel != null && minimap != null)
                _minimapLabel.text = minimap.IsVisible ? "Minimap: an" : "Minimap: aus";
            if (_gridLabel != null && gridCalibrator != null)
                _gridLabel.text = gridCalibrator.IsArmed
                    ? "An Wand halten, beide Grips drücken, loslassen"
                    : (floor != null && Mathf.Abs(floor.gridYawDegrees) > 0.05f
                        ? $"Grid ausrichten ({floor.gridYawDegrees:F0}°)"
                        : "Grid ausrichten");
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

        // ---- UI construction ----

        /// <summary>
        /// The scene's EventSystem needs an XRUIInputModule for the XR ray
        /// interactors to drive uGUI; other input modules are disabled so
        /// EventSystem picks ours.
        /// </summary>
        private static void EnsureXrUiInputModule()
        {
            var es = EventSystem.current;
            if (es == null) es = FindFirstObjectByType<EventSystem>();
            if (es == null)
                es = new GameObject("EventSystem", typeof(EventSystem))
                    .GetComponent<EventSystem>();

            if (es.GetComponent<XRUIInputModule>() != null) return;
            foreach (var module in es.GetComponents<BaseInputModule>())
                module.enabled = false;
            es.gameObject.AddComponent<XRUIInputModule>();
        }

        private void BuildPanel()
        {
            _root = new GameObject("ControlPanel", typeof(RectTransform), typeof(Canvas));
            _root.transform.SetParent(transform, false);
            var canvas = _root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rootRt = (RectTransform)_root.transform;
            rootRt.sizeDelta = new Vector2(480f, 860f);
            _root.transform.localScale = Vector3.one * 0.001f; // 0.48 x 0.86 m
            _root.AddComponent<GraphicRaycaster>();            // editor mouse
            _root.AddComponent<TrackedDeviceGraphicRaycaster>(); // XR rays

            var bg = new GameObject("Background", typeof(RectTransform)).AddComponent<Image>();
            bg.transform.SetParent(_root.transform, false);
            bg.color = new Color(0.05f, 0.05f, 0.08f, 0.85f);
            Stretch(bg.rectTransform);

            var content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(_root.transform, false);
            Stretch((RectTransform)content.transform);
            var layout = content.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(20, 20, 18, 18);
            layout.spacing = 12f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            AddText(content.transform, "WiFi-Scanner", 30f, FontStyles.Bold,
                TextAlignmentOptions.Center);
            _statsText = AddText(content.transform, "…", 22f, FontStyles.Normal,
                TextAlignmentOptions.TopLeft);

            AddLegend(content.transform);

            AddText(content.transform, "Export-Name (optional):", 20f,
                FontStyles.Normal, TextAlignmentOptions.Left);
            AddNameField(content.transform);

            _recLabel = AddButtonRow(content.transform,
                ("❚❚ Pause", OnRecordClicked),
                ("Export", OnExportClicked))[0];
            _modeLabel = AddButtonRow(content.transform,
                ("Ansicht: Beides", OnModeClicked),
                ("Löschen", OnClearClicked))[0];
            _meshLabel = AddButtonRow(content.transform,
                ("Raum scannen", OnScanClicked),
                ("Mesh: aus", OnMeshToggleClicked))[1];
            var toggleLabels = AddButtonRow(content.transform,
                ("Occlusion: aus", OnOcclusionClicked),
                ("Minimap: an", OnMinimapClicked));
            _occlusionLabel = toggleLabels[0];
            _minimapLabel = toggleLabels[1];
            _gridLabel = AddButtonRow(content.transform,
                ("Grid ausrichten", OnGridCalibrateClicked))[0];

            float startOpacity = cloud != null ? cloud.opacity : 0.6f;
            AddSliderRow(content.transform, "Deckkraft", 0f, 1f, startOpacity,
                false, OnOpacityChanged);

            _filterLabel = AddText(content.transform,
                $"Filter: {_filterMin} … {_filterMax} dBm", 22f,
                FontStyles.Normal, TextAlignmentOptions.Left);
            AddSliderRow(content.transform, null, FilterRangeMin, FilterRangeMax,
                _filterMin, true, OnFilterMinChanged);
            AddSliderRow(content.transform, null, FilterRangeMin, FilterRangeMax,
                _filterMax, true, OnFilterMaxChanged);
        }

        private static TextMeshProUGUI AddText(Transform parent, string text,
            float size, FontStyles style, TextAlignmentOptions align)
        {
            var go = new GameObject("Text", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = size;
            tmp.fontStyle = style;
            tmp.alignment = align;
            tmp.color = Color.white;
            return tmp;
        }

        private void AddLegend(Transform parent)
        {
            var strip = new GameObject("Legend", typeof(RectTransform));
            strip.transform.SetParent(parent, false);
            strip.AddComponent<LayoutElement>().preferredHeight = 22f;
            var raw = strip.AddComponent<RawImage>();
            raw.texture = HeatmapLegend.CreateGradientTexture();

            var labels = new GameObject("LegendLabels", typeof(RectTransform));
            labels.transform.SetParent(parent, false);
            labels.AddComponent<LayoutElement>().preferredHeight = 22f;
            var row = labels.AddComponent<HorizontalLayoutGroup>();
            row.childControlWidth = true;
            row.childForceExpandWidth = true;
            int mid = (HeatmapColorMapper.WeakDbm + HeatmapColorMapper.StrongDbm) / 2;
            AddText(labels.transform, $"{HeatmapColorMapper.WeakDbm} dBm", 18f,
                FontStyles.Normal, TextAlignmentOptions.Left);
            AddText(labels.transform, $"{mid} dBm", 18f,
                FontStyles.Normal, TextAlignmentOptions.Center);
            AddText(labels.transform, $"{HeatmapColorMapper.StrongDbm} dBm", 18f,
                FontStyles.Normal, TextAlignmentOptions.Right);
        }

        /// <summary>
        /// Text field for the optional export name (files become
        /// name_Datum_Uhrzeit). Clicking it with the ray opens the Quest's
        /// system keyboard (TMP_InputField + TouchScreenKeyboard).
        /// </summary>
        private void AddNameField(Transform parent)
        {
            var go = new GameObject("ExportNameField", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            go.AddComponent<LayoutElement>().preferredHeight = 48f;
            var bg = go.AddComponent<Image>();
            bg.color = new Color(0.12f, 0.13f, 0.18f, 1f);
            var field = go.AddComponent<TMP_InputField>();
            field.targetGraphic = bg;

            var viewportGo = new GameObject("Text Area", typeof(RectTransform));
            viewportGo.transform.SetParent(go.transform, false);
            viewportGo.AddComponent<RectMask2D>();
            var viewport = (RectTransform)viewportGo.transform;
            Stretch(viewport);
            viewport.offsetMin = new Vector2(12f, 6f);
            viewport.offsetMax = new Vector2(-12f, -7f);

            var placeholder = AddText(viewportGo.transform, "wifi", 22f,
                FontStyles.Italic, TextAlignmentOptions.MidlineLeft);
            placeholder.color = new Color(1f, 1f, 1f, 0.35f);
            Stretch(placeholder.rectTransform);

            var text = AddText(viewportGo.transform, "", 22f,
                FontStyles.Normal, TextAlignmentOptions.MidlineLeft);
            Stretch(text.rectTransform);

            field.textViewport = viewport;
            field.textComponent = text;
            field.placeholder = placeholder;
            field.characterLimit = 40;
            if (collector != null) field.SetTextWithoutNotify(collector.exportName);
            field.onValueChanged.AddListener(value =>
            {
                if (collector != null) collector.exportName = value;
            });
        }

        /// <summary>Adds a row of equally sized buttons; returns their labels.</summary>
        private TextMeshProUGUI[] AddButtonRow(Transform parent,
            params (string label, UnityEngine.Events.UnityAction onClick)[] buttons)
        {
            var rowGo = new GameObject("ButtonRow", typeof(RectTransform));
            rowGo.transform.SetParent(parent, false);
            rowGo.AddComponent<LayoutElement>().preferredHeight = 58f;
            var row = rowGo.AddComponent<HorizontalLayoutGroup>();
            row.spacing = 12f;
            row.childControlWidth = true;
            row.childControlHeight = true;
            row.childForceExpandWidth = true;
            row.childForceExpandHeight = true;

            var labels = new TextMeshProUGUI[buttons.Length];
            for (int i = 0; i < buttons.Length; i++)
            {
                var go = new GameObject("Button", typeof(RectTransform));
                go.transform.SetParent(rowGo.transform, false);
                var img = go.AddComponent<Image>();
                img.color = new Color(0.18f, 0.2f, 0.28f, 1f);
                var btn = go.AddComponent<Button>();
                btn.targetGraphic = img;
                btn.onClick.AddListener(buttons[i].onClick);

                labels[i] = AddText(go.transform, buttons[i].label, 22f,
                    FontStyles.Normal, TextAlignmentOptions.Center);
                Stretch(labels[i].rectTransform);
            }
            return labels;
        }

        private void AddSliderRow(Transform parent, string label, float min,
            float max, float value, bool wholeNumbers,
            UnityEngine.Events.UnityAction<float> onChanged)
        {
            if (!string.IsNullOrEmpty(label))
                AddText(parent, label, 22f, FontStyles.Normal, TextAlignmentOptions.Left);

            var sgo = new GameObject("Slider", typeof(RectTransform));
            sgo.transform.SetParent(parent, false);
            sgo.AddComponent<LayoutElement>().preferredHeight = 36f;
            var slider = sgo.AddComponent<Slider>();

            var bgGo = new GameObject("Background", typeof(RectTransform));
            bgGo.transform.SetParent(sgo.transform, false);
            var bgImg = bgGo.AddComponent<Image>();
            bgImg.color = new Color(0.25f, 0.25f, 0.3f, 1f);
            var bgRt = bgImg.rectTransform;
            bgRt.anchorMin = new Vector2(0f, 0.35f);
            bgRt.anchorMax = new Vector2(1f, 0.65f);
            bgRt.offsetMin = bgRt.offsetMax = Vector2.zero;

            var fillArea = new GameObject("Fill Area", typeof(RectTransform));
            fillArea.transform.SetParent(sgo.transform, false);
            var faRt = (RectTransform)fillArea.transform;
            faRt.anchorMin = new Vector2(0f, 0.35f);
            faRt.anchorMax = new Vector2(1f, 0.65f);
            faRt.offsetMin = new Vector2(6f, 0f);
            faRt.offsetMax = new Vector2(-6f, 0f);

            var fillGo = new GameObject("Fill", typeof(RectTransform));
            fillGo.transform.SetParent(fillArea.transform, false);
            var fillImg = fillGo.AddComponent<Image>();
            fillImg.color = new Color(0.3f, 0.65f, 0.95f, 1f);
            var fillRt = fillImg.rectTransform;
            fillRt.anchorMin = Vector2.zero;
            fillRt.anchorMax = new Vector2(0f, 1f);
            fillRt.sizeDelta = new Vector2(10f, 0f);

            var handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
            handleArea.transform.SetParent(sgo.transform, false);
            var haRt = (RectTransform)handleArea.transform;
            Stretch(haRt);
            haRt.offsetMin = new Vector2(14f, 0f);
            haRt.offsetMax = new Vector2(-14f, 0f);

            var handleGo = new GameObject("Handle", typeof(RectTransform));
            handleGo.transform.SetParent(handleArea.transform, false);
            var handleImg = handleGo.AddComponent<Image>();
            handleImg.color = Color.white;
            var handleRt = handleImg.rectTransform;
            handleRt.anchorMin = new Vector2(0f, 0f);
            handleRt.anchorMax = new Vector2(0f, 1f);
            handleRt.sizeDelta = new Vector2(28f, 0f);

            slider.targetGraphic = handleImg;
            slider.fillRect = fillRt;
            slider.handleRect = handleRt;
            slider.minValue = min;
            slider.maxValue = max;
            slider.wholeNumbers = wholeNumbers;
            slider.SetValueWithoutNotify(value);
            slider.onValueChanged.AddListener(onChanged);
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }
}
