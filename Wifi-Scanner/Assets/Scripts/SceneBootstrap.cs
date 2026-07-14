using UnityEngine;

namespace WifiScanner
{
    /// <summary>
    /// One-stop runtime setup. Put this on a single empty GameObject in the
    /// scene and press Build &amp; Run — it creates the point cloud, the sample
    /// collector and the network streamer, wires them to the head camera, and
    /// (optionally) starts recording. No manual Inspector wiring required.
    ///
    /// Data safety without any UI:
    /// - Autosaves periodically to wifi_autosave.csv/json (overwritten).
    /// - Writes a final timestamped export when the app is paused or quit.
    ///
    /// Note: the point material is created at runtime from the URP Unlit shader.
    /// If that shader gets stripped from the build, add
    /// "Universal Render Pipeline/Unlit" under
    /// Project Settings → Graphics → Always Included Shaders.
    /// </summary>
    public class SceneBootstrap : MonoBehaviour
    {
        [Header("Live stream to PC (optional)")]
        public bool enableStreaming = true;
        public string pcIp = "192.168.1.100";
        public int pcPort = 9999;

        [Header("Web dashboard (browser on the WLAN)")]
        [Tooltip("Serve a live dashboard + scan downloads at http://<quest-ip>:<port>.")]
        public bool enableWebDashboard = true;
        public int webPort = 8080;

        [Header("Recording")]
        [Tooltip("Off by default: recording is started deliberately (A button " +
                 "or panel), not by app launch.")]
        public bool autoStartRecording = false;
        [Tooltip("Seconds between autosaves to wifi_autosave (0 = disabled).")]
        public float autosaveInterval = 30f;
        [Tooltip("Ask at start-up whether to resume the last autosave.")]
        public bool enableResumePrompt = true;
        [Tooltip("First-run tutorial card (re-runnable via System tab).")]
        public bool enableOnboarding = true;
        [Tooltip("Scan ALL visible networks while recording (per-BSSID " +
                 "heatmaps for neighbours too). Toggleable in the panel.")]
        public bool enableMultiApScan = true;
        [Tooltip("A = pause/resume recording, B = export (right controller).")]
        public bool enableControllerControls = true;
        [Tooltip("Clickable tool palette on the left wrist (replaces the old " +
                 "info HUD): select the exclusive tools with the right ray.")]
        public bool enableToolPalette = true;

        [Header("Sampling density")]
        [Tooltip("Minimum distance between points (m). A new point is only drawn " +
                 "when no other point is within this radius. Smaller = denser.")]
        public float minPointSpacing = 0.2f;
        [Tooltip("How often a candidate is evaluated (s). Just a throttle — not a " +
                 "time threshold that spawns points on its own.")]
        public float sampleInterval = 0.2f;
        [Tooltip("RSSI readings averaged per recorded point (smoothing).")]
        public int smoothingWindow = 5;

        [Header("Point cloud")]
        public float pointSize = 0.06f;
        [Range(0f, 1f)]
        [Tooltip("Initial point opacity (~0.6 = half-transparent). Adjust in-headset " +
                 "with the left thumbstick (up = fade in, down = fade out).")]
        public float startOpacity = 0.6f;
        [Tooltip("Max simultaneous mapped points; oldest reused beyond this.")]
        public int maxPoints = 8000;
        [Tooltip("Assign a Material asset (shader = URP/Unlit). Strongly " +
                 "recommended — a runtime-created material gets its shader " +
                 "variant stripped on device and renders magenta.")]
        public Material pointMaterialOverride;

        [Header("Floor heatmap")]
        [Tooltip("IDW-interpolated floor heatmap with gap display for unmeasured cells.")]
        public bool enableFloorHeatmap = true;
        [Tooltip("URP/Unlit Material asset, transparent (FloorHeatMat).")]
        public Material floorMaterialOverride;
        [Tooltip("Heatmap texel size (m).")]
        public float heatmapCellSize = 0.1f;
        [Tooltip("Cells farther than this from any measurement show as grey gaps.")]
        public float maxInterpolationDistance = 1.5f;

