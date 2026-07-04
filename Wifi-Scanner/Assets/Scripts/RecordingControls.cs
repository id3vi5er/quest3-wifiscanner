using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;

namespace WifiScanner
{
    /// <summary>
    /// Controller buttons to pause/resume recording, export, and reset, using the
    /// Input System with code-defined XR bindings (no Input Action asset needed).
    ///
    /// Default (main hand = right Touch controller):
    ///   A (primary)        = toggle recording (pause / resume)
    ///   B (secondary)      = export now (works while paused too)
    ///   other hand primary = reset: clear all points (next export starts empty)
    /// The reset is on the opposite hand so it can't be hit by accident.
    ///
    /// Added and wired automatically by SceneBootstrap.
    /// </summary>
    public class RecordingControls : MonoBehaviour
    {
        public SampleCollector collector;

        [Tooltip("Main hand for A=toggle / B=export. Reset is on the other hand.")]
        public bool useRightHand = true;
        public bool hapticFeedback = true;

        private InputAction _toggleAction;
        private InputAction _exportAction;
        private InputAction _clearAction;
        private XRNode _mainNode;
        private XRNode _otherNode;

        private void OnEnable()
        {
            string mainHand = useRightHand ? "RightHand" : "LeftHand";
            string otherHand = useRightHand ? "LeftHand" : "RightHand";
            _mainNode = useRightHand ? XRNode.RightHand : XRNode.LeftHand;
            _otherNode = useRightHand ? XRNode.LeftHand : XRNode.RightHand;

            _toggleAction = new InputAction("ToggleRecording", InputActionType.Button,
                $"<XRController>{{{mainHand}}}/primaryButton");
            _exportAction = new InputAction("Export", InputActionType.Button,
                $"<XRController>{{{mainHand}}}/secondaryButton");
            _clearAction = new InputAction("Reset", InputActionType.Button,
                $"<XRController>{{{otherHand}}}/primaryButton");

            _toggleAction.Enable();
            _exportAction.Enable();
            _clearAction.Enable();
        }

        private void OnDisable()
        {
            _toggleAction?.Disable();
            _exportAction?.Disable();
            _clearAction?.Disable();
        }

        private void Update()
        {
            if (collector == null) return;

            if (_toggleAction.WasPressedThisFrame())
            {
                if (collector.IsRecording) collector.StopRecording();
                else collector.StartRecording();
                Debug.Log($"[RecordingControls] Recording " +
                          $"{(collector.IsRecording ? "RESUMED" : "PAUSED")} " +
                          $"({collector.SampleCount} points).");
                Pulse(_mainNode);
            }

            if (_exportAction.WasPressedThisFrame())
            {
                string dir = collector.Export();
                Debug.Log($"[RecordingControls] Exported {collector.SampleCount} points to {dir}.");
                Pulse(_mainNode);
            }

            if (_clearAction.WasPressedThisFrame())
            {
                int had = collector.SampleCount;
                collector.ClearAll();
                Debug.Log($"[RecordingControls] Reset — cleared {had} points. " +
                          "Next export/autosave starts fresh.");
                Pulse(_otherNode);
            }
        }

        private void Pulse(XRNode node)
        {
            if (!hapticFeedback) return;
            var device = InputDevices.GetDeviceAtXRNode(node);
            if (device.isValid &&
                device.TryGetHapticCapabilities(out var caps) && caps.supportsImpulse)
                device.SendHapticImpulse(0, 0.5f, 0.08f);
        }
    }
}
