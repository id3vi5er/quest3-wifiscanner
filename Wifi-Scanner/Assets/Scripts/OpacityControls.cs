using UnityEngine;
using UnityEngine.InputSystem;

namespace WifiScanner
{
    /// <summary>
    /// Left thumbstick controls the global opacity of the point cloud, using the
    /// Input System with a code-defined XR binding (no Input Action asset needed).
    ///
    /// The stick re-centres, so this is rate-based rather than absolute:
    ///   push up   = fade points in  (towards 100%)
    ///   push down = fade points out (towards 0%)
    ///   release   = current opacity holds
    ///
    /// Added and wired automatically by SceneBootstrap.
    /// </summary>
    public class OpacityControls : MonoBehaviour
    {
        public PointCloudRenderer cloud;
        [Tooltip("Optional: the floor heatmap fades together with the points.")]
        public FloorHeatmapRenderer floor;

        [Tooltip("Opacity change per second at full stick deflection " +
                 "(0.6 = full 0..100% sweep in ~1.7 s).")]
        public float fadeSpeed = 0.6f;

        [Tooltip("Stick values below this magnitude are ignored (no drift).")]
        public float deadzone = 0.15f;

        private InputAction _stickAction;

        private void OnEnable()
        {
            _stickAction = new InputAction("Opacity", InputActionType.Value,
                "<XRController>{LeftHand}/primary2DAxis", expectedControlType: "Vector2");
            _stickAction.Enable();
        }

        private void OnDisable()
        {
            _stickAction?.Disable();
        }

        private void Update()
        {
            if (cloud == null) return;

            float y = _stickAction.ReadValue<Vector2>().y;
            if (Mathf.Abs(y) < deadzone) return;

            cloud.SetOpacity(cloud.opacity + y * fadeSpeed * Time.deltaTime);
            if (floor != null) floor.SetOpacity(cloud.opacity);
        }
    }
}