        [Header("View mode")]
        [Tooltip("Initial view: point cloud, floor heatmap, or both. Cycle with " +
                 "the right thumbstick click or the panel button.")]
        public VizMode startViewMode = VizMode.Both;

        [Header("Control panel")]
        [Tooltip("Interactive world-space panel (XR ray + trigger). Toggle with " +
                 "the left controller's menu button.")]
        public bool enableControlPanel = true;

        [Header("Room tracing")]
        [Tooltip("Manual floor-plan tracing (panel button 'Raum zeichnen'): " +
                 "place room corners with the right controller, export as " +
                 "real-scale SVG. No Space Setup scan needed.")]
        public bool enableRoomTracer = true;
        [Tooltip("Distance of the tracing cursor in front of the controller (m).")]
        public float roomCursorDistance = 0.3f;
        [Tooltip("Snap distance to the first corner for closing a room (m).")]
        public float roomSnapRadius = 0.3f;
        [Tooltip("Show the floating tracing cursor dot.")]
        public bool roomCursorVisible = true;
        [Tooltip("Diameter of the tracing cursor dot (m).")]
        public float roomCursorDiameter = 0.05f;
        [Tooltip("Cut doors/windows into traced walls (panel 'Türen/Fenster').")]
        public bool enableOpenings = true;

        [Header("Minimap")]
        [Tooltip("North-up heatmap minimap on the right wrist with a headset " +
                 "position/direction marker. Toggle: left Y button or panel.")]
        public bool enableMinimap = true;
        [Tooltip("Draw the traced room outlines (floor plan) over the minimap. " +
                 "Also toggleable at runtime via the panel.")]
        public bool minimapShowFloorPlan = true;
        [Tooltip("false = north-up (arrow rotates), true = map rotates with " +
                 "the head, arrow centred. Also toggleable via the panel.")]
        public bool minimapRotateWithHead = false;

        [Header("Multi-storey")]
        [Tooltip("Tag points per floor, auto-switch on height change, filter the " +
                 "heatmap/point cloud per storey (panel 'Etagen').")]
        public bool enableMultiFloor = true;

        [Header("Analysis tools")]
        [Tooltip("Access-Point locator: floating router icon at the weighted-" +
                 "centroid estimate of the connected AP. Toggle via the panel.")]
        public bool enableApLocator = true;
        [Tooltip("Minimum samples before the AP estimate is shown.")]
        public int apLocatorMinSamples = 15;
        [Tooltip("Latency test: drop nodes and measure round-trip (TCP connect).")]
        public bool enablePingTester = true;
        [Tooltip("Host:port used for the latency measurement.")]
        public string pingHost = "google.com";
        public int pingPort = 443;
        [Tooltip("Spatial notes: place typed 3D anchors at the controller.")]
        public bool enableSpatialNotes = true;
        [Tooltip("Laser distance measurer between two frozen points.")]
        public bool enableDistanceMeasurer = true;

        [Header("Audio feedback")]
        [Tooltip("Geiger-counter ticking: beep rate/pitch follows the live " +
                 "RSSI. Starts off; toggled via the panel ('Audio').")]
        public bool enableGeigerAudio = true;

        [Header("Haptic feedback")]
        [Tooltip("Global strength of ALL controller vibrations (tool clicks, " +
                 "placements, deletions). 0 = off, 1 = default, 2 = double. " +
                 "Live-adjustable in Play Mode.")]
        [Range(0f, 2f)]
        public float hapticStrength = 1f;

        [Header("Controllers")]
        [Tooltip("Hide the rendered controller models; rays, menus and HUD stay.")]
        public bool hideControllerModels = true;

        [Header("Dynamic occlusion (environment depth)")]
        [Tooltip("Real-time depth occlusion: points/heatmap disappear behind " +
                 "real objects, no room scan needed. Needs the OpenXR feature " +
                 "'Meta Quest: Occlusion'. Toggled via the panel.")]
        public bool enableDynamicOcclusion = true;
        [Tooltip("Start with dynamic occlusion already on.")]
        public bool occlusionOnAtStart = false;

