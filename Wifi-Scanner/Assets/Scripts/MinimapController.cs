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
    /// Self-contained like the left-hand ToolPalette: creates its own
    /// TrackedPoseDriver anchor and world-space canvas at runtime. Below the
    /// map sits a compact live status block (REC/RSSI/points/SSID/storey +
    /// colour legend) — the info that used to live on the left wrist before
    /// the palette took that spot. The map texture is the
    /// FloorHeatmapRenderer's live texture (recomputed every ~2 s in every view
    /// mode); the renderer allocates a NEW texture when the area grows, so the
    /// reference and aspect are re-read periodically.
    ///
    /// The map is always fully opaque. The RIGHT thumbstick up/down zooms the
    /// map in/out (a recent walking trail — the last ~10 m of travel — is drawn
    /// over the map and fades out behind you).
    ///
    /// Toggle: left controller Y button, or the panel button "Minimap".
    /// Created and wired by SceneBootstrap.
    /// </summary>
    public class MinimapController : MonoBehaviour
    {
        [Header("Data sources")]
        public FloorHeatmapRenderer floor;
        [Tooltip("Optional: traced room outlines drawn over the map.")]
        public RoomTracer roomTracer;
        [Tooltip("Optional: while drill planning is active the right stick " +
                 "adjusts the drill depth, so map opacity backs off.")]
        public DrillPlanner drillPlanner;
        [Tooltip("Optional: live status line under the map (REC/RSSI/points/" +
                 "SSID) — took over from the removed left-wrist info HUD.")]
        public SampleCollector collector;
        [Tooltip("Optional: shows the active storey in the status line.")]
        public FloorManager floorManager;
        [Tooltip("Optional: while the coverage prediction is visible, the map " +
                 "shows ITS texture instead of the measured heatmap (same grid " +
                 "space, so all overlays stay georeferenced).")]
        public CoverageSimulator coverage;
        [Tooltip("Optional: draws the estimated AP position(s) on the map " +
                 "(cyan dots, follows the AP-locator visibility + filter).")]
        public ApLocator apLocator;
        [Tooltip("Optional: draws virtual-AP notes on the map (violet dots).")]
        public SpatialNoteManager spatialNotes;

        [Header("Wrist panel placement (relative to the right controller)")]
        public Vector3 panelLocalPosition = new Vector3(0f, 0.04f, -0.06f);
        [Tooltip("90 = lying flat on top of the controller (face up).")]
        public Vector3 panelLocalEuler = new Vector3(90f, 0f, 0f);
        public float panelScale = 0.0004f;

        [Header("Map")]
        [Tooltip("Maximum edge length of the map image in canvas units.")]
        public float maxMapUnits = 230f;
        [Tooltip("false = north-up (map fixed, arrow rotates). true = map " +
                 "rotates with the head, arrow sits centred pointing up.")]
        public bool rotateWithHead = false;
        [Tooltip("Seconds between texture/aspect refreshes (marker moves every frame).")]
        public float refreshInterval = 0.5f;
        public bool visibleAtStart = true;

        [Header("Floor plan overlay")]
        [Tooltip("Draw the traced room outlines (RoomTracer) over the map.")]
        public bool showFloorPlan = true;
        [Tooltip("Pixel resolution of the outline overlay texture.")]
        public int floorPlanResolution = 256;

        [Header("Zoom (RIGHT thumbstick up/down)")]
        [Tooltip("Current zoom: 1 = the whole recorded map fits the window; " +
                 "higher = zoomed in around the player; below 1 = zoomed out " +
                 "(the whole map shrinks with margin for more context).")]
        [Range(0.2f, 12f)]
        public float zoom = 1f;
        [Tooltip("Smallest zoom (below 1 shows the map smaller than the window).")]
        public float minZoom = 0.25f;
        public float maxZoom = 8f;
        [Tooltip("Zoom change per second at full stick deflection (multiplicative).")]
        public float zoomSpeed = 2f;
        public float stickDeadzone = 0.15f;

        [Header("Walking trail")]
        [Tooltip("Draw the recent path walked over the map.")]
        public bool showTrail = true;
        [Tooltip("Maximum length of the trail in metres; older parts drop off.")]
        public float trailMaxLength = 10f;
        [Tooltip("Minimum head movement (m) before a new trail point is added.")]
        public float trailSampleDistance = 0.15f;
        [Tooltip("Pixel resolution of the trail overlay texture.")]
        public int trailResolution = 256;
        public Color trailColor = new Color(1f, 0.55f, 0.1f, 1f);

        public bool IsVisible => _canvasGo != null && _canvasGo.activeSelf;

        private GameObject _canvasGo;
        private RectTransform _rotator;
        private RawImage _map;
        private RawImage _floorPlan;
        private Texture2D _floorPlanTex;
        private Color32[] _floorPlanPixels;
        private RawImage _trail;
        private Texture2D _trailTex;
        private Color32[] _trailPixels;
        private RawImage _apOverlay;
        private Texture2D _apOverlayTex;
        private Color32[] _apOverlayPixels;
        private readonly System.Collections.Generic.List<Vector2> _trailXZ =
            new System.Collections.Generic.List<Vector2>();
        private Vector2 _fittedMapSize;
        private RectTransform _marker;
        private TextMeshProUGUI _placeholder;
        private TextMeshProUGUI _statusText;
        private TextMeshProUGUI _title;
        private InputAction _toggleAction;
        private InputAction _zoomStick;
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
            canvas.overrideSorting = true;
            canvas.sortingOrder = 210;
            var rt = (RectTransform)canvasTr;
            // Extra height below the map for the status line + legend (the
            // info that used to live on the removed left-wrist HUD).
            rt.sizeDelta = new Vector2(260f, 372f);

            var bg = new GameObject("Panel", typeof(RectTransform)).AddComponent<Image>();
            bg.transform.SetParent(canvasTr, false);
            bg.color = new Color(0f, 0f, 0f, 0.90f);
            Stretch(bg.rectTransform);

            _title = new GameObject("Title", typeof(RectTransform))
                .AddComponent<TextMeshProUGUI>();
            _title.transform.SetParent(canvasTr, false);
            _title.text = LocalizationManager.Get("minimap.title");
            _title.fontSize = 18f;
            _title.color = Color.white;
            _title.alignment = TextAlignmentOptions.Center;
            var titleRt = _title.rectTransform;
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.pivot = new Vector2(0.5f, 1f);
            titleRt.offsetMin = new Vector2(0f, -26f);
            titleRt.offsetMax = new Vector2(0f, -4f);

            // Viewport (masked square) > rotator > map image. In north-up
            // mode the map sits centred and the marker moves; in rotate mode
            // the map is shifted so the player lands on the rotator pivot and
            // the rotator spins the map around that point — the mask clips
            // whatever sticks out.
            var viewportGo = new GameObject("MapViewport", typeof(RectTransform));
            viewportGo.transform.SetParent(canvasTr, false);
            viewportGo.AddComponent<RectMask2D>();
            // Anchored to the TOP edge so the map keeps its place under the
            // title while the canvas grows downwards for the status block.
            var viewportRt = (RectTransform)viewportGo.transform;
            viewportRt.anchorMin = viewportRt.anchorMax = new Vector2(0.5f, 1f);
            viewportRt.pivot = new Vector2(0.5f, 1f);
            viewportRt.anchoredPosition = new Vector2(0f, -30f);
            viewportRt.sizeDelta = new Vector2(maxMapUnits, maxMapUnits);

            var rotatorGo = new GameObject("MapRotator", typeof(RectTransform));
            rotatorGo.transform.SetParent(viewportGo.transform, false);
            _rotator = (RectTransform)rotatorGo.transform;
            _rotator.anchorMin = _rotator.anchorMax = new Vector2(0.5f, 0.5f);
            _rotator.pivot = new Vector2(0.5f, 0.5f);
            _rotator.sizeDelta = Vector2.zero;

            var mapGo = new GameObject("Map", typeof(RectTransform));
            mapGo.transform.SetParent(rotatorGo.transform, false);
            _map = mapGo.AddComponent<RawImage>();
            var mapRt = _map.rectTransform;
            mapRt.anchorMin = mapRt.anchorMax = new Vector2(0.5f, 0.5f);
            mapRt.pivot = new Vector2(0.5f, 0.5f);
            mapRt.sizeDelta = new Vector2(maxMapUnits, maxMapUnits);
            _map.color = Color.white; // always fully opaque
            _map.enabled = false;

            // Room-outline overlay, stretched over the map so its pixels map
            // 1:1 onto the map's georeference.
            var planGo = new GameObject("FloorPlan", typeof(RectTransform));
            planGo.transform.SetParent(mapGo.transform, false);
            _floorPlan = planGo.AddComponent<RawImage>();
            _floorPlan.raycastTarget = false;
            Stretch(_floorPlan.rectTransform);
            floorPlanResolution = Mathf.Clamp(floorPlanResolution, 64, 1024);
            _floorPlanTex = new Texture2D(floorPlanResolution, floorPlanResolution,
                TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            _floorPlanPixels = new Color32[floorPlanResolution * floorPlanResolution];
            _floorPlan.texture = _floorPlanTex;
            _floorPlan.enabled = false;

            // Walking-trail overlay, stacked above the floor plan so it draws
            // on top; same 1:1 georeference as the map, so it scales/pans and
            // rotates with the map automatically.
            var trailGo = new GameObject("Trail", typeof(RectTransform));
            trailGo.transform.SetParent(mapGo.transform, false);
            _trail = trailGo.AddComponent<RawImage>();
            _trail.raycastTarget = false;
            Stretch(_trail.rectTransform);
            trailResolution = Mathf.Clamp(trailResolution, 64, 1024);
            _trailTex = new Texture2D(trailResolution, trailResolution,
                TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            _trailPixels = new Color32[trailResolution * trailResolution];
            _trail.texture = _trailTex;
            _trail.enabled = false;

            // AP-marker overlay on top (estimated APs + virtual-AP notes),
            // same 1:1 georeference as floor plan and trail.
            var apGo = new GameObject("ApMarkers", typeof(RectTransform));
            apGo.transform.SetParent(mapGo.transform, false);
            _apOverlay = apGo.AddComponent<RawImage>();
            _apOverlay.raycastTarget = false;
            Stretch(_apOverlay.rectTransform);
            _apOverlayTex = new Texture2D(floorPlanResolution, floorPlanResolution,
                TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            _apOverlayPixels = new Color32[floorPlanResolution * floorPlanResolution];
            _apOverlay.texture = _apOverlayTex;
            _apOverlay.enabled = false;

            _placeholder = new GameObject("Placeholder", typeof(RectTransform))
                .AddComponent<TextMeshProUGUI>();
            _placeholder.transform.SetParent(canvasTr, false);
            _placeholder.text = LocalizationManager.Get("minimap.no_data");
            _placeholder.fontSize = 16f;
            _placeholder.color = new Color(1f, 1f, 1f, 0.6f);
            _placeholder.alignment = TextAlignmentOptions.Center;
            Stretch(_placeholder.rectTransform);

            // Position/view-direction arrow — child of the VIEWPORT (not the
            // map), so it can stay centred while the map rotates underneath.
            var markerGo = new GameObject("Marker", typeof(RectTransform));
            markerGo.transform.SetParent(viewportGo.transform, false);
            var markerImg = markerGo.AddComponent<RawImage>();
            markerImg.texture = CreateArrowTexture();
            _marker = markerImg.rectTransform;
            _marker.anchorMin = _marker.anchorMax = new Vector2(0.5f, 0.5f);
            _marker.pivot = new Vector2(0.5f, 0.5f);
            _marker.sizeDelta = new Vector2(16f, 16f);
            markerGo.SetActive(false);

            // --- Status block below the map (from the removed left-wrist HUD):
            //     2 lines REC/RSSI/points/SSID/storey + colour-scale legend ---
            _statusText = new GameObject("Status", typeof(RectTransform))
                .AddComponent<TextMeshProUGUI>();
            _statusText.transform.SetParent(canvasTr, false);
            _statusText.fontSize = 15f;
            _statusText.color = Color.white;
            _statusText.alignment = TextAlignmentOptions.TopLeft;
            _statusText.textWrappingMode = TextWrappingModes.NoWrap;
            var statusRt = _statusText.rectTransform;
            statusRt.anchorMin = new Vector2(0f, 1f);
            statusRt.anchorMax = new Vector2(1f, 1f);
            statusRt.pivot = new Vector2(0.5f, 1f);
            statusRt.offsetMin = new Vector2(15f, -30f - maxMapUnits - 48f);
            statusRt.offsetMax = new Vector2(-15f, -30f - maxMapUnits - 6f);

            var legendGo = new GameObject("Legend", typeof(RectTransform));
            legendGo.transform.SetParent(canvasTr, false);
            var legend = legendGo.AddComponent<RawImage>();
            legend.texture = HeatmapLegend.CreateGradientTexture();
            var lrt = legend.rectTransform;
            lrt.anchorMin = new Vector2(0f, 0f);
            lrt.anchorMax = new Vector2(1f, 0f);
            lrt.pivot = new Vector2(0.5f, 0f);
            lrt.offsetMin = new Vector2(15f, 24f);
            lrt.offsetMax = new Vector2(-15f, 36f);

            var legendLabel = new GameObject("LegendLabel", typeof(RectTransform))
                .AddComponent<TextMeshProUGUI>();
            legendLabel.transform.SetParent(canvasTr, false);
            legendLabel.text = $"{HeatmapColorMapper.WeakDbm} dBm   →   " +
                               $"{HeatmapColorMapper.StrongDbm} dBm";
            legendLabel.fontSize = 12f;
            legendLabel.color = new Color(1f, 1f, 1f, 0.7f);
            legendLabel.alignment = TextAlignmentOptions.Center;
            var llRt = legendLabel.rectTransform;
            llRt.anchorMin = new Vector2(0f, 0f);
            llRt.anchorMax = new Vector2(1f, 0f);
            llRt.pivot = new Vector2(0.5f, 0f);
            llRt.offsetMin = new Vector2(15f, 6f);
            llRt.offsetMax = new Vector2(-15f, 22f);

            _canvasGo.SetActive(visibleAtStart);
        }

        private void OnEnable()
        {
            LocalizationManager.LanguageChanged += OnLanguageChanged;
            _toggleAction = new InputAction("ToggleMinimap", InputActionType.Button,
                "<XRController>{LeftHand}/secondaryButton");
            _toggleAction.Enable();
            _zoomStick = new InputAction("MinimapZoom", InputActionType.Value,
                "<XRController>{RightHand}/primary2DAxis", expectedControlType: "Vector2");
            _zoomStick.Enable();
        }

        private void OnDisable()
        {
            LocalizationManager.LanguageChanged -= OnLanguageChanged;
            _toggleAction?.Disable();
            _zoomStick?.Disable();
        }

        private void OnLanguageChanged()
        {
            if (_title != null) _title.text = LocalizationManager.Get("minimap.title");
            if (_placeholder != null)
                _placeholder.text = LocalizationManager.Get("minimap.no_data");
            RefreshStatus();
        }

        /// <summary>Map zoom factor (1 = whole map fits, higher = zoomed in
        /// around the player).</summary>
        public void SetZoom(float value)
        {
            zoom = Mathf.Clamp(value, minZoom, maxZoom);
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

            // Record the walking trail even while the map is hidden, so the
            // path is already there when it is toggled back on.
            SampleTrail();

            if (_canvasGo == null || !_canvasGo.activeSelf || floor == null) return;

            // Right stick up/down = map zoom (multiplicative, rate-based) —
            // unless drill planning owns the stick for its depth adjustment.
            if (drillPlanner == null || !drillPlanner.IsActive)
            {
                float y = _zoomStick.ReadValue<Vector2>().y;
                if (Mathf.Abs(y) > stickDeadzone)
                    SetZoom(zoom * (1f + y * zoomSpeed * Time.deltaTime));
            }

            _refreshTimer -= Time.deltaTime;
            if (_refreshTimer <= 0f)
            {
                _refreshTimer = refreshInterval;
                RefreshMap();
                RefreshStatus();
            }
            if (_hasMap)
                UpdateMarker();
        }

        // Live status lines under the map (throttled with the map refresh).
        private void RefreshStatus()
        {
            if (_statusText == null || collector == null) return;
            string rec = LocalizationManager.Get(collector.IsRecording
                ? "ui.stats.recording" : "ui.stats.paused");
            int secs = Mathf.RoundToInt(collector.RecordingSeconds);
            string storey = floorManager != null
                ? $" · {LocalizationManager.DisplayFloorLabel(floorManager.CurrentLabel)}"
                : "";
            string ssid = string.IsNullOrEmpty(collector.LastSsid)
                ? "—" : collector.LastSsid;
            _statusText.text = LocalizationManager.Format("minimap.status",
                rec, secs / 60, secs % 60, collector.LastRssi,
                collector.SampleCount, ssid, storey);
        }

        // Re-reads the texture reference (recreated on area growth) and keeps
        // the image aspect equal to the mapped world area's aspect. While the
        // coverage prediction is showing, the map mirrors IT (same grid space).
        private void RefreshMap()
        {
            Vector2 worldMin;
            float cell;
            int w, h;
            Texture2D sourceTex;
            if (coverage != null && coverage.IsVisible &&
                coverage.TryGetMapInfo(out worldMin, out cell, out w, out h))
            {
                _hasMap = true;
                sourceTex = coverage.CoverageTexture;
            }
            else
            {
                _hasMap = floor.TryGetMapInfo(out worldMin, out cell, out w, out h);
                sourceTex = floor.HeatmapTexture;
            }
            _map.enabled = _hasMap;
            _placeholder.gameObject.SetActive(!_hasMap);
            _marker.gameObject.SetActive(_hasMap);
            if (!_hasMap)
            {
                _floorPlan.enabled = false;
                _trail.enabled = false;
                _apOverlay.enabled = false;
                return;
            }

            if (_map.texture != sourceTex)
                _map.texture = sourceTex;

            float worldW = w * cell;
            float worldH = h * cell;
            float scale = maxMapUnits / Mathf.Max(worldW, worldH);
            _fittedMapSize = new Vector2(worldW * scale, worldH * scale);

            RedrawFloorPlan(worldMin, cell, w, h);
            RedrawTrail(worldMin, cell, w, h);
            RedrawApMarkers(worldMin, cell, w, h);
        }

        // Estimated AP positions (cyan, gated by the AP-locator toggle and its
        // per-AP filter) and virtual-AP notes (violet, always).
        private void RedrawApMarkers(Vector2 worldMin, float cell, int w, int h)
        {
            bool anyEstimates = apLocator != null && apLocator.IsVisible &&
                                apLocator.Estimates.Count > 0;
            bool anyNotes = false;
            if (spatialNotes != null)
                foreach (var note in spatialNotes.Notes)
                    if (note.type == "VirtualAp") { anyNotes = true; break; }

            _apOverlay.enabled = anyEstimates || anyNotes;
            if (!_apOverlay.enabled) return;

            var clear = new Color32(0, 0, 0, 0);
            for (int i = 0; i < _apOverlayPixels.Length; i++)
                _apOverlayPixels[i] = clear;

            int res = floorPlanResolution;
            float mapW = w * cell;
            float mapH = h * cell;

            if (anyEstimates)
            {
                var cyan = new Color32(0, 210, 255, 255);
                var dark = new Color32(10, 40, 55, 255);
                string filter = apLocator.BssidFilter;
                foreach (var est in apLocator.Estimates)
                {
                    if (filter.Length > 0 && est.bssid != filter) continue;
                    Vector2 p = WorldToOverlayPx(new Vector2(est.posX, est.posZ),
                        worldMin, mapW, mapH, res);
                    PlotDisc(p, 5f, dark, res, _apOverlayPixels);  // outline
                    PlotDisc(p, 3.5f, cyan, res, _apOverlayPixels);
                }
            }
            if (anyNotes)
            {
                var violet = new Color32(200, 90, 255, 255);
                var dark = new Color32(50, 15, 65, 255);
                foreach (var note in spatialNotes.Notes)
                {
                    if (note.type != "VirtualAp") continue;
                    Vector2 p = WorldToOverlayPx(new Vector2(note.posX, note.posZ),
                        worldMin, mapW, mapH, res);
                    PlotDisc(p, 5f, dark, res, _apOverlayPixels);
                    PlotDisc(p, 3.5f, violet, res, _apOverlayPixels);
                }
            }

            _apOverlayTex.SetPixels32(_apOverlayPixels);
            _apOverlayTex.Apply(false);
        }

        private static void PlotDisc(Vector2 center, float radius, Color32 c,
            int res, Color32[] pixels)
        {
            int x0 = Mathf.Max(0, Mathf.FloorToInt(center.x - radius));
            int x1 = Mathf.Min(res - 1, Mathf.CeilToInt(center.x + radius));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(center.y - radius));
            int y1 = Mathf.Min(res - 1, Mathf.CeilToInt(center.y + radius));
            float r2 = radius * radius;
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float dx = x - center.x, dy = y - center.y;
                if (dx * dx + dy * dy <= r2)
                    pixels[y * res + x] = c;
            }
        }

        /// <summary>North-up (fixed map, rotating arrow) vs. ego view
        /// (centred arrow, map rotates with the head).</summary>
        public void ToggleRotationMode() => rotateWithHead = !rotateWithHead;

        /// <summary>Show/hide the traced-rooms overlay on the map.</summary>
        public void SetFloorPlanVisible(bool visible)
        {
            showFloorPlan = visible;
            _refreshTimer = 0f; // apply on the next Update
        }

        public void ToggleFloorPlan() => SetFloorPlanVisible(!showFloorPlan);

        // Rasterises the traced room outlines into the overlay texture using
        // the same georeference as the heatmap texture underneath.
        private void RedrawFloorPlan(Vector2 worldMin, float cell, int w, int h)
        {
            bool show = showFloorPlan && roomTracer != null &&
                        roomTracer.Rooms.Count > 0;
            _floorPlan.enabled = show;
            if (!show) return;

            var clear = new Color32(0, 0, 0, 0);
            for (int i = 0; i < _floorPlanPixels.Length; i++)
                _floorPlanPixels[i] = clear;

            // Same light blue as the AR outlines (RoomTracer's line material,
            // _BaseColor (0, 0.86, 1)).
            var lineColor = new Color32(0, 219, 255, 255);
            int res = floorPlanResolution;
            float mapW = w * cell;
            float mapH = h * cell;

            // Stacked storeys: the map shows only the ACTIVE storey's rooms
            // (unless "Alle" is selected) — the floor plan above/below would
            // just overlap confusingly.
            bool filterFloor = floorManager != null && !floorManager.ShowAll &&
                               floorManager.Floors.Count > 0;
            float activeBase = filterFloor ? floorManager.ActiveBaseY : 0f;

            foreach (var room in roomTracer.Rooms)
            {
                if (filterFloor && Mathf.Abs(room.floorY - activeBase) > 1f)
                    continue;
                int n = room.cornersXZ.Count;
                for (int i = 0; i < n; i++)
                {
                    Vector2 pa = WorldToOverlayPx(room.cornersXZ[i], worldMin, mapW, mapH, res);
                    Vector2 pb = WorldToOverlayPx(room.cornersXZ[(i + 1) % n], worldMin, mapW, mapH, res);
                    PlotLine(pa, pb, lineColor, res, _floorPlanPixels);
                }
            }

            _floorPlanTex.SetPixels32(_floorPlanPixels);
            _floorPlanTex.Apply(false);
        }

        // Samples the head position into the trail at most every
        // trailSampleDistance metres, then trims the oldest points so the total
        // path length never exceeds trailMaxLength.
        private void SampleTrail()
        {
            if (!showTrail) return;
            var cam = Camera.main;
            if (cam == null) return;

            Vector3 p = cam.transform.position;
            var xz = new Vector2(p.x, p.z);
            if (_trailXZ.Count == 0 ||
                Vector2.Distance(_trailXZ[_trailXZ.Count - 1], xz) >= trailSampleDistance)
            {
                _trailXZ.Add(xz);
                TrimTrail();
            }
        }

        // Keeps only the newest points whose cumulative length is within
        // trailMaxLength; everything older simply drops off.
        private void TrimTrail()
        {
            float total = 0f;
            int keepFrom = 0;
            for (int i = _trailXZ.Count - 1; i > 0; i--)
            {
                total += Vector2.Distance(_trailXZ[i], _trailXZ[i - 1]);
                if (total > trailMaxLength) { keepFrom = i; break; }
            }
            if (keepFrom > 0) _trailXZ.RemoveRange(0, keepFrom);
        }

        // Rasterises the recent walking trail into its overlay texture using the
        // same georeference as the heatmap. The tail fades to transparent so the
        // direction of travel reads clearly.
        private void RedrawTrail(Vector2 worldMin, float cell, int w, int h)
        {
            bool show = showTrail && _trailXZ.Count > 1;
            _trail.enabled = show;
            if (!show) return;

            var clear = new Color32(0, 0, 0, 0);
            for (int i = 0; i < _trailPixels.Length; i++)
                _trailPixels[i] = clear;

            int res = trailResolution;
            float mapW = w * cell;
            float mapH = h * cell;
            int n = _trailXZ.Count;
            var baseColor = (Color)trailColor;

            for (int i = 0; i < n - 1; i++)
            {
                Vector2 pa = WorldToOverlayPx(_trailXZ[i], worldMin, mapW, mapH, res);
                Vector2 pb = WorldToOverlayPx(_trailXZ[i + 1], worldMin, mapW, mapH, res);
                // 0 at the oldest segment, 1 at the newest (in front of you).
                float t = (i + 1) / (float)(n - 1);
                var c = baseColor;
                c.a *= t;
                PlotLine(pa, pb, c, res, _trailPixels);
            }

            _trailTex.SetPixels32(_trailPixels);
            _trailTex.Apply(false);
        }

        private Vector2 WorldToOverlayPx(Vector2 worldXZ, Vector2 worldMin,
            float mapW, float mapH, int res)
        {
            // Same grid-space conversion as the heatmap/marker.
            Vector2 g = floor.WorldToMapXZ(new Vector3(worldXZ.x, 0f, worldXZ.y));
            float u = (g.x - worldMin.x) / mapW;
            float v = (g.y - worldMin.y) / mapH;
            return new Vector2(u * (res - 1), v * (res - 1));
        }

        // Samples along the segment; pixels outside the map area are skipped
        // (rooms may extend beyond the recorded heatmap bounds).
        private static void PlotLine(Vector2 a, Vector2 b, Color32 color, int res,
            Color32[] target)
        {
            int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) * 2f));
            for (int s = 0; s <= steps; s++)
            {
                Vector2 p = Vector2.Lerp(a, b, s / (float)steps);
                int x = Mathf.RoundToInt(p.x);
                int y = Mathf.RoundToInt(p.y);
                if (x < 0 || x >= res || y < 0 || y >= res) continue;
                target[y * res + x] = color;
                // Thicken to ~2 px for readability.
                if (x + 1 < res) target[y * res + x + 1] = color;
                if (y + 1 < res) target[(y + 1) * res + x] = color;
            }
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
            float u = (mapPos.x - worldMin.x) / (w * cell);
            float v = (mapPos.y - worldMin.y) / (h * cell);

            // Apply the zoom: the displayed map is the fitted size scaled by the
            // zoom factor (below 1 shrinks it inside the window = zoomed out).
            Vector2 size = _fittedMapSize * Mathf.Max(0.05f, zoom);
            _map.rectTransform.sizeDelta = size;
            Vector2 view = new Vector2(maxMapUnits, maxMapUnits);
            Vector2 playerLocal = new Vector2((u - 0.5f) * size.x,
                                              (v - 0.5f) * size.y);

            // Head yaw relative to the calibrated grid (map "up" = grid +Z).
            Vector3 fwd = cam.transform.forward;
            float yaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg
                        - floor.gridYawDegrees;

            if (!rotateWithHead)
            {
                // North-up: pan the map to keep the player centred (once it is
                // larger than the window from zooming), but never past its
                // edges. At zoom 1 the whole map fits, so the offset is 0 and
                // the arrow simply moves like before.
                _rotator.localEulerAngles = Vector3.zero;
                Vector2 offset = new Vector2(
                    ClampPan(-playerLocal.x, size.x, view.x),
                    ClampPan(-playerLocal.y, size.y, view.y));
                _map.rectTransform.anchoredPosition = offset;

                // Arrow at the player's screen position, clamped into the
                // window so it stays visible outside the recorded area.
                Vector2 mk = offset + playerLocal;
                mk.x = Mathf.Clamp(mk.x, -view.x * 0.5f, view.x * 0.5f);
                mk.y = Mathf.Clamp(mk.y, -view.y * 0.5f, view.y * 0.5f);
                _marker.anchoredPosition = mk;
                _marker.localEulerAngles = new Vector3(0f, 0f, -yaw);
            }
            else
            {
                // Ego view: arrow centred pointing up; the map is shifted so
                // the player sits on the rotator pivot, then spun by the head
                // yaw around exactly that point.
                _map.rectTransform.anchoredPosition = -playerLocal;
                _rotator.localEulerAngles = new Vector3(0f, 0f, yaw);
                _marker.anchoredPosition = Vector2.zero;
                _marker.localEulerAngles = Vector3.zero;
            }
        }

        // Offset that keeps the player centred without revealing empty space
        // beyond the map edges. When the map is not larger than the window it
        // stays centred.
        private static float ClampPan(float desired, float mapSize, float viewSize)
        {
            if (mapSize <= viewSize) return 0f;
            float limit = (mapSize - viewSize) * 0.5f;
            return Mathf.Clamp(desired, -limit, limit);
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
