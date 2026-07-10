using System;
using System.Collections.Generic;
using UnityEngine;

namespace WifiScanner
{
    /// <summary>One estimated wall attenuation (for the _walls.json export).</summary>
    [Serializable]
    public class WallLoss
    {
        public int room;       // room index
        public int edge;       // polygon edge index
        public float lossDb;   // estimated attenuation
        public int samplesNear; // samples on the AP side
        public int samplesFar;  // samples on the other side
    }

    /// <summary>
    /// Estimates the attenuation of each traced wall from the measurements on
    /// its two sides. Uses the ApLocator's fitted path-loss model: the
    /// distance-corrected value v = rssi + 10·n·log10(d) estimates P1m minus
    /// all obstacle losses on the path — the difference of the side means is
    /// therefore (approximately) THIS wall's loss, assuming the AP-side
    /// samples pass through one wall less. Only walls with enough samples
    /// within <see cref="bandwidth"/> metres on BOTH sides get an estimate.
    ///
    /// Passive display tool: "Wand-Dämpfung" on the panel (Räume tab) toggles
    /// floating labels at the wall midpoints. The per-wall values also feed
    /// the coverage prediction (CoverageSimulator) and are exported to
    /// {stem}_walls.json. Created and wired by SceneBootstrap.
    /// </summary>
    public class WallLossEstimator : MonoBehaviour
    {
        [Header("Wired by SceneBootstrap")]
        public SampleCollector collector;
        public RoomTracer roomTracer;
        public ApLocator apLocator;

        [Header("Estimation")]
        [Tooltip("Max distance of a sample from the wall plane to count (m).")]
        public float bandwidth = 1.5f;
        [Tooltip("Minimum samples on EACH side of a wall for an estimate.")]
        public int minSamplesPerSide = 5;
        [Tooltip("Walls shorter than this are skipped (m).")]
        public float minWallLength = 0.5f;
        [Tooltip("Seconds between re-estimates while labels are visible.")]
        public float updateInterval = 3f;
        public bool visibleAtStart = false;

        public bool IsVisible { get; private set; }
        /// <summary>All current estimates (rebuilt on every pass).</summary>
        public IReadOnlyList<WallLoss> Losses => _losses;

        private readonly List<WallLoss> _losses = new List<WallLoss>();
        private readonly Dictionary<long, float> _lossByWall =
            new Dictionary<long, float>();
        private readonly List<WorldLabel> _labels = new List<WorldLabel>();
        private GameObject _root;
        private float _timer;
        private int _lastRevision = -1;

        private void Start()
        {
            _root = new GameObject("WallLossLabels");
            _root.transform.SetParent(transform, false);
            IsVisible = visibleAtStart;
        }

        public void Toggle() => SetVisible(!IsVisible);

        public void SetVisible(bool visible)
        {
            IsVisible = visible;
            if (!visible) HideLabels();
            else _timer = updateInterval; // estimate on the next Update
        }

        /// <summary>Estimated loss for one wall; false when no estimate exists.
        /// Used by the coverage prediction.</summary>
        public bool TryGetWallLoss(int roomIdx, int edgeIdx, out float lossDb)
        {
            return _lossByWall.TryGetValue(Key(roomIdx, edgeIdx), out lossDb);
        }

        private static long Key(int room, int edge) => ((long)room << 20) | (uint)edge;

        private void Update()
        {
            if (!IsVisible || collector == null || roomTracer == null) return;

            _timer += Time.deltaTime;
            if (_timer < updateInterval) return;
            // Skip the pass while nothing changed (labels stay put).
            if (collector.DataRevision == _lastRevision) return;
            _timer = 0f;
            _lastRevision = collector.DataRevision;

            Recompute();
            RefreshLabels();
        }

