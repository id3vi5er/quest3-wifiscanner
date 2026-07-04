using UnityEngine;

namespace WifiScanner
{
    /// <summary>
    /// Shared helper for the colour-scale legend shown on the control panel and
    /// the wrist HUD: a horizontal RdYlGn gradient strip generated from
    /// <see cref="HeatmapColorMapper"/>, labelled in dBm by the callers.
    /// </summary>
    public static class HeatmapLegend
    {
        /// <summary>1-px-high gradient strip from weak (left) to strong (right).</summary>
        public static Texture2D CreateGradientTexture(int width = 256)
        {
            var tex = new Texture2D(width, 1, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            var pixels = new Color32[width];
            for (int x = 0; x < width; x++)
                pixels[x] = HeatmapColorMapper.Evaluate(x / (float)(width - 1));
            tex.SetPixels32(pixels);
            tex.Apply(false);
            return tex;
        }
    }
}
