using System;
using UnityEngine;

namespace WifiScanner
{
    /// <summary>
    /// One measured point in the WiFi cloud: the headset position plus the
    /// WiFi readings of the currently connected access point at that spot.
    /// </summary>
    [Serializable]
    public struct Sample
    {
        public long timestampMs;   // Unix epoch milliseconds
        public float posX;
        public float posY;
        public float posZ;
        public int rssiDbm;        // e.g. -45 (strong) .. -90 (weak)
        public int linkSpeedMbps;
        public int frequencyMhz;
        public string ssid;
        public string bssid;
        public int floor; // storey index this point was recorded on (multi-floor)

        // Optional aggregation metadata. JsonUtility leaves fields that are
        // absent in older sessions at zero; NormalizeLegacyMetadata turns
        // those records into one effective measurement without inventing a
        // variance or tracking confidence.
        public int sampleCount;
        public float rssiMeanDbm;
        public float rssiM2; // Welford sum of squared deviations
        public long firstTimestampMs;
        public long lastTimestampMs;
        [Range(-1f, 1f)] public float trackingQuality;  // -1 = unavailable
        [Range(-1f, 1f)] public float alignmentQuality; // -1 = not assessed

        public bool HasRssiVariance => sampleCount > 1 && rssiM2 >= 0f;
        public float RssiVariance => HasRssiVariance
            ? rssiM2 / (sampleCount - 1) : float.NaN;

        public static Sample Create(Vector3 pos, WifiInfo wifi, int floor = 0)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return new Sample
            {
                timestampMs = now,
                posX = pos.x,
                posY = pos.y,
                posZ = pos.z,
                rssiDbm = wifi.rssiDbm,
                linkSpeedMbps = wifi.linkSpeedMbps,
                frequencyMhz = wifi.frequencyMhz,
                ssid = wifi.ssid,
                bssid = wifi.bssid,
                floor = floor,
                sampleCount = 1,
                rssiMeanDbm = wifi.rssiDbm,
                rssiM2 = 0f,
                firstTimestampMs = now,
                lastTimestampMs = now,
                trackingQuality = -1f,
                alignmentQuality = -1f,
            };
        }

        /// <summary>Repairs metadata omitted by pre-quality JSON sessions.</summary>
        public void NormalizeLegacyMetadata()
        {
            bool legacy = sampleCount <= 0;
            if (legacy)
            {
                sampleCount = 1;
                rssiMeanDbm = rssiDbm;
                rssiM2 = 0f;
                trackingQuality = -1f;
                alignmentQuality = -1f;
            }
            else if (Mathf.Abs(rssiMeanDbm) < 0.001f && rssiDbm != 0)
                rssiMeanDbm = rssiDbm;

            if (firstTimestampMs <= 0) firstTimestampMs = timestampMs;
            if (lastTimestampMs <= 0) lastTimestampMs = timestampMs;
            if (timestampMs <= 0) timestampMs = lastTimestampMs;
            if (sampleCount == 1) rssiM2 = 0f;
        }

        /// <summary>Adds one RSSI observation with Welford's stable online
        /// mean/variance update. The rounded legacy rssiDbm stays in sync.</summary>
        public void FoldRssi(int observationDbm, long observationTimestampMs)
        {
            NormalizeLegacyMetadata();
            int nextCount = sampleCount + 1;
            float delta = observationDbm - rssiMeanDbm;
            rssiMeanDbm += delta / nextCount;
            float delta2 = observationDbm - rssiMeanDbm;
            rssiM2 += delta * delta2;
            sampleCount = nextCount;
            lastTimestampMs = observationTimestampMs;
            timestampMs = observationTimestampMs;
            rssiDbm = Mathf.RoundToInt(rssiMeanDbm);
        }

        public string ToCsvRow()
        {
            // ssid may contain commas/quotes -> wrap and escape.
            string safeSsid = "\"" + (ssid ?? "").Replace("\"", "\"\"") + "\"";
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string variance = HasRssiVariance ? RssiVariance.ToString("F4",
                inv) : "";
            return string.Join(",", timestampMs.ToString(inv), posX.ToString("F4",inv),
                posY.ToString("F4",inv), posZ.ToString("F4",inv), rssiDbm.ToString(inv),
                linkSpeedMbps.ToString(inv), frequencyMhz.ToString(inv), safeSsid,
                bssid ?? "", floor.ToString(inv), sampleCount.ToString(inv),
                rssiMeanDbm.ToString("F4",inv), rssiM2.ToString("F4",inv), variance,
                firstTimestampMs.ToString(inv), lastTimestampMs.ToString(inv),
                trackingQuality.ToString("F3",inv), alignmentQuality.ToString("F3",inv));
        }

        public static string CsvHeader()
        {
            return "timestampMs,posX,posY,posZ,rssiDbm,linkSpeedMbps,frequencyMhz,ssid,bssid,floor,sampleCount,rssiMeanDbm,rssiM2,rssiVariance,firstTimestampMs,lastTimestampMs,trackingQuality,alignmentQuality";
        }

        /// <summary>Human-readable WiFi band for a channel frequency.</summary>
        public static string BandLabel(int frequencyMhz)
        {
            if (frequencyMhz >= 5925) return "6 GHz";
            if (frequencyMhz >= 4900) return "5 GHz";
            if (frequencyMhz >= 2300) return "2,4 GHz";
            return "?";
        }
    }
}
