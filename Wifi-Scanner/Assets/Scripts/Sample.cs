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

        public static Sample Create(Vector3 pos, WifiInfo wifi, int floor = 0)
        {
            return new Sample
            {
                timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                posX = pos.x,
                posY = pos.y,
                posZ = pos.z,
                rssiDbm = wifi.rssiDbm,
                linkSpeedMbps = wifi.linkSpeedMbps,
                frequencyMhz = wifi.frequencyMhz,
                ssid = wifi.ssid,
                bssid = wifi.bssid,
                floor = floor
            };
        }

        public string ToCsvRow()
        {
            // ssid may contain commas/quotes -> wrap and escape.
            string safeSsid = "\"" + (ssid ?? "").Replace("\"", "\"\"") + "\"";
            return $"{timestampMs},{posX:F4},{posY:F4},{posZ:F4},{rssiDbm},{linkSpeedMbps},{frequencyMhz},{safeSsid},{bssid},{floor}";
        }

        public static string CsvHeader()
        {
            return "timestampMs,posX,posY,posZ,rssiDbm,linkSpeedMbps,frequencyMhz,ssid,bssid,floor";
        }
    }
}
