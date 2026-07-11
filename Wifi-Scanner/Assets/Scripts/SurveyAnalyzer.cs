using System.Collections.Generic;
using UnityEngine;

namespace WifiScanner
{
    /// <summary>
    /// Automatic survey findings for the report: rasterises every traced room
    /// (0.3 m cells, nearest measurement within 1.5 m on the room's storey)
    /// and derives an assessment instead of raw tables only:
    ///   - coverage share at/above a threshold (default −75 dBm)
    ///   - the largest contiguous weak zone (area, position, room)
    ///   - rooms with insufficient measurement density
    ///   - AP overlap share and potential roaming gaps (weak single-AP areas)
    /// Pure computation, called once by the ReportGenerator at export time.
    /// </summary>
    public static class SurveyAnalyzer
    {
        public const int DefaultThresholdDbm = -75;
        private const float CellSize = 0.3f;
        private const float SampleRadius = 1.5f;
        private const float MinSamplesPerSqm = 0.5f;
        private const float MinMeasuredShare = 0.6f;

        public class RoomFinding
        {
            public string name;
            public float areaSqm;
            public float measuredShare;   // 0..1 of the room's cells
            public float samplesPerSqm;
        }

        public class Findings
        {
            public bool valid;
            public string invalidReason = "";
            public int thresholdDbm;
            public float roomAreaSqm;
            public float coveredShare;    // measured cells >= threshold
            public float weakShare;       // measured cells < threshold
            public float unmeasuredShare;
            public float largestWeakAreaSqm;
            public Vector2 largestWeakCenterXZ;
            public string largestWeakRoom = "";
            public readonly List<RoomFinding> lowDensityRooms = new List<RoomFinding>();
            public int apCount;
            public int roamCount;
            public float overlapShare;      // measured cells reached by >= 2 APs
            public float roamingGapAreaSqm; // weak cells with only ONE AP nearby
        }

        public static Findings Analyze(SampleCollector collector,
            int thresholdDbm = DefaultThresholdDbm)
        {
            var findings = new Findings { thresholdDbm = thresholdDbm };
            var rooms = collector.roomTracer != null ? collector.roomTracer.Rooms : null;
            if (rooms == null || rooms.Count == 0)
            {
                findings.invalidReason = "keine Räume gezeichnet";
                return findings;
            }
            var samples = collector.Samples;
            if (samples.Count == 0)
            {
                findings.invalidReason = "keine Messpunkte";
                return findings;
            }
            findings.valid = true;
            findings.apCount = collector.KnownBssids.Count;
            findings.roamCount = collector.roaming != null
                ? collector.roaming.Events.Count : 0;

            // --- XZ spatial hash over all samples (storey filter per query) ---
            var hash = new Dictionary<long, List<int>>();
            for (int i = 0; i < samples.Count; i++)
            {
                long key = HashKey(
                    Mathf.FloorToInt(samples[i].posX / SampleRadius),
                    Mathf.FloorToInt(samples[i].posZ / SampleRadius));
                if (!hash.TryGetValue(key, out var list))
                    hash[key] = list = new List<int>();
                list.Add(i);
            }

            int totalCells = 0, coveredCells = 0, weakCells = 0, measuredCells = 0;
            int overlapCells = 0, gapCells = 0;
            var nearbyBssids = new HashSet<string>();

            for (int r = 0; r < rooms.Count; r++)
            {
                var room = rooms[r];
                var poly = room.cornersXZ;
                if (poly.Count < 3) continue;
                string roomName = string.IsNullOrEmpty(room.name)
                    ? $"Raum {r + 1}" : room.name;

                // Room bbox -> cell grid; remember weak cells for clustering.
                Vector2 min = poly[0], max = poly[0];
                foreach (var c in poly)
                {
                    min = Vector2.Min(min, c);
                    max = Vector2.Max(max, c);
                }
                int gw = Mathf.Max(1, Mathf.CeilToInt((max.x - min.x) / CellSize));
                int gh = Mathf.Max(1, Mathf.CeilToInt((max.y - min.y) / CellSize));
                var weak = new bool[gw * gh];
                int roomCells = 0, roomMeasured = 0, roomSamples = 0;

                float yLo = room.floorY - 0.3f;
                float yHi = room.floorY + (room.height > 0.05f ? room.height : 2.7f);

                for (int gy = 0; gy < gh; gy++)
                for (int gx = 0; gx < gw; gx++)
                {
                    var p = new Vector2(min.x + (gx + 0.5f) * CellSize,
                                        min.y + (gy + 0.5f) * CellSize);
                    if (!PointInPolygon(p, poly)) continue;
                    roomCells++;

                    // Nearest sample on this storey within SampleRadius.
                    float bestSqr = SampleRadius * SampleRadius;
                    int bestRssi = 0;
                    bool hit = false;
                    nearbyBssids.Clear();
                    int hx = Mathf.FloorToInt(p.x / SampleRadius);
                    int hz = Mathf.FloorToInt(p.y / SampleRadius);
                    for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (!hash.TryGetValue(HashKey(hx + dx, hz + dz), out var list))
                            continue;
                        foreach (int i in list)
                        {
                            var s = samples[i];
                            if (s.posY < yLo || s.posY > yHi) continue;
                            float ddx = s.posX - p.x;
                            float ddz = s.posZ - p.y;
                            float sqr = ddx * ddx + ddz * ddz;
                            if (sqr > SampleRadius * SampleRadius) continue;
                            if (!string.IsNullOrEmpty(s.bssid))
                                nearbyBssids.Add(s.bssid);
                            if (sqr <= bestSqr)
                            {
                                bestSqr = sqr;
                                bestRssi = s.rssiDbm;
                                hit = true;
                            }
                        }
                    }

                    if (!hit) continue;
                    measuredCells++;
                    roomMeasured++;
                    if (nearbyBssids.Count >= 2) overlapCells++;
                    if (bestRssi >= thresholdDbm)
                    {
                        coveredCells++;
                    }
                    else
                    {
                        weakCells++;
                        weak[gy * gw + gx] = true;
                        if (nearbyBssids.Count <= 1) gapCells++;
                    }
                }
                totalCells += roomCells;

                // Largest contiguous weak zone in THIS room (4-neighbour BFS).
                FindLargestCluster(weak, gw, gh, min, out float areaSqm,
                    out Vector2 center);
                if (areaSqm > findings.largestWeakAreaSqm)
                {
                    findings.largestWeakAreaSqm = areaSqm;
                    findings.largestWeakCenterXZ = center;
                    findings.largestWeakRoom = roomName;
                }

                // Measurement density per room.
                for (int i = 0; i < samples.Count; i++)
                {
                    var s = samples[i];
                    if (s.posY < yLo || s.posY > yHi) continue;
                    if (PointInPolygon(new Vector2(s.posX, s.posZ), poly))
                        roomSamples++;
                }
                float area = roomCells * CellSize * CellSize;
                float density = area > 0.5f ? roomSamples / area : 0f;
                float share = roomCells > 0 ? roomMeasured / (float)roomCells : 0f;
                if (area > 1f && (density < MinSamplesPerSqm || share < MinMeasuredShare))
                    findings.lowDensityRooms.Add(new RoomFinding
                    {
                        name = roomName,
                        areaSqm = area,
                        measuredShare = share,
                        samplesPerSqm = density,
                    });
            }

