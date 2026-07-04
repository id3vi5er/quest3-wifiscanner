using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.UI;

namespace WifiScanner
{
    /// <summary>
    /// Wrist minimap on the RIGHT controller: shows the live floor-heatmap
    /// texture as a north-up map (world-aligned, aspect-correct) with an arrow
    /// marker for the headset's position and view direction. Lets you see the
    /// whole recorded heatmap in-headset and spot unmeasured grey gaps while
    /// walking.
    ///
    /// Self-contained like the left-hand HUD: creates its own TrackedPoseDriver
    /// anchor and world-space canvas at runtime. The map texture is the
    /// FloorHeatmapRenderer's live texture (recomputed every ~2 s in every view
    /// mode); the renderer allocates a NEW texture when the area grows, so the
    /// reference and aspect are re-read periodically.
    ///
    /// Toggle: left controller Y button, or the panel button "Minimap".
    /// Created and wired by SceneBootstrap.
    /// </summary>
    public class MinimapController : MonoBehaviour
    {
        [Header("Data source")]
        public FloorHeatmapRenderer floor;

        [Header("Wrist panel placement (relative to the right controller)")]
        public Vector3 panelLocalPosition = new Vector3(0f, 0.04f, -0.06f);
        [Tooltip("90 = lying flat on top of the controller (face up).")]
        public Vector3 panelLocalEuler = new Vector3(90f, 0f, 0f);
        public float panelScale = 0.0004f;

        [Header("Map")]
        [Tooltip("Maximum edge length of the map image in canvas units.")]
        public float maxMapUnits = 230f;
        [Tooltip("Seconds between texture/aspect refreshes (marker moves every frame).")]
        public float refreshInterval = 0.5f;
        public bool visibleAtStart = true;

        [Header("Opacity (independent of the floor/point opacity)")]
        [Range(0f, 1f)]
        [Tooltip("Adjusted in-headset with the RIGHT thumbstick up/down.")]
        public float mapOpacity = 1f;
        public float fadeSpeed = 0.6f;
        public float stickDeadzone = 0.15f;

        public bool IsVisible => _canvasGo != null && _canvasGo.activeSelf;

        private GameObject _canvasGo;
        private RawImage _map;
        private RectTransform _marker;
        private TextMeshProUGUI _placeholder;
        private InputAction _toggleAction;
        private InputAction _opacityStick;
        private float _refreshTimer;
        private bool _hasMap;

