using System;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace WifiScanner
{
    /// <summary>
    /// Core orchestrator. Each tick it reads the headset position and the
    /// connected-AP RSSI. On a throttled cadence it checks whether any existing
    /// point already sits within <see cref="minPointSpacing"/> of the current
    /// head position: if not, it records a new smoothed Sample; if so, it folds
    /// the reading into that nearest point's running-average RSSI instead of
    /// spawning a duplicate. This keeps the point density spatially constant
    /// (no pile-ups when standing still, no duplicates when revisiting).
    ///
    /// Attach to a GameObject in the scene and wire up the references in the
    /// Inspector — or let SceneBootstrap create and wire everything at runtime.
    /// 'headAnchor' is the head camera (XR Origin > Camera Offset > Main Camera);
    /// if left empty it falls back to Camera.main.
    /// </summary>
    public class SampleCollector : MonoBehaviour
    {
        [Header("Scene references")]
        [Tooltip("Head camera transform (XR Origin Main Camera). Empty = Camera.main.")]
        public Transform headAnchor;
        public PointCloudRenderer pointCloud;
        public NetworkStreamer streamer;
        [Tooltip("Optional: exports the scanned room mesh as OBJ next to CSV/JSON.")]
        public RoomMeshController roomMesh;
        [Tooltip("Optional: exports the floor heatmap as PNG next to CSV/JSON.")]
        public FloorHeatmapRenderer floorHeatmap;
        [Tooltip("Optional name prefix for exports: <name>_<Datum>_<Uhrzeit>. " +
                 "Empty = wifi_<Datum>_<Uhrzeit>. Set via the panel's name field.")]
        public string exportName = "";

        [Header("Sampling")]
        [Tooltip("Minimum distance between points (m). A new point is only created " +
                 "when no existing point lies within this radius; otherwise the " +
                 "nearest one is updated. Controls the constant point density.")]
        public float minPointSpacing = 0.2f;
        [Tooltip("How often a candidate position is evaluated (s). Pure throttle — " +
                 "NOT a time threshold that spawns points on its own.")]
        public float sampleInterval = 0.2f;
        [Tooltip("Number of RSSI readings averaged per recorded point.")]
        public int smoothingWindow = 5;

        public bool IsRecording { get; private set; }
        public int SampleCount => _samples.Count;
        public int LastRssi { get; private set; } = -127;
        public string LastSsid { get; private set; } = "";
        /// <summary>Total time spent recording (s), across pauses. Reset by ClearAll.</summary>
        public float RecordingSeconds { get; private set; }
        /// <summary>Recorded points (running-averaged per grid cell). Read-only view
        /// for renderers that need positions + RSSI (e.g. the floor heatmap).</summary>
        public IReadOnlyList<Sample> Samples => _samples;
        /// <summary>Bumped on every data change — including running-average
        /// updates of EXISTING points, which don't change SampleCount. Consumers
        /// (floor heatmap) re-render when this differs, so revisited areas
        /// recolour with the refined averages.</summary>
        public int DataRevision { get; private set; }

        private readonly List<Sample> _samples = new List<Sample>();
        private readonly Queue<int> _rssiWindow = new Queue<int>();

        // Spatial hash grid for O(1) nearest-point lookup. Cell size == spacing,
        // so any point within minPointSpacing lives in one of the 27 neighbours.
        private readonly Dictionary<Vector3Int, List<int>> _grid =
            new Dictionary<Vector3Int, List<int>>();
        // Parallel to _samples: render handle and measurement count per point.
        private readonly List<int> _renderHandles = new List<int>();
        private readonly List<int> _hitCounts = new List<int>();
        private float _timeSinceLastEval;

        private void Start()
        {
            RequestPermissions();
            if (headAnchor == null && Camera.main != null)
                headAnchor = Camera.main.transform; // editor fallback
        }

        private void Update()
        {
            // Always poll RSSI for the live readout, even when not recording.
            WifiInfo wifi = WifiReader.Read();
            if (wifi.valid)
            {
                PushRssi(wifi.rssiDbm);
                LastRssi = wifi.rssiDbm;
                LastSsid = wifi.ssid;
            }

            if (!IsRecording || headAnchor == null) return;
            RecordingSeconds += Time.deltaTime;

            _timeSinceLastEval += Time.deltaTime;
            if (_timeSinceLastEval < sampleInterval) return;
            _timeSinceLastEval = 0f;
            if (!wifi.valid) return;

            Vector3 pos = headAnchor.position;
            int rssi = SmoothedRssi();
            int nearest = FindNearestWithin(pos, minPointSpacing);
            if (nearest < 0) RecordSample(pos, rssi, wifi); // new point
            else             UpdateSample(nearest, rssi);   // fold into existing
        }

        private void RecordSample(Vector3 pos, int rssi, WifiInfo wifi)
        {
            wifi.rssiDbm = rssi;
            var sample = Sample.Create(pos, wifi);

            int index = _samples.Count;
            _samples.Add(sample);
            _hitCounts.Add(1);
            AddToGrid(index, pos);

            int handle = pointCloud != null ? pointCloud.AddPoint(pos, rssi) : -1;
            _renderHandles.Add(handle);
            DataRevision++;

            if (streamer != null)
                streamer.Send(sample);
        }

        // Fold a new reading into an existing point as a running average and
        // recolour it. Not re-streamed, to avoid duplicates on the PC viewer.
        private void UpdateSample(int i, int newRssi)
        {
            int c = _hitCounts[i];
            int avg = Mathf.RoundToInt((_samples[i].rssiDbm * c + newRssi) / (float)(c + 1));
            _hitCounts[i] = c + 1;

            var s = _samples[i];
            s.rssiDbm = avg;
            _samples[i] = s;
            DataRevision++;

            if (pointCloud != null)
                pointCloud.UpdatePoint(_renderHandles[i], avg);
        }

        // ---- Spatial hash grid ----

        private Vector3Int CellOf(Vector3 p) =>
            Vector3Int.FloorToInt(p / minPointSpacing);

        private void AddToGrid(int index, Vector3 pos)
        {
            Vector3Int cell = CellOf(pos);
            if (!_grid.TryGetValue(cell, out var list))
            {
                list = new List<int>();
                _grid[cell] = list;
            }
            list.Add(index);
        }

        // Index of the closest existing sample within radius r, or -1 if none.
        private int FindNearestWithin(Vector3 pos, float r)
        {
            Vector3Int c = CellOf(pos);
            float bestSqr = r * r;
            int best = -1;
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                var cell = new Vector3Int(c.x + dx, c.y + dy, c.z + dz);
                if (!_grid.TryGetValue(cell, out var list)) continue;
                foreach (int idx in list)
                {
                    var s = _samples[idx];
                    float sqr = (new Vector3(s.posX, s.posY, s.posZ) - pos).sqrMagnitude;
                    if (sqr <= bestSqr)
                    {
                        bestSqr = sqr;
                        best = idx;
                    }
                }
            }
            return best;
        }

        private void PushRssi(int rssi)
        {
            _rssiWindow.Enqueue(rssi);
            while (_rssiWindow.Count > Mathf.Max(1, smoothingWindow))
                _rssiWindow.Dequeue();
        }

        private int SmoothedRssi()
        {
            if (_rssiWindow.Count == 0) return LastRssi;
            int sum = 0;
            foreach (int r in _rssiWindow) sum += r;
            return Mathf.RoundToInt((float)sum / _rssiWindow.Count);
        }

        // ---- Public control surface (wired to UI buttons) ----

        public void StartRecording()
        {
            IsRecording = true;
            _timeSinceLastEval = sampleInterval; // evaluate immediately
        }

        public void StopRecording() => IsRecording = false;

        public void ClearAll()
        {
            _samples.Clear();
            _grid.Clear();
            _renderHandles.Clear();
            _hitCounts.Clear();
            RecordingSeconds = 0f;
            DataRevision++;
            pointCloud?.Clear();
        }

        /// <summary>Min/max/average RSSI over all recorded points. False if empty.</summary>
        public bool TryGetRssiStats(out int min, out int max, out float avg)
        {
            min = 0; max = 0; avg = 0f;
            if (_samples.Count == 0) return false;
            min = int.MaxValue;
            max = int.MinValue;
            long sum = 0;
            foreach (var s in _samples)
            {
                if (s.rssiDbm < min) min = s.rssiDbm;
                if (s.rssiDbm > max) max = s.rssiDbm;
                sum += s.rssiDbm;
            }
            avg = sum / (float)_samples.Count;
            return true;
        }

        public string Export(string fixedName = null)
        {
            // Compute the stem here so the room-mesh OBJ shares it with CSV/JSON
            // and the three files load up congruently on the PC.
            string stem = fixedName ?? $"{SanitizedExportName()}_{DateTime.Now:yyyyMMdd_HHmmss}";
            string dir = DataExporter.Export(_samples, stem);
            if (roomMesh != null)
                roomMesh.ExportObj(stem);
            if (floorHeatmap != null)
                floorHeatmap.ExportPng(stem);
            return dir;
        }

        // User-chosen export prefix, stripped of filesystem-hostile characters;
        // falls back to "wifi" when empty.
        private string SanitizedExportName()
        {
            string name = (exportName ?? "").Trim();
            if (name.Length == 0) return "wifi";
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }

        private void RequestPermissions()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // Fine location is what un-redacts SSID/BSSID/RSSI on Android 13+.
            if (!Permission.HasUserAuthorizedPermission(Permission.FineLocation))
                Permission.RequestUserPermission(Permission.FineLocation);

            const string nearbyWifi = "android.permission.NEARBY_WIFI_DEVICES";
            if (!Permission.HasUserAuthorizedPermission(nearbyWifi))
                Permission.RequestUserPermission(nearbyWifi);
#endif
        }
    }
}