            if (totalCells == 0)
            {
                findings.valid = false;
                findings.invalidReason = "Raumflächen leer";
                return findings;
            }
            float cellArea = CellSize * CellSize;
            findings.roomAreaSqm = totalCells * cellArea;
            findings.coveredShare = coveredCells / (float)totalCells;
            findings.weakShare = weakCells / (float)totalCells;
            findings.unmeasuredShare = 1f - (measuredCells / (float)totalCells);
            findings.overlapShare = measuredCells > 0
                ? overlapCells / (float)measuredCells : 0f;
            findings.roamingGapAreaSqm = gapCells * cellArea;
            return findings;
        }

        // BFS over the weak mask; returns the biggest component's area + centre.
        private static void FindLargestCluster(bool[] weak, int gw, int gh,
            Vector2 min, out float areaSqm, out Vector2 centerXZ)
        {
            areaSqm = 0f;
            centerXZ = Vector2.zero;
            var visited = new bool[weak.Length];
            var queue = new Queue<int>();
            for (int start = 0; start < weak.Length; start++)
            {
                if (!weak[start] || visited[start]) continue;
                int count = 0;
                float sumX = 0f, sumY = 0f;
                visited[start] = true;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    int idx = queue.Dequeue();
                    int gx = idx % gw, gy = idx / gw;
                    count++;
                    sumX += gx;
                    sumY += gy;
                    Consider(gx - 1, gy);
                    Consider(gx + 1, gy);
                    Consider(gx, gy - 1);
                    Consider(gx, gy + 1);

                    void Consider(int nx, int ny)
                    {
                        if (nx < 0 || nx >= gw || ny < 0 || ny >= gh) return;
                        int n = ny * gw + nx;
                        if (!weak[n] || visited[n]) return;
                        visited[n] = true;
                        queue.Enqueue(n);
                    }
                }
                float area = count * CellSize * CellSize;
                if (area > areaSqm)
                {
                    areaSqm = area;
                    centerXZ = new Vector2(
                        min.x + (sumX / count + 0.5f) * CellSize,
                        min.y + (sumY / count + 0.5f) * CellSize);
                }
            }
        }

        private static bool PointInPolygon(Vector2 p, List<Vector2> poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                if (poly[i].y > p.y != poly[j].y > p.y &&
                    p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) /
                          (poly[j].y - poly[i].y) + poly[i].x)
                    inside = !inside;
            }
            return inside;
        }

        private static long HashKey(int x, int z) => ((long)x << 32) ^ (uint)z;
    }
}
