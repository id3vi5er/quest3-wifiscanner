using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace WifiScanner
{
    /// <summary>
    /// Dynamic occlusion via the Quest's real-time environment depth ("Meta
    /// Quest: Occlusion" OpenXR feature, XR_META_environment_depth). Unlike the
    /// static room-mesh occluder this needs NO Space Setup scan and also hides
    /// WiFi visuals behind moving objects (people, doors, furniture).
    ///
    /// How it works: AROcclusionManager + ARShaderOcclusion on the head camera
    /// publish the depth texture, reprojection matrices and the global
    /// XR_HARD_OCCLUSION keyword; the WifiScanner/OccludableUnlit shader (used
    /// by PointMat and FloorHeatMat) then depth-tests every fragment against
    /// the real world. UI canvases (panel, HUD) use their own shaders and stay
    /// visible.
    ///
    /// Toggled from the control panel; off by default. In the Editor (no depth
    /// subsystem) the toggle is a no-op and everything renders normally.
    /// Created and wired by SceneBootstrap.
    /// </summary>
    public class DynamicOcclusionController : MonoBehaviour
    {
        [Tooltip("Depth quality/performance trade-off while occlusion is on.")]
        public EnvironmentDepthMode depthMode = EnvironmentDepthMode.Fastest;
        [Tooltip("Start with dynamic occlusion already enabled.")]
        public bool occlusionOnAtStart = false;

        public bool OcclusionEnabled { get; private set; }

        private AROcclusionManager _manager;
        private ARShaderOcclusion _shaderOcclusion;

        private void Start()
        {
            RequestScenePermission();

            var cam = Camera.main;
            if (cam == null)
            {
                Debug.LogError("[DynOcclusion] No Camera.main — cannot set up " +
                               "environment-depth occlusion.");
                return;
            }

            _manager = cam.GetComponent<AROcclusionManager>();
            if (_manager == null)
                _manager = cam.gameObject.AddComponent<AROcclusionManager>();
            _manager.requestedEnvironmentDepthMode = EnvironmentDepthMode.Disabled;

            _shaderOcclusion = cam.GetComponent<ARShaderOcclusion>();
            if (_shaderOcclusion == null)
                _shaderOcclusion = cam.gameObject.AddComponent<ARShaderOcclusion>();
            _shaderOcclusion.occlusionShaderMode = AROcclusionShaderMode.HardOcclusion;
            _shaderOcclusion.enabled = false; // sets _IsOcclusionOn = 0

            SetOcclusionEnabled(occlusionOnAtStart);
            Debug.Log("[DynOcclusion] Ready (needs the OpenXR 'Meta Quest: " +
                      "Occlusion' feature; device build only).");
        }

        public void ToggleOcclusion() => SetOcclusionEnabled(!OcclusionEnabled);

        /// <summary>
        /// On: depth sensing runs and the occlusion keyword takes effect after
        /// the first depth frame. Off: shader occlusion is disabled AND depth
        /// sensing is stopped to save headset performance.
        /// </summary>
        public void SetOcclusionEnabled(bool enable)
        {
            OcclusionEnabled = enable;
            if (_manager == null || _shaderOcclusion == null) return;

            _manager.requestedEnvironmentDepthMode =
                enable ? depthMode : EnvironmentDepthMode.Disabled;
            _shaderOcclusion.enabled = enable;
            Debug.Log($"[DynOcclusion] Dynamische Occlusion -> {(enable ? "AN" : "AUS")}");
        }

        private static void RequestScenePermission()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // The depth API needs spatial-data permission, independent of
            // whether the room-mesh feature also requested it.
            const string useScene = "com.oculus.permission.USE_SCENE";
            if (!Permission.HasUserAuthorizedPermission(useScene))
                Permission.RequestUserPermission(useScene);
#endif
        }
    }
}
