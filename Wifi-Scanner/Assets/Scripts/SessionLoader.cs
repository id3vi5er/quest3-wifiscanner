using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace WifiScanner
{
    /// <summary>
    /// Loads a saved scan (&lt;stem&gt;.json samples + the optional sidecars
    /// _rooms/_drill/_ping/_notes/_measure) back into the running app: samples,
    /// traced rooms (incl. openings and heights), drill lines, ping nodes,
    /// spatial notes, measurements, floor levels and grid yaw. The panel cycles
    /// through <see cref="Stems"/> and calls <see cref="LoadCurrent"/>.
    ///
    /// Loaded world coordinates rarely match the CURRENT tracking origin (the
    /// Quest re-anchors on every start) — <see cref="ApplySessionTransform"/>
    /// applies the rigid transform computed by the ScanAligner to everything
    /// (samples, rooms, tool elements, floors, grid yaw). Created and wired by
    /// SceneBootstrap.
    /// </summary>
    public class SessionLoader : MonoBehaviour
    {
        [Header("Wired by SceneBootstrap")]
        public SampleCollector collector;
        public RoomTracer roomTracer;         // optional
        public FloorManager floorManager;     // optional
        public FloorHeatmapRenderer floor;    // optional
        public NetworkStreamer streamer;      // optional (re-stream after load)
        public DrillPlanner drillPlanner;     // optional
        public PingTester pingTester;         // optional
        public ThroughputTester throughputTester; // optional
        public SpatialNoteManager spatialNotes;   // optional
        public DistanceMeasurer distanceMeasurer; // optional

        [Tooltip("Samples re-streamed per frame after a load (UDP/WebSocket).")]
        public int restreamChunk = 250;

        private readonly List<string> _stems = new List<string>();
        private int _stemIndex;
        private Coroutine _restream;

        public IReadOnlyList<string> Stems => _stems;
        public string CurrentStem =>
            _stems.Count > 0 ? _stems[Mathf.Clamp(_stemIndex, 0, _stems.Count - 1)] : null;

        private void Start()
        {
            RefreshStems();
        }

        /// <summary>Re-lists the scans on disk, keeping the selection if possible.</summary>
        public void RefreshStems()
        {
            string selected = CurrentStem;
            _stems.Clear();
            _stems.AddRange(DataExporter.ListScanStems());
            _stemIndex = Mathf.Max(0, _stems.IndexOf(selected));
        }

        public void CycleStem()
        {
            RefreshStems();
            if (_stems.Count == 0) return;
            _stemIndex = (_stemIndex + 1) % _stems.Count;
        }

        /// <summary>Loads a specific scan by stem (e.g. the autosave from the
        /// resume prompt). False when the stem does not exist on disk.</summary>
        public bool LoadStem(string stem)
        {
            RefreshStems();
            int index = _stems.IndexOf(stem);
            if (index < 0) return false;
            _stemIndex = index;
            return LoadCurrent();
        }

        /// <summary>Replaces the running session with the selected scan.</summary>
        public bool LoadCurrent()
        {
            string stem = CurrentStem;
            if (stem == null || collector == null) return false;

            if (!DataExporter.TryLoadSamples(stem, out var samples,
                    out var floors, out float yaw))
                return false;

            ExclusiveTools.DeactivateOthers(null); // release whichever tool is active
            collector.StopRecording();
            // Hard boundary BEFORE sidecars are loaded: their Load APIs emit
            // the fresh rooms/tool packets that must survive the reset.
            streamer?.SendRaw("{\"type\":\"clear\",\"scope\":\"session\"}");

            // Guard against files whose floor indices exceed the floor list.
            int floorCount = floors != null ? floors.Count : 1;
            for (int i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                s.floor = Mathf.Clamp(s.floor, 0, floorCount - 1);
                samples[i] = s;
            }

            // Floors BEFORE samples: the point-cloud floor filter re-applies on
            // the FloorManager version bump and must see the new levels.
            floorManager?.LoadFloors(floors);
            collector.LoadSamples(samples);

            if (roomTracer != null)
            {
                if (DataExporter.TryLoadRooms(stem, out var rooms, out float roomsYaw))
                {
                    yaw = roomsYaw;
                    roomTracer.LoadRooms(rooms);
                }
                else
                {
                    // No sidecar: still clear rooms left over from before.
                    roomTracer.LoadRooms(new List<RoomOutline>());
                }
            }

            // Tool sidecars: load what exists, clear leftovers from before
            // (the Load APIs treat null as "clear") — same policy as the rooms.
            if (drillPlanner != null)
                drillPlanner.LoadLines(
                    DataExporter.TryLoadDrill(stem, out var drills) ? drills : null);
            if (pingTester != null)
                pingTester.LoadNodes(
                    DataExporter.TryLoadPing(stem, out var pings) ? pings : null);
            if (throughputTester != null)
                throughputTester.LoadNodes(
                    DataExporter.TryLoadThroughput(stem, out var speeds) ? speeds : null);
            if (spatialNotes != null)
                spatialNotes.LoadNotes(
                    DataExporter.TryLoadNotes(stem, out var notes) ? notes : null);
            if (distanceMeasurer != null)
                distanceMeasurer.LoadMeasurements(
                    DataExporter.TryLoadMeasure(stem, out var ms) ? ms : null);

            // Undo entries from before the swap would restore foreign data.
            UndoStack.Clear();

            floor?.SetGridYaw(yaw);
            RestartRestream();
            Debug.Log($"[SessionLoader] Scan '{stem}' geladen: " +
                      $"{samples.Count} Punkte, " +
                      $"{(roomTracer != null ? roomTracer.Rooms.Count : 0)} Raum/Räume, " +
                      $"{floorCount} Etage(n).");
            return true;
        }

        /// <summary>
        /// Rotates the whole session by <paramref name="deltaYawDegrees"/>
        /// around world Y, then translates by <paramref name="t"/> — samples,
        /// rooms (corners, floorY, opening heights), floor levels and grid yaw.
        /// Runs the same load pipeline with transformed copies.
        /// </summary>
        public void ApplySessionTransform(float deltaYawDegrees, Vector3 t)
        {
            if (collector == null) return;
            streamer?.SendRaw("{\"type\":\"clear\",\"scope\":\"session\"}");
            Quaternion r = Quaternion.Euler(0f, deltaYawDegrees, 0f);

            var moved = new List<Sample>(collector.Samples.Count);
            foreach (var s in collector.Samples)
            {
                Vector3 p = r * new Vector3(s.posX, s.posY, s.posZ) + t;
                var c = s;
                c.posX = p.x;
                c.posY = p.y;
                c.posZ = p.z;
                moved.Add(c);
            }
            collector.LoadSamples(moved);

            if (roomTracer != null)
            {
                // The outline objects survive LoadRooms' internal ClearRooms —
                // only the list itself is rebuilt.
                var rooms = new List<RoomOutline>(roomTracer.Rooms);
                foreach (var room in rooms)
                {
                    for (int i = 0; i < room.cornersXZ.Count; i++)
                    {
                        var c = room.cornersXZ[i];
                        Vector3 p = r * new Vector3(c.x, 0f, c.y);
                        room.cornersXZ[i] = new Vector2(p.x + t.x, p.z + t.z);
                    }
                    room.floorY += t.y;
                    // startAlong/endAlong are along-edge distances and thus
                    // invariant; only the absolute heights shift.
                    if (room.openings != null)
                        foreach (var op in room.openings)
                        {
                            op.bottomY += t.y;
                            op.topY += t.y;
                        }
                }
                roomTracer.LoadRooms(rooms);
            }

            // Tool elements ride along with the same rigid transform. Lengths
            // (drill depth, measurement metres) are invariant under it.
            if (drillPlanner != null && drillPlanner.Lines.Count > 0)
            {
                var lines = new List<DrillLine>(drillPlanner.Lines.Count);
                foreach (var l in drillPlanner.Lines)
                    lines.Add(new DrillLine
                    {
                        entry = r * l.entry + t,
                        direction = r * l.direction,
                        depth = l.depth,
                    });
                drillPlanner.LoadLines(lines);
            }

            if (pingTester != null && pingTester.NodeCount > 0)
            {
                var nodes = new List<PingSample>(pingTester.NodeCount);
                foreach (var n in pingTester.Samples)
                {
                    Vector3 p = r * new Vector3(n.posX, n.posY, n.posZ) + t;
                    nodes.Add(new PingSample
                    {
                        posX = p.x, posY = p.y, posZ = p.z,
                        latencyMs = n.latencyMs,
                    });
                }
                pingTester.LoadNodes(nodes);
            }

            if (throughputTester != null && throughputTester.NodeCount > 0)
            {
                var nodes = new List<ThroughputSample>(throughputTester.NodeCount);
                foreach (var n in throughputTester.Samples)
                {
                    Vector3 p = r * new Vector3(n.posX, n.posY, n.posZ) + t;
                    nodes.Add(new ThroughputSample
                    {
                        timestampMs = n.timestampMs,
                        posX = p.x, posY = p.y, posZ = p.z, floor = n.floor,
                        questToBrowserMbps = n.questToBrowserMbps,
                        browserToQuestMbps = n.browserToQuestMbps,
                        latencyMs = n.latencyMs, client = n.client,
                        ssid = n.ssid, bssid = n.bssid,
                        frequencyMhz = n.frequencyMhz,
                    });
                }
                throughputTester.LoadNodes(nodes);
            }

            if (spatialNotes != null && spatialNotes.Notes.Count > 0)
            {
                var notes = new List<SpatialNote>(spatialNotes.Notes.Count);
                foreach (var n in spatialNotes.Notes)
                {
                    Vector3 p = r * new Vector3(n.posX, n.posY, n.posZ) + t;
                    notes.Add(new SpatialNote
                    {
                        posX = p.x, posY = p.y, posZ = p.z,
                        type = n.type, text = n.text,
                    });
                }
                spatialNotes.LoadNotes(notes);
            }

            if (distanceMeasurer != null && distanceMeasurer.Measurements.Count > 0)
            {
                var ms = new List<Measurement>(distanceMeasurer.Measurements.Count);
                foreach (var m in distanceMeasurer.Measurements)
                {
                    Vector3 a = r * new Vector3(m.ax, m.ay, m.az) + t;
                    Vector3 b = r * new Vector3(m.bx, m.by, m.bz) + t;
                    ms.Add(new Measurement
                    {
                        ax = a.x, ay = a.y, az = a.z,
                        bx = b.x, by = b.y, bz = b.z,
                        meters = m.meters,
                    });
                }
                distanceMeasurer.LoadMeasurements(ms);
            }

            if (floorManager != null)
            {
                var shifted = new List<FloorLevel>();
                foreach (var f in floorManager.Floors)
                    shifted.Add(new FloorLevel { baseY = f.baseY + t.y, label = f.label });
                floorManager.LoadFloors(shifted);
            }

            if (floor != null)
                floor.SetGridYaw(floor.gridYawDegrees + deltaYawDegrees);

            RestartRestream();
            Debug.Log($"[SessionLoader] Session ausgerichtet: dYaw {deltaYawDegrees:F1}°, " +
                      $"dXZ ({t.x:F2}, {t.z:F2}) m, dY {t.y:F2} m.");
        }

        // Pushes freshly loaded/transformed samples after the caller already
        // emitted the hard clear and the individual Load APIs broadcast their
        // new sidecar state.
        private void RestartRestream()
        {
            if (streamer == null) return;
            if (_restream != null) StopCoroutine(_restream);
            _restream = StartCoroutine(Restream());
        }

        private IEnumerator Restream()
        {
            var samples = collector.Samples;
            for (int i = 0; i < samples.Count; i++)
            {
                streamer.Send(samples[i]);
                if ((i + 1) % Mathf.Max(1, restreamChunk) == 0)
                    yield return null; // spread the burst over frames
            }
            _restream = null;
        }
    }
}