        [Header("Dynamic room mesh (environment depth)")]
        [Tooltip("Offer the live Lasertag-style room scanner in the control panel. " +
                 "The scanner itself remains off until explicitly enabled.")]
        public bool enableDynamicRoomMesh = true;
        [Tooltip("Start room reconstruction immediately. Off by default to avoid " +
                 "sensor/GPU cost until the user enables it.")]
        public bool roomScanOnAtStart = false;
        [Tooltip("Initial visibility of an existing/generated room mesh. This does " +
                 "not start or stop scanning.")]
        public bool roomMeshVisibleAtStart = true;

        // NOTE: the OpenXR feature "Meta Quest: Meshing" stays ENABLED in the
        // project settings even though the scene-mesh room scan was removed —
        // it is what injects the USE_SCENE permission into the manifest, which
        // the dynamic occlusion (Depth API) requires.

        private SampleCollector _collector;
        private float _autosaveTimer;
        private const string AutosavePrefsKey = "wifi_autosave_enabled";

        public bool AutosaveEnabled { get; private set; }

        public void ToggleAutosave() => SetAutosaveEnabled(!AutosaveEnabled);

        public void SetAutosaveEnabled(bool enabled)
        {
            AutosaveEnabled = enabled;
            _autosaveTimer = 0f;
            PlayerPrefs.SetInt(AutosavePrefsKey, enabled ? 1 : 0);
            PlayerPrefs.Save();
            Debug.Log($"[SceneBootstrap] Autosave -> {(enabled ? "AN" : "AUS")}");
        }

        // Applies the inspector slider while tuning in Play Mode.
        private void OnValidate()
        {
            Haptics.GlobalStrength = hapticStrength;
        }

