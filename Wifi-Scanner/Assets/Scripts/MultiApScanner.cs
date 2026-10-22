using UnityEngine;

namespace WifiScanner
{
    /// <summary>
    /// Multi-AP scanning: feeds ALL visible networks into the sample pipeline
    /// while recording (the connected AP keeps its dense per-frame live path —
    /// this only enriches FOREIGN BSSIDs). Store-safe, no ADB tricks:
    ///
    ///  - Polls the scan-result CACHE every few seconds (free, unthrottled;
    ///    also catches scans the OS triggers itself) and ingests each FRESH
    ///    batch once, deduped via the results' µs-since-boot timestamps.
    ///  - Spends the throttled active-scan budget (~4 per 2 min) by DISTANCE
    ///    walked, not by timer, so scans land where new information is.
    ///  - Adapts its pacing: startScan() returning false (throttled) backs
    ///    off to budget spacing; success streaks speed back up — devices
    ///    without throttling get fast scans automatically.
    ///
    /// Batches older than <see cref="maxBatchAgeSeconds"/> are dropped (the
    /// head has moved too far since the scan — positions would lie).
    /// Toggled from the panel (Aufnahme tab). Created and wired by
    /// SceneBootstrap.
    /// </summary>
    public class MultiApScanner : MonoBehaviour
    {
        [Header("Wired by SceneBootstrap")]
        public SampleCollector collector;

        [Header("Ingest")]
        [Tooltip("Networks weaker than this are ignored (dBm).")]
        public int minRssiDbm = -90;
        [Tooltip("Seconds between cache polls (reading is unthrottled).")]
        public float pollInterval = 3f;
        [Tooltip("Batches older than this are dropped — the head has moved (s).")]
        public float maxBatchAgeSeconds = 8f;

        [Header("Active-scan budget")]
        [Tooltip("Walk at least this far before spending another scan (m).")]
        public float scanMoveDistance = 6f;
        [Tooltip("Request a scan at the latest after this many seconds.")]
        public float maxScanInterval = 45f;
        [Tooltip("Spacing between scan requests while UNthrottled (s).")]
        public float fastSpacing = 6f;
        [Tooltip("Spacing after a throttled request (Android budget: 4/2 min).")]
        public float throttledSpacing = 35f;

        [Tooltip("Multi-AP remains available in the Scan menu, but starts off so " +
                 "normal recording and Survey use the connected AP only.")]
        public bool enabledAtStart = false;

        public bool IsEnabled { get; private set; }
        /// <summary>Distinct networks seen by scanning this session.</summary>
        public int NetworkCount { get; private set; }
        /// <summary>True while requests come back throttled (budget pacing).</summary>
        public bool Throttled { get; private set; }

        private float _pollTimer;
        private long _lastIngestedTimestampUs;
        private float _lastIngestRealtime = -999f;
        private float _lastRequestRealtime = -999f;
        private float _currentSpacing;
        private int _successStreak;
        private Vector3 _lastIngestPos;
        private readonly System.Collections.Generic.HashSet<string> _seen =
            new System.Collections.Generic.HashSet<string>();

        private void Start()
        {
            IsEnabled = enabledAtStart;
            _currentSpacing = fastSpacing;
        }

        public void Toggle() => SetEnabled(!IsEnabled);

        public void SetEnabled(bool enabled)
        {
            IsEnabled = enabled;
            Debug.Log($"[MultiApScanner] Multi-AP-Scan {(enabled ? "AN" : "AUS")}.");
        }

        /// <summary>Panel status line.</summary>
        public string StatusLabel
        {
            get
            {
                if (!IsEnabled) return LocalizationManager.Get("multi_ap.off");
                if (collector == null || !collector.IsRecording)
                    return LocalizationManager.Get("multi_ap.waiting");
                float age = Time.realtimeSinceStartup - _lastIngestRealtime;
                string ago = _lastIngestRealtime < 0f
                    ? LocalizationManager.Get("multi_ap.no_scan")
                    : LocalizationManager.Format("multi_ap.scan_ago",
                        Mathf.Max(0, Mathf.RoundToInt(age)));
                string throttle = Throttled
                    ? LocalizationManager.Get("multi_ap.throttled") : "";
                return LocalizationManager.Format("multi_ap.status",
                    NetworkCount, ago, throttle);
            }
        }

