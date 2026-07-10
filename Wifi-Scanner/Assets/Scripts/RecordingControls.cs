using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;

namespace WifiScanner
{
    /// <summary>
    /// Controller buttons to pause/resume recording and export, using the
    /// Input System with code-defined XR bindings (no Input Action asset needed).
    ///
    /// Default (main hand = right Touch controller):
    ///   A (primary)   = toggle recording (pause / resume)
    ///   B (secondary) = export now (works while paused too)
    ///
    /// Clearing the scan is deliberately NOT on a controller button (too easy
    /// to hit by accident, destroys the recording) — it lives only on the
    /// control panel. Added and wired automatically by SceneBootstrap.
    /// </summary>
    public class RecordingControls : MonoBehaviour
    {
        public SampleCollector collector;

        [Tooltip("Hand for A=toggle / B=export.")]
        public bool useRightHand = true;
        public bool hapticFeedback = true;

        private InputAction _toggleAction;
        private InputAction _exportAction;
        private XRNode _mainNode;

        private void OnEnable()
        {
            string mainHand = useRightHand ? "RightHand" : "LeftHand";
            _mainNode = useRightHand ? XRNode.RightHand : XRNode.LeftHand;

            _toggleAction = new InputAction("ToggleRecording", InputActionType.Button,
                $"<XRController>{{{mainHand}}}/primaryButton");
            _exportAction = new InputAction("Export", InputActionType.Button,
                $"<XRController>{{{mainHand}}}/secondaryButton");

            _toggleAction.Enable();
            _exportAction.Enable();
        }

        private void OnDisable()
        {
            _toggleAction?.Disable();
            _exportAction?.Disable();
        }

        private void Update()
        {
            if (collector == null) return;
            // While any controller tool (tracer/drill/delete/measure/notes/…)
            // owns the trigger, A/B belong to it — suspend recording controls.
            if (ExclusiveTools.AnyActive) return;

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
