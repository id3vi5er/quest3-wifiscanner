using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace WifiScanner
{
    /// <summary>
    /// Writes a self-contained survey report ({stem}_report.html) next to the
    /// other exports: statistics, the heatmap PNGs of every storey (embedded
    /// as base64), the combined floor-plan SVG, the AP fix, wall attenuations
    /// and tables for notes / ping nodes / measurements / drill lines. One
    /// file, no external assets — openable on any phone/PC, also served by
    /// the web dashboard's download list.
    ///
    /// Runs at the END of <see cref="SampleCollector.Export"/> so the heatmap/
    /// SVG files it embeds already exist on disk.
    /// </summary>
    public static class ReportGenerator
    {
        public static string Write(SampleCollector collector, string stem)
        {
            string dir = Application.persistentDataPath;
            var sb = new StringBuilder(1 << 20);

            sb.AppendLine("<!DOCTYPE html><html lang=\"de\"><head>");
            sb.AppendLine("<meta charset=\"utf-8\">");
            sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            sb.Append("<title>WiFi-Survey ").Append(Esc(stem)).AppendLine("</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body{font-family:system-ui,sans-serif;margin:24px;" +
                          "background:#12141a;color:#e8e8ee;max-width:960px}");
            sb.AppendLine("h1{font-size:1.5em}h2{font-size:1.15em;margin-top:1.6em;" +
                          "border-bottom:1px solid #333;padding-bottom:4px}");
            sb.AppendLine("table{border-collapse:collapse;width:100%;font-size:0.9em}");
            sb.AppendLine("th,td{border:1px solid #333;padding:6px 8px;text-align:left}");
            sb.AppendLine("th{background:#1d2330}img,svg{max-width:100%;height:auto;" +
                          "background:#fff;border-radius:6px}");
            sb.AppendLine(".kpi{display:inline-block;background:#1d2330;border-radius:8px;" +
                          "padding:10px 16px;margin:4px 8px 4px 0}");
            sb.AppendLine(".kpi b{display:block;font-size:1.3em}");
            sb.AppendLine(".muted{color:#9aa}");
            sb.AppendLine("</style></head><body>");

            // ---- Header + KPIs ----
            sb.Append("<h1>WiFi-Survey-Report</h1><p class=\"muted\">Scan „")
              .Append(Esc(stem)).Append("“ · erstellt ")
              .Append(DateTime.Now.ToString("dd.MM.yyyy HH:mm"))
              .AppendLine(" · Quest 3 WiFi-Scanner</p>");

            int secs = Mathf.RoundToInt(collector.RecordingSeconds);
            string stats = collector.TryGetRssiStats(out int min, out int max, out float avg)
                ? $"{min} / {max} / {avg:F0} dBm"
                : "– / – / –";
            sb.Append("<div>")
              .Append(Kpi($"{collector.SampleCount}", "Messpunkte"))
              .Append(Kpi($"{secs / 60:D2}:{secs % 60:D2}", "Aufnahmezeit"))
              .Append(Kpi(stats, "Min / Max / Ø RSSI"))
              .Append(Kpi(Esc(string.IsNullOrEmpty(collector.LastSsid)
                  ? "–" : collector.LastSsid), "Netzwerk (SSID)"));
            if (collector.floorManager != null)
                sb.Append(Kpi($"{collector.floorManager.Floors.Count}", "Etagen"));
            if (collector.roomTracer != null)
                sb.Append(Kpi($"{collector.roomTracer.Rooms.Count}", "Räume"));
            sb.AppendLine("</div>");

            // ---- Heatmaps (per storey when available) ----
            AppendHeatmaps(sb, collector, stem, dir);

            // ---- Combined floor plan SVG ----
            string combined = Path.Combine(dir, $"{stem}_combined.svg");
            if (File.Exists(combined))
            {
                sb.AppendLine("<h2>Grundriss + Heatmap (kombiniert)</h2>");
                AppendInlineSvg(sb, combined);
            }
            else
            {
                string roomsSvg = Path.Combine(dir, $"{stem}_rooms.svg");
                if (File.Exists(roomsSvg))
                {
                    sb.AppendLine("<h2>Grundriss</h2>");
                    AppendInlineSvg(sb, roomsSvg);
                }
            }

            // ---- AP fix ----
            var ap = collector.apLocator;
            if (ap != null && ap.HasFix)
            {
                sb.AppendLine("<h2>Access Point (geschätzt)</h2><table>");
                sb.AppendLine("<tr><th>SSID</th><th>Position (x/y/z, m)</th>" +
                              "<th>stärkster RSSI</th><th>Unsicherheit</th>" +
                              "<th>Pfadverlust n</th><th>P<sub>1m</sub></th></tr>");
                sb.Append("<tr><td>").Append(Esc(ap.FixSsid))
                  .Append("</td><td>").Append(Fmt(ap.FixPosition))
                  .Append("</td><td>").Append(ap.FixRssi).Append(" dBm")
                  .Append("</td><td>").Append(ap.HasFit ? $"±{ap.FixRadius:F1} m" : "– (nur Schwerpunkt)")
                  .Append("</td><td>").Append(ap.HasFit ? ap.PathLossExponent.ToString("F2") : "–")
                  .Append("</td><td>").Append(ap.HasFit ? $"{ap.PowerAt1mDbm:F1} dBm" : "–")
                  .AppendLine("</td></tr></table>");
            }

            // ---- Wall attenuations ----
            var walls = collector.wallLoss;
            if (walls != null && walls.Losses.Count > 0)
            {
                sb.AppendLine("<h2>Wand-Dämpfungen (geschätzt)</h2><table>");
                sb.AppendLine("<tr><th>Raum</th><th>Wand</th><th>Dämpfung</th>" +
                              "<th>Messpunkte (nah/fern)</th></tr>");
                foreach (var w in walls.Losses)
                    sb.Append("<tr><td>").Append(w.room + 1)
                      .Append("</td><td>").Append(w.edge + 1)
                      .Append("</td><td>").Append($"{w.lossDb:F1} dB")
                      .Append("</td><td>").Append(w.samplesNear).Append(" / ")
                      .Append(w.samplesFar).AppendLine("</td></tr>");
                sb.AppendLine("</table>");

                // Floor plan with the dB values ON the walls — much easier to
                // map to reality than the table indices.
                string wallsSvg = Path.Combine(dir, $"{stem}_walls.svg");
                if (File.Exists(wallsSvg))
                {
                    sb.AppendLine("<h3>Grundriss mit Wand-Dämpfungen</h3>");
                    AppendInlineSvg(sb, wallsSvg);
                }
            }

            // ---- Notes ----
            var notes = collector.spatialNotes;
            if (notes != null && notes.Notes.Count > 0)
            {
                sb.AppendLine("<h2>Notizen</h2><table>");
                sb.AppendLine("<tr><th>Typ</th><th>Text</th><th>Position (m)</th></tr>");
                foreach (var n in notes.Notes)
                    sb.Append("<tr><td>").Append(Esc(NoteTypeLabel(n.type)))
                      .Append("</td><td>").Append(Esc(n.text))
                      .Append("</td><td>")
                      .Append(Fmt(new Vector3(n.posX, n.posY, n.posZ)))
                      .AppendLine("</td></tr>");
                sb.AppendLine("</table>");
            }

            // ---- Ping nodes ----
            var ping = collector.pingTester;
            if (ping != null && ping.NodeCount > 0)
            {
                sb.AppendLine("<h2>Latenz-Messpunkte (TCP-Connect)</h2><table>");
                sb.AppendLine("<tr><th>#</th><th>Latenz</th><th>Position (m)</th></tr>");
                int i = 1;
                foreach (var p in ping.Samples)
                    sb.Append("<tr><td>").Append(i++)
                      .Append("</td><td>")
                      .Append(p.latencyMs >= 0 ? $"{p.latencyMs} ms" : "Fehler")
                      .Append("</td><td>")
                      .Append(Fmt(new Vector3(p.posX, p.posY, p.posZ)))
                      .AppendLine("</td></tr>");
                sb.AppendLine("</table>");
            }

            // ---- Measurements ----
            var measurer = collector.distanceMeasurer;
            if (measurer != null && measurer.Measurements.Count > 0)
            {
                sb.AppendLine("<h2>Distanz-Messungen</h2><table>");
                sb.AppendLine("<tr><th>#</th><th>Länge</th><th>Von</th><th>Nach</th></tr>");
                int i = 1;
                foreach (var m in measurer.Measurements)
                    sb.Append("<tr><td>").Append(i++)
                      .Append("</td><td>").Append($"{m.meters:F3} m")
                      .Append("</td><td>").Append(Fmt(new Vector3(m.ax, m.ay, m.az)))
                      .Append("</td><td>").Append(Fmt(new Vector3(m.bx, m.by, m.bz)))
                      .AppendLine("</td></tr>");
                sb.AppendLine("</table>");
            }

            // ---- Drill lines ----
            var drill = collector.drillPlanner;
            if (drill != null && drill.Lines.Count > 0)
            {
                sb.AppendLine("<h2>Bohrplanung</h2><table>");
                sb.AppendLine("<tr><th>#</th><th>Eintrittspunkt (m)</th><th>Tiefe</th></tr>");
                int i = 1;
                foreach (var l in drill.Lines)
                    sb.Append("<tr><td>").Append(i++)
                      .Append("</td><td>").Append(Fmt(l.entry))
                      .Append("</td><td>").Append($"{l.depth:F2} m")
                      .AppendLine("</td></tr>");
                sb.AppendLine("</table>");
            }

            sb.AppendLine("<p class=\"muted\">Koordinaten sind Unity-Weltkoordinaten " +
                          "der Aufnahme-Session (X/Z horizontal, Y hoch) und passen " +
                          "zu den posX/Y/Z-Spalten der CSV-Datei. Heatmap-Farben: " +
                          $"{HeatmapColorMapper.WeakDbm} dBm (rot) … " +
                          $"{HeatmapColorMapper.StrongDbm} dBm (grün), " +
                          "grau = nicht vermessen.</p>");
            sb.AppendLine("</body></html>");

            string path = Path.Combine(dir, $"{stem}_report.html");
            File.WriteAllText(path, sb.ToString());
            Debug.Log($"[ReportGenerator] Wrote survey report to {path}");
            return path;
        }

        // Embeds every heatmap PNG of this scan (base64), per-storey when
        // multi-storey files exist, else the single main heatmap.
        private static void AppendHeatmaps(StringBuilder sb,
            SampleCollector collector, string stem, string dir)
        {
            var files = new List<(string path, string caption)>();
            if (collector.floorManager != null &&
                collector.floorManager.Floors.Count > 1)
            {
                foreach (var floorLevel in collector.floorManager.Floors)
                {
                    string p = Path.Combine(dir,
                        $"{stem}_heatmap_{floorLevel.label}.png");
                    if (File.Exists(p))
                        files.Add((p, $"Etage {floorLevel.label}"));
                }
            }
            if (files.Count == 0)
            {
                string p = Path.Combine(dir, $"{stem}_heatmap.png");
                if (File.Exists(p)) files.Add((p, "Boden-Heatmap"));
            }
            if (files.Count == 0) return;

            sb.AppendLine("<h2>Heatmaps</h2>");
            foreach (var (path, caption) in files)
            {
                sb.Append("<h3>").Append(Esc(caption)).AppendLine("</h3>");
                sb.Append("<img alt=\"").Append(Esc(caption))
                  .Append("\" src=\"data:image/png;base64,")
                  .Append(Convert.ToBase64String(File.ReadAllBytes(path)))
                  .AppendLine("\">");
            }
        }

        // Inlines an exported SVG (dropping the XML declaration so it embeds
        // as an HTML element).
        private static void AppendInlineSvg(StringBuilder sb, string path)
        {
            string svg = File.ReadAllText(path);
            int start = svg.IndexOf("<svg", StringComparison.Ordinal);
            if (start < 0) return;
            sb.AppendLine(svg.Substring(start));
        }

        private static string Kpi(string value, string label) =>
            $"<span class=\"kpi\"><b>{value}</b>{Esc(label)}</span>";

        private static string Fmt(Vector3 v) =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0:F2} / {1:F2} / {2:F2}", v.x, v.y, v.z);

        private static string NoteTypeLabel(string type) =>
            type == "VirtualAp" ? "Virtueller AP"
            : type == "Interference" ? "Störquelle"
            : "Text";

        private static string Esc(string s) =>
            string.IsNullOrEmpty(s)
                ? ""
                : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
