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
        }

        /// <summary>
        /// Exports both CSV and JSON. Returns the directory written to.
        /// If <paramref name="fixedName"/> is given, that exact stem is used and
        /// re-exporting overwrites it (for autosave). Otherwise a timestamped
        /// pair is written.
        /// </summary>
        public static string Export(IReadOnlyList<Sample> samples, string fixedName = null)
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

            var wrapper = new SampleList { samples = new List<Sample>(samples) };
            File.WriteAllText(jsonPath, JsonUtility.ToJson(wrapper, true));

            Debug.Log($"[DataExporter] Wrote {samples.Count} samples to {csvPath} and {jsonPath}");
            return dir;
        }

        /// <summary>
        /// Writes the given meshes (e.g. the scanned room's scene meshes) into a
        /// single Wavefront OBJ next to the CSV/JSON export. Vertices are
        /// world-space Unity coordinates — deliberately NOT converted to OBJ's
        /// right-handed convention, so they are congruent with the posX/Y/Z
        /// columns of the WiFi samples when analysed together on the PC.
        /// </summary>
        public static string ExportMeshesObj(IEnumerable<MeshFilter> meshFilters, string stem)
        {
            string path = Path.Combine(Application.persistentDataPath, $"{stem}.obj");
            var sb = new StringBuilder();
            sb.AppendLine("# Quest3 WiFi-Scanner room mesh export");
            sb.AppendLine("# Unity left-handed world coordinates (matches wifi CSV posX/Y/Z)");

            int vertexOffset = 1; // OBJ indices are 1-based
            int meshIndex = 0;
            foreach (var mf in meshFilters)
            {
                if (mf == null || mf.sharedMesh == null) continue;
                var mesh = mf.sharedMesh;
                var tr = mf.transform;
                sb.AppendLine($"o room_mesh_{meshIndex++}");

                var vertices = mesh.vertices;
                foreach (var v in vertices)
                {
                    Vector3 w = tr.TransformPoint(v);
                    sb.Append("v ").Append(w.x.ToString("F4", System.Globalization.CultureInfo.InvariantCulture))
                      .Append(' ').Append(w.y.ToString("F4", System.Globalization.CultureInfo.InvariantCulture))
                      .Append(' ').AppendLine(w.z.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
                }

                var triangles = mesh.triangles;
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    sb.Append("f ").Append(triangles[i] + vertexOffset)
                      .Append(' ').Append(triangles[i + 1] + vertexOffset)
                      .Append(' ').Append(triangles[i + 2] + vertexOffset).AppendLine();
                }
                vertexOffset += vertices.Length;
            }

            File.WriteAllText(path, sb.ToString());
            Debug.Log($"[DataExporter] Wrote room mesh OBJ to {path}");
            return path;
        }

        [Serializable]
        private class RoomList
        {
            public float gridYawDegrees;
            public List<RoomOutline> rooms;
        }

        /// <summary>
        /// Writes the manually traced rooms as a real-scale SVG floor plan
        /// (1 SVG user unit = 1 mm, width/height in mm => defined real
        /// measurements) with each wall labelled with its length in metres,
        /// plus the raw corners as JSON (basis for a later 3D export).
        /// Corners are rotated into grid space so walls are axis-parallel
        /// after the wall calibration.
        /// </summary>
        public static string ExportRoomsSvg(IReadOnlyList<RoomOutline> rooms,
            float gridYawDegrees, string stem)
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
                "  <!-- Quest3 WiFi-Scanner Grundriss | {0:yyyy-MM-dd HH:mm} | " +
                "1 unit = 1 mm | gridYaw {1:F1} deg | {2} Raum/Räume -->",
                DateTime.Now, gridYawDegrees, rooms.Count));
            AppendRoomShapes(sb, roomsMm, offset, haloText: false);
            sb.AppendLine("</svg>");

            string svgPath = Path.Combine(dir, $"{stem}_rooms.svg");
            File.WriteAllText(svgPath, sb.ToString());

            var wrapper = new RoomList
            {
                gridYawDegrees = gridYawDegrees,
                rooms = new List<RoomOutline>(rooms),
            };
            File.WriteAllText(Path.Combine(dir, $"{stem}_rooms.json"),
                JsonUtility.ToJson(wrapper, true));

            Debug.Log($"[DataExporter] Wrote {rooms.Count} room outline(s) to {svgPath}");
            return svgPath;
        }

        /// <summary>
        /// Combined view: the heatmap PNG as background image and the traced
        /// floor plan drawn on top — one real-scale SVG (1 unit = 1 mm), both
        /// layers in the shared grid-space coordinate system so they are
        /// congruent. Bounds are the union of heatmap and rooms.
        /// </summary>
        public static string ExportCombinedSvg(byte[] heatmapPng,
            Vector2 gridMinXZ, float cellSize, int texW, int texH,
            float gridYawDegrees, IReadOnlyList<RoomOutline> rooms, string stem)
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

            AppendRoomShapes(sb, roomsMm, offset, haloText: true);
            sb.AppendLine("</svg>");

            string path = Path.Combine(Application.persistentDataPath,
                $"{stem}_combined.svg");
            File.WriteAllText(path, sb.ToString());
            Debug.Log($"[DataExporter] Wrote combined heatmap+floor-plan SVG to {path}");
            return path;
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

        // Polygons + wall-length labels. haloText draws a white halo around
        // the labels so they stay readable on the coloured heatmap background.
        private static void AppendRoomShapes(StringBuilder sb,
            List<List<Vector2>> roomsMm, Vector2 offset, bool haloText)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string textExtra = haloText
                ? " stroke=\"#ffffff\" stroke-width=\"30\" paint-order=\"stroke\""
                : "";
            foreach (var pts in roomsMm)
            {
                sb.Append("  <polygon fill=\"#4488cc\" fill-opacity=\"0.06\" " +
                          "stroke=\"#222222\" stroke-width=\"20\" points=\"");
                for (int i = 0; i < pts.Count; i++)
                {
                    if (i > 0) sb.Append(' ');
                    Vector2 p = pts[i] + offset;
                    sb.Append(p.x.ToString("F0", inv)).Append(',')
                      .Append(p.y.ToString("F0", inv));
                }
                sb.AppendLine("\"/>");

                // Wall lengths in metres at each edge midpoint.
                for (int i = 0; i < pts.Count; i++)
                {
                    Vector2 a = pts[i] + offset;
                    Vector2 b = pts[(i + 1) % pts.Count] + offset;
                    Vector2 mid = (a + b) * 0.5f;
                    float meters = Vector2.Distance(a, b) / 1000f;
                    sb.AppendLine(string.Format(inv,
                        "  <text x=\"{0:F0}\" y=\"{1:F0}\" font-size=\"120\" " +
                        "fill=\"#333333\" text-anchor=\"middle\"{2}>{3:F2} m</text>",
                        mid.x, mid.y, textExtra, meters));
                }
            }
        }
    }
}
