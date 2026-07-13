using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace WifiScanner
{
    /// <summary>
    /// Distinguishes a normal palette tap from a held trigger press. Unity's
    /// Button.onClick cannot suppress the release-click after a long press, so
    /// palette tool buttons route both gestures through this small component.
    /// </summary>
    public sealed class PalettePressHandler : MonoBehaviour,
        IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        [Tooltip("Seconds the trigger must stay held to open tool options.")]
        public float holdSeconds = 0.65f;

        public Action onShortPress;
        public Action onLongPress;

        private bool _pressed;
        private bool _longFired;
        private float _pressedAt;

        public void OnPointerDown(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;
            _pressed = true;
            _longFired = false;
            _pressedAt = Time.unscaledTime;
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (!_pressed || eventData.button != PointerEventData.InputButton.Left)
                return;
            _pressed = false;
            if (!_longFired) onShortPress?.Invoke();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            // A moving wrist can make the ray leave the button. Cancelling is
            // safer than selecting a neighbouring tool on release.
            _pressed = false;
        }

        private void Update()
        {
            if (!_pressed || _longFired || onLongPress == null ||
                Time.unscaledTime - _pressedAt < holdSeconds)
                return;
            _longFired = true;
            onLongPress.Invoke();
        }

        private void OnDisable()
        {
            _pressed = false;
            _longFired = false;
        }
    }
}
