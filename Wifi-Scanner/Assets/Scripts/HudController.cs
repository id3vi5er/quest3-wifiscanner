using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.UI;

namespace WifiScanner
{
    /// <summary>
    /// Wrist-mounted HUD on the LEFT controller. Shows the live measurements
    /// (REC state, point count, RSSI, SSID, opacity) and the controller button
    /// bindings as TextMeshPro text on a small world-space panel — glance at your
    /// left wrist to read it, it never blocks the view.
    ///
    /// Self-contained: at runtime it creates its own tracked anchor (a
    /// TrackedPoseDriver bound to the left controller pose, parented into the XR
    /// tracking space so it sits correctly in the world) and its own canvas.
    /// Created and wired by SceneBootstrap — no manual scene setup needed.
    /// </summary>
    public class HudController : MonoBehaviour
    {
        [Header("Data sources")]
        public SampleCollector collector;
        public PointCloudRenderer cloud;
        public VisualizationModeController modeController;

        [Header("Wrist panel placement (relative to the left controller)")]
        [Tooltip("Offset of the panel from the controller origin, in metres.")]
        public Vector3 panelLocalPosition = new Vector3(0f, 0.04f, -0.06f);
        [Tooltip("Tilt of the panel. 90 = lying flat on top of the controller " +
                 "(face up); smaller values tilt it towards you.")]
        public Vector3 panelLocalEuler = new Vector3(90f, 0f, 0f);
        [Tooltip("Metres per canvas unit (panel size = sizeDelta * this).")]
        public float panelScale = 0.0004f;

        [TextArea]
        [Tooltip("Static button-binding text shown at the bottom of the HUD.")]
        public string bindings =
            "──  Tasten  ──\n" +
            "A   Aufnahme  Pause/Weiter\n" +
            "B   Export\n" +
            "L-Stick ↑/↓   Sichtbarkeit\n" +
            "R-Stick ↑/↓   Minimap-Deckkraft\n" +
            "R-Stick-Klick   Ansicht\n" +
            "Y (links)   Minimap\n" +
            "Menü (links)   Panel";

        private TextMeshProUGUI _text;

        private void Start()
        {
            Transform trackingSpace = ResolveTrackingSpace();

            // --- Tracked anchor that follows the left controller pose ---
            var anchorGo = new GameObject("WristHudAnchor");
            anchorGo.transform.SetParent(trackingSpace, false);
            var driver = anchorGo.AddComponent<TrackedPoseDriver>();
            driver.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
            driver.positionInput = new InputActionProperty(new InputAction(
                "HudPos", InputActionType.Value,
                "<XRController>{LeftHand}/devicePosition", expectedControlType: "Vector3"));
            driver.rotationInput = new InputActionProperty(new InputAction(
                "HudRot", InputActionType.Value,
                "<XRController>{LeftHand}/deviceRotation", expectedControlType: "Quaternion"));

            // --- World-space canvas panel sitting on the wrist ---
            var canvasGo = new GameObject("WristHudCanvas", typeof(RectTransform), typeof(Canvas));
            var canvasTr = canvasGo.transform;
            canvasTr.SetParent(anchorGo.transform, false);
            canvasTr.localPosition = panelLocalPosition;
            canvasTr.localRotation = Quaternion.Euler(panelLocalEuler);
            canvasTr.localScale = Vector3.one * panelScale;

            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rt = (RectTransform)canvasTr;
            rt.sizeDelta = new Vector2(380f, 400f);

            // Semi-transparent background so text stays legible over passthrough.
            var panelGo = new GameObject("Panel", typeof(RectTransform));
            panelGo.transform.SetParent(canvasTr, false);
            var panelImg = panelGo.AddComponent<Image>();
            panelImg.color = new Color(0f, 0f, 0f, 0.55f);
            StretchToParent(panelImg.rectTransform);

            // Text on top.
            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(canvasTr, false);
            _text = textGo.AddComponent<TextMeshProUGUI>();
            _text.fontSize = 22f;
            _text.color = Color.white;
            _text.alignment = TextAlignmentOptions.TopLeft;
            _text.textWrappingMode = TextWrappingModes.NoWrap;
            _text.margin = new Vector4(16f, 14f, 16f, 14f);
            StretchToParent(_text.rectTransform);

            // Compact colour-scale legend strip at the bottom edge (weak red on
            // the left .. strong green on the right, labelled in the text above).
            var legendGo = new GameObject("Legend", typeof(RectTransform));
            legendGo.transform.SetParent(canvasTr, false);
            var legend = legendGo.AddComponent<RawImage>();
            legend.texture = HeatmapLegend.CreateGradientTexture();
            var lrt = legend.rectTransform;
            lrt.anchorMin = new Vector2(0f, 0f);
            lrt.anchorMax = new Vector2(1f, 0f);
            lrt.pivot = new Vector2(0.5f, 0f);
            lrt.offsetMin = new Vector2(16f, 10f);
            lrt.offsetMax = new Vector2(-16f, 24f);
        }

        private void Update()
        {
            if (_text == null || collector == null) return;

            string rec = collector.IsRecording
                ? "<color=#7CFC7C>● REC</color>"
                : "<color=#FF6B6B>■ STOP</color>";
            int opacityPct = cloud != null ? Mathf.RoundToInt(cloud.opacity * 100f) : 0;

            string mode = modeController != null ? modeController.ModeLabel : "-";

            _text.text =
                $"{rec}\n" +
                $"Punkte: {collector.SampleCount}\n" +
                $"RSSI: {collector.LastRssi} dBm\n" +
                $"SSID: {collector.LastSsid}\n" +
                $"Ansicht: {mode}\n" +
                $"Deckkraft: {opacityPct}%\n\n" +
                bindings +
                $"\n\n{HeatmapColorMapper.WeakDbm} dBm  →  {HeatmapColorMapper.StrongDbm} dBm";
        }

        /// <summary>
        /// The controller pose from the TrackedPoseDriver is expressed in the XR
        /// tracking space (the "Camera Offset" that also holds the head camera).
        /// Parenting the anchor there places the wrist HUD correctly in the world.
        /// Falls back to this object's transform if the camera can't be found.
        /// </summary>
        private Transform ResolveTrackingSpace()
        {
            var cam = Camera.main;
            if (cam != null && cam.transform.parent != null)
                return cam.transform.parent; // Camera Offset
            Debug.LogWarning("[HudController] Camera Offset not found — wrist HUD " +
                             "may be positionally offset. Is the XR camera tagged " +
                             "MainCamera under a Camera Offset?");
            return transform;
        }

        private static void StretchToParent(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }
}
