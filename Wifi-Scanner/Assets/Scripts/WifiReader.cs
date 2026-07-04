using UnityEngine;

namespace WifiScanner
{
    /// <summary>Plain container for one WiFi reading.</summary>
    public struct WifiInfo
    {
        public bool valid;
        public int rssiDbm;
        public int linkSpeedMbps;
        public int frequencyMhz;
        public string ssid;
        public string bssid;

        public static WifiInfo Invalid => new WifiInfo { valid = false, rssiDbm = -127 };
    }

    /// <summary>
    /// Bridge to Android's WifiManager via JNI reflection (no separate Java
    /// plugin / .jar needed). Reads the RSSI of the *currently connected* AP.
    ///
    /// Context acquisition works with BOTH Android application entry points:
    /// - "Activity":     UnityPlayer.currentActivity is available.
    /// - "GameActivity": (Unity 6 default) currentActivity is null, so we fall
    ///                   back to ActivityThread.currentApplication(), which
    ///                   returns the Application (a Context) regardless of entry.
    ///
    /// Uses the legacy WifiManager.getConnectionInfo() path: deprecated since
    /// API 31 but still functional on Horizon OS. If a future OS update blocks
    /// it, switch to the ConnectivityManager + NetworkCallback approach
    /// (see README "Risiken").
    /// </summary>
    public static class WifiReader
    {
        private static AndroidJavaObject _wifiManager;
        private static bool _initialized;

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                AndroidJavaObject context = GetApplicationContext();
                if (context == null)
                {
                    Debug.LogError("[WifiReader] Could not obtain Android context.");
                    return;
                }
                // Context.WIFI_SERVICE == "wifi"
                _wifiManager = context.Call<AndroidJavaObject>("getSystemService", "wifi");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[WifiReader] Init failed: {e}");
                _wifiManager = null;
            }
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static AndroidJavaObject GetApplicationContext()
        {
            // Preferred: via the player's current activity (Application Entry = Activity).
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                {
                    if (activity != null)
                        return activity.Call<AndroidJavaObject>("getApplicationContext");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[WifiReader] currentActivity unavailable ({e.Message}); " +
                                 "falling back to ActivityThread.");
            }

            // Fallback: works under GameActivity (Unity 6 default), no activity needed.
            try
            {
                using (var activityThread = new AndroidJavaClass("android.app.ActivityThread"))
                {
                    var app = activityThread.CallStatic<AndroidJavaObject>("currentApplication");
                    if (app != null)
                        return app.Call<AndroidJavaObject>("getApplicationContext");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[WifiReader] ActivityThread fallback failed: {e}");
            }

            return null;
        }
#endif

        /// <summary>Read the current connected-AP WiFi info, or Invalid on failure.</summary>
        public static WifiInfo Read()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            EnsureInitialized();
            if (_wifiManager == null) return WifiInfo.Invalid;

            try
            {
                using (var info = _wifiManager.Call<AndroidJavaObject>("getConnectionInfo"))
                {
                    if (info == null) return WifiInfo.Invalid;

                    var result = new WifiInfo
                    {
                        valid = true,
                        rssiDbm = info.Call<int>("getRssi"),
                        linkSpeedMbps = info.Call<int>("getLinkSpeed"),
                        frequencyMhz = info.Call<int>("getFrequency"),
                        ssid = info.Call<string>("getSSID"),
                        bssid = info.Call<string>("getBSSID")
                    };

                    // getSSID() returns the name wrapped in quotes, or
                    // "<unknown ssid>" when location permission is missing.
                    if (!string.IsNullOrEmpty(result.ssid))
                        result.ssid = result.ssid.Trim('"');

                    return result;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[WifiReader] Read failed: {e}");
                return WifiInfo.Invalid;
            }
#else
            // Editor stub: fake a value so the rest of the pipeline can be
            // tested without a device. Strength drifts with distance from origin.
            float dist = Camera.main != null ? Camera.main.transform.position.magnitude : 0f;
            int fakeRssi = Mathf.Clamp(-40 - Mathf.RoundToInt(dist * 8f), -95, -30);
            return new WifiInfo
            {
                valid = true,
                rssiDbm = fakeRssi,
                linkSpeedMbps = 300,
                frequencyMhz = 5180,
                ssid = "EDITOR_FAKE_AP",
                bssid = "00:11:22:33:44:55"
            };
#endif
        }
    }
}
