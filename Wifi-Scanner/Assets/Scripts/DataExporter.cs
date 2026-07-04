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
    }
}
