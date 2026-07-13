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
        [Tooltip("Optional: exports the floor heatmap as PNG next to CSV/JSON.")]
        public FloorHeatmapRenderer floorHeatmap;
        [Tooltip("Optional name prefix for exports: <name>_<Datum>_<Uhrzeit>. " +
                 "Empty = wifi_<Datum>_<Uhrzeit>. Set via the panel's name field.")]
        public string exportName = "";
        [Tooltip("Optional: exports manually traced rooms as SVG+JSON next to CSV.")]
        public RoomTracer roomTracer;
        [Tooltip("Optional: exports placed drill lines (SVG crosses + JSON).")]
        public DrillPlanner drillPlanner;
        [Tooltip("Optional: exports the estimated AP position (_apfix.json).")]
        public ApLocator apLocator;
        [Tooltip("Optional: exports placed latency nodes (_ping.json).")]
        public PingTester pingTester;
        [Tooltip("Optional: exports browser-assisted local throughput nodes.")]
        public ThroughputTester throughputTester;
        [Tooltip("Optional: exports spatial notes (_notes.json).")]
        public SpatialNoteManager spatialNotes;
        [Tooltip("Optional: exports distance measurements (_measure.json).")]
        public DistanceMeasurer distanceMeasurer;
        [Tooltip("Optional: exports estimated wall attenuations (_walls.json).")]
        public WallLossEstimator wallLoss;
        [Tooltip("Optional: exports the coverage prediction (_coverage.png).")]
        public CoverageSimulator coverage;
        [Tooltip("Optional: exports roaming events (_roam.json).")]
        public RoamingTracker roaming;
        [Tooltip("Optional: multi-storey manager — tags each point with its floor.")]
        public FloorManager floorManager;

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

        /// <summary>One access point seen in the data (unique BSSID).</summary>
        [Serializable]
        public class BssidInfo
        {
            public string bssid;
            public string ssid;
            public int frequencyMhz;
            public int sampleCount;
        }

        public bool IsRecording { get; private set; }
        public int SampleCount => _samples.Count;
        public int LastRssi { get; private set; } = -127;
        public string LastSsid { get; private set; } = "";
        /// <summary>BSSID of the currently connected AP ("" while unknown) —
        /// polled every frame like LastRssi (roaming detection).</summary>
        public string LastBssid { get; private set; } = "";
        /// <summary>Channel frequency of the connected AP (MHz, 0 unknown).</summary>
        public int LastFrequency { get; private set; }
        /// <summary>All access points (unique BSSIDs) seen in the recorded
        /// samples, in first-seen order — feeds the per-AP heatmap filter.</summary>
        public IReadOnlyList<BssidInfo> KnownBssids => _bssids;
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
        private readonly List<BssidInfo> _bssids = new List<BssidInfo>();
        private readonly Dictionary<string, BssidInfo> _bssidByKey =
            new Dictionary<string, BssidInfo>();

        // Spatial hash grid for O(1) nearest-point lookup. Cell size == spacing,
        // so any point within minPointSpacing lives in one of the 27 neighbours.
        private readonly Dictionary<Vector3Int, List<int>> _grid =
            new Dictionary<Vector3Int, List<int>>();
        // Parallel to _samples: render handle and measurement count per point.
        private readonly List<int> _renderHandles = new List<int>();
        private readonly List<int> _hitCounts = new List<int>();
        private float _timeSinceLastEval;
        private int _lastFloorVersion = -1;

        private void Start()
        {
            RequestPermissions();
            if (headAnchor == null && Camera.main != null)
                headAnchor = Camera.main.transform; // editor fallback
        }

        private void Update()
        {
            // Apply active-floor changes to the point cloud filter (view storey).
            if (floorManager != null && pointCloud != null &&
                floorManager.Version != _lastFloorVersion)
            {
                _lastFloorVersion = floorManager.Version;
                pointCloud.SetFloorFilter(floorManager.ActiveIndex, floorManager.ShowAll);
            }

            // Always poll RSSI for the live readout, even when not recording.
            WifiInfo wifi = WifiReader.Read();
            if (wifi.valid)
            {
                PushRssi(wifi.rssiDbm);
                LastRssi = wifi.rssiDbm;
                LastSsid = wifi.ssid;
                LastBssid = wifi.bssid ?? "";
                LastFrequency = wifi.frequencyMhz;
            }

            if (!IsRecording || headAnchor == null) return;
            RecordingSeconds += Time.deltaTime;

            _timeSinceLastEval += Time.deltaTime;
            if (_timeSinceLastEval < sampleInterval) return;
            _timeSinceLastEval = 0f;
            if (!wifi.valid) return;

            Vector3 pos = headAnchor.position;
            int rssi = SmoothedRssi();
            // Folding is BSSID-exact: after roaming, a revisited spot gets a
            // NEW point for the new AP instead of polluting the old AP's
            // running average — each BSSID keeps its own clean heatmap.
            int nearest = FindNearestWithin(pos, minPointSpacing, wifi.bssid);
            if (nearest < 0) RecordSample(pos, rssi, wifi); // new point
            else             UpdateSample(nearest, rssi);   // fold into existing
        }

        private void RecordSample(Vector3 pos, int rssi, WifiInfo wifi)
        {
            wifi.rssiDbm = rssi;
            // A point belongs to the storey directly below the headset.
            int floor = floorManager != null ? floorManager.RecordingFloor(pos.y) : 0;
            var sample = Sample.Create(pos, wifi, floor);

            int index = _samples.Count;
            _samples.Add(sample);
            _hitCounts.Add(1);
            AddToGrid(index, pos);
            RegisterBssid(in sample);

            int handle = pointCloud != null ? pointCloud.AddPoint(pos, rssi, floor) : -1;
            _renderHandles.Add(handle);
            DataRevision++;

            if (streamer != null)
                streamer.Send(sample);
        }

        /// <summary>
        /// Records one multi-AP SCAN result (foreign BSSID) at the given head
        /// position. Same pipeline as RecordSample — BSSID-exact folding,
        /// registry, revision, streaming — but WITHOUT a point-cloud sphere
        /// (handle −1): with a dozen visible networks the cloud would stack a
        /// dozen spheres per spot; scan data feeds heatmaps/minimap/exports.
        /// Ignored while not recording.
        /// </summary>
        public void AddScanSample(Vector3 pos, WifiInfo wifi)
        {
            if (!IsRecording || !wifi.valid || string.IsNullOrEmpty(wifi.bssid))
                return;

            int nearest = FindNearestWithin(pos, minPointSpacing, wifi.bssid);
            if (nearest >= 0)
            {
                UpdateSample(nearest, wifi.rssiDbm);
                return;
            }

            int floor = floorManager != null ? floorManager.RecordingFloor(pos.y) : 0;
            var sample = Sample.Create(pos, wifi, floor);
            int index = _samples.Count;
            _samples.Add(sample);
            _hitCounts.Add(1);
            AddToGrid(index, pos);
            RegisterBssid(in sample);
            _renderHandles.Add(-1); // no cloud sphere for scan-derived points
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

        // Index of the closest existing sample of the SAME BSSID within radius
        // r, or -1 if none (points of other APs at the same spot don't count —
        // their averages must stay unmixed).
        private int FindNearestWithin(Vector3 pos, float r, string bssid)
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
                    if (!string.Equals(s.bssid, bssid, StringComparison.Ordinal))
                        continue;
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

        // Keeps the unique-BSSID registry (per-AP heatmap filter) in sync.
        private void RegisterBssid(in Sample s)
        {
            if (string.IsNullOrEmpty(s.bssid)) return;
            if (!_bssidByKey.TryGetValue(s.bssid, out var info))
            {
                info = new BssidInfo
                {
                    bssid = s.bssid,
                    ssid = s.ssid,
                    frequencyMhz = s.frequencyMhz,
                };
                _bssidByKey[s.bssid] = info;
                _bssids.Add(info);
            }
            info.sampleCount++;
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
            _bssids.Clear();
            _bssidByKey.Clear();
            _rssiWindow.Clear();
            LastRssi = -127;
            LastSsid = "";
            LastBssid = "";
            LastFrequency = 0;
            _timeSinceLastEval = 0f;
            RecordingSeconds = 0f;
            DataRevision++;
            pointCloud?.Clear();
        }

        /// <summary>Starts from a genuinely blank in-memory survey while
        /// preserving previously exported timestamped scans. The rolling
        /// autosave is removed so a later app start cannot resurrect the old
        /// session by mistake. Recording remains stopped; the UI decides when
        /// to start the fresh scan.</summary>
        public void ResetSession()
        {
            StopRecording();
            ExclusiveTools.DeactivateOthers(null);

            // Clear the live consumers first. Empty tool packets emitted below
            // then become the new server snapshot for late browser clients.
            streamer?.SendRaw("{\"type\":\"clear\",\"scope\":\"session\"}");

            ClearAll();
            floorManager?.LoadFloors(null);
            floorHeatmap?.ResetForNewSession();
            roomTracer?.ResetForNewSession();
            drillPlanner?.ClearLines();
            pingTester?.ClearNodes();
            throughputTester?.ClearNodes();
            spatialNotes?.ClearNotes();
            distanceMeasurer?.ClearMeasurements();
            roaming?.ClearEvents();
            apLocator?.ClearEstimates();
            wallLoss?.ResetForNewSession();
            coverage?.ResetForNewSession();
            UndoStack.Clear();
            DataExporter.DeleteExportStem(ResumePrompt.AutosaveStem);

            Debug.Log("[SampleCollector] Neue leere Survey-Session angelegt.");
        }

        /// <summary>
        /// Replaces the whole in-memory session (session load / re-alignment).
        /// Rebuilds the point cloud and spatial hash; samples are NOT streamed
        /// here — the caller re-streams if needed.
        /// </summary>
        public void LoadSamples(IReadOnlyList<Sample> loaded)
        {
            StopRecording();
            ClearAll();
            // The pool is empty after Clear(), so raising the capacity now can
            // never collide with existing ring-buffer handles.
            if (pointCloud != null && loaded.Count + 1000 > pointCloud.maxPoints)
                pointCloud.maxPoints = loaded.Count + 1000;
            for (int i = 0; i < loaded.Count; i++)
            {
                var s = loaded[i];
                var pos = new Vector3(s.posX, s.posY, s.posZ);
                _samples.Add(s);
                _hitCounts.Add(1);
                AddToGrid(i, pos);
                RegisterBssid(in s);
                _renderHandles.Add(pointCloud != null
                    ? pointCloud.AddPoint(pos, s.rssiDbm, s.floor)
                    : -1);
            }
            DataRevision++; // one bump -> heatmap + AP locator rebuild once
            Debug.Log($"[SampleCollector] {loaded.Count} Samples geladen.");
        }

        /// <summary>
        /// Remaps the floor tags of already-recorded samples after a storey was
        /// INSERTED at <paramref name="atIndex"/> (e.g. a basement below the
        /// known floors): indices at/above the slot shifted by +1. Rebuilds the
        /// point cloud with the corrected tags. Subscribed to
        /// <see cref="FloorManager.FloorInserted"/> by SceneBootstrap.
        /// </summary>
        public void OnFloorInserted(int atIndex)
        {
            if (_samples.Count == 0) return;
            bool wasRecording = IsRecording;
            float seconds = RecordingSeconds;
            var moved = new List<Sample>(_samples);
            for (int i = 0; i < moved.Count; i++)
            {
                var s = moved[i];
                if (s.floor >= atIndex)
                {
                    s.floor++;
                    moved[i] = s;
                }
            }
            LoadSamples(moved); // rebuild cloud + spatial hash, one revision bump
            RecordingSeconds = seconds; // ClearAll inside LoadSamples reset it
            if (wasRecording) StartRecording();
            Debug.Log($"[SampleCollector] Etagen-Indizes ab {atIndex} remappt.");
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
            string dir = DataExporter.Export(_samples, stem,
                floorManager != null ? floorManager.Floors : null,
                floorHeatmap != null ? floorHeatmap.gridYawDegrees : 0f);
            byte[] heatmapPng = floorHeatmap != null
                ? floorHeatmap.ExportPng(stem)
                : null;
            var drills = drillPlanner != null && drillPlanner.Lines.Count > 0
                ? drillPlanner.Lines
                : null;
            if (roomTracer != null)
                roomTracer.Export(stem, drills); // SVG + JSON + extruded OBJ
            if (drills != null)
                DataExporter.ExportDrillJson(drills,
                    floorHeatmap != null ? floorHeatmap.gridYawDegrees : 0f, stem);

            // New analysis tools: each writes its own JSON if it has data.
            if (apLocator != null && apLocator.HasFix)
                DataExporter.ExportApFixJson(apLocator.FixPosition, apLocator.FixSsid,
                    apLocator.FixRssi, stem,
                    apLocator.HasFit ? apLocator.FixRadius : 0f,
                    apLocator.PathLossExponent, apLocator.PowerAt1mDbm,
                    apLocator.Estimates);
            if (roaming != null && roaming.Events.Count > 0)
                DataExporter.ExportRoamJson(roaming.Events, stem);
            if (pingTester != null && pingTester.Samples.Count > 0)
                DataExporter.ExportPingJson(pingTester.Samples, stem);
            if (throughputTester != null && throughputTester.Samples.Count > 0)
                DataExporter.ExportThroughputJson(throughputTester.Samples, stem);
            if (spatialNotes != null && spatialNotes.Notes.Count > 0)
                DataExporter.ExportNotesJson(spatialNotes.Notes, stem);
            if (distanceMeasurer != null && distanceMeasurer.Measurements.Count > 0)
                DataExporter.ExportMeasureJson(distanceMeasurer.Measurements, stem);
            if (wallLoss != null && apLocator != null && apLocator.HasFit)
            {
                wallLoss.Recompute(); // fresh estimate for the export
                if (wallLoss.Losses.Count > 0)
                {
                    DataExporter.ExportWallsJson(wallLoss.Losses,
                        apLocator.PathLossExponent, apLocator.PowerAt1mDbm, stem);
                    // Floor plan with the dB values written on the walls —
                    // one per storey (mixed storeys are unreadable in 2D).
                    if (roomTracer != null && roomTracer.Rooms.Count > 0)
                    {
                        float yaw = floorHeatmap != null
                            ? floorHeatmap.gridYawDegrees : 0f;
                        if (floorManager != null && floorManager.Floors.Count > 1)
                            foreach (var level in floorManager.Floors)
                                DataExporter.ExportWallsSvg(roomTracer.Rooms,
                                    yaw, wallLoss.Losses, stem,
                                    level.baseY, "_" + level.label);
                        else
                            DataExporter.ExportWallsSvg(roomTracer.Rooms,
                                yaw, wallLoss.Losses, stem);
                    }
                }
            }
            coverage?.ExportPng(stem); // null-op without rooms or AP source

            // Combined view: heatmap as background, floor plan on top — both
            // layers share the grid-space georeference. The heatmap PNG is the
            // ACTIVE storey's, so only that storey's rooms/drills go on top.
            if (heatmapPng != null && roomTracer != null &&
                roomTracer.Rooms.Count > 0 &&
                floorHeatmap.TryGetMapInfo(out Vector2 gridMin, out float cell,
                    out int texW, out int texH))
            {
                IReadOnlyList<RoomOutline> combinedRooms = roomTracer.Rooms;
                IReadOnlyList<DrillLine> combinedDrills = drills;
                if (floorManager != null && floorManager.Floors.Count > 1)
                {
                    float baseY = floorManager.ActiveBaseY;
                    var roomList = new List<RoomOutline>();
                    foreach (var room in roomTracer.Rooms)
                        if (Mathf.Abs(room.floorY - baseY) <= 1f)
                            roomList.Add(room);
                    combinedRooms = roomList;
                    if (drills != null)
                    {
                        var drillList = new List<DrillLine>();
                        foreach (var d in drills)
                            if (d.entry.y >= baseY - 0.3f && d.entry.y <= baseY + 3.2f)
                                drillList.Add(d);
                        combinedDrills = drillList.Count > 0 ? drillList : null;
                    }
                }
                if (combinedRooms.Count > 0)
                    DataExporter.ExportCombinedSvg(heatmapPng, gridMin, cell,
                        texW, texH, floorHeatmap.gridYawDegrees,
                        combinedRooms, stem, combinedDrills);
            }

            // Survey report LAST — it embeds the files written above. The
            // rolling autosave only exists for session recovery; rebuilding a
            // large self-contained HTML report every 30 seconds wastes I/O and
            // can race with dashboard downloads.
            if (!string.Equals(fixedName, "wifi_autosave",
                    StringComparison.Ordinal))
                ReportGenerator.Write(this, stem);
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
