using UnityEngine;
using UnityEngine.InputSystem;

namespace WifiScanner
{
    /// <summary>
    /// Two-controller wall calibration for the heatmap grid: arm it from the
    /// panel ("Grid ausrichten"), hold BOTH controllers against the wall,
    /// press and hold both GRIP buttons — a line is drawn between the
    /// controllers — and release to align the heatmap raster parallel to that
    /// line (FloorHeatmapRenderer.SetGridYaw). Pressing the panel button again
    /// while armed cancels.
    ///
    /// Controller poses are read as tracking-space positions (same Input
    /// System code-binding pattern as the HUD anchors) and transformed through
    /// the Camera Offset into world space. Created and wired by SceneBootstrap.
    /// </summary>
    public class GridCalibrator : MonoBehaviour
    {
        public FloorHeatmapRenderer floor;
        [Tooltip("Material ASSET for the calibration line (cloned + tinted; " +
                 "SceneBootstrap passes the point material).")]
        public Material lineMaterial;
        [Tooltip("How far the line is extended beyond each controller (m).")]
        public float lineExtension = 1.5f;
        [Tooltip("Minimum controller distance for a valid calibration (m).")]
        public float minControllerDistance = 0.15f;

        /// <summary>Armed = waiting for the user to hold both grips.</summary>
        public bool IsArmed { get; private set; }

        private InputAction _leftGrip;
        private InputAction _rightGrip;
        private InputAction _leftPos;
        private InputAction _rightPos;
        private LineRenderer _line;
        private bool _holding;

        private void OnEnable()
        {
            _leftGrip = new InputAction("CalLGrip", InputActionType.Button,
                "<XRController>{LeftHand}/gripPressed");
            _rightGrip = new InputAction("CalRGrip", InputActionType.Button,
                "<XRController>{RightHand}/gripPressed");
            _leftPos = new InputAction("CalLPos", InputActionType.Value,
                "<XRController>{LeftHand}/devicePosition", expectedControlType: "Vector3");
            _rightPos = new InputAction("CalRPos", InputActionType.Value,
                "<XRController>{RightHand}/devicePosition", expectedControlType: "Vector3");
            _leftGrip.Enable();
            _rightGrip.Enable();
            _leftPos.Enable();
            _rightPos.Enable();
        }

        private void OnDisable()
        {
            _leftGrip?.Disable();
            _rightGrip?.Disable();
            _leftPos?.Disable();
            _rightPos?.Disable();
        }

        public void ToggleArmed()
        {
            IsArmed = !IsArmed;
            if (!IsArmed) HideLine();
            string state = IsArmed
                ? "Bereit: beide Controller an die Wand, beide Grips halten, " +
                  "loslassen zum Übernehmen."
                : "Abgebrochen.";
            Debug.Log($"[GridCalibrator] {state}");
        }

        private void Update()
        {
            if (!IsArmed) return;

            bool both = _leftGrip.IsPressed() && _rightGrip.IsPressed();
            if (both)
            {
                _holding = true;
                ShowLine(LeftWorld(), RightWorld());
            }
            else if (_holding)
            {
                // Released -> apply. Use the last drawn positions.
                _holding = false;
                HideLine();
                IsArmed = false;
                Apply(LeftWorld(), RightWorld());
            }
        }

        private void Apply(Vector3 left, Vector3 right)
        {
            Vector3 dir = right - left;
            dir.y = 0f;
            if (dir.magnitude < minControllerDistance)
            {
                Debug.LogWarning("[GridCalibrator] Controller zu nah beieinander — " +
                                 "Kalibrierung verworfen.");
                return;
            }
            if (floor == null) return;
            // Yaw of the wall line vs. world +Z; the grid's axes then run
            // parallel/perpendicular to the wall.
            float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            floor.SetGridYaw(yaw);
        }

        // ---- Line visual ----

        private void ShowLine(Vector3 left, Vector3 right)
        {
            if (_line == null)
            {
                var go = new GameObject("GridCalibrationLine");
                go.transform.SetParent(transform, false);
                _line = go.AddComponent<LineRenderer>();
                _line.positionCount = 2;
                _line.startWidth = _line.endWidth = 0.008f;
                _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _line.receiveShadows = false;
                if (lineMaterial != null)
                {
                    // Clone of a material ASSET — shader variant stays in the
                    // build (same rule as everywhere else in this project).
                    var mat = new Material(lineMaterial);
                    mat.SetColor("_BaseColor", new Color(0f, 0.86f, 1f, 1f));
                    _line.sharedMaterial = mat;
                }
            }
            Vector3 dir = (right - left).normalized;
            _line.SetPosition(0, left - dir * lineExtension);
            _line.SetPosition(1, right + dir * lineExtension);
            _line.gameObject.SetActive(true);
        }

        private void HideLine()
        {
            if (_line != null) _line.gameObject.SetActive(false);
        }

        // Tracking-space controller positions -> world via the Camera Offset.
        private Vector3 LeftWorld() => ToWorld(_leftPos.ReadValue<Vector3>());
        private Vector3 RightWorld() => ToWorld(_rightPos.ReadValue<Vector3>());

        private static Vector3 ToWorld(Vector3 trackingSpacePos)
        {
            var cam = Camera.main;
            if (cam != null && cam.transform.parent != null)
                return cam.transform.parent.TransformPoint(trackingSpacePos);
            return trackingSpacePos;
        }
    }
}
