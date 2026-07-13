using System.Collections.Generic;
using UnityEngine;

namespace WifiScanner
{
    /// <summary>One access point from a WiFi scan result set.</summary>
    public struct ScanAp
    {
        public string ssid;
        public string bssid;
        public int rssiDbm;
        public int frequencyMhz;
        public long timestampUs; // ScanResult.timestamp: µs since device boot
    }

    /// <summary>
    /// Bridge to Android's WifiManager SCAN API via JNI reflection (companion
    /// to <see cref="WifiReader"/>, which reads only the CONNECTED AP).
    ///
    /// <see cref="TryStartScan"/> requests an active scan — Android throttles
    /// these to ~4 per 2 minutes for foreground apps and returns false when
    /// throttled; the caller adapts its pacing. <see cref="GetResults"/> reads
    /// the system-wide result CACHE, which also fills from scans the OS
    /// triggers on its own (roaming/auto-join) — polling it is free and
    /// unthrottled; freshness is judged via each result's timestamp.
    /// No BroadcastReceiver: it's an abstract class (not proxyable via
    /// AndroidJavaProxy) and timestamp-deduped polling is equivalent here.
    /// </summary>
    public static class WifiScan
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
                    Debug.LogError("[WifiScan] Could not obtain Android context.");
                    return;
                }
                _wifiManager = context.Call<AndroidJavaObject>("getSystemService", "wifi");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[WifiScan] Init failed: {e}");
                _wifiManager = null;
            }
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        // Same two-path context acquisition as WifiReader (Activity entry and
        // GameActivity fallback via ActivityThread).
        private static AndroidJavaObject GetApplicationContext()
        {
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                {
                    if (activity != null)
                        return activity.Call<AndroidJavaObject>("getApplicationContext");
                }
            }
            catch (System.Exception)
            {
                // fall through to ActivityThread
            }
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
                Debug.LogError($"[WifiScan] ActivityThread fallback failed: {e}");
            }
            return null;
        }
#endif

        /// <summary>Requests an active scan. False when throttled/unavailable —
        /// the caller backs off to budget pacing.</summary>
        public static bool TryStartScan()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            EnsureInitialized();
            if (_wifiManager == null) return false;
            try
            {
                return _wifiManager.Call<bool>("startScan");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[WifiScan] startScan failed: {e.Message}");
                return false;
            }
#else
            return true; // Editor: pretend scans always succeed
#endif
        }

        /// <summary>
        /// The current scan-result cache (all visible networks). Empty without
        /// location permission. Each entry carries the µs-since-boot timestamp
        /// of the scan that produced it — dedupe batches with it.
        /// </summary>
        public static List<ScanAp> GetResults()
        {
            var results = new List<ScanAp>();
#if UNITY_ANDROID && !UNITY_EDITOR
            EnsureInitialized();
            if (_wifiManager == null) return results;
            try
            {
                using (var list = _wifiManager.Call<AndroidJavaObject>("getScanResults"))
                {
                    if (list == null) return results;
                    int count = list.Call<int>("size");
                    for (int i = 0; i < count; i++)
                    {
                        using (var item = list.Call<AndroidJavaObject>("get", i))
                        {
                            if (item == null) continue;
                            // ScanResult exposes these as PUBLIC FIELDS.
                            results.Add(new ScanAp
                            {
                                ssid = item.Get<string>("SSID") ?? "",
                                bssid = item.Get<string>("BSSID") ?? "",
                                rssiDbm = item.Get<int>("level"),
                                frequencyMhz = item.Get<int>("frequency"),
                                timestampUs = item.Get<long>("timestamp"),
                            });
                        }
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[WifiScan] getScanResults failed: {e.Message}");
            }
#else
            // Editor stub: three fake neighbour networks so the AP dropdown,
            // per-BSSID heatmaps and exports are testable without a device.
            float t = Time.realtimeSinceStartup;
            long stamp = (long)(t / 10f) * 10_000_000L; // "new scan" every 10 s
            Vector3 head = Camera.main != null
                ? Camera.main.transform.position : Vector3.zero;
            void Fake(string ssid, string bssid, Vector3 ap, int freq)
            {
                float d = Mathf.Max(0.5f, Vector3.Distance(head, ap));
                results.Add(new ScanAp
                {
                    ssid = ssid,
                    bssid = bssid,
                    rssiDbm = Mathf.Clamp(-40 - Mathf.RoundToInt(
                        20f * Mathf.Log10(d) * 1.4f), -95, -35),
                    frequencyMhz = freq,
                    timestampUs = stamp,
                });
            }
            Fake("EDITOR_FAKE_AP", "00:11:22:33:44:55", new Vector3(0f, 2f, 0f), 5180);
            Fake("Nachbar_2G", "aa:bb:cc:00:11:22", new Vector3(8f, 2f, 3f), 2437);
            Fake("Nachbar_5G", "aa:bb:cc:00:11:23", new Vector3(-6f, 2f, -4f), 5500);
#endif
            return results;
        }
    }
}
