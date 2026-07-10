using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace WifiScanner
{
    /// <summary>
    /// Shared hover state of all clickable XR UI surfaces (control panel,
    /// tool palette). Each surface tests the right controller's pointer ray
    /// against its own canvas every frame and reports the result here; the
    /// exclusive tools consult the aggregate (via
    /// <see cref="ControlPanel.PointerOverPanel"/>) to NOT also fire their
    /// trigger/A/B when the user is clicking a UI button.
    /// </summary>
    public static class XrUiHover
    {
        private static readonly Dictionary<object, bool> _over =
            new Dictionary<object, bool>();
        private static readonly Vector3[] _corners = new Vector3[4];

        /// <summary>True while the pointer ray lies over ANY registered surface.</summary>
        public static bool Any
        {
            get
            {
                foreach (bool over in _over.Values)
                    if (over) return true;
                return false;
            }
        }

        /// <summary>Reports a surface's hover state (call every frame).</summary>
        public static void Set(object key, bool over) => _over[key] = over;

        /// <summary>Removes a surface (call from OnDisable so no state sticks).</summary>
        public static void Clear(object key) => _over.Remove(key);

        /// <summary>
        /// Casts a pointer ray against a world-space RectTransform and returns
        /// whether it hits inside (with a fractional margin around the edges).
        /// Uses the rect's actual world corners, so pivot/scale don't matter.
        /// </summary>
        public static bool RayOverRect(Vector3 origin, Vector3 dir,
            RectTransform rect, float margin, float maxDist = 10f)
        {
            rect.GetWorldCorners(_corners);
            Vector3 bl = _corners[0];          // bottom-left
            Vector3 right = _corners[3] - bl;  // bottom-right - bl
            Vector3 up = _corners[1] - bl;     // top-left - bl
            Vector3 normal = Vector3.Cross(right, up);
            if (normal.sqrMagnitude < 1e-9f) return false;
            normal.Normalize();

            float denom = Vector3.Dot(normal, dir);
            if (Mathf.Abs(denom) < 1e-6f) return false;
            float t = Vector3.Dot(bl - origin, normal) / denom;
            if (t < 0f || t > maxDist) return false;

            Vector3 rel = origin + dir * t - bl;
            float u = Vector3.Dot(rel, right) / Vector3.Dot(right, right);
            float v = Vector3.Dot(rel, up) / Vector3.Dot(up, up);
            return u >= -margin && u <= 1f + margin
                && v >= -margin && v <= 1f + margin;
        }

        /// <summary>
        /// The scene's EventSystem needs an XRUIInputModule for the XR ray
        /// interactors to drive uGUI; other input modules are disabled so
        /// EventSystem picks ours. Idempotent — called by every UI surface.
        /// </summary>
        public static void EnsureXrUiInputModule()
        {
            var es = EventSystem.current;
            if (es == null) es = Object.FindFirstObjectByType<EventSystem>();
            if (es == null)
                es = new GameObject("EventSystem", typeof(EventSystem))
                    .GetComponent<EventSystem>();

            if (es.GetComponent<XRUIInputModule>() != null) return;
            foreach (var module in es.GetComponents<BaseInputModule>())
                module.enabled = false;
            es.gameObject.AddComponent<XRUIInputModule>();
        }
    }
}
