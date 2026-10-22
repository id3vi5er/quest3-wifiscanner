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
        public enum SurveyCellStatus
        {
            Unplanned,
            Open,
            Measured,
            Sufficient,
            Critical,
            Unwalkable,
        }

        public enum SurveyLayerMode { Off, Progress, Confidence }

        [System.Serializable]
        public class QualityConfig
        {
            public float cellSize = 0.5f;
            public float measurementRadius = 0.75f;
            public int minimumEffectiveMeasurements = 3;
            public float maximumRssiStdDev = 5f;
            public float staleAfterMinutes = 60f;
            public float targetMeasurementSpacing = 0.5f;
        }

        [System.Serializable]
        public class CellQuality
        {
            public float score;
            public float densityScore;
            public float distanceScore;
            public float stabilityScore = -1f;
            public float freshnessScore;
            public float trackingScore = -1f;
            public float alignmentScore = -1f;
            public float nearestSampleDistance = -1f;
            public float rssiMeanDbm;
            public float rssiVariance = -1f;
            public float rssiStdDev = -1f;
            public int effectiveMeasurements;
            public int aggregatePoints;
            public long newestTimestampMs;
            public bool interpolated;
            public bool stale;
        }

        [System.Serializable]
        public class SurveyCell
        {
            public string id;
            public int floor;
            public string floorLabel;
            public int roomIndex;
            public string roomName;
            public float centerX;
            public float centerZ;
            public float gridX;
            public float gridZ;
            public SurveyCellStatus status;
            public string statusName;
            public CellQuality quality;
        }

        [System.Serializable]
        public class SurveyFloorResult
        {
            public int floor;
            public string floorLabel;
            public float cellSize;
            public float gridYawDegrees;
            public int totalCells;
            public int openCells;
            public int measuredCells;
            public int sufficientCells;
            public int criticalCells;
            public int unwalkableCells;
            public float completionPercent;
            public float averageConfidence;
            public bool valid;
            public List<SurveyCell> cells = new List<SurveyCell>();
        }

        public const int DefaultThresholdDbm = -75;
        private const float CellSize = 0.3f;
        private const float SampleRadius = 1.5f;
        private const float MinSamplesPerSqm = 0.5f;
        private const float MinMeasuredShare = 0.6f;

        /// <summary>
        /// Shared live/export survey calculation. Cells exist only inside the
        /// traced room polygons. Unknown tracking/alignment values are omitted
        /// from the weighted score instead of being treated as precise data.
        /// </summary>
        public static SurveyFloorResult BuildSurvey(
            IReadOnlyList<Sample> samples, IReadOnlyList<RoomOutline> rooms,
            int floorIndex, string floorLabel, float floorBaseY,
            QualityConfig config, ISet<string> unwalkable = null,
            string bssidFilter = null, long nowMs = 0,
            float gridYawDegrees = 0f)
        {
            config ??= new QualityConfig();
            float cell = Mathf.Max(0.1f, config.cellSize);
            float radius = Mathf.Max(0.1f, config.measurementRadius);
            nowMs = nowMs > 0 ? nowMs :
                System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var result = new SurveyFloorResult
            {
                floor = floorIndex,
                floorLabel = floorLabel ?? floorIndex.ToString(),
                cellSize = cell,
                gridYawDegrees = gridYawDegrees,
            };
            if (rooms == null || samples == null) return result;

            // Candidate samples are storey- and (optionally) AP-exact.
            var candidates = new List<Sample>();
            for (int i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                if (s.floor != floorIndex) continue;
                if (!string.IsNullOrEmpty(bssidFilter) && s.bssid != bssidFilter)
                    continue;
                s.NormalizeLegacyMetadata();
                candidates.Add(s);
            }

            for (int roomIndex = 0; roomIndex < rooms.Count; roomIndex++)
            {
                var room = rooms[roomIndex];
                if (room?.cornersXZ == null || room.cornersXZ.Count < 3 ||
                    Mathf.Abs(room.floorY - floorBaseY) > 1f)
                    continue;
                Vector2 firstGrid = RotateXZ(room.cornersXZ[0], -gridYawDegrees);
                Vector2 min = firstGrid, max = min;
                for (int i = 1; i < room.cornersXZ.Count; i++)
                {
                    Vector2 corner = RotateXZ(room.cornersXZ[i], -gridYawDegrees);
                    min = Vector2.Min(min, corner);
                    max = Vector2.Max(max, corner);
                }
                int x0 = Mathf.FloorToInt(min.x / cell);
                int x1 = Mathf.CeilToInt(max.x / cell);
                int z0 = Mathf.FloorToInt(min.y / cell);
                int z1 = Mathf.CeilToInt(max.y / cell);
                for (int z = z0; z < z1; z++)
                for (int x = x0; x < x1; x++)
                {
                    var gridCenter = new Vector2((x + 0.5f) * cell,
                                                 (z + 0.5f) * cell);
                    Vector2 center = RotateXZ(gridCenter, gridYawDegrees);
                    if (!PointInPolygon(center, room.cornersXZ)) continue;
                    string id = $"{floorIndex}:{x}:{z}";
                    var surveyCell = new SurveyCell
                    {
                        id = id,
                        floor = floorIndex,
                        floorLabel = result.floorLabel,
                        roomIndex = roomIndex,
                        roomName = string.IsNullOrEmpty(room.name)
                            ? $"Room {roomIndex + 1}" : room.name,
                        centerX = center.x,
                        centerZ = center.y,
                        gridX = gridCenter.x,
                        gridZ = gridCenter.y,
                    };
                    surveyCell.quality = ComputeQuality(center, candidates,
                        radius, config, nowMs);
                    if (unwalkable != null && unwalkable.Contains(id))
                        surveyCell.status = SurveyCellStatus.Unwalkable;
                    else if (surveyCell.quality.effectiveMeasurements == 0)
                        surveyCell.status = SurveyCellStatus.Open;
                    else if (surveyCell.quality.effectiveMeasurements <
                             config.minimumEffectiveMeasurements)
                        surveyCell.status = SurveyCellStatus.Measured;
                    else if (surveyCell.quality.rssiStdDev >= 0f &&
                             surveyCell.quality.rssiStdDev <=
                             config.maximumRssiStdDev &&
                             surveyCell.quality.nearestSampleDistance <= radius)
                        surveyCell.status = SurveyCellStatus.Sufficient;
                    else
                        surveyCell.status = SurveyCellStatus.Critical;
                    surveyCell.statusName = surveyCell.status.ToString();
                    result.cells.Add(surveyCell);
                }
            }
            Summarize(result);
            return result;
        }

        private static Vector2 RotateXZ(Vector2 value, float degrees)
        {
            double radians = degrees * (System.Math.PI / 180.0);
            float sin = (float)System.Math.Sin(radians);
            float cos = (float)System.Math.Cos(radians);
            return new Vector2(value.x * cos + value.y * sin,
                               -value.x * sin + value.y * cos);
        }

        private static CellQuality ComputeQuality(Vector2 center,
            List<Sample> samples, float radius, QualityConfig config, long nowMs)
        {
            var q = new CellQuality();
            float r2 = radius * radius;
            // In the unfiltered "all APs" view, never interpret the level
            // difference between neighbouring BSSIDs as temporal RSSI noise.
            // Use the locally best-supported BSSID for density and variance.
            var apCounts = new Dictionary<string, int>();
            for (int i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                float dx = s.posX - center.x, dz = s.posZ - center.y;
                if (dx * dx + dz * dz > r2) continue;
                string key = s.bssid ?? "";
                apCounts.TryGetValue(key, out int count);
                apCounts[key] = count + Mathf.Max(1, s.sampleCount);
            }
            string dominantBssid = null;
            int dominantCount = -1;
            foreach (var ap in apCounts)
                if (ap.Value > dominantCount)
                { dominantCount = ap.Value; dominantBssid = ap.Key; }

            float nearest = float.MaxValue;
            double mean = 0d, m2 = 0d;
            int totalN = 0;
            double trackingSum = 0d, alignmentSum = 0d;
            int trackingN = 0, alignmentN = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                if (dominantBssid != null && (s.bssid ?? "") != dominantBssid)
                    continue;
                float dx = s.posX - center.x, dz = s.posZ - center.y;
                float dist2 = dx * dx + dz * dz;
                if (dist2 > r2) continue;
                float dist = Mathf.Sqrt(dist2);
                if (dist < nearest) nearest = dist;
                int n = Mathf.Max(1, s.sampleCount);
                double sampleMean = s.sampleCount > 0 ? s.rssiMeanDbm : s.rssiDbm;
                double sampleM2 = s.sampleCount > 1 ? Mathf.Max(0f, s.rssiM2) : 0d;
                if (totalN == 0)
                {
                    mean = sampleMean;
                    m2 = sampleM2;
                    totalN = n;
                }
                else
                {
                    double delta = sampleMean - mean;
                    int combined = totalN + n;
                    mean += delta * n / combined;
                    m2 += sampleM2 + delta * delta * totalN * n / combined;
                    totalN = combined;
                }
                q.aggregatePoints++;
                q.newestTimestampMs = System.Math.Max(q.newestTimestampMs,
                    s.lastTimestampMs > 0 ? s.lastTimestampMs : s.timestampMs);
                if (s.trackingQuality >= 0f)
                { trackingSum += s.trackingQuality * n; trackingN += n; }
                if (s.alignmentQuality >= 0f)
                { alignmentSum += s.alignmentQuality * n; alignmentN += n; }
            }
            q.effectiveMeasurements = totalN;
            q.nearestSampleDistance = nearest < float.MaxValue ? nearest : -1f;
            q.interpolated = nearest > config.targetMeasurementSpacing;
            if (totalN > 0) q.rssiMeanDbm = (float)mean;
            if (totalN > 1)
            {
                q.rssiVariance = (float)(m2 / (totalN - 1));
                q.rssiStdDev = Mathf.Sqrt(Mathf.Max(0f, q.rssiVariance));
            }
            q.densityScore = Mathf.Clamp01(totalN /
                (float)Mathf.Max(1, config.minimumEffectiveMeasurements));
            q.distanceScore = nearest < float.MaxValue
                ? Mathf.Clamp01(1f - nearest / radius) : 0f;
            if (q.rssiStdDev >= 0f)
                q.stabilityScore = Mathf.Clamp01(1f - q.rssiStdDev /
                    Mathf.Max(0.1f, config.maximumRssiStdDev * 2f));
            float ageMinutes = q.newestTimestampMs > 0
                ? Mathf.Max(0f, (nowMs - q.newestTimestampMs) / 60000f)
                : config.staleAfterMinutes;
            q.freshnessScore = Mathf.Clamp01(1f - ageMinutes /
                Mathf.Max(1f, config.staleAfterMinutes));
            q.stale = ageMinutes >= config.staleAfterMinutes;
            if (trackingN > 0) q.trackingScore = (float)(trackingSum / trackingN);
            if (alignmentN > 0) q.alignmentScore = (float)(alignmentSum / alignmentN);

            float weighted = q.densityScore * 0.28f + q.distanceScore * 0.24f +
                             q.freshnessScore * 0.14f;
            float weights = 0.66f;
            if (q.stabilityScore >= 0f)
            { weighted += q.stabilityScore * 0.20f; weights += 0.20f; }
            if (q.trackingScore >= 0f)
            { weighted += q.trackingScore * 0.07f; weights += 0.07f; }
            if (q.alignmentScore >= 0f)
            { weighted += q.alignmentScore * 0.07f; weights += 0.07f; }
            q.score = weights > 0f ? Mathf.Clamp01(weighted / weights) * 100f : 0f;
            return q;
        }

        private static void Summarize(SurveyFloorResult result)
        {
            float confidence = 0f;
            for (int i = 0; i < result.cells.Count; i++)
            {
                var c = result.cells[i];
                switch (c.status)
                {
                    case SurveyCellStatus.Open: result.openCells++; break;
                    case SurveyCellStatus.Measured: result.measuredCells++; break;
                    case SurveyCellStatus.Sufficient: result.sufficientCells++; break;
                    case SurveyCellStatus.Critical: result.criticalCells++; break;
                    case SurveyCellStatus.Unwalkable: result.unwalkableCells++; break;
                }
                if (c.status != SurveyCellStatus.Unwalkable)
                    confidence += c.quality.score;
            }
            result.totalCells = result.cells.Count;
            int walkable = result.totalCells - result.unwalkableCells;
            result.completionPercent = walkable > 0
                ? result.sufficientCells * 100f / walkable : 0f;
            result.averageConfidence = walkable > 0 ? confidence / walkable : 0f;
            result.valid = walkable > 0 && result.sufficientCells == walkable;
        }

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
                findings.invalidReason = LocalizationManager.Get(
                    "report.invalid.no_rooms");
                return findings;
            }
            var samples = collector.Samples;
            if (samples.Count == 0)
            {
                findings.invalidReason = LocalizationManager.Get(
                    "report.invalid.no_points");
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
                    ? LocalizationManager.Format("report.room_fallback", r + 1)
                    : room.name;

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
                findings.invalidReason = LocalizationManager.Get(
                    "report.invalid.empty_rooms");
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
