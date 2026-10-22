using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace WifiScanner
{
    /// <summary>
    /// Shared builders for the runtime-generated uGUI surfaces (control panel,
    /// tool palette). Moved out of ControlPanel so both panels use the same
    /// look: dark buttons, TMP labels, hand-built sliders, Quest-keyboard
    /// text fields.
    /// </summary>
    public static class UiBuilder
    {
        /// <summary>Default button background colour.</summary>
        public static readonly Color ButtonColor = new Color(0.18f, 0.2f, 0.28f, 1f);
        /// <summary>Highlight colour for active tabs/tools (same blue as sliders).</summary>
        public static readonly Color ActiveColor = new Color(0.3f, 0.65f, 0.95f, 1f);

        /// <summary>
        /// Turns an existing button cell into a compact Android-settings-style
        /// check field: option name on the left, state indicator on the right.
        /// The button keeps its existing click listener.
        /// </summary>
        public static UiCheckButton MakeCheckButton(TextMeshProUGUI label,
            Image background, bool initialValue)
        {
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.fontSize = 19f;
            label.rectTransform.offsetMin = new Vector2(12f, 0f);
            label.rectTransform.offsetMax = new Vector2(-48f, 0f);

            var boxGo = new GameObject("CheckBox", typeof(RectTransform));
            boxGo.transform.SetParent(background.transform, false);
            var boxRect = (RectTransform)boxGo.transform;
            boxRect.anchorMin = boxRect.anchorMax = new Vector2(1f, 0.5f);
            boxRect.pivot = new Vector2(1f, 0.5f);
            boxRect.anchoredPosition = new Vector2(-10f, 0f);
            boxRect.sizeDelta = new Vector2(28f, 28f);
            var box = boxGo.AddComponent<Image>();
            box.raycastTarget = false;

            var check = AddText(boxGo.transform, "✓", 22f,
                FontStyles.Bold, TextAlignmentOptions.Center);
            check.raycastTarget = false;
            Stretch(check.rectTransform);

            var visual = background.gameObject.AddComponent<UiCheckButton>();
            visual.Initialize(background, box, check, initialValue);
            return visual;
        }

        // Small grey section caption with a bit of air above it.
        public static void AddHeader(Transform parent, string title)
        {
            var tmp = AddText(parent, title.ToUpperInvariant(), 17f,
                FontStyles.Bold, TextAlignmentOptions.BottomLeft);
            tmp.color = new Color(1f, 1f, 1f, 0.45f);
            tmp.characterSpacing = 6f;
            tmp.gameObject.AddComponent<LayoutElement>().preferredHeight = 34f;
        }

        public static TextMeshProUGUI AddText(Transform parent, string text,
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

        /// <summary>Gradient strip + weak/mid/strong dBm labels.</summary>
        public static void AddLegend(Transform parent)
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
        /// Generic one-line text field; clicking it with the ray opens the
        /// Quest's system keyboard (TMP_InputField + overlay keyboard).
        /// </summary>
        public static TMP_InputField AddTextField(Transform parent,
            string placeholderText, string initial,
            UnityEngine.Events.UnityAction<string> onValueChanged,
            UnityEngine.Events.UnityAction<string> onEndEdit)
        {
            var go = new GameObject("TextField", typeof(RectTransform));
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

            var placeholder = AddText(viewportGo.transform, placeholderText, 22f,
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
            // Quest: use the system overlay keyboard (together with the
            // oculus.software.overlay_keyboard manifest feature).
            field.shouldHideSoftKeyboard = false;
            field.keyboardType = TouchScreenKeyboardType.Default;
            field.SetTextWithoutNotify(initial ?? "");
            if (onValueChanged != null) field.onValueChanged.AddListener(onValueChanged);
            if (onEndEdit != null) field.onEndEdit.AddListener(onEndEdit);
            return field;
        }

        /// <summary>Adds a row of equally sized buttons; returns their labels.</summary>
        public static TextMeshProUGUI[] AddButtonRow(Transform parent,
            params (string label, UnityEngine.Events.UnityAction onClick)[] buttons)
        {
            return AddButtonRow(parent, out _, buttons);
        }

        /// <summary>
        /// Like <see cref="AddButtonRow(Transform,(string,UnityEngine.Events.UnityAction)[])"/>
        /// but also returns the button background images (for highlight tinting,
        /// e.g. the active tab).
        /// </summary>
        public static TextMeshProUGUI[] AddButtonRow(Transform parent,
            out Image[] backgrounds,
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
            backgrounds = new Image[buttons.Length];
            for (int i = 0; i < buttons.Length; i++)
            {
                var go = new GameObject("Button", typeof(RectTransform));
                go.transform.SetParent(rowGo.transform, false);
                var img = go.AddComponent<Image>();
                img.color = ButtonColor;
                var btn = go.AddComponent<Button>();
                btn.targetGraphic = img;
                btn.onClick.AddListener(buttons[i].onClick);

                labels[i] = AddText(go.transform, buttons[i].label, 22f,
                    FontStyles.Normal, TextAlignmentOptions.Center);
                Stretch(labels[i].rectTransform);
                backgrounds[i] = img;
            }
            return labels;
        }

        public static Slider AddSliderRow(Transform parent, string label, float min,
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
            fillImg.color = ActiveColor;
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
            return slider;
        }

        /// <summary>Settings-style row: option name left, slider right.</summary>
        public static Slider AddSettingSliderRow(Transform parent, string label,
            float min, float max, float value, bool wholeNumbers,
            UnityEngine.Events.UnityAction<float> onChanged)
        {
            var row = new GameObject("SettingSliderRow", typeof(RectTransform));
            row.transform.SetParent(parent, false);
            row.AddComponent<LayoutElement>().preferredHeight = 58f;
            var layout = row.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 14f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            layout.childAlignment = TextAnchor.MiddleCenter;
            var text = AddText(row.transform, label, 19f, FontStyles.Normal,
                TextAlignmentOptions.MidlineLeft);
            var textLe = text.gameObject.AddComponent<LayoutElement>();
            textLe.preferredWidth = 205f;
            textLe.preferredHeight = 58f;
            textLe.flexibleWidth = 1f;
            var sliderHost = new GameObject("SliderHost", typeof(RectTransform));
            sliderHost.transform.SetParent(row.transform, false);
            var sliderLe = sliderHost.AddComponent<LayoutElement>();
            sliderLe.preferredWidth = 210f;
            sliderLe.preferredHeight = 36f;
            sliderLe.flexibleWidth = 1f;
            var sliderLayout = sliderHost.AddComponent<VerticalLayoutGroup>();
            sliderLayout.childControlWidth = true;
            sliderLayout.childControlHeight = true;
            sliderLayout.childForceExpandWidth = true;
            sliderLayout.childForceExpandHeight = false;
            return AddSliderRow(sliderHost.transform, null, min, max, value,
                wholeNumbers, onChanged);
        }

        /// <summary>Settings-style choice: option name left, value right.</summary>
        public static TextMeshProUGUI AddValueButtonRow(Transform parent,
            string label, string value, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject("ValueButton", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            go.AddComponent<LayoutElement>().preferredHeight = 58f;
            var image = go.AddComponent<Image>();
            image.color = ButtonColor;
            var button = go.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(onClick);
            var layout = go.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(12, 12, 0, 0);
            layout.spacing = 12f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            var name = AddText(go.transform, label, 19f, FontStyles.Normal,
                TextAlignmentOptions.MidlineLeft);
            name.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            var selected = AddText(go.transform, value, 19f, FontStyles.Bold,
                TextAlignmentOptions.MidlineRight);
            selected.color = new Color(.48f,.78f,1f,1f);
            selected.gameObject.AddComponent<LayoutElement>().preferredWidth = 190f;
            return selected;
        }

        public static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }

    /// <summary>Visual state for a button that behaves like a checked setting.</summary>
    public sealed class UiCheckButton : MonoBehaviour
    {
        private Image _background;
        private Image _box;
        private TextMeshProUGUI _check;

        public bool IsChecked { get; private set; }

        public void Initialize(Image background, Image box,
            TextMeshProUGUI check, bool initialValue)
        {
            _background = background;
            _box = box;
            _check = check;
            SetChecked(initialValue);
        }

        public void SetChecked(bool value)
        {
            IsChecked = value;
            if (_background != null)
                _background.color = value
                    ? new Color(0.16f, 0.36f, 0.53f, 1f)
                    : UiBuilder.ButtonColor;
            if (_box != null)
                _box.color = value
                    ? UiBuilder.ActiveColor
                    : new Color(0.11f, 0.12f, 0.16f, 1f);
            if (_check != null)
            {
                _check.text = value ? "✓" : "";
                _check.color = Color.white;
            }
        }
    }
}
