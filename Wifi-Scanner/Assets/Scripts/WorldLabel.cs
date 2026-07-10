using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace WifiScanner
{
    /// <summary>
    /// A small self-contained floating text label on a world-space canvas that
    /// billboards toward the head camera. Reused by the analysis tools (AP
    /// locator, ping nodes, spatial notes, distance measurer) to hover a value
    /// above a world point.
    ///
    /// Create with <see cref="Create"/>, then each frame call
    /// <see cref="SetWorldPosition"/> (billboarding happens automatically).
    /// </summary>
    public class WorldLabel : MonoBehaviour
    {
        private TextMeshProUGUI _text;

        public static WorldLabel Create(Transform parent, float worldScale = 0.0016f,
            float fontSize = 26f)
        {
            var go = new GameObject("WorldLabel", typeof(RectTransform), typeof(Canvas));
            go.transform.SetParent(parent, false);
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(360f, 120f);
            go.transform.localScale = Vector3.one * worldScale;

            var bg = new GameObject("BG", typeof(RectTransform)).AddComponent<Image>();
            bg.transform.SetParent(go.transform, false);
            bg.color = new Color(0f, 0f, 0f, 0.55f);
            bg.raycastTarget = false;
            Stretch(bg.rectTransform);

            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(go.transform, false);
            var text = textGo.AddComponent<TextMeshProUGUI>();
            text.fontSize = fontSize;
            text.alignment = TextAlignmentOptions.Center;
            text.color = Color.white;
            text.raycastTarget = false;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            Stretch(text.rectTransform);

            var wl = go.AddComponent<WorldLabel>();
            wl._text = text;
            return wl;
        }

        public void SetText(string s)
        {
            if (_text != null) _text.text = s;
        }

        public void SetColor(Color c)
        {
            if (_text != null) _text.color = c;
        }

        public void SetWorldPosition(Vector3 p) => transform.position = p;

        public void SetVisible(bool visible)
        {
            if (gameObject.activeSelf != visible) gameObject.SetActive(visible);
        }

        private void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null) return;
            // Face the head: the canvas front (local +Z) points away from the
            // camera so the text reads the right way round.
            Vector3 dir = transform.position - cam.transform.position;
            if (dir.sqrMagnitude > 1e-6f)
                transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }
}
