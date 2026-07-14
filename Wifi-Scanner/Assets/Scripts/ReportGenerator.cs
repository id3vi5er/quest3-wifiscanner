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
        private static string T(string key) => LocalizationManager.Get(key);
        private static string F(string key, params object[] args) =>
            LocalizationManager.Format(key, args);

        public static string Write(SampleCollector collector, string stem)
        {
            string dir = Application.persistentDataPath;
            var sb = new StringBuilder(1 << 20);

            sb.Append("<!DOCTYPE html><html lang=\"")
              .Append(LocalizationManager.CurrentCode).AppendLine("\"><head>");
            sb.AppendLine("<meta charset=\"utf-8\">");
            sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            sb.Append("<title>").Append(Esc(T("report.title"))).Append(" – ")
              .Append(Esc(stem)).AppendLine("</title>");
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
            string date = LocalizationManager.CurrentLanguage == AppLanguage.German
                ? DateTime.Now.ToString("dd.MM.yyyy HH:mm")
                : DateTime.Now.ToString("MM/dd/yyyy h:mm tt",
                    System.Globalization.CultureInfo.GetCultureInfo("en-US"));
            sb.Append("<h1>").Append(T("report.title"))
              .Append("</h1><p class=\"muted\">")
              .Append(F("report.created", Esc(stem), date))
              .AppendLine("</p>");

            int secs = Mathf.RoundToInt(collector.RecordingSeconds);
            string stats = collector.TryGetRssiStats(out int min, out int max, out float avg)
                ? $"{min} / {max} / {avg:F0} dBm"
                : "– / – / –";
            sb.Append("<div>")
              .Append(Kpi($"{collector.SampleCount}", T("report.kpi.points")))
              .Append(Kpi($"{secs / 60:D2}:{secs % 60:D2}", T("report.kpi.duration")))
              .Append(Kpi(stats, T("report.kpi.rssi")))
              .Append(Kpi(Esc(string.IsNullOrEmpty(collector.LastSsid)
                  ? "–" : collector.LastSsid), T("report.kpi.network")));
            if (collector.floorManager != null)
                sb.Append(Kpi($"{collector.floorManager.Floors.Count}",
                    T("report.kpi.floors")));
            if (collector.roomTracer != null)
                sb.Append(Kpi($"{collector.roomTracer.Rooms.Count}",
                    T("report.kpi.rooms")));
            sb.AppendLine("</div>");

            // ---- Automatic findings (assessment, not just tables) ----
            AppendFindings(sb, collector);

            // ---- Heatmaps (per storey when available) ----
            AppendHeatmaps(sb, collector, stem, dir);

            // ---- Combined floor plan SVG (active storey) ----
            string combined = Path.Combine(dir, $"{stem}_combined.svg");
            if (File.Exists(combined))
            {
                sb.Append("<h2>").Append(T("report.combined")).AppendLine("</h2>");
                AppendInlineSvg(sb, combined);
            }

            // ---- Floor plans (one per storey) ----
            var floorPlanFiles = new List<(string path, string caption)>();
            if (collector.floorManager != null &&
                collector.floorManager.Floors.Count > 1)
            {
                foreach (var level in collector.floorManager.Floors)
                {
                    string p = Path.Combine(dir, $"{stem}_rooms_{level.label}.svg");
                    if (File.Exists(p))
                        floorPlanFiles.Add((p, F("report.floor_caption",
                            LocalizationManager.DisplayFloorLabel(level.label))));
                }
            }
            else if (!File.Exists(combined))
            {
                string p = Path.Combine(dir, $"{stem}_rooms.svg");
                if (File.Exists(p))
                    floorPlanFiles.Add((p, T("report.floor_plan")));
            }
            if (floorPlanFiles.Count > 0)
            {
                sb.Append("<h2>").Append(T("report.floor_plans")).AppendLine("</h2>");
                foreach (var (svgPath, caption) in floorPlanFiles)
                {
                    sb.Append("<h3>").Append(Esc(caption)).AppendLine("</h3>");
                    AppendInlineSvg(sb, svgPath);
                }
            }

            // ---- AP fixes (one estimate per BSSID) ----
            var ap = collector.apLocator;
            if (ap != null && ap.Estimates.Count > 0)
            {
                sb.Append("<h2>").Append(T("report.ap.title"))
                  .AppendLine("</h2><table>");
                sb.AppendLine(T("report.ap.headers"));
                int apIndex = 1;
                foreach (var est in ap.Estimates)
                    sb.Append("<tr><td>").Append(apIndex++)
                      .Append("</td><td>").Append(Esc(est.ssid))
                      .Append("</td><td>").Append(Esc(est.bssid))
                      .Append("</td><td>").Append(Sample.BandLabel(est.frequencyMhz))
                      .Append("</td><td>").Append(Fmt(est.Position))
                      .Append("</td><td>").Append(est.strongestRssi).Append(" dBm")
                      .Append("</td><td>").Append(est.hasFit
                          ? $"±{est.radius:F1} m" : T("report.ap.centroid_only"))
                      .Append("</td><td>").Append(est.hasFit
                          ? est.pathLossN.ToString("F2") : "–")
                      .Append("</td><td>").Append(est.hasFit
                          ? $"{est.powerAt1m:F1} dBm" : "–")
                      .AppendLine("</td></tr>");
                sb.AppendLine("</table>");
            }

            // ---- Roaming events ----
            var roaming = collector.roaming;
            if (roaming != null && roaming.Events.Count > 0)
            {
                sb.Append("<h2>").Append(T("report.roaming.title"))
                  .AppendLine("</h2><table>");
                sb.AppendLine(T("report.roaming.headers"));
                int i = 1;
                foreach (var ev in roaming.Events)
                    sb.Append("<tr><td>").Append(i++)
                      .Append("</td><td>").Append(Esc(ev.fromBssid))
                      .Append("</td><td>").Append(Esc(ev.toBssid))
                      .Append("</td><td>")
                      .Append(Sample.BandLabel(ev.fromFrequencyMhz)).Append(" → ")
                      .Append(Sample.BandLabel(ev.toFrequencyMhz))
                      .Append("</td><td>")
                      .Append(Fmt(new Vector3(ev.posX, ev.posY, ev.posZ)))
                      .AppendLine("</td></tr>");
                sb.AppendLine("</table>");
            }

            // ---- Wall attenuations ----
            var walls = collector.wallLoss;
            if (walls != null && walls.Losses.Count > 0)
            {
                sb.Append("<h2>").Append(T("report.walls.title"))
                  .AppendLine("</h2><table>");
                sb.AppendLine(T("report.walls.headers"));
                foreach (var w in walls.Losses)
                    sb.Append("<tr><td>").Append(RoomName(collector, w.room))
                      .Append("</td><td>").Append(w.edge + 1)
                      .Append("</td><td>").Append($"{w.lossDb:F1} dB")
                      .Append("</td><td>").Append(w.samplesNear).Append(" / ")
                      .Append(w.samplesFar).AppendLine("</td></tr>");
                sb.AppendLine("</table>");

                // Floor plan with the dB values ON the walls — much easier to
                // map to reality than the table indices. One per storey when
                // multi-storey files exist.
                var wallPlans = new List<(string path, string caption)>();
                if (collector.floorManager != null &&
                    collector.floorManager.Floors.Count > 1)
                {
                    foreach (var level in collector.floorManager.Floors)
                    {
                        string p = Path.Combine(dir,
                            $"{stem}_walls_{level.label}.svg");
                        if (File.Exists(p))
                            wallPlans.Add((p, F("report.walls.caption",
                                LocalizationManager.DisplayFloorLabel(level.label))));
                    }
                }
                else
                {
                    string p = Path.Combine(dir, $"{stem}_walls.svg");
                    if (File.Exists(p))
                        wallPlans.Add((p, T("report.walls.plan")));
                }
                foreach (var (svgPath, caption) in wallPlans)
                {
                    sb.Append("<h3>").Append(Esc(caption)).AppendLine("</h3>");
                    AppendInlineSvg(sb, svgPath);
                }
            }

            // ---- Notes ----
            var notes = collector.spatialNotes;
            if (notes != null && notes.Notes.Count > 0)
            {
                sb.Append("<h2>").Append(T("report.notes.title"))
                  .AppendLine("</h2><table>");
                sb.AppendLine(T("report.notes.headers"));
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
                sb.Append("<h2>").Append(T("report.ping.title"))
                  .AppendLine("</h2><table>");
                sb.AppendLine(T("report.ping.headers"));
                int i = 1;
                foreach (var p in ping.Samples)
                    sb.Append("<tr><td>").Append(i++)
                      .Append("</td><td>")
                      .Append(p.latencyMs >= 0 ? $"{p.latencyMs} ms"
                          : T("ping.error"))
                      .Append("</td><td>")
                      .Append(Fmt(new Vector3(p.posX, p.posY, p.posZ)))
                      .AppendLine("</td></tr>");
                sb.AppendLine("</table>");
            }

            // ---- Local browser-assisted throughput nodes ----
            var throughput = collector.throughputTester;
            if (throughput != null && throughput.NodeCount > 0)
            {
                sb.Append("<h2>").Append(T("report.throughput.title"))
                  .AppendLine("</h2><table>");
                sb.AppendLine(T("report.throughput.headers"));
                int i = 1;
                foreach (var n in throughput.Samples)
                    sb.Append("<tr><td>").Append(i++)
                      .Append("</td><td>").Append($"{n.questToBrowserMbps:F1} Mbit/s")
                      .Append("</td><td>").Append($"{n.browserToQuestMbps:F1} Mbit/s")
                      .Append("</td><td>").Append($"{n.latencyMs:F0} ms")
                      .Append("</td><td>").Append(Sample.BandLabel(n.frequencyMhz))
                      .Append("</td><td>")
                      .Append(Fmt(new Vector3(n.posX, n.posY, n.posZ)))
                      .AppendLine("</td></tr>");
                sb.AppendLine("</table>");
            }

            // ---- Measurements ----
            var measurer = collector.distanceMeasurer;
            if (measurer != null && measurer.Measurements.Count > 0)
            {
                sb.Append("<h2>").Append(T("report.distance.title"))
                  .AppendLine("</h2><table>");
                sb.AppendLine(T("report.distance.headers"));
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
                sb.Append("<h2>").Append(T("report.drill.title"))
                  .AppendLine("</h2><table>");
                sb.AppendLine(T("report.drill.headers"));
                int i = 1;
                foreach (var l in drill.Lines)
                    sb.Append("<tr><td>").Append(i++)
                      .Append("</td><td>").Append(Fmt(l.entry))
                      .Append("</td><td>").Append($"{l.depth:F2} m")
                      .AppendLine("</td></tr>");
                sb.AppendLine("</table>");
            }

            sb.Append("<p class=\"muted\">")
              .Append(F("report.footer", HeatmapColorMapper.WeakDbm,
                  HeatmapColorMapper.StrongDbm))
              .AppendLine("</p>");
            sb.AppendLine("</body></html>");

            string path = Path.Combine(dir, $"{stem}_report.html");
            File.WriteAllText(path, sb.ToString());
            Debug.Log($"[ReportGenerator] Wrote survey report to {path}");
            return path;
        }

        // Renders the SurveyAnalyzer assessment as traffic-light bullets.
        private static void AppendFindings(StringBuilder sb, SampleCollector collector)
        {
            var f = SurveyAnalyzer.Analyze(collector);
            sb.Append("<h2>").Append(T("report.findings.title"))
              .AppendLine("</h2>");
            if (!f.valid)
            {
                sb.Append("<p class=\"muted\">")
                  .Append(F("report.findings.invalid", Esc(f.invalidReason)))
                  .AppendLine("</p>");
                return;
            }

            sb.AppendLine("<ul style=\"line-height:1.7\">");

            // Coverage share vs. threshold.
            string covColor = f.coveredShare >= 0.8f ? "#7CFC7C"
                : f.coveredShare >= 0.5f ? "#FFB86B" : "#FF6B6B";
            sb.Append("<li><b style=\"color:").Append(covColor).Append("\">")
              .Append($"{f.coveredShare * 100f:F0} %")
              .Append(F("report.findings.coverage", f.roomAreaSqm,
                  f.thresholdDbm, f.weakShare * 100f,
                  f.unmeasuredShare * 100f))
              .AppendLine("</li>");

            // Largest contiguous weak zone.
            if (f.largestWeakAreaSqm > 0.5f)
                sb.Append("<li>")
                  .Append(F("report.findings.weak_zone", f.largestWeakAreaSqm,
                      Esc(f.largestWeakRoom), f.largestWeakCenterXZ.x,
                      f.largestWeakCenterXZ.y))
                  .AppendLine("</li>");
            else
                sb.Append("<li style=\"color:#7CFC7C\">")
                  .Append(T("report.findings.no_weak_zone")).AppendLine("</li>");

            // Measurement density per room.
            if (f.lowDensityRooms.Count == 0)
            {
                sb.Append("<li style=\"color:#7CFC7C\">")
                  .Append(T("report.findings.density_ok")).AppendLine("</li>");
            }
            else
            {
                sb.Append("<li style=\"color:#FFB86B\">")
                  .Append(T("report.findings.density_start"));
                for (int i = 0; i < f.lowDensityRooms.Count; i++)
                {
                    var room = f.lowDensityRooms[i];
                    if (i > 0) sb.Append(", ");
                    sb.Append("<b>").Append(Esc(room.name)).Append("</b> (")
                      .Append(F("report.findings.density_room",
                          room.measuredShare * 100f, room.samplesPerSqm))
                      .Append(')');
                }
                sb.Append(T("report.findings.density_end")).AppendLine("</li>");
            }

            // AP overlap / roaming gaps.
            if (f.apCount < 2)
            {
                sb.Append("<li class=\"muted\">")
                  .Append(T("report.findings.one_ap")).AppendLine("</li>");
            }
            else
            {
                sb.Append("<li>").Append(f.apCount)
                  .Append(F("report.findings.overlap", f.roamCount,
                      f.overlapShare * 100f));
                if (f.roamingGapAreaSqm > 1f)
                    sb.Append(" <span style=\"color:#FFB86B\">")
                      .Append(F("report.findings.roaming_gap",
                          f.roamingGapAreaSqm)).Append("</span>");
                sb.AppendLine("</li>");
            }

            sb.AppendLine("</ul>");
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
                        files.Add((p, F("report.floor_caption",
                            LocalizationManager.DisplayFloorLabel(floorLevel.label))));
                }
            }
            if (files.Count == 0)
            {
                string p = Path.Combine(dir, $"{stem}_heatmap.png");
                if (File.Exists(p)) files.Add((p, T("report.floor_heatmap")));
            }
            // Per-AP maps (only written when several BSSIDs were measured).
            var bssids = collector.KnownBssids;
            if (bssids.Count > 1)
                for (int i = 0; i < bssids.Count; i++)
                {
                    string band = Sample.BandLabel(bssids[i].frequencyMhz);
                    string p = Path.Combine(dir,
                        $"{stem}_heatmap_AP{i + 1}_{band.Replace(",", "_").Replace(" ", "")}.png");
                    if (File.Exists(p))
                        files.Add((p, $"AP {i + 1} · {band} ({bssids[i].bssid})"));
                }
            if (files.Count == 0) return;

            sb.Append("<h2>").Append(T("report.heatmaps")).AppendLine("</h2>");
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

        /// <summary>User-given room name, falling back to "Raum n".</summary>
        private static string RoomName(SampleCollector collector, int index)
        {
            var rooms = collector.roomTracer != null ? collector.roomTracer.Rooms : null;
            if (rooms != null && index >= 0 && index < rooms.Count &&
                !string.IsNullOrEmpty(rooms[index].name))
                return Esc(rooms[index].name);
            return F("report.room_fallback", index + 1);
        }

        private static string Fmt(Vector3 v) =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0:F2} / {1:F2} / {2:F2}", v.x, v.y, v.z);

        private static string NoteTypeLabel(string type) => LocalizationManager.Get(
            type == "VirtualAp" ? "note.virtual_ap"
            : type == "Interference" ? "note.interference"
            : "note.text");

        private static string Esc(string s) =>
            string.IsNullOrEmpty(s)
                ? ""
                : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
