using UnityEngine;

namespace WifiScanner
{
    /// <summary>
    /// Maps an RSSI value (dBm) to a continuous heatmap colour using the same
    /// RdYlGn palette as the Python viewer (tools/receiver.py), so the in-headset
    /// cloud and the PC 2D/3D views match. Green = strong, red = weak.
    /// </summary>
    public static class HeatmapColorMapper
    {
        // Typical indoor WiFi range. Tune to your environment.
        public const int StrongDbm = -40; // green end
        public const int WeakDbm = -85;   // red end

        // matplotlib "RdYlGn" 11-class stops, index 0 = red (weak) .. 10 = green (strong).
        private static readonly Color[] RdYlGn =
        {
            new Color(0.647f, 0.000f, 0.149f), // #a50026
            new Color(0.843f, 0.188f, 0.153f), // #d73027
            new Color(0.957f, 0.427f, 0.263f), // #f46d43
            new Color(0.992f, 0.682f, 0.380f), // #fdae61
            new Color(0.996f, 0.878f, 0.545f), // #fee08b
            new Color(1.000f, 1.000f, 0.749f), // #ffffbf
            new Color(0.851f, 0.937f, 0.545f), // #d9ef8b
            new Color(0.651f, 0.851f, 0.416f), // #a6d96a
            new Color(0.400f, 0.741f, 0.388f), // #66bd63
            new Color(0.102f, 0.596f, 0.314f), // #1a9850
            new Color(0.000f, 0.408f, 0.216f), // #006837
        };

        /// <summary>Continuous colour for an RSSI value (dBm).</summary>
        public static Color Map(int rssiDbm)
        {
            float t = Mathf.Clamp01(Mathf.InverseLerp(WeakDbm, StrongDbm, rssiDbm));
            return Evaluate(t);
        }

        /// <summary>Sample the RdYlGn gradient at t in [0,1] (0 = red, 1 = green).</summary>
        public static Color Evaluate(float t)
        {
            int last = RdYlGn.Length - 1;
            float scaled = Mathf.Clamp01(t) * last;
            int i = Mathf.Clamp(Mathf.FloorToInt(scaled), 0, last - 1);
            return Color.Lerp(RdYlGn[i], RdYlGn[i + 1], scaled - i);
        }
    }
}