        /// <summary>Runs one estimation pass (also callable on demand, e.g.
        /// right before an export or a coverage simulation).</summary>
        public void Recompute()
        {
            _losses.Clear();
            _lossByWall.Clear();
            if (collector == null || roomTracer == null ||
                apLocator == null || !apLocator.HasFit)
                return;

            var samples = collector.Samples;
            var rooms = roomTracer.Rooms;
            Vector3 ap = apLocator.FixPosition;
            float n = apLocator.PathLossExponent;
            Vector2 apXZ = new Vector2(ap.x, ap.z);
            // Model and samples must belong to the SAME access point —
            // foreign-BSSID samples (after roaming) would skew the estimate.
            string apBssid = apLocator.ActiveBssid;

            for (int r = 0; r < rooms.Count; r++)
            {
                var room = rooms[r];
                int corners = room.cornersXZ.Count;
                float roomTop = room.floorY + (room.height > 0.05f ? room.height : 2.7f);
                for (int e = 0; e < corners; e++)
                {
                    if (!RoomTracer.TryGetEdge(room, e, out Vector2 a,
                            out Vector2 dir, out float len) || len < minWallLength)
                        continue;

                    // Which side of the wall is the AP on? Its samples pass
                    // through one wall less towards this wall.
                    Vector2 toAp = apXZ - a;
                    float apSide = Mathf.Sign(dir.x * toAp.y - dir.y * toAp.x);
                    if (apSide == 0f) continue;

                    double sumNear = 0, sumFar = 0;
                    int cntNear = 0, cntFar = 0;
                    for (int i = 0; i < samples.Count; i++)
                    {
                        var s = samples[i];
                        if (apBssid.Length > 0 && s.bssid != apBssid) continue;
                        if (s.posY < room.floorY - 0.3f || s.posY > roomTop + 0.3f)
                            continue;
                        float dx = s.posX - a.x;
                        float dz = s.posZ - a.y;
                        float along = dir.x * dx + dir.y * dz;
                        if (along < 0f || along > len) continue;
                        float perp = dir.x * dz - dir.y * dx; // signed distance
                        if (Mathf.Abs(perp) > bandwidth || Mathf.Abs(perp) < 0.05f)
                            continue;

                        // Distance-corrected value: estimates P1m − path losses.
                        float d = Vector3.Distance(
                            new Vector3(s.posX, s.posY, s.posZ), ap);
                        double v = s.rssiDbm
                            + 10.0 * n * Math.Log10(Mathf.Max(d, 0.3f));

                        if (Mathf.Sign(perp) == apSide) { sumNear += v; cntNear++; }
                        else { sumFar += v; cntFar++; }
                    }

                    if (cntNear < minSamplesPerSide || cntFar < minSamplesPerSide)
                        continue;
                    float loss = (float)(sumNear / cntNear - sumFar / cntFar);
                    loss = Mathf.Clamp(loss, 0f, 30f); // negative = noise
                    _losses.Add(new WallLoss
                    {
                        room = r,
                        edge = e,
                        lossDb = loss,
                        samplesNear = cntNear,
                        samplesFar = cntFar,
                    });
                    _lossByWall[Key(r, e)] = loss;
                }
            }
        }

        // One floating label per estimated wall, at the wall's midpoint.
        private void RefreshLabels()
        {
            while (_labels.Count < _losses.Count)
                _labels.Add(WorldLabel.Create(_root.transform));

            var rooms = roomTracer.Rooms;
            for (int i = 0; i < _labels.Count; i++)
            {
                bool used = i < _losses.Count;
                _labels[i].SetVisible(used && IsVisible);
                if (!used) continue;

                var wl = _losses[i];
                if (wl.room >= rooms.Count) continue;
                var room = rooms[wl.room];
                if (!RoomTracer.TryGetEdge(room, wl.edge, out Vector2 a,
                        out Vector2 dir, out float len))
                    continue;
                Vector2 mid = a + dir * (len * 0.5f);
                float y = room.floorY + (room.height > 0.05f ? room.height : 2.4f) * 0.55f;
                _labels[i].SetWorldPosition(new Vector3(mid.x, y, mid.y));
                _labels[i].SetText($"≈ {wl.lossDb:F1} dB");
                _labels[i].SetColor(wl.lossDb > 8f
                    ? new Color(1f, 0.5f, 0.3f)
                    : new Color(0f, 0.86f, 1f));
            }
        }

        private void HideLabels()
        {
            foreach (var label in _labels)
                label.SetVisible(false);
        }
    }
}
