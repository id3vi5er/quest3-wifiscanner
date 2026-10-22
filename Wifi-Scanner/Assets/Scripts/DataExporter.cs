using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace WifiScanner
{
    /// <summary>
    /// Writes the collected samples to CSV and JSON under
    /// Application.persistentDataPath. Retrieve from the headset with:
    ///   adb pull /sdcard/Android/data/&lt;package&gt;/files/
    /// </summary>
    public static class DataExporter
    {
        [Serializable]
        private class SampleList
        {
            public List<Sample> samples;
            // Both absent in pre-multi-storey files: floors stays null,
            // gridYawDegrees 0 — the loaders fall back to defaults.
            public float gridYawDegrees;
            public List<FloorLevel> floors;
        }

        // Sidecar suffixes that share a scan's stem — everything that is NOT
        // the loadable samples file itself.
        private static readonly string[] SidecarSuffixes =
        {
            "_rooms", "_drill", "_apfix", "_ping", "_notes", "_measure",
            "_heatmap", "_walls", "_report", "_coverage", "_roam",
            "_throughput", "_survey",
        };

        /// <summary>
        /// Exports both CSV and JSON. Returns the directory written to.
        /// If <paramref name="fixedName"/> is given, that exact stem is used and
        /// re-exporting overwrites it (for autosave). Otherwise a timestamped
        /// pair is written. Floor levels and grid yaw ride along in the JSON so
        /// a session can be fully restored later.
        /// </summary>
        public static string Export(IReadOnlyList<Sample> samples, string fixedName = null,
            IReadOnlyList<FloorLevel> floors = null, float gridYawDegrees = 0f)
        {
            string stem = fixedName ?? $"wifi_{DateTime.Now:yyyyMMdd_HHmmss}";
            string dir = Application.persistentDataPath;

            string csvPath = Path.Combine(dir, $"{stem}.csv");
            string jsonPath = Path.Combine(dir, $"{stem}.json");

            var sb = new StringBuilder();
            sb.AppendLine(Sample.CsvHeader());
            foreach (var s in samples)
                sb.AppendLine(s.ToCsvRow());
            File.WriteAllText(csvPath, sb.ToString());

            var wrapper = new SampleList
            {
                samples = new List<Sample>(samples),
                gridYawDegrees = gridYawDegrees,
                floors = floors != null ? new List<FloorLevel>(floors) : null,
            };
            File.WriteAllText(jsonPath, JsonUtility.ToJson(wrapper, true));

            Debug.Log($"[DataExporter] Wrote {samples.Count} samples to {csvPath} and {jsonPath}");
            return dir;
        }

        /// <summary>
        /// Reads a scan's samples JSON back. <paramref name="floors"/> is null
        /// for files written before multi-storey support.
        /// </summary>
        public static bool TryLoadSamples(string stem, out List<Sample> samples,
            out List<FloorLevel> floors, out float gridYawDegrees)
        {
            samples = null;
            floors = null;
            gridYawDegrees = 0f;
            string path = Path.Combine(Application.persistentDataPath, $"{stem}.json");
            if (!File.Exists(path))
                return TryLoadSamplesCsv(stem, out samples);
            try
            {
                var wrapper = JsonUtility.FromJson<SampleList>(File.ReadAllText(path));
                if (wrapper?.samples == null) return false;
                samples = wrapper.samples;
                for (int i = 0; i < samples.Count; i++)
                {
                    var sample = samples[i];
                    sample.NormalizeLegacyMetadata();
                    samples[i] = sample;
                }
                floors = wrapper.floors != null && wrapper.floors.Count > 0
                    ? wrapper.floors : null;
                gridYawDegrees = wrapper.gridYawDegrees;
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DataExporter] Scan '{stem}' nicht lesbar: {e.Message}");
                return false;
            }
        }

        // Header-based fallback keeps standalone pre-metadata CSV sessions
        // importable. Unknown columns are ignored; missing aggregation fields
        // are normalized to one measurement with unknown variance/quality.
        private static bool TryLoadSamplesCsv(string stem, out List<Sample> samples)
        {
            samples = null;
            string path = Path.Combine(Application.persistentDataPath, $"{stem}.csv");
            if (!File.Exists(path)) return false;
            try
            {
                string[] lines = File.ReadAllLines(path);
                if (lines.Length < 2) return false;
                var headers = SplitCsvLine(lines[0]);
                var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < headers.Count; i++) index[headers[i]] = i;
                var loaded = new List<Sample>();
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                for (int line = 1; line < lines.Length; line++)
                {
                    if (string.IsNullOrWhiteSpace(lines[line])) continue;
                    var v = SplitCsvLine(lines[line]);
                    string Get(string name) => index.TryGetValue(name, out int i) &&
                        i < v.Count ? v[i] : "";
                    long.TryParse(Get("timestampMs"), out long timestamp);
                    float.TryParse(Get("posX"), System.Globalization.NumberStyles.Float, inv, out float x);
                    float.TryParse(Get("posY"), System.Globalization.NumberStyles.Float, inv, out float y);
                    float.TryParse(Get("posZ"), System.Globalization.NumberStyles.Float, inv, out float z);
                    int.TryParse(Get("rssiDbm"), out int rssi);
                    int.TryParse(Get("linkSpeedMbps"), out int speed);
                    int.TryParse(Get("frequencyMhz"), out int frequency);
                    int.TryParse(Get("floor"), out int floor);
                    int.TryParse(Get("sampleCount"), out int count);
                    float.TryParse(Get("rssiMeanDbm"), System.Globalization.NumberStyles.Float, inv, out float mean);
                    float.TryParse(Get("rssiM2"), System.Globalization.NumberStyles.Float, inv, out float m2);
                    long.TryParse(Get("firstTimestampMs"), out long first);
                    long.TryParse(Get("lastTimestampMs"), out long last);
                    float tracking = -1f, alignment = -1f;
                    if (!string.IsNullOrEmpty(Get("trackingQuality")))
                        float.TryParse(Get("trackingQuality"), System.Globalization.NumberStyles.Float, inv, out tracking);
                    if (!string.IsNullOrEmpty(Get("alignmentQuality")))
                        float.TryParse(Get("alignmentQuality"), System.Globalization.NumberStyles.Float, inv, out alignment);
                    var sample = new Sample
                    {
                        timestampMs=timestamp,posX=x,posY=y,posZ=z,rssiDbm=rssi,
                        linkSpeedMbps=speed,frequencyMhz=frequency,ssid=Get("ssid"),
                        bssid=Get("bssid"),floor=floor,sampleCount=count,
                        rssiMeanDbm=mean,rssiM2=m2,firstTimestampMs=first,
                        lastTimestampMs=last,trackingQuality=tracking,
                        alignmentQuality=alignment,
                    };
                    sample.NormalizeLegacyMetadata();
                    loaded.Add(sample);
                }
                samples = loaded;
                return loaded.Count > 0;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DataExporter] CSV '{stem}' nicht lesbar: {e.Message}");
                return false;
            }
        }

        private static List<string> SplitCsvLine(string line)
        {
            var fields = new List<string>();
            var current = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                    { current.Append('"'); i++; }
                    else quoted = !quoted;
                }
                else if (c == ',' && !quoted)
                { fields.Add(current.ToString()); current.Clear(); }
                else current.Append(c);
            }
            fields.Add(current.ToString());
            return fields;
        }

        /// <summary>Reads a scan's rooms sidecar; false when absent/corrupt.</summary>
        public static bool TryLoadRooms(string stem, out List<RoomOutline> rooms,
            out float gridYawDegrees)
        {
            rooms = null;
            gridYawDegrees = 0f;
            string path = Path.Combine(Application.persistentDataPath,
                $"{stem}_rooms.json");
            if (!File.Exists(path)) return false;
            try
            {
                var wrapper = JsonUtility.FromJson<RoomList>(File.ReadAllText(path));
                if (wrapper?.rooms == null) return false;
                rooms = wrapper.rooms;
                gridYawDegrees = wrapper.gridYawDegrees;
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DataExporter] Räume '{stem}' nicht lesbar: {e.Message}");
                return false;
            }
        }

        // Shared reader for the small tool sidecars ({stem}_{suffix}.json).
        // Null when the file is absent or corrupt.
        private static T ReadSidecar<T>(string stem, string suffix) where T : class
        {
            string path = Path.Combine(Application.persistentDataPath,
                $"{stem}_{suffix}.json");
            if (!File.Exists(path)) return null;
            try
            {
                return JsonUtility.FromJson<T>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DataExporter] Sidecar '{stem}_{suffix}' " +
                                 $"nicht lesbar: {e.Message}");
                return null;
            }
        }

        /// <summary>True when a loadable scan with this stem exists; also
        /// returns its last write time (local) for display.</summary>
        public static bool TryGetScanInfo(string stem, out DateTime writtenLocal)
        {
            writtenLocal = default;
            string path = Path.Combine(Application.persistentDataPath, $"{stem}.json");
            if (!File.Exists(path))
                path = Path.Combine(Application.persistentDataPath, $"{stem}.csv");
            if (!File.Exists(path)) return false;
            writtenLocal = File.GetLastWriteTime(path);
            return true;
        }

        /// <summary>Reads a scan's drill-lines sidecar; false when absent/corrupt.</summary>
        public static bool TryLoadDrill(string stem, out List<DrillLine> lines)
        {
            lines = ReadSidecar<DrillList>(stem, "drill")?.lines;
            return lines != null;
        }

        /// <summary>Reads a scan's ping-nodes sidecar; false when absent/corrupt.</summary>
        public static bool TryLoadPing(string stem, out List<PingSample> nodes)
        {
            nodes = ReadSidecar<PingList>(stem, "ping")?.nodes;
            return nodes != null;
        }

        public static bool TryLoadThroughput(string stem,
            out List<ThroughputSample> nodes)
        {
            nodes = ReadSidecar<ThroughputList>(stem, "throughput")?.nodes;
            return nodes != null;
        }

        /// <summary>Reads a scan's spatial-notes sidecar; false when absent/corrupt.</summary>
        public static bool TryLoadNotes(string stem, out List<SpatialNote> notes)
        {
            notes = ReadSidecar<NotesList>(stem, "notes")?.notes;
            return notes != null;
        }

        /// <summary>Reads a scan's measurements sidecar; false when absent/corrupt.</summary>
        public static bool TryLoadMeasure(string stem, out List<Measurement> measurements)
        {
            measurements = ReadSidecar<MeasureList>(stem, "measure")?.measurements;
            return measurements != null;
        }

        /// <summary>
        /// All loadable scan stems in persistentDataPath (sidecar JSONs like
        /// _rooms/_drill excluded), newest first. wifi_autosave is included on
        /// purpose (crash recovery).
        /// </summary>
        public static List<string> ListScanStems()
        {
            var stems = new List<string>();
            try
            {
                var files = new List<string>(Directory.GetFiles(
                    Application.persistentDataPath, "*.json"));
                foreach (var csv in Directory.GetFiles(
                    Application.persistentDataPath, "*.csv"))
                {
                    string json = Path.ChangeExtension(csv, ".json");
                    if (!File.Exists(json)) files.Add(csv);
                }
                files.Sort((a, b) =>
                    File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
                foreach (var f in files)
                {
                    string stem = Path.GetFileNameWithoutExtension(f);
                    // Per-storey heatmap metas (_heatmap_EG, _heatmap_OG1, …)
                    // carry a floor label after the suffix — EndsWith misses them.
                    bool sidecar = stem.Contains("_heatmap_") ||
                                   stem.Contains("_survey_");
                    foreach (var suffix in SidecarSuffixes)
                        if (stem.EndsWith(suffix, StringComparison.Ordinal))
                        { sidecar = true; break; }
                    if (!sidecar) stems.Add(stem);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DataExporter] Scan-Liste fehlgeschlagen: {e.Message}");
            }
            return stems;
        }

        /// <summary>
        /// Extrudes the traced rooms (floor polygon + pulled-up height) into a
        /// single Wavefront OBJ: one wall quad (two triangles) per polygon
        /// edge. Rooms without a height are skipped. Vertices are world-space
        /// Unity coordinates — deliberately NOT converted to OBJ's right-handed
        /// convention, so they are congruent with the posX/Y/Z columns of the
        /// WiFi samples when analysed together on the PC. Floor/ceiling caps
        /// are omitted (rooms may be concave); cap them in Blender if needed.
        /// </summary>
        public static string ExportRoomsObj(IReadOnlyList<RoomOutline> rooms, string stem)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("# Quest3 WiFi-Scanner traced rooms (extruded walls)");
            sb.AppendLine("# Right-handed: world X is negated so the mesh is not " +
                          "mirrored in standard 3D tools. To overlay the wifi CSV, " +
                          "negate its posX (posY/posZ unchanged).");

            int vertexOffset = 1; // OBJ indices are 1-based
            int roomIndex = 0;
            int exported = 0;
            foreach (var room in rooms)
            {
                roomIndex++;
                int n = room.cornersXZ.Count;
                if (n < 3 || room.height <= 0.01f) continue;
                exported++;
                sb.AppendLine($"o room_{roomIndex}");

                float y0 = room.floorY;
                float y1 = room.floorY + room.height;
                // Each wall edge is emitted as one or more rectangles (own
                // vertices) so door/window openings become real holes: solid
                // columns beside a hole plus the sill strip below and the lintel
                // strip above it.
                for (int i = 0; i < n; i++)
                {
                    Vector2 a = room.cornersXZ[i];
                    Vector2 b = room.cornersXZ[(i + 1) % n];
                    Vector2 e = b - a;
                    float len = e.magnitude;
                    if (len < 1e-4f) continue;
                    Vector2 dir = e / len;

                    var ops = new List<WallOpening>();
                    if (room.openings != null)
                        foreach (var op in room.openings)
                            if (op.edgeIndex == i) ops.Add(op);
                    ops.Sort((p, q) => p.startAlong.CompareTo(q.startAlong));

                    float cursor = 0f;
                    foreach (var op in ops)
                    {
                        float u0 = Mathf.Clamp(op.startAlong, 0f, len);
                        float u1 = Mathf.Clamp(op.endAlong, 0f, len);
                        if (u1 <= cursor) continue;
                        u0 = Mathf.Max(u0, cursor);
                        float ob = Mathf.Clamp(op.bottomY, y0, y1);
                        float ot = Mathf.Clamp(op.topY, y0, y1);
                        if (u0 > cursor)
                            EmitWallRect(sb, inv, a, dir, cursor, u0, y0, y1, ref vertexOffset);
                        if (ob > y0)
                            EmitWallRect(sb, inv, a, dir, u0, u1, y0, ob, ref vertexOffset);
                        if (ot < y1)
                            EmitWallRect(sb, inv, a, dir, u0, u1, ot, y1, ref vertexOffset);
                        cursor = Mathf.Max(cursor, u1);
                    }
                    if (cursor < len)
                        EmitWallRect(sb, inv, a, dir, cursor, len, y0, y1, ref vertexOffset);
                }
            }

            if (exported == 0)
            {
                Debug.Log("[DataExporter] No rooms with height — OBJ skipped.");
                return null;
            }

            string path = Path.Combine(Application.persistentDataPath, $"{stem}_rooms.obj");
            File.WriteAllText(path, sb.ToString());
            Debug.Log($"[DataExporter] Wrote {exported} extruded room(s) to {path}");
            return path;
        }

        // One wall rectangle [u0,u1] (metres along the edge) × [yb,yt] as its own
        // 4 vertices + 2 triangles. Vertices are Unity world coords (matches CSV).
        private static void EmitWallRect(StringBuilder sb,
            System.Globalization.CultureInfo inv, Vector2 a, Vector2 dir,
            float u0, float u1, float yb, float yt, ref int vBase)
        {
            Vector2 p0 = a + dir * u0;
            Vector2 p1 = a + dir * u1;
            // X negated (left-handed Unity -> right-handed OBJ); winding reversed
            // to keep the outward normal after the mirror.
            AppendVertex(sb, inv, -p0.x, yb, p0.y);
            AppendVertex(sb, inv, -p1.x, yb, p1.y);
            AppendVertex(sb, inv, -p1.x, yt, p1.y);
            AppendVertex(sb, inv, -p0.x, yt, p0.y);
            sb.AppendLine($"f {vBase} {vBase + 2} {vBase + 1}");
            sb.AppendLine($"f {vBase} {vBase + 3} {vBase + 2}");
            vBase += 4;
        }

        private static void AppendVertex(StringBuilder sb,
            System.Globalization.CultureInfo inv, float x, float y, float z)
        {
            sb.Append("v ").Append(x.ToString("F4", inv))
              .Append(' ').Append(y.ToString("F4", inv))
              .Append(' ').AppendLine(z.ToString("F4", inv));
        }

        [Serializable]
        private class RoomList
        {
            public float gridYawDegrees;
            public List<RoomOutline> rooms;
        }

        /// <summary>
        /// Writes traced rooms as a real-scale SVG floor plan (1 SVG user unit
        /// = 1 mm) with each wall labelled with its length in metres. With
        /// multiple storeys the caller invokes this once PER STOREY with a
        /// "_EG"/"_OG1"-style <paramref name="fileSuffix"/> and only that
        /// storey's rooms — one readable plan per floor. The raw corners JSON
        /// (ALL rooms) rides along unless <paramref name="writeJson"/> is off.
        /// Corners are rotated into grid space so walls are axis-parallel
        /// after the wall calibration.
        /// </summary>
        public static string ExportRoomsSvg(IReadOnlyList<RoomOutline> rooms,
            float gridYawDegrees, string stem,
            IReadOnlyList<DrillLine> drills = null, string fileSuffix = "",
            bool writeJson = true)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string dir = Application.persistentDataPath;

            var roomsMm = RoomsToGridMm(rooms, gridYawDegrees,
                out Vector2 min, out Vector2 max);

            const float margin = 250f; // mm
            float w = max.x - min.x + 2f * margin;
            float h = max.y - min.y + 2f * margin;
            Vector2 offset = new Vector2(margin - min.x, margin - min.y);

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine(string.Format(inv,
                "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{0:F0}mm\" " +
                "height=\"{1:F0}mm\" viewBox=\"0 0 {0:F0} {1:F0}\">", w, h));
            sb.AppendLine(string.Format(inv,
                "  <!-- Quest3 WiFi-Scanner Grundriss{3} | {0:yyyy-MM-dd HH:mm} | " +
                "1 unit = 1 mm | gridYaw {1:F1} deg | {2} Raum/Räume -->",
                DateTime.Now, gridYawDegrees, rooms.Count,
                fileSuffix.Length > 0 ? " Etage " + fileSuffix.TrimStart('_') : ""));
            AppendRoomShapes(sb, rooms, roomsMm, offset, haloText: false);
            AppendDrillMarks(sb, drills, gridYawDegrees, offset);
            sb.AppendLine("</svg>");

            string svgPath = Path.Combine(dir, $"{stem}_rooms{fileSuffix}.svg");
            File.WriteAllText(svgPath, sb.ToString());

            if (writeJson)
                ExportRoomsJson(rooms, gridYawDegrees, stem);

            Debug.Log($"[DataExporter] Wrote {rooms.Count} room outline(s) to {svgPath}");
            return svgPath;
        }

        /// <summary>All room outlines (every storey) as JSON — the loadable
        /// data file behind the per-storey SVG plans.</summary>
        public static string ExportRoomsJson(IReadOnlyList<RoomOutline> rooms,
            float gridYawDegrees, string stem)
        {
            var wrapper = new RoomList
            {
                gridYawDegrees = gridYawDegrees,
                rooms = new List<RoomOutline>(rooms),
            };
            string path = Path.Combine(Application.persistentDataPath,
                $"{stem}_rooms.json");
            File.WriteAllText(path, JsonUtility.ToJson(wrapper, true));
            return path;
        }

        /// <summary>
        /// Combined view: the heatmap PNG as background image and the traced
        /// floor plan drawn on top — one real-scale SVG (1 unit = 1 mm), both
        /// layers in the shared grid-space coordinate system so they are
        /// congruent. Bounds are the union of heatmap and rooms.
        /// </summary>
        public static string ExportCombinedSvg(byte[] heatmapPng,
            Vector2 gridMinXZ, float cellSize, int texW, int texH,
            float gridYawDegrees, IReadOnlyList<RoomOutline> rooms, string stem,
            IReadOnlyList<DrillLine> drills = null)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            var roomsMm = RoomsToGridMm(rooms, gridYawDegrees,
                out Vector2 min, out Vector2 max);

            // Heatmap image rect in the same mm frame. PNG row 0 (visual top
            // after encoding) is the max-Z edge, matching SVG's y-down axis.
            float imgX = gridMinXZ.x * 1000f;
            float imgW = texW * cellSize * 1000f;
            float imgH = texH * cellSize * 1000f;
            float imgY = -(gridMinXZ.y + texH * cellSize) * 1000f;
            min = Vector2.Min(min, new Vector2(imgX, imgY));
            max = Vector2.Max(max, new Vector2(imgX + imgW, imgY + imgH));

            const float margin = 250f; // mm
            float w = max.x - min.x + 2f * margin;
            float h = max.y - min.y + 2f * margin;
            Vector2 offset = new Vector2(margin - min.x, margin - min.y);

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine(string.Format(inv,
                "<svg xmlns=\"http://www.w3.org/2000/svg\" " +
                "xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"{0:F0}mm\" " +
                "height=\"{1:F0}mm\" viewBox=\"0 0 {0:F0} {1:F0}\">", w, h));
            sb.AppendLine(string.Format(inv,
                "  <!-- Quest3 WiFi-Scanner kombinierte Ansicht (Heatmap + " +
                "Grundriss) | {0:yyyy-MM-dd HH:mm} | 1 unit = 1 mm | gridYaw " +
                "{1:F1} deg -->", DateTime.Now, gridYawDegrees));

            string b64 = Convert.ToBase64String(heatmapPng);
            sb.Append(string.Format(inv,
                "  <image x=\"{0:F0}\" y=\"{1:F0}\" width=\"{2:F0}\" " +
                "height=\"{3:F0}\" preserveAspectRatio=\"none\" ",
                imgX + offset.x, imgY + offset.y, imgW, imgH));
            sb.Append("xlink:href=\"data:image/png;base64,").Append(b64)
              .AppendLine("\"/>");

            AppendRoomShapes(sb, rooms, roomsMm, offset, haloText: true);
            AppendDrillMarks(sb, drills, gridYawDegrees, offset);
            sb.AppendLine("</svg>");

            string path = Path.Combine(Application.persistentDataPath,
                $"{stem}_combined.svg");
            File.WriteAllText(path, sb.ToString());
            Debug.Log($"[DataExporter] Wrote combined heatmap+floor-plan SVG to {path}");
            return path;
        }

        [Serializable]
        private class DrillList
        {
            public float gridYawDegrees;
            public List<DrillLine> lines;
        }

        /// <summary>Raw drill lines (entry/direction/depth, Unity world
        /// coordinates) as JSON next to the other exports.</summary>
        public static string ExportDrillJson(IReadOnlyList<DrillLine> lines,
            float gridYawDegrees, string stem)
        {
            var wrapper = new DrillList
            {
                gridYawDegrees = gridYawDegrees,
                lines = new List<DrillLine>(lines),
            };
            string path = Path.Combine(Application.persistentDataPath,
                $"{stem}_drill.json");
            File.WriteAllText(path, JsonUtility.ToJson(wrapper, true));
            Debug.Log($"[DataExporter] Wrote {lines.Count} drill line(s) to {path}");
            return path;
        }

        [Serializable]
        private class ApFixJson
        {
            // Legacy single-AP fields = the ACTIVE estimate.
            public float posX, posY, posZ;
            public string ssid;
            public int rssi;
            public float radius;    // position uncertainty (m); 0 = centroid only
            public float pathLossN; // fitted path-loss exponent
            public float powerAt1m; // fitted P1m (dBm)
            // All per-BSSID estimates (multi-AP since roaming support).
            public List<ApLocator.ApEstimate> aps;
        }

        /// <summary>Estimated access-point position(s) as JSON: the active
        /// estimate in the legacy top-level fields plus every per-BSSID
        /// estimate in <c>aps</c>.</summary>
        public static string ExportApFixJson(Vector3 pos, string ssid, int rssi,
            string stem, float radius = 0f, float pathLossN = 0f, float powerAt1m = 0f,
            IReadOnlyList<ApLocator.ApEstimate> allEstimates = null)
        {
            var wrapper = new ApFixJson
            {
                posX = pos.x, posY = pos.y, posZ = pos.z,
                ssid = ssid, rssi = rssi,
                radius = radius, pathLossN = pathLossN, powerAt1m = powerAt1m,
                aps = allEstimates != null
                    ? new List<ApLocator.ApEstimate>(allEstimates)
                    : null,
            };
            string path = Path.Combine(Application.persistentDataPath,
                $"{stem}_apfix.json");
            File.WriteAllText(path, JsonUtility.ToJson(wrapper, true));
            Debug.Log($"[DataExporter] Wrote AP fix to {path}");
            return path;
        }

        [Serializable]
        private class WallsList
        {
            public float pathLossN;
            public float powerAt1m;
            public List<WallLoss> walls;
        }

        /// <summary>Estimated per-wall attenuations (dB) as JSON.</summary>
        public static string ExportWallsJson(IReadOnlyList<WallLoss> walls,
            float pathLossN, float powerAt1m, string stem)
        {
            var wrapper = new WallsList
            {
                pathLossN = pathLossN,
                powerAt1m = powerAt1m,
                walls = new List<WallLoss>(walls),
            };
            string path = Path.Combine(Application.persistentDataPath,
                $"{stem}_walls.json");
            File.WriteAllText(path, JsonUtility.ToJson(wrapper, true));
            Debug.Log($"[DataExporter] Wrote {walls.Count} wall loss estimate(s) to {path}");
            return path;
        }

        /// <summary>
        /// Floor plan SVG with the estimated wall attenuations written directly
        /// on the walls ({stem}_walls{suffix}.svg) — the wall-loss table alone
        /// is hard to map to real walls. With multiple storeys the caller
        /// invokes this once per storey (<paramref name="floorBaseY"/> filters
        /// the rooms, loss indices are remapped alongside). Same real-scale mm
        /// layout as the rooms SVG.
        /// </summary>
        public static string ExportWallsSvg(IReadOnlyList<RoomOutline> allRooms,
            float gridYawDegrees, IReadOnlyList<WallLoss> allLosses, string stem,
            float floorBaseY = float.NaN, string fileSuffix = "")
        {
            IReadOnlyList<RoomOutline> rooms;
            IReadOnlyList<WallLoss> losses;
            if (!float.IsNaN(floorBaseY))
            {
                var useRooms = new List<RoomOutline>();
                var indexMap = new Dictionary<int, int>();
                for (int i = 0; i < allRooms.Count; i++)
                    if (Mathf.Abs(allRooms[i].floorY - floorBaseY) <= 1f)
                    {
                        indexMap[i] = useRooms.Count;
                        useRooms.Add(allRooms[i]);
                    }
                if (useRooms.Count == 0) return null;
                var useLosses = new List<WallLoss>();
                foreach (var loss in allLosses)
                    if (indexMap.TryGetValue(loss.room, out int ni))
                        useLosses.Add(new WallLoss
                        {
                            room = ni,
                            edge = loss.edge,
                            lossDb = loss.lossDb,
                            samplesNear = loss.samplesNear,
                            samplesFar = loss.samplesFar,
                        });
                rooms = useRooms;
                losses = useLosses;
            }
            else
            {
                rooms = allRooms;
                losses = allLosses;
            }

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var roomsMm = RoomsToGridMm(rooms, gridYawDegrees,
                out Vector2 min, out Vector2 max);

            const float margin = 250f; // mm
            float w = max.x - min.x + 2f * margin;
            float h = max.y - min.y + 2f * margin;
            Vector2 offset = new Vector2(margin - min.x, margin - min.y);

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine(string.Format(inv,
                "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{0:F0}mm\" " +
                "height=\"{1:F0}mm\" viewBox=\"0 0 {0:F0} {1:F0}\">", w, h));
            sb.AppendLine(string.Format(inv,
                "  <!-- Quest3 WiFi-Scanner Wand-Dämpfungen | {0:yyyy-MM-dd HH:mm} | " +
                "1 unit = 1 mm | gridYaw {1:F1} deg -->",
                DateTime.Now, gridYawDegrees));
            AppendRoomShapes(sb, rooms, roomsMm, offset, haloText: false);

            foreach (var loss in losses)
            {
                if (loss.room >= roomsMm.Count) continue;
                var pts = roomsMm[loss.room];
                int n = pts.Count;
                if (n < 3 || loss.edge >= n) continue;
                Vector2 centroid = Vector2.zero;
                foreach (var p in pts) centroid += p;
                centroid /= n;
                Vector2 a = pts[loss.edge] + offset;
                Vector2 b = pts[(loss.edge + 1) % n] + offset;
                Vector2 mid = (a + b) * 0.5f;
                Vector2 inward = (centroid + offset - mid).normalized * 180f;
                Vector2 pos = mid + inward;
                sb.AppendLine(string.Format(inv,
                    "  <text x=\"{0:F0}\" y=\"{1:F0}\" font-size=\"120\" " +
                    "font-weight=\"bold\" fill=\"#cc4422\" text-anchor=\"middle\" " +
                    "stroke=\"#ffffff\" stroke-width=\"30\" paint-order=\"stroke\">" +
                    "{2:F1} dB</text>",
                    pos.x, pos.y, loss.lossDb));
            }
            sb.AppendLine("</svg>");

            string path = Path.Combine(Application.persistentDataPath,
                $"{stem}_walls{fileSuffix}.svg");
            File.WriteAllText(path, sb.ToString());
            Debug.Log($"[DataExporter] Wrote wall-attenuation floor plan to {path}");
            return path;
        }

        [Serializable]
        private class PingList
        {
            public List<PingSample> nodes;
        }

        [Serializable]
        private class ThroughputList
        {
            public List<ThroughputSample> nodes;
        }

        /// <summary>Placed latency nodes (world position + ms) as JSON.</summary>
        public static string ExportPingJson(IReadOnlyList<PingSample> nodes, string stem)
        {
            var wrapper = new PingList { nodes = new List<PingSample>(nodes) };
            string path = Path.Combine(Application.persistentDataPath,
                $"{stem}_ping.json");
            File.WriteAllText(path, JsonUtility.ToJson(wrapper, true));
            Debug.Log($"[DataExporter] Wrote {nodes.Count} ping node(s) to {path}");
            return path;
        }

        public static string ExportThroughputJson(
            IReadOnlyList<ThroughputSample> nodes, string stem)
        {
            var wrapper = new ThroughputList
                { nodes = new List<ThroughputSample>(nodes) };
            string path = Path.Combine(Application.persistentDataPath,
                $"{stem}_throughput.json");
            File.WriteAllText(path, JsonUtility.ToJson(wrapper, true));
            Debug.Log($"[DataExporter] Wrote {nodes.Count} throughput node(s) to {path}");
            return path;
        }

        [Serializable]
        private class RoamList
        {
            public List<RoamEvent> events;
        }

        /// <summary>Roaming events (position + BSSID/band change) as JSON.</summary>
        public static string ExportRoamJson(IReadOnlyList<RoamEvent> events, string stem)
        {
            var wrapper = new RoamList { events = new List<RoamEvent>(events) };
            string path = Path.Combine(Application.persistentDataPath,
                $"{stem}_roam.json");
            File.WriteAllText(path, JsonUtility.ToJson(wrapper, true));
            Debug.Log($"[DataExporter] Wrote {events.Count} roaming event(s) to {path}");
            return path;
        }

        [Serializable]
        private class NotesList
        {
            public List<SpatialNote> notes;
        }

        /// <summary>Spatial notes (position + type + text) as JSON.</summary>
        public static string ExportNotesJson(IReadOnlyList<SpatialNote> notes, string stem)
        {
            var wrapper = new NotesList { notes = new List<SpatialNote>(notes) };
            string path = Path.Combine(Application.persistentDataPath,
                $"{stem}_notes.json");
            File.WriteAllText(path, JsonUtility.ToJson(wrapper, true));
            Debug.Log($"[DataExporter] Wrote {notes.Count} note(s) to {path}");
            return path;
        }

        [Serializable]
        private class MeasureList
        {
            public List<Measurement> measurements;
        }

        /// <summary>Completed distance measurements (two endpoints + length) as JSON.</summary>
        public static string ExportMeasureJson(IReadOnlyList<Measurement> ms, string stem)
        {
            var wrapper = new MeasureList { measurements = new List<Measurement>(ms) };
            string path = Path.Combine(Application.persistentDataPath,
                $"{stem}_measure.json");
            File.WriteAllText(path, JsonUtility.ToJson(wrapper, true));
            Debug.Log($"[DataExporter] Wrote {ms.Count} measurement(s) to {path}");
            return path;
        }

        // Drill entry points as red crosses in the floor-plan SVGs (same
        // world -> grid-mm transform as the room corners).
        private static void AppendDrillMarks(StringBuilder sb,
            IReadOnlyList<DrillLine> drills, float gridYawDegrees, Vector2 offset)
        {
            if (drills == null || drills.Count == 0) return;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            Quaternion worldToGrid = Quaternion.Euler(0f, -gridYawDegrees, 0f);
            const float arm = 80f; // mm
            foreach (var d in drills)
            {
                Vector3 g = worldToGrid * new Vector3(d.entry.x, 0f, d.entry.z);
                float x = g.x * 1000f + offset.x;
                float y = -g.z * 1000f + offset.y;
                sb.AppendLine(string.Format(inv,
                    "  <line x1=\"{0:F0}\" y1=\"{1:F0}\" x2=\"{2:F0}\" y2=\"{3:F0}\" " +
                    "stroke=\"#cc2222\" stroke-width=\"20\"/>",
                    x - arm, y - arm, x + arm, y + arm));
                sb.AppendLine(string.Format(inv,
                    "  <line x1=\"{0:F0}\" y1=\"{1:F0}\" x2=\"{2:F0}\" y2=\"{3:F0}\" " +
                    "stroke=\"#cc2222\" stroke-width=\"20\"/>",
                    x - arm, y + arm, x + arm, y - arm));
                sb.AppendLine(string.Format(inv,
                    "  <circle cx=\"{0:F0}\" cy=\"{1:F0}\" r=\"{2:F0}\" fill=\"none\" " +
                    "stroke=\"#cc2222\" stroke-width=\"15\"/>", x, y, arm));
            }
        }

        /// <summary>
        /// Frees device storage: deletes ALL export files (csv/json/obj/png/svg,
        /// including autosaves) from persistentDataPath. Deliberately filters by
        /// extension — Unity's own files there (unity.ver, global-metadata.dat,
        /// …) must survive. Returns the number of deleted files.
        /// </summary>
        public static int DeleteAllExports()
        {
            string[] patterns = { "*.csv", "*.json", "*.obj", "*.ply", "*.png", "*.svg", "*.html" };
            int count = 0;
            foreach (string pattern in patterns)
            {
                foreach (string file in Directory.GetFiles(
                             Application.persistentDataPath, pattern))
                {
                    try
                    {
                        File.Delete(file);
                        count++;
                    }
                    catch (IOException e)
                    {
                        Debug.LogWarning($"[DataExporter] Konnte {file} nicht " +
                                         $"löschen: {e.Message}");
                    }
                }
            }
            Debug.Log($"[DataExporter] {count} Export-Datei(en) gelöscht.");
            return count;
        }

        /// <summary>Deletes one export family (main files plus every sidecar)
        /// without touching timestamped/manual scans. Used when a deliberate
        /// new session invalidates the rolling crash-recovery autosave.</summary>
        public static int DeleteExportStem(string stem)
        {
            if (string.IsNullOrWhiteSpace(stem)) return 0;
            string[] patterns = { "*.csv", "*.json", "*.obj", "*.ply", "*.png", "*.svg", "*.html" };
            int count = 0;
            foreach (string pattern in patterns)
                foreach (string file in Directory.GetFiles(
                             Application.persistentDataPath, pattern))
                {
                    string fileStem = Path.GetFileNameWithoutExtension(file);
                    if (fileStem != stem && !fileStem.StartsWith(stem + "_",
                            StringComparison.Ordinal))
                        continue;
                    try { File.Delete(file); count++; }
                    catch (IOException e)
                    {
                        Debug.LogWarning($"[DataExporter] Konnte {file} nicht " +
                                         $"löschen: {e.Message}");
                    }
                }
            return count;
        }

        // World-XZ corners -> grid space (rotated by -gridYaw) in mm, SVG
        // y-down (Z flipped); also returns the bounds over all rooms.
        private static List<List<Vector2>> RoomsToGridMm(
            IReadOnlyList<RoomOutline> rooms, float gridYawDegrees,
            out Vector2 min, out Vector2 max)
        {
            Quaternion worldToGrid = Quaternion.Euler(0f, -gridYawDegrees, 0f);
            var roomsMm = new List<List<Vector2>>();
            min = new Vector2(float.MaxValue, float.MaxValue);
            max = new Vector2(float.MinValue, float.MinValue);
            foreach (var room in rooms)
            {
                var pts = new List<Vector2>(room.cornersXZ.Count);
                foreach (var c in room.cornersXZ)
                {
                    Vector3 g = worldToGrid * new Vector3(c.x, 0f, c.y);
                    var p = new Vector2(g.x * 1000f, -g.z * 1000f);
                    pts.Add(p);
                    min = Vector2.Min(min, p);
                    max = Vector2.Max(max, p);
                }
                roomsMm.Add(pts);
            }
            return roomsMm;
        }

        // Fill polygons + walls (drawn edge-by-edge so door/window openings leave
        // real gaps) + door-swing / window symbols + wall-length labels. haloText
        // draws a white halo around the labels for the heatmap background.
        private static void AppendRoomShapes(StringBuilder sb,
            IReadOnlyList<RoomOutline> rooms, List<List<Vector2>> roomsMm,
            Vector2 offset, bool haloText)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string textExtra = haloText
                ? " stroke=\"#ffffff\" stroke-width=\"30\" paint-order=\"stroke\""
                : "";
            for (int r = 0; r < roomsMm.Count; r++)
            {
                var pts = roomsMm[r];
                var room = rooms[r];
                int n = pts.Count;

                // Fill only — the walls are drawn separately so they can gap.
                sb.Append("  <polygon fill=\"#4488cc\" fill-opacity=\"0.06\" " +
                          "stroke=\"none\" points=\"");
                Vector2 centroid = Vector2.zero;
                for (int i = 0; i < n; i++)
                {
                    if (i > 0) sb.Append(' ');
                    Vector2 p = pts[i] + offset;
                    centroid += p;
                    sb.Append(p.x.ToString("F0", inv)).Append(',')
                      .Append(p.y.ToString("F0", inv));
                }
                sb.AppendLine("\"/>");
                if (n > 0) centroid /= n;

                for (int i = 0; i < n; i++)
                {
                    Vector2 a = pts[i] + offset;
                    Vector2 b = pts[(i + 1) % n] + offset;
                    float worldLen = Vector2.Distance(room.cornersXZ[i],
                        room.cornersXZ[(i + 1) % n]);

                    var ops = new List<WallOpening>();
                    if (room.openings != null)
                        foreach (var op in room.openings)
                            if (op.edgeIndex == i) ops.Add(op);
                    ops.Sort((p, q) => p.startAlong.CompareTo(q.startAlong));

                    if (worldLen < 1e-4f || ops.Count == 0)
                    {
                        AppendWallLine(sb, inv, a, b);
                    }
                    else
                    {
                        float cursor = 0f;
                        foreach (var op in ops)
                        {
                            float sf = Mathf.Clamp01(op.startAlong / worldLen);
                            float ef = Mathf.Clamp01(op.endAlong / worldLen);
                            if (ef <= cursor) continue;
                            sf = Mathf.Max(sf, cursor);
                            if (sf > cursor)
                                AppendWallLine(sb, inv, Vector2.Lerp(a, b, cursor),
                                    Vector2.Lerp(a, b, sf));
                            Vector2 ps = Vector2.Lerp(a, b, sf);
                            Vector2 pe = Vector2.Lerp(a, b, ef);
                            if (op.isDoor)
                            {
                                // Hinge (Anschlagseite) = the jamb nearest the
                                // first-clicked point; the swing arc starts there.
                                float hf = worldLen > 1e-4f ? op.hingeAlong / worldLen : sf;
                                bool hingeStart = Mathf.Abs(hf - sf) <= Mathf.Abs(hf - ef);
                                AppendDoorMarker(sb, inv, hingeStart ? ps : pe,
                                    hingeStart ? pe : ps, centroid);
                            }
                            else AppendWindowMarker(sb, inv, ps, pe);
                            cursor = Mathf.Max(cursor, ef);
                        }
                        if (cursor < 1f)
                            AppendWallLine(sb, inv, Vector2.Lerp(a, b, cursor), b);
                    }

                    // Wall length in metres at the edge midpoint.
                    Vector2 mid = (a + b) * 0.5f;
                    sb.AppendLine(string.Format(inv,
                        "  <text x=\"{0:F0}\" y=\"{1:F0}\" font-size=\"120\" " +
                        "fill=\"#333333\" text-anchor=\"middle\"{2}>{3:F2} m</text>",
                        mid.x, mid.y, textExtra, worldLen));
                }

                // Room name at the centroid.
                if (!string.IsNullOrEmpty(room.name))
                    sb.AppendLine(string.Format(inv,
                        "  <text x=\"{0:F0}\" y=\"{1:F0}\" font-size=\"160\" " +
                        "font-weight=\"bold\" fill=\"#1a4a6a\" " +
                        "text-anchor=\"middle\"{2}>{3}</text>",
                        centroid.x, centroid.y, textExtra, XmlEscape(room.name)));
            }
        }

        private static string XmlEscape(string s) =>
            string.IsNullOrEmpty(s)
                ? ""
                : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        private static void AppendWallLine(StringBuilder sb,
            System.Globalization.CultureInfo inv, Vector2 a, Vector2 b)
        {
            sb.AppendLine(string.Format(inv,
                "  <line x1=\"{0:F0}\" y1=\"{1:F0}\" x2=\"{2:F0}\" y2=\"{3:F0}\" " +
                "stroke=\"#222222\" stroke-width=\"20\"/>", a.x, a.y, b.x, b.y));
        }

        // Door: hinge at the first jamb, leaf drawn open into the room plus a
        // quarter-circle swing arc.
        private static void AppendDoorMarker(StringBuilder sb,
            System.Globalization.CultureInfo inv, Vector2 ps, Vector2 pe,
            Vector2 centroid)
        {
            float w = Vector2.Distance(ps, pe);
            if (w < 1f) return;
            Vector2 u = (pe - ps) / w;
            Vector2 perp = new Vector2(-u.y, u.x);
            if (Vector2.Dot(perp, centroid - ps) < 0f) perp = -perp; // into the room
            Vector2 leafEnd = ps + perp * w;
            int sweep = (perp.x * u.y - perp.y * u.x) > 0f ? 1 : 0;
            AppendWallLine(sb, inv, ps, leafEnd);
            sb.AppendLine(string.Format(inv,
                "  <path d=\"M {0:F0} {1:F0} A {2:F0} {2:F0} 0 0 {3} {4:F0} {5:F0}\" " +
                "fill=\"none\" stroke=\"#555555\" stroke-width=\"12\"/>",
                leafEnd.x, leafEnd.y, w, sweep, pe.x, pe.y));
        }

        // Window: a double line across the opening within the wall thickness.
        private static void AppendWindowMarker(StringBuilder sb,
            System.Globalization.CultureInfo inv, Vector2 ps, Vector2 pe)
        {
            float w = Vector2.Distance(ps, pe);
            if (w < 1f) return;
            Vector2 u = (pe - ps) / w;
            Vector2 perp = new Vector2(-u.y, u.x) * 40f; // 40 mm offset
            sb.AppendLine(string.Format(inv,
                "  <line x1=\"{0:F0}\" y1=\"{1:F0}\" x2=\"{2:F0}\" y2=\"{3:F0}\" " +
                "stroke=\"#555555\" stroke-width=\"12\"/>",
                ps.x + perp.x, ps.y + perp.y, pe.x + perp.x, pe.y + perp.y));
            sb.AppendLine(string.Format(inv,
                "  <line x1=\"{0:F0}\" y1=\"{1:F0}\" x2=\"{2:F0}\" y2=\"{3:F0}\" " +
                "stroke=\"#555555\" stroke-width=\"12\"/>",
                ps.x - perp.x, ps.y - perp.y, pe.x - perp.x, pe.y - perp.y));
        }
    }
}
