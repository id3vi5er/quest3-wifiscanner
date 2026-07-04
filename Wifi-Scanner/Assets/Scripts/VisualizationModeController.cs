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
    }

    /// <summary>
    /// Switches between the view modes point cloud / floor heatmap / both.
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
        public VizMode startMode = VizMode.Both;

        public VizMode Mode { get; private set; }
        public event Action<VizMode> ModeChanged;

        private InputAction _cycleAction;

        public string ModeLabel => Mode switch
        {
            VizMode.PointCloud => "Punktwolke",
            VizMode.FloorHeatmap => "Heatmap",
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
            SetMode((VizMode)(((int)Mode + 1) % 3));

        public void SetMode(VizMode mode)
        {
            Mode = mode;
            bool showCloud = mode != VizMode.FloorHeatmap;
            bool showFloor = mode != VizMode.PointCloud;
            if (cloud != null) cloud.gameObject.SetActive(showCloud);
            if (floor != null) floor.SetVisible(showFloor);
            ModeChanged?.Invoke(mode);
            Debug.Log($"[VizMode] Ansicht -> {ModeLabel}");
        }
    }
}