        private void Awake()
        {
            LocalizationManager.Initialize();
            Haptics.GlobalStrength = hapticStrength;
            AutosaveEnabled = PlayerPrefs.GetInt(AutosavePrefsKey, 1) != 0;

            // --- Point cloud renderer + runtime instanced material ---
            var cloudGo = new GameObject("WifiCloud");
            cloudGo.transform.SetParent(transform, false);
            var cloud = cloudGo.AddComponent<PointCloudRenderer>();
            cloud.pointSize = pointSize;
            cloud.maxPoints = maxPoints;
            cloud.opacity = startOpacity;
            cloud.pointMaterial = pointMaterialOverride != null
                ? pointMaterialOverride
                : CreatePointMaterial();
            if (pointMaterialOverride != null)
                Debug.Log($"[SceneBootstrap] Point material from asset " +
                          $"'{pointMaterialOverride.name}' (shader " +
                          $"'{pointMaterialOverride.shader.name}').");

            // --- Network streamer (user-set IP from a previous session wins
            //     over the value baked into the build) ---
            var streamer = gameObject.AddComponent<NetworkStreamer>();
            streamer.enableStreaming = enableStreaming;
            streamer.SetTarget(PlayerPrefs.GetString("wifi_pc_ip", pcIp), pcPort);

            // --- Sample collector ---
            _collector = gameObject.AddComponent<SampleCollector>();
            _collector.pointCloud = cloud;
            _collector.streamer = streamer;
            _collector.minPointSpacing = minPointSpacing;
            _collector.sampleInterval = sampleInterval;
            _collector.smoothingWindow = smoothingWindow;
            // headAnchor left null -> SampleCollector falls back to Camera.main
            // (the XR Origin head camera).

            // --- Embedded web dashboard (serves the page + live WS + downloads) ---
            WebDashboardServer webServer = null;
            if (enableWebDashboard)
            {
                webServer = gameObject.AddComponent<WebDashboardServer>();
                webServer.port = webPort;
                webServer.collector = _collector;
                streamer.webServer = webServer; // fan the live stream to browsers
            }

            // --- Multi-storey manager (tags points per floor) ---
            FloorManager floorManager = null;
            if (enableMultiFloor)
            {
                floorManager = gameObject.AddComponent<FloorManager>();
                _collector.floorManager = floorManager;
                // Inserting a storey below shifts floor indices — remap tags.
                floorManager.FloorInserted += _collector.OnFloorInserted;
            }

            // --- Floor heatmap (IDW-interpolated, with gap display) ---
            FloorHeatmapRenderer floor = null;
            if (enableFloorHeatmap)
            {
                var floorGo = new GameObject("FloorHeatmap");
                floorGo.transform.SetParent(transform, false);
                floor = floorGo.AddComponent<FloorHeatmapRenderer>();
                floor.collector = _collector;
                floor.floorManager = floorManager;
                floor.floorMaterial = floorMaterialOverride;
                streamer.floor = floor; // grid yaw for the web dashboard

                floor.cellSize = heatmapCellSize;
                floor.maxInterpolationDistance = maxInterpolationDistance;
                floor.opacity = startOpacity;
                _collector.floorHeatmap = floor; // PNG rides along with each export
                if (floorMaterialOverride == null)
                    Debug.LogWarning("[SceneBootstrap] floorMaterialOverride not set — " +
                                     "assign Assets/FloorHeatMat.mat or the floor " +
                                     "heatmap stays invisible.");
            }

            // --- View mode switching (point cloud / heatmap / both) ---
            var vizMode = gameObject.AddComponent<VisualizationModeController>();
            vizMode.cloud = cloud;
            vizMode.floor = floor;
            vizMode.startMode = enableFloorHeatmap ? startViewMode : VizMode.PointCloud;


            // --- Grid calibration (two controllers against a wall) ---
            GridCalibrator gridCal = null;
            if (floor != null)
            {
                gridCal = gameObject.AddComponent<GridCalibrator>();
                gridCal.floor = floor;
                gridCal.lineMaterial = pointMaterialOverride;
            }

            // --- Manual room tracing (floor-plan corners -> SVG export) ---
            RoomTracer roomTracer = null;
            if (enableRoomTracer)
            {
                roomTracer = gameObject.AddComponent<RoomTracer>();
                roomTracer.floor = floor;
                roomTracer.visualMaterial = pointMaterialOverride;
                roomTracer.cursorDistance = roomCursorDistance;
                roomTracer.snapRadius = roomSnapRadius;
                roomTracer.cursorVisible = roomCursorVisible;
                roomTracer.cursorDiameter = roomCursorDiameter;
                roomTracer.streamer = streamer; // live floor plan on the PC
                roomTracer.floorManager = floorManager; // work plane = active storey
                _collector.roomTracer = roomTracer;
            }

            // --- Drill planning + delete mode (need traced rooms) ---
            DrillPlanner drill = null;
            DeleteController deleter = null;
            if (roomTracer != null)
            {
                drill = gameObject.AddComponent<DrillPlanner>();
                drill.roomTracer = roomTracer;
                drill.visualMaterial = pointMaterialOverride;
                drill.streamer = streamer; // live drill lines on the PC
                deleter = gameObject.AddComponent<DeleteController>();
                deleter.roomTracer = roomTracer;
                deleter.drill = drill;
                _collector.drillPlanner = drill;
            }

            // --- Doors & windows cut into traced walls ---
            OpeningTool openingTool = null;
            if (roomTracer != null && enableOpenings)
            {
                openingTool = gameObject.AddComponent<OpeningTool>();
                openingTool.roomTracer = roomTracer;
                openingTool.visualMaterial = pointMaterialOverride;
            }

            // --- Minimap on the right wrist (needs the floor heatmap as source) ---
            MinimapController minimap = null;
            if (enableMinimap && floor != null)
            {
                minimap = gameObject.AddComponent<MinimapController>();
                minimap.floor = floor;
                minimap.roomTracer = roomTracer;
                minimap.showFloorPlan = minimapShowFloorPlan;
                minimap.rotateWithHead = minimapRotateWithHead;
                minimap.drillPlanner = drill; // right stick belongs to drilling
                minimap.collector = _collector; // status line under the map
                minimap.floorManager = floorManager;
            }

            // --- AP locator (passive: floating router icon at the estimate) ---
            ApLocator apLocator = null;
            if (enableApLocator)
            {
                apLocator = gameObject.AddComponent<ApLocator>();
                apLocator.collector = _collector;
                apLocator.visualMaterial = pointMaterialOverride;
                apLocator.streamer = streamer;
                apLocator.minSamples = apLocatorMinSamples;
                _collector.apLocator = apLocator;
                if (minimap != null) minimap.apLocator = apLocator; // map dots
            }

            // --- Wall attenuation estimate (needs rooms + AP fit) ---
            WallLossEstimator wallLoss = null;
            if (apLocator != null && roomTracer != null)
            {
                wallLoss = gameObject.AddComponent<WallLossEstimator>();
                wallLoss.collector = _collector;
                wallLoss.roomTracer = roomTracer;
                wallLoss.apLocator = apLocator;
                _collector.wallLoss = wallLoss;
            }

            // --- Coverage prediction (virtual APs / real fix over the rooms) ---
            CoverageSimulator coverage = null;
            if (roomTracer != null && floorMaterialOverride != null)
            {
                coverage = gameObject.AddComponent<CoverageSimulator>();
                coverage.roomTracer = roomTracer;
                coverage.apLocator = apLocator;
                coverage.floorManager = floorManager;
                coverage.floorMaterial = floorMaterialOverride;
                coverage.wallLoss = wallLoss;
                coverage.floor = floor;               // shared grid space
                coverage.modeController = vizMode;    // floor hand-over
                vizMode.coverage = coverage;
                if (minimap != null) minimap.coverage = coverage;
                _collector.coverage = coverage;       // _coverage.png on export
            }

            // --- Ping tester (latency nodes) ---
            PingTester pingTester = null;
            if (enablePingTester)
            {
                pingTester = gameObject.AddComponent<PingTester>();
                pingTester.visualMaterial = pointMaterialOverride;
                pingTester.streamer = streamer;
                pingTester.host = pingHost;
                pingTester.port = pingPort;
                _collector.pingTester = pingTester;
            }

            // --- Browser-assisted local throughput nodes (no receiver.py) ---
            ThroughputTester throughputTester = null;
            if (webServer != null)
            {
                throughputTester = gameObject.AddComponent<ThroughputTester>();
                throughputTester.visualMaterial = pointMaterialOverride;
                throughputTester.streamer = streamer;
                throughputTester.floorManager = floorManager;
                throughputTester.collector = _collector;
                webServer.throughput = throughputTester;
                _collector.throughputTester = throughputTester;
                if (floorManager != null)
                    floorManager.FloorInserted += throughputTester.OnFloorInserted;
            }

            // --- Spatial notes (controller-anchored) ---
            SpatialNoteManager spatialNotes = null;
            if (enableSpatialNotes)
            {
                spatialNotes = gameObject.AddComponent<SpatialNoteManager>();
                spatialNotes.visualMaterial = pointMaterialOverride;
                spatialNotes.streamer = streamer;
                _collector.spatialNotes = spatialNotes;
                if (coverage != null)
                    coverage.spatialNotes = spatialNotes; // virtual APs
                if (minimap != null)
                    minimap.spatialNotes = spatialNotes;  // virtual APs on the map
            }

            // --- Distance measurer (laser between two frozen points) ---
            DistanceMeasurer distanceMeasurer = null;
            if (enableDistanceMeasurer)
            {
                distanceMeasurer = gameObject.AddComponent<DistanceMeasurer>();
                distanceMeasurer.visualMaterial = pointMaterialOverride;
                _collector.distanceMeasurer = distanceMeasurer;
            }

            // --- Session loader + scan aligner (load saved scans, re-anchor
            //     them onto the current tracking origin) ---
            var sessionLoader = gameObject.AddComponent<SessionLoader>();
            sessionLoader.collector = _collector;
            sessionLoader.roomTracer = roomTracer;
            sessionLoader.floorManager = floorManager;
            sessionLoader.floor = floor;
            sessionLoader.streamer = streamer;
            sessionLoader.drillPlanner = drill;
            sessionLoader.pingTester = pingTester;
            sessionLoader.throughputTester = throughputTester;
            sessionLoader.spatialNotes = spatialNotes;
            sessionLoader.distanceMeasurer = distanceMeasurer;

            ScanAligner scanAligner = null;
            if (roomTracer != null)
            {
                scanAligner = gameObject.AddComponent<ScanAligner>();
                scanAligner.roomTracer = roomTracer;
                scanAligner.sessionLoader = sessionLoader;
                scanAligner.lineMaterial = pointMaterialOverride;
            }

            // --- "Letzte Session fortsetzen?" card when an autosave exists ---
            if (enableResumePrompt && autosaveInterval > 0f)
            {
                var resume = gameObject.AddComponent<ResumePrompt>();
                resume.sessionLoader = sessionLoader;
            }

            // --- First-run tutorial ---
            OnboardingGuide onboarding = null;
            if (enableOnboarding)
            {
                onboarding = gameObject.AddComponent<OnboardingGuide>();
                onboarding.collector = _collector;
                onboarding.roomTracer = roomTracer;
            }

            // The universal delete mode can now target every element type.
            if (deleter != null)
            {
                deleter.distanceMeasurer = distanceMeasurer;
                deleter.pingTester = pingTester;
                deleter.throughputTester = throughputTester;
                deleter.spatialNotes = spatialNotes;
            }

            // --- Multi-AP scanning (all visible networks -> sample pipeline) ---
            MultiApScanner multiAp = null;
            if (enableMultiApScan)
            {
                multiAp = gameObject.AddComponent<MultiApScanner>();
                multiAp.collector = _collector;
            }

            // --- Roaming tracker (marks BSSID switches in the room) ---
            var roaming = gameObject.AddComponent<RoamingTracker>();
            roaming.collector = _collector;
            roaming.streamer = streamer;
            roaming.visualMaterial = pointMaterialOverride;
            _collector.roaming = roaming;

            // --- Geiger-counter audio feedback (RSSI -> tick rate) ---
            GeigerCounter geiger = null;
            if (enableGeigerAudio)
            {
                geiger = gameObject.AddComponent<GeigerCounter>();
                geiger.collector = _collector;
            }

            // --- Dynamic occlusion via real-time environment depth ---
            DynamicOcclusionController depthCoordinator = null;
            if (enableDynamicOcclusion || enableDynamicRoomMesh)
            {
                depthCoordinator = gameObject.AddComponent<DynamicOcclusionController>();
                depthCoordinator.occlusionOnAtStart =
                    enableDynamicOcclusion && occlusionOnAtStart;
            }
            DynamicOcclusionController occlusion = enableDynamicOcclusion
                ? depthCoordinator : null;

            // --- Optional Lasertag-style TSDF room reconstruction ---
            DynamicRoomMeshController roomMesh = null;
            if (enableDynamicRoomMesh)
            {
                roomMesh = gameObject.AddComponent<DynamicRoomMeshController>();
                roomMesh.scanOnAtStart = roomScanOnAtStart;
                roomMesh.meshVisibleAtStart = roomMeshVisibleAtStart;
                roomMesh.Configure(depthCoordinator, _collector, floor);
                roomMesh.webServer = webServer;
                _collector.dynamicRoomMesh = roomMesh;
            }

            // --- Hide controller models (rays/menus stay usable) ---
            if (hideControllerModels)
                gameObject.AddComponent<ControllerModelHider>();

            // --- Controller controls (A = pause/resume, B = export) ---
            if (enableControllerControls)
            {
                var controls = gameObject.AddComponent<RecordingControls>();
                controls.collector = _collector;
            }

            // --- Left thumbstick controls opacity (points + floor heatmap) ---
            var opacityControls = gameObject.AddComponent<OpacityControls>();
            opacityControls.cloud = cloud;
            opacityControls.floor = floor;
            opacityControls.coverage = coverage;

            // --- Tool palette (left wrist): tap a tool with the right ray ---
            if (enableToolPalette)
            {
                var palette = gameObject.AddComponent<ToolPalette>();
                palette.roomTracer = roomTracer;
                palette.openingTool = openingTool;
                palette.drillPlanner = drill;
                palette.distanceMeasurer = distanceMeasurer;
                palette.spatialNotes = spatialNotes;
                palette.pingTester = pingTester;
                palette.deleteController = deleter;
                palette.scanAligner = scanAligner;
            }

            // --- Interactive world-space control panel (menu button toggles) ---
            if (enableControlPanel)
            {
                var panel = gameObject.AddComponent<ControlPanel>();
                panel.collector = _collector;
                panel.bootstrap = this;
                panel.cloud = cloud;
                panel.floor = floor;
                panel.modeController = vizMode;
                panel.occlusion = occlusion;
                panel.dynamicRoomMesh = roomMesh;
                panel.streamer = streamer;
                panel.streamPort = pcPort;
                panel.minimap = minimap;
                panel.gridCalibrator = gridCal;
                panel.floorManager = floorManager;
                panel.webServer = webServer;
                panel.roomTracer = roomTracer;
                panel.apLocator = apLocator;
                panel.pingTester = pingTester;
                panel.spatialNotes = spatialNotes;
                panel.distanceMeasurer = distanceMeasurer;
                panel.geiger = geiger;
                panel.sessionLoader = sessionLoader;
                panel.wallLoss = wallLoss;
                panel.coverage = coverage;
                panel.onboarding = onboarding;
                panel.roaming = roaming;
                panel.multiAp = multiAp;
                panel.throughputTester = throughputTester;
            }

            Debug.Log("[SceneBootstrap] WiFi scanner initialised " +
                      $"(stream={enableStreaming} -> {pcIp}:{pcPort}).");
        }

