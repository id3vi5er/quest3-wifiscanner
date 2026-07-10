using UnityEngine;
using UnityEngine.XR;

namespace WifiScanner
{
    /// <summary>
    /// One-line controller vibration for UI/tool feedback. Uses the XR
    /// InputDevices haptic impulse (works on Quest via OpenXR); silently does
    /// nothing when the device has no rumble (Editor play mode, hand tracking).
    /// Amplitudes are deliberately conventions, not per-call magic numbers:
    ///   Tick   = light   (cursor snapped, type cycled, tool switched)
    ///   Place  = medium  (element placed / action applied)
    ///   Heavy  = strong  (deleted, destructive action armed/executed)
    /// </summary>
    public static class Haptics
    {
        /// <summary>Global strength multiplier for ALL pulses (0 = haptics
        /// off, 1 = default, up to 2 = double). Set from the SceneBootstrap
        /// inspector slider ("Haptic strength").</summary>
        public static float GlobalStrength = 1f;

        public static void TickRight() => Pulse(true, 0.25f, 0.015f);
        public static void TickLeft() => Pulse(false, 0.25f, 0.015f);
        public static void PlaceRight() => Pulse(true, 0.5f, 0.03f);
        public static void HeavyRight() => Pulse(true, 0.85f, 0.06f);

        public static void Pulse(bool rightHand, float amplitude, float duration)
        {
            float strength = Mathf.Clamp01(amplitude * Mathf.Clamp(GlobalStrength, 0f, 2f));
            if (strength <= 0f) return; // haptics disabled

            var device = InputDevices.GetDeviceAtXRNode(
                rightHand ? XRNode.RightHand : XRNode.LeftHand);
            if (device.isValid &&
                device.TryGetHapticCapabilities(out HapticCapabilities caps) &&
                caps.supportsImpulse)
            {
                device.SendHapticImpulse(0u, strength, duration);
            }
        }
    }
}
