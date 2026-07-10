using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace WifiScanner
{
    public enum VizMode
    {
        PointCloud,
        FloorHeatmap,
        Both,
        Off,
    }

    /// <summary>
    /// Switches between the view modes point cloud / floor heatmap / both /
    /// off (pure passthrough, nothing drawn).
    /// Recording keeps running in every mode — only the presentation changes.
    ///
    /// Bound to the right thumbstick CLICK (code-defined Input System binding,
    /// same pattern as RecordingControls) and to the "Ansicht" button on the
    /// control panel. Added and wired automatically by SceneBootstrap.
    /// </summary>
    public class VisualizationModeController : MonoBehaviour
    {
        public PointCloudRenderer cloud;
        public FloorHeatmapRenderer floor;
        [Tooltip("Optional: while the coverage prediction is visible it OWNS " +
                 "the floor — the measured heatmap stays hidden in every mode.")]
        public CoverageSimulator coverage;
        public VizMode startMode = VizMode.Both;

        public VizMode Mode { get; private set; }
        public event Action<VizMode> ModeChanged;

        private InputAction _cycleAction;

        public string ModeLabel => Mode switch
        {
            VizMode.PointCloud => "Punktwolke",
            VizMode.FloorHeatmap => "Heatmap",
            VizMode.Off => "Aus",
            _ => "Beides",
        };

        private void Start() => SetMode(startMode);

        private void OnEnable()
        {
            _cycleAction = new InputAction("CycleVizMode", InputActionType.Button,
                "<XRController>{RightHand}/primary2DAxisClick");
            _cycleAction.Enable();
        }

        private void OnDisable()
        {
            _cycleAction?.Disable();
        }

        private void Update()
        {
            if (_cycleAction.WasPressedThisFrame())
                CycleMode();
        }

        public void CycleMode() =>
            SetMode((VizMode)(((int)Mode + 1) % 4));

        /// <summary>Re-applies the current mode's visibility — called by the
        /// CoverageSimulator when the prediction takes/releases the floor.</summary>
        public void ReapplyVisibility() => SetMode(Mode);

        public void SetMode(VizMode mode)
        {
            Mode = mode;
            bool showCloud = mode == VizMode.PointCloud || mode == VizMode.Both;
            bool showFloor = (mode == VizMode.FloorHeatmap || mode == VizMode.Both)
                && (coverage == null || !coverage.IsVisible);
            if (cloud != null) cloud.gameObject.SetActive(showCloud);
            if (floor != null) floor.SetVisible(showFloor);
            ModeChanged?.Invoke(mode);
            Debug.Log($"[VizMode] Ansicht -> {ModeLabel}");
        }
    }
}