        private void Start()
        {
            Transform trackingSpace = ResolveTrackingSpace();

            var anchorGo = new GameObject("MinimapAnchor");
            anchorGo.transform.SetParent(trackingSpace, false);
            var driver = anchorGo.AddComponent<TrackedPoseDriver>();
            driver.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
            driver.positionInput = new InputActionProperty(new InputAction(
                "MinimapPos", InputActionType.Value,
                "<XRController>{RightHand}/devicePosition", expectedControlType: "Vector3"));
            driver.rotationInput = new InputActionProperty(new InputAction(
                "MinimapRot", InputActionType.Value,
                "<XRController>{RightHand}/deviceRotation", expectedControlType: "Quaternion"));

            _canvasGo = new GameObject("MinimapCanvas", typeof(RectTransform), typeof(Canvas));
            var canvasTr = _canvasGo.transform;
            canvasTr.SetParent(anchorGo.transform, false);
            canvasTr.localPosition = panelLocalPosition;
            canvasTr.localRotation = Quaternion.Euler(panelLocalEuler);
            canvasTr.localScale = Vector3.one * panelScale;

            var canvas = _canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rt = (RectTransform)canvasTr;
            rt.sizeDelta = new Vector2(260f, 290f);

            var bg = new GameObject("Panel", typeof(RectTransform)).AddComponent<Image>();
            bg.transform.SetParent(canvasTr, false);
            bg.color = new Color(0f, 0f, 0f, 0.55f);
            Stretch(bg.rectTransform);

            var title = new GameObject("Title", typeof(RectTransform))
                .AddComponent<TextMeshProUGUI>();
            title.transform.SetParent(canvasTr, false);
            title.text = "Minimap";
            title.fontSize = 18f;
            title.color = Color.white;
            title.alignment = TextAlignmentOptions.Center;
            var titleRt = title.rectTransform;
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.pivot = new Vector2(0.5f, 1f);
            titleRt.offsetMin = new Vector2(0f, -26f);
            titleRt.offsetMax = new Vector2(0f, -4f);

            // Map image, centred in the remaining area; resized per refresh so
            // the world aspect ratio is preserved (north-up, +Z = up).
            var mapGo = new GameObject("Map", typeof(RectTransform));
            mapGo.transform.SetParent(canvasTr, false);
            _map = mapGo.AddComponent<RawImage>();
            var mapRt = _map.rectTransform;
            mapRt.anchorMin = mapRt.anchorMax = new Vector2(0.5f, 0.5f);
            mapRt.pivot = new Vector2(0.5f, 0.5f);
            mapRt.anchoredPosition = new Vector2(0f, -13f);
            mapRt.sizeDelta = new Vector2(maxMapUnits, maxMapUnits);
            _map.color = new Color(1f, 1f, 1f, mapOpacity);
            _map.enabled = false;

            _placeholder = new GameObject("Placeholder", typeof(RectTransform))
                .AddComponent<TextMeshProUGUI>();
            _placeholder.transform.SetParent(canvasTr, false);
            _placeholder.text = "(noch keine Daten)";
            _placeholder.fontSize = 16f;
            _placeholder.color = new Color(1f, 1f, 1f, 0.6f);
            _placeholder.alignment = TextAlignmentOptions.Center;
            Stretch(_placeholder.rectTransform);

            // Position/view-direction arrow, child of the map so its
            // anchoredPosition maps directly onto map coordinates.
            var markerGo = new GameObject("Marker", typeof(RectTransform));
            markerGo.transform.SetParent(mapGo.transform, false);
            var markerImg = markerGo.AddComponent<RawImage>();
            markerImg.texture = CreateArrowTexture();
            _marker = markerImg.rectTransform;
            _marker.anchorMin = _marker.anchorMax = new Vector2(0.5f, 0.5f);
            _marker.pivot = new Vector2(0.5f, 0.5f);
            _marker.sizeDelta = new Vector2(16f, 16f);
            markerGo.SetActive(false);

            _canvasGo.SetActive(visibleAtStart);
        }

        private void OnEnable()
        {
            _toggleAction = new InputAction("ToggleMinimap", InputActionType.Button,
                "<XRController>{LeftHand}/secondaryButton");
            _toggleAction.Enable();
            _opacityStick = new InputAction("MinimapOpacity", InputActionType.Value,
                "<XRController>{RightHand}/primary2DAxis", expectedControlType: "Vector2");
            _opacityStick.Enable();
        }

        private void OnDisable()
        {
            _toggleAction?.Disable();
            _opacityStick?.Disable();
        }

        /// <summary>Map image opacity — independent of the floor heatmap and
        /// point cloud (their opacity lives in materials, not in the shared
        /// texture).</summary>
        public void SetOpacity(float value01)
        {
            mapOpacity = Mathf.Clamp01(value01);
            if (_map != null) _map.color = new Color(1f, 1f, 1f, mapOpacity);
        }

        public void Toggle() => SetVisible(!IsVisible);

        public void SetVisible(bool visible)
        {
            if (_canvasGo != null) _canvasGo.SetActive(visible);
        }

        private void Update()
        {
            if (_toggleAction.WasPressedThisFrame())
                Toggle();

            if (_canvasGo == null || !_canvasGo.activeSelf || floor == null) return;

            // Right stick up/down = map opacity (rate-based, like the left
            // stick for the floor/points).
            float y = _opacityStick.ReadValue<Vector2>().y;
            if (Mathf.Abs(y) > stickDeadzone)
                SetOpacity(mapOpacity + y * fadeSpeed * Time.deltaTime);

            _refreshTimer -= Time.deltaTime;
            if (_refreshTimer <= 0f)
            {
                _refreshTimer = refreshInterval;
                RefreshMap();
            }
            if (_hasMap)
                UpdateMarker();
        }