        private void Update()
        {
            if (!IsEnabled || collector == null || !collector.IsRecording)
                return;

            _pollTimer += Time.deltaTime;
            if (_pollTimer < pollInterval) return;
            _pollTimer = 0f;

            var cam = Camera.main;
            if (cam == null) return;
            Vector3 head = cam.transform.position;

            IngestFreshResults(head);
            MaybeRequestScan(head);
        }

        // Reads the cache; a batch is ingested once when its newest timestamp
        // advances past the last ingested one AND the batch is recent enough.
        private void IngestFreshResults(Vector3 head)
        {
            var results = WifiScan.GetResults();
            if (results.Count == 0) return;

            long newest = 0;
            foreach (var ap in results)
                if (ap.timestampUs > newest) newest = ap.timestampUs;
            if (newest <= _lastIngestedTimestampUs) return; // same batch as before

            // Batch age: result timestamps are µs since boot, Unity's realtime
            // clock counts from app start — both advance in lockstep, so the
            // DIFFERENCE between "now" and the batch is measurable via the
            // timestamp delta once we have a reference pair.
            if (_lastIngestedTimestampUs > 0)
            {
                float batchAdvanceSeconds =
                    (newest - _lastIngestedTimestampUs) / 1_000_000f;
                float wallAdvanceSeconds =
                    Time.realtimeSinceStartup - _lastIngestRealtime;
                float batchAge = wallAdvanceSeconds - batchAdvanceSeconds;
                if (batchAge > maxBatchAgeSeconds)
                {
                    // Stale cache content (scan happened long ago) — remember
                    // the timestamp so the batch isn't re-checked, but drop it.
                    _lastIngestedTimestampUs = newest;
                    _lastIngestRealtime = Time.realtimeSinceStartup;
                    return;
                }
            }
            _lastIngestedTimestampUs = newest;
            _lastIngestRealtime = Time.realtimeSinceStartup;
            _lastIngestPos = head;

            string connected = collector.LastBssid;
            int ingested = 0;
            foreach (var ap in results)
            {
                if (ap.rssiDbm < minRssiDbm) continue;
                if (string.IsNullOrEmpty(ap.bssid)) continue;
                // The connected AP is already densely sampled per frame.
                if (ap.bssid == connected) continue;

                collector.AddScanSample(head, new WifiInfo
                {
                    valid = true,
                    rssiDbm = ap.rssiDbm,
                    frequencyMhz = ap.frequencyMhz,
                    linkSpeedMbps = 0,
                    ssid = string.IsNullOrEmpty(ap.ssid) ? "(versteckt)" : ap.ssid,
                    bssid = ap.bssid,
                });
                if (_seen.Add(ap.bssid)) NetworkCount = _seen.Count;
                ingested++;
            }
            if (ingested > 0)
                Debug.Log($"[MultiApScanner] Batch ingestiert: {ingested} Netze " +
                          $"({NetworkCount} gesamt).");
        }

        // Spends the active-scan budget by distance walked; adapts spacing to
        // the observed throttling.
        private void MaybeRequestScan(Vector3 head)
        {
            float sinceRequest = Time.realtimeSinceStartup - _lastRequestRealtime;
            if (sinceRequest < _currentSpacing) return;

            float moved = Vector3.Distance(head, _lastIngestPos);
            float sinceIngest = Time.realtimeSinceStartup - _lastIngestRealtime;
            bool wantScan = moved >= scanMoveDistance ||
                            sinceIngest >= maxScanInterval ||
                            _lastIngestRealtime < 0f;
            if (!wantScan) return;

            _lastRequestRealtime = Time.realtimeSinceStartup;
            bool ok = WifiScan.TryStartScan();
            if (ok)
            {
                _successStreak++;
                if (_successStreak >= 2) // two in a row: throttle is off/idle
                {
                    Throttled = false;
                    _currentSpacing = fastSpacing;
                }
            }
            else
            {
                _successStreak = 0;
                Throttled = true;
                _currentSpacing = throttledSpacing;
            }
        }
    }
}
