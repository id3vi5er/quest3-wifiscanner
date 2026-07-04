using UnityEngine;

#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace WifiScanner
{
    /// <summary>
    /// SCHRITT 0 — Risiko-Absicherung.
    ///
    /// Eigenständiger Gerätetest: fordert die Location-Permission an und gibt
    /// einmal pro Sekunde die RSSI/SSID-Werte des verbundenen AP per Debug.Log
    /// aus. Damit lässt sich VOR dem vollen App-Aufbau beweisen, dass Horizon OS
    /// einer App den WiFi-Zugriff gewährt.
    ///
    /// Benötigt nur: WifiProbe.cs + WifiReader.cs + Sample.cs +
    /// Assets/Plugins/Android/AndroidManifest.xml
    ///
    /// Verwendung:
    ///   1. Leeres GameObject in die Szene legen, dieses Skript anhängen.
    ///   2. Build & Run auf die Quest, Location-Permission gewähren.
    ///   3. Logcat beobachten:  adb logcat -s Unity:V
    ///
    /// Erwartung: z. B. ~-45 dBm nahe am Router, ~-80 dBm weit weg.
    /// Steht dort "<unknown ssid>" oder rssi -127 -> Permission/Location fehlt.
    /// </summary>
    public class WifiProbe : MonoBehaviour
    {
        [Tooltip("Ausgabe-Intervall in Sekunden.")]
        public float interval = 1.0f;

        private float _timer;

        private void Start()
        {
            DontDestroyOnLoad(gameObject);
            RequestPermissions();
            Debug.Log("[WifiProbe] gestartet — warte auf WiFi-Werte …");
        }

        private void Update()
        {
            _timer += Time.deltaTime;
            if (_timer < interval) return;
            _timer = 0f;

            WifiInfo w = WifiReader.Read();
            if (!w.valid)
            {
                Debug.LogWarning("[WifiProbe] Keine gültigen WiFi-Daten " +
                                 "(WifiManager null oder Ausnahme).");
                return;
            }

            string quality =
                w.rssiDbm >= -50 ? "ausgezeichnet" :
                w.rssiDbm >= -65 ? "gut" :
                w.rssiDbm >= -75 ? "mittel" :
                w.rssiDbm >= -85 ? "schwach" : "sehr schwach";

            Debug.Log($"[WifiProbe] RSSI={w.rssiDbm} dBm ({quality})  " +
                      $"SSID=\"{w.ssid}\"  BSSID={w.bssid}  " +
                      $"Link={w.linkSpeedMbps} Mbps  Freq={w.frequencyMhz} MHz");

            if (w.rssiDbm <= -127 || w.ssid == "<unknown ssid>")
                Debug.LogWarning("[WifiProbe] Werte wirken redaktiert -> " +
                                 "Location-Permission/Standortdienst prüfen.");
        }

        private void RequestPermissions()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(Permission.FineLocation))
                Permission.RequestUserPermission(Permission.FineLocation);

            const string nearbyWifi = "android.permission.NEARBY_WIFI_DEVICES";
            if (!Permission.HasUserAuthorizedPermission(nearbyWifi))
                Permission.RequestUserPermission(nearbyWifi);
#endif
        }
    }
}