        // Re-reads the texture reference (recreated on area growth) and keeps
        // the image aspect equal to the mapped world area's aspect.
        private void RefreshMap()
        {
            _hasMap = floor.TryGetMapInfo(out _, out float cell, out int w, out int h);
            _map.enabled = _hasMap;
            _placeholder.gameObject.SetActive(!_hasMap);
            _marker.gameObject.SetActive(_hasMap);
            if (!_hasMap) return;

            if (_map.texture != floor.HeatmapTexture)
                _map.texture = floor.HeatmapTexture;

            float worldW = w * cell;
            float worldH = h * cell;
            float scale = maxMapUnits / Mathf.Max(worldW, worldH);
            _map.rectTransform.sizeDelta = new Vector2(worldW * scale, worldH * scale);
        }

        private void UpdateMarker()
        {
            var cam = Camera.main;
            if (cam == null) return;
            if (!floor.TryGetMapInfo(out Vector2 worldMin, out float cell,
                    out int w, out int h))
                return;

            // Map coordinates live in the (possibly wall-calibrated) grid
            // space, so convert the head position the same way.
            Vector2 mapPos = floor.WorldToMapXZ(cam.transform.position);
            // Clamped to the map edge when outside the recorded area.
            float u = Mathf.Clamp01((mapPos.x - worldMin.x) / (w * cell));
            float v = Mathf.Clamp01((mapPos.y - worldMin.y) / (h * cell));
            Vector2 size = _map.rectTransform.sizeDelta;
            _marker.anchoredPosition = new Vector2((u - 0.5f) * size.x,
                                                   (v - 0.5f) * size.y);

            // Map "up" = grid +Z, so the arrow shows the head yaw relative to
            // the calibrated grid.
            Vector3 fwd = cam.transform.forward;
            float yaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg
                        - floor.gridYawDegrees;
            _marker.localEulerAngles = new Vector3(0f, 0f, -yaw);
        }

        /// <summary>Upward-pointing cyan arrow with a dark outline, on a
        /// transparent 32x32 texture.</summary>
        private static Texture2D CreateArrowTexture()
        {
            const int size = 32;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            var clear = new Color32(0, 0, 0, 0);
            var outline = new Color32(10, 30, 40, 255);
            var fill = new Color32(0, 220, 255, 255);

            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                pixels[y * size + x] =
                    InTriangle(x, y, 3f) ? outline :
                    clear;
            }
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                if (InTriangle(x, y, 6.5f))
                    pixels[y * size + x] = fill;
            }
            tex.SetPixels32(pixels);
            tex.Apply(false);
            return tex;

            // Triangle apex top-centre, base bottom; inset shrinks it (outline).
            static bool InTriangle(int x, int y, float inset)
            {
                Vector2 a = new Vector2(16f, 30f - inset);          // apex
                Vector2 b = new Vector2(4f + inset, 2f + inset);    // bottom left
                Vector2 c = new Vector2(28f - inset, 2f + inset);   // bottom right
                Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                float d1 = Cross(p, a, b);
                float d2 = Cross(p, b, c);
                float d3 = Cross(p, c, a);
                bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
                bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;
                return !(hasNeg && hasPos);
            }

            static float Cross(Vector2 p, Vector2 v1, Vector2 v2) =>
                (p.x - v2.x) * (v1.y - v2.y) - (v1.x - v2.x) * (p.y - v2.y);
        }

        private Transform ResolveTrackingSpace()
        {
            var cam = Camera.main;
            if (cam != null && cam.transform.parent != null)
                return cam.transform.parent; // Camera Offset
            Debug.LogWarning("[Minimap] Camera Offset not found — minimap may be " +
                             "positionally offset.");
            return transform;
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