        private void Start()
        {
            ApplyAndroidTaskLabel();
            if (autoStartRecording)
            {
                _collector.StartRecording();
                Debug.Log("[SceneBootstrap] Recording started.");
            }
        }

        private static void ApplyAndroidTaskLabel()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var unityPlayer = new AndroidJavaClass(
                    "com.unity3d.player.UnityPlayer");
                using var activity = unityPlayer.GetStatic<AndroidJavaObject>(
                    "currentActivity");
                using var description = new AndroidJavaObject(
                    "android.app.ActivityManager$TaskDescription",
                    Application.productName);
                activity.Call("setTaskDescription", description);
                Debug.Log($"[SceneBootstrap] Android task label -> " +
                          $"'{Application.productName}'.");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[SceneBootstrap] Task-Label konnte nicht " +
                                 $"gesetzt werden: {e.Message}");
            }
#endif
        }

        private static Material CreatePointMaterial()
        {
            // Unity's own URP Unlit shader — compiles on-device and handles XR
            // stereo natively (a hand-written shader's stereo variant gets
            // stripped on Android and renders magenta). Add "Universal Render
            // Pipeline/Unlit" to Project Settings → Graphics → Always Included
            // Shaders so Shader.Find resolves it in the build.
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                Debug.LogError("[SceneBootstrap] URP Unlit shader not found — add it " +
                               "to Always Included Shaders (Project Settings → Graphics).");
                return null;
            }
            Debug.Log($"[SceneBootstrap] Point material using shader '{shader.name}'.");
            return new Material(shader);
        }

        private void Update()
        {
            if (!AutosaveEnabled || autosaveInterval <= 0f || _collector == null ||
                _collector.SampleCount == 0)
                return;

            _autosaveTimer += Time.deltaTime;
            if (_autosaveTimer >= autosaveInterval)
            {
                _autosaveTimer = 0f;
                _collector.Export("wifi_autosave");
            }
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                // Horizon OS displays ActivityManager.TaskDescription in the
                // minimized/running-app card. Unity's GameActivity leaves its
                // label null even with correct manifest labels.
                ApplyAndroidTaskLabel();
                FinalExport();
            }
        }

        private void OnApplicationQuit() => FinalExport();

        private void FinalExport()
        {
            if (_collector != null && _collector.SampleCount > 0)
                _collector.Export(); // timestamped final file
        }
    }
}
