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

        [Header("Recording")]
        [Tooltip("Off by default: recording is started deliberately (A button " +
                 "or panel), not by app launch.")]
        public bool autoStartRecording = false;
        [Tooltip("Seconds between autosaves to wifi_autosave (0 = disabled).")]
        public float autosaveInterval = 30f;
        [Tooltip("A = pause/resume recording, B = export (right controller).")]
        public bool enableControllerControls = true;
        [Tooltip("Wrist HUD on the left controller: live readings + button bindings.")]
        public bool enableHud = true;

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

        [Header("Minimap")]
        [Tooltip("North-up heatmap minimap on the right wrist with a headset " +
                 "position/direction marker. Toggle: left Y button or panel.")]
        public bool enableMinimap = true;

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

        [Header("Room mesh (3D reconstruction)")]
        [Tooltip("Scene-mesh room reconstruction: ghost overlay + OBJ export. " +
                 "Needs the OpenXR feature 'Meta Quest: Meshing' (Android).")]
        public bool enableRoomMesh = true;
        [Tooltip("URP/Unlit Material asset, transparent (RoomMeshMat).")]
        public Material roomMeshMaterialOverride;
        [Tooltip("URP/Unlit Material asset for occlusion mode: alpha 0 + ZWrite " +
                 "(RoomMeshOccluderMat). Real walls then hide points/heatmap " +
                 "behind them.")]
        public Material roomMeshOccluderMaterialOverride;

        private SampleCollector _collector;
        private float _autosaveTimer;

        private void Awake()
        {
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

            // --- Network streamer ---
            var streamer = gameObject.AddComponent<NetworkStreamer>();
            streamer.enableStreaming = enableStreaming;
            streamer.SetTarget(pcIp, pcPort);

            // --- Sample collector ---
            _collector = gameObject.AddComponent<SampleCollector>();
            _collector.pointCloud = cloud;
            _collector.streamer = streamer;
            _collector.minPointSpacing = minPointSpacing;
            _collector.sampleInterval = sampleInterval;
            _collector.smoothingWindow = smoothingWindow;
            // headAnchor left null -> SampleCollector falls back to Camera.main
            // (the XR Origin head camera).

            // --- Floor heatmap (IDW-interpolated, with gap display) ---
            FloorHeatmapRenderer floor = null;
            if (enableFloorHeatmap)
            {
                var floorGo = new GameObject("FloorHeatmap");
                floorGo.transform.SetParent(transform, false);
                floor = floorGo.AddComponent<FloorHeatmapRenderer>();
                floor.collector = _collector;
                floor.floorMaterial = floorMaterialOverride;
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

            // --- Room mesh: scene-mesh 3D reconstruction + OBJ export ---
            RoomMeshController roomMesh = null;
            if (enableRoomMesh)
            {
                roomMesh = gameObject.AddComponent<RoomMeshController>();
                roomMesh.meshMaterial = roomMeshMaterialOverride;
                roomMesh.occluderMaterial = roomMeshOccluderMaterialOverride;
                _collector.roomMesh = roomMesh; // OBJ rides along with each export
                if (roomMeshMaterialOverride == null)
                    Debug.LogWarning("[SceneBootstrap] roomMeshMaterialOverride not set — " +
                                     "assign Assets/RoomMeshMat.mat or the room mesh " +
                                     "stays invisible.");
            }

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
                _collector.roomTracer = roomTracer;
            }

            // --- Minimap on the right wrist (needs the floor heatmap as source) ---
            MinimapController minimap = null;
            if (enableMinimap && floor != null)
            {
                minimap = gameObject.AddComponent<MinimapController>();
                minimap.floor = floor;
            }

            // --- Dynamic occlusion via real-time environment depth ---
            DynamicOcclusionController occlusion = null;
            if (enableDynamicOcclusion)
            {
                occlusion = gameObject.AddComponent<DynamicOcclusionController>();
                occlusion.occlusionOnAtStart = occlusionOnAtStart;
            }

            // --- Hide controller models (rays/menus stay usable) ---
            if (hideControllerModels)
                gameObject.AddComponent<ControllerModelHider>();

            // --- Controller controls (A = pause/resume, B = export) ---
            if (enableControllerControls)
            {
                var controls = gameObject.AddComponent<RecordingControls>();
                controls.collector = _collector;
                controls.tracer = roomTracer;
            }

            // --- Left thumbstick controls opacity (points + floor heatmap) ---
            var opacityControls = gameObject.AddComponent<OpacityControls>();
            opacityControls.cloud = cloud;
            opacityControls.floor = floor;

            // --- Wrist HUD (left controller): live readings + bindings ---
            if (enableHud)
            {
                var hud = gameObject.AddComponent<HudController>();
                hud.collector = _collector;
                hud.cloud = cloud;
                hud.modeController = vizMode;
            }

            // --- Interactive world-space control panel (menu button toggles) ---
            if (enableControlPanel)
            {
                var panel = gameObject.AddComponent<ControlPanel>();
                panel.collector = _collector;
                panel.cloud = cloud;
                panel.floor = floor;
                panel.modeController = vizMode;
                panel.roomMesh = roomMesh;
                panel.occlusion = occlusion;
                panel.minimap = minimap;
                panel.gridCalibrator = gridCal;
                panel.roomTracer = roomTracer;
            }

            Debug.Log("[SceneBootstrap] WiFi scanner initialised " +
                      $"(stream={enableStreaming} -> {pcIp}:{pcPort}).");
        }

        private void Start()
        {
            if (autoStartRecording)
            {
                _collector.StartRecording();
                Debug.Log("[SceneBootstrap] Recording started.");
            }
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
            if (autosaveInterval <= 0f || _collector == null || _collector.SampleCount == 0)
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
            if (paused) FinalExport();
        }

        private void OnApplicationQuit() => FinalExport();

        private void FinalExport()
        {
            if (_collector != null && _collector.SampleCount > 0)
                _collector.Export(); // timestamped final file
        }
    }
}
