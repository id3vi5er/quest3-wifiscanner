using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace WifiScanner
{
    /// <summary>
    /// Access-Point locator. Two stages:
    ///  1. Weighted centroid (w = 10^(rssi/10), linear power) as a robust
    ///     initial guess.
    ///  2. Log-distance path-loss fit: RSSI(d) = P1m − 10·n·log10(d). A small
    ///     XZ pattern search moves the AP candidate while (P1m, n) are solved
    ///     per step by linear regression over log-distance — the Y stays at
    ///     the centroid height (all samples sit near head height, so the AP's
    ///     height is not observable). The fit residual yields an uncertainty
    ///     radius, drawn as a cyan ring around the router icon.
    ///
    /// The heavy fit is throttled (refitInterval); the icon glides to the
    /// estimate via SmoothDamp. Passive tool (no trigger): the panel button
    /// just shows/hides the icon. Fit results are streamed ("apfix" packet)
    /// and written to {stem}_apfix.json on export. The fitted model (P1m, n)
    /// also feeds the wall-loss estimator and the coverage prediction.
    /// Created and wired by SceneBootstrap.
    /// </summary>
    public class ApLocator : MonoBehaviour
    {
        [Header("Wired by SceneBootstrap")]
        public SampleCollector collector;
        [Tooltip("Material ASSET (URP/Unlit) cloned for the icon quad.")]
        public Material visualMaterial;
        public NetworkStreamer streamer;

        [Header("Locator")]
        [Tooltip("Minimum samples before an estimate is shown.")]
        public int minSamples = 15;
        [Tooltip("Lift the icon this far above the weighted-centroid height (m). " +
                 "Head-height samples usually sit below a ceiling/wall AP.")]
        public float heightOffset = 0.4f;
        [Tooltip("Edge length of the router icon (m).")]
        public float iconSize = 0.22f;
        [Tooltip("Smoothing time for the icon glide (s).")]
        public float smoothTime = 0.6f;
        [Tooltip("Minimum seconds between path-loss refits (the fit is ~100x " +
                 "more expensive than the centroid).")]
        public float refitInterval = 2f;
        public bool visibleAtStart = false;

        public bool IsVisible => _root != null && _root.activeSelf;
        public bool HasFix { get; private set; }
        public Vector3 FixPosition { get; private set; }
        public string FixSsid { get; private set; } = "";
        public int FixRssi { get; private set; } = -127;
        /// <summary>True when the path-loss fit converged (not just centroid).</summary>
        public bool HasFit { get; private set; }
        /// <summary>Position uncertainty of the fix (m, from fit residual).</summary>
        public float FixRadius { get; private set; } = 3f;
        /// <summary>Fitted path-loss exponent n (free space = 2).</summary>
        public float PathLossExponent { get; private set; } = 2.7f;
        /// <summary>Fitted power at 1 m distance (dBm).</summary>
        public float PowerAt1mDbm { get; private set; } = -40f;

        /// <summary>One AP estimate (per BSSID). The single-AP properties above
        /// always mirror the ACTIVE estimate (selected per-AP heatmap, else the
        /// AP with the most samples) so wall-loss/coverage consumers keep
        /// working unchanged.</summary>
        [Serializable]
        public class ApEstimate
        {
            public string bssid = "";
            public string ssid = "";
            public int frequencyMhz;
            public int strongestRssi = -127;
            public float posX, posY, posZ;
            public bool hasFit;
            public float radius;
            public float pathLossN = 2.7f;
            public float powerAt1m = -40f;
            public int sampleCount;

            public Vector3 Position => new Vector3(posX, posY, posZ);
        }

        /// <summary>All per-BSSID estimates (first-seen order).</summary>
        public System.Collections.Generic.IReadOnlyList<ApEstimate> Estimates => _estimates;

        /// <summary>BSSID of the ACTIVE estimate ("" while none) — consumers
        /// that mix model and samples (wall loss) filter their samples to it.</summary>
        public string ActiveBssid { get; private set; } = "";

        /// <summary>Current per-AP display filter ("" = all APs) — the minimap
        /// mirrors it for its AP markers.</summary>
        public string BssidFilter => _bssidFilter;

        // Per-estimate visuals (icon + label + uncertainty ring).
        private class Visual
        {
            public Transform icon;
            public WorldLabel label;
            public LineRenderer ring;
            public Vector3 velocity;
            public bool hasTarget;
        }

        private readonly System.Collections.Generic.List<ApEstimate> _estimates =
            new System.Collections.Generic.List<ApEstimate>();
        private readonly System.Collections.Generic.Dictionary<string, Visual> _visuals =
            new System.Collections.Generic.Dictionary<string, Visual>();
        private string _bssidFilter = ""; // "" = alle APs sichtbar

        private GameObject _root;
        private Material _iconMat;
        private Mesh _quadMesh;

        private int _lastRevision = -1;
        private float _refitTimer;
        private bool _dirty;
        private float _resendTimer;

        private static readonly Vector2[] SearchDirs =
        {
            new Vector2(1f, 0f), new Vector2(-1f, 0f),
            new Vector2(0f, 1f), new Vector2(0f, -1f),
        };
        private const int RingSegments = 40;

        [Serializable]
        private class ApFixPacket
        {
            public string type = "apfix";
            // Legacy single-AP fields = the ACTIVE estimate (receiver.py).
            public float posX, posY, posZ;
            public string ssid;
            public string bssid;
            public int rssi;
            public float radius;       // uncertainty (m); 0 = centroid only
            public float pathLossN;    // fitted exponent
            public float powerAt1m;    // fitted P1m (dBm)
            // All per-BSSID estimates (dashboard draws these).
            public System.Collections.Generic.List<ApEstimate> aps;
        }

        private Material _ringMat;

        private void Start()
        {
            var temp = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _quadMesh = temp.GetComponent<MeshFilter>().sharedMesh;
            Destroy(temp);

            _root = new GameObject("ApLocatorVisuals");
            _root.transform.SetParent(transform, false);

            if (visualMaterial != null)
            {
                _iconMat = new Material(visualMaterial); // asset clone keeps its variant
                MakeIconMaterial(_iconMat, CreateRouterTexture());
                _ringMat = new Material(visualMaterial);
                _ringMat.SetColor("_BaseColor", new Color(0f, 0.86f, 1f, 0.55f));
            }
            else
            {
                Debug.LogError("[ApLocator] visualMaterial not assigned — assign the " +
                               "point material in SceneBootstrap.");
            }

            _root.SetActive(visibleAtStart);
        }

        // Icon + label + uncertainty ring for ONE estimate (per BSSID).
        private Visual CreateVisual()
        {
            var v = new Visual();
            var iconGo = new GameObject("RouterIcon");
            iconGo.transform.SetParent(_root.transform, false);
            iconGo.transform.localScale = Vector3.one * iconSize;
            iconGo.AddComponent<MeshFilter>().sharedMesh = _quadMesh;
            var mr = iconGo.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _iconMat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            v.icon = iconGo.transform;

            v.label = WorldLabel.Create(_root.transform);

            var ringGo = new GameObject("UncertaintyRing");
            ringGo.transform.SetParent(_root.transform, false);
            v.ring = ringGo.AddComponent<LineRenderer>();
            v.ring.positionCount = RingSegments + 1;
            v.ring.startWidth = v.ring.endWidth = 0.012f;
            v.ring.loop = false;
            v.ring.shadowCastingMode = ShadowCastingMode.Off;
            v.ring.receiveShadows = false;
            if (_ringMat != null) v.ring.sharedMaterial = _ringMat;
            v.ring.enabled = false;
            return v;
        }

        private static void SetVisualShown(Visual v, bool shown)
        {
            if (v.icon != null && v.icon.gameObject.activeSelf != shown)
                v.icon.gameObject.SetActive(shown);
            if (!shown)
            {
                v.label?.SetVisible(false);
                if (v.ring != null) v.ring.enabled = false;
            }
        }

        // ---- Panel control ----

        public void Toggle() => SetVisible(!IsVisible);

        public void SetVisible(bool visible)
        {
            if (_root != null) _root.SetActive(visible);
        }

        /// <summary>Restricts the visible icons to one AP (the selected per-AP
        /// heatmap). "" or null = all APs. The single-AP properties re-map to
        /// the selection so wall-loss/coverage follow the active heatmap.</summary>
        public void SetBssidFilter(string bssid)
        {
            _bssidFilter = bssid ?? "";
            UpdateActive();
            if (HasFix) SendApUpdate();
        }

        public void ClearEstimates()
        {
            foreach (var kv in _visuals)
            {
                var v = kv.Value;
                if (v.icon != null) Destroy(v.icon.gameObject);
                if (v.label != null) Destroy(v.label.gameObject);
                if (v.ring != null) Destroy(v.ring.gameObject);
            }
            _visuals.Clear();
            _estimates.Clear();
            _bssidFilter = "";
            _dirty = false;
            _refitTimer = 0f;
            _resendTimer = 0f;
            // A full dashboard "clear" packet removes the previous apfix
            // snapshot; no invalid legacy single-AP packet is necessary.
        }

        private void Update()
        {
            if (collector == null) return;

            // The path-loss fit is throttled: note data changes, refit at most
            // every refitInterval seconds.
            if (collector.DataRevision != _lastRevision)
            {
                _lastRevision = collector.DataRevision;
                _dirty = true;
            }
            _refitTimer += Time.deltaTime;
            if (_dirty && _refitTimer >= refitInterval)
            {
                _refitTimer = 0f;
                _dirty = false;
                Recompute();
            }

            // Periodic re-broadcast so a PC viewer started later still syncs.
            if (streamer != null && HasFix)
            {
                _resendTimer += Time.deltaTime;
                if (_resendTimer >= 3f) SendApUpdate();
            }

            if (_root == null || !_root.activeSelf) return;

            // Glide every (visible) estimate's icon toward its target.
            foreach (var est in _estimates)
            {
                if (!_visuals.TryGetValue(est.bssid, out var v)) continue;
                bool shown = _bssidFilter.Length == 0 || est.bssid == _bssidFilter;
                SetVisualShown(v, shown);
                if (!shown) continue;

                Vector3 target = est.Position + Vector3.up * heightOffset;
                Vector3 pos = v.hasTarget ? v.icon.position : target;
                pos = Vector3.SmoothDamp(pos, target, ref v.velocity, smoothTime);
                v.hasTarget = true;
                v.icon.position = pos;
                Billboard(v.icon);
                UpdateRing(v.ring, pos, est);
                v.label.SetVisible(true);
                v.label.SetWorldPosition(pos + Vector3.up * (iconSize * 0.75f));
                string unc = est.hasFit ? $"\n±{est.radius:F1} m" : "";
                string band = Sample.BandLabel(est.frequencyMhz);
                v.label.SetText(
                    $"{(string.IsNullOrEmpty(est.ssid) ? "AP" : est.ssid)} · {band}\n" +
                    $"~{est.strongestRssi} dBm{unc}");
            }
        }

        private void Recompute()
        {
            // Group the samples per access point (BSSID) — every AP gets its
            // own estimate, matching the per-AP heatmaps.
            var samples = collector.Samples;
            var groups = new System.Collections.Generic.Dictionary<string,
                System.Collections.Generic.List<Sample>>();
            for (int i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                if (string.IsNullOrEmpty(s.bssid)) continue;
                if (!groups.TryGetValue(s.bssid, out var list))
                    groups[s.bssid] = list = new System.Collections.Generic.List<Sample>();
                list.Add(s);
            }

            // Drop estimates whose AP vanished (session load/clear).
            for (int i = _estimates.Count - 1; i >= 0; i--)
            {
                string key = _estimates[i].bssid;
                if (groups.TryGetValue(key, out var list) && list.Count >= minSamples)
                    continue;
                if (_visuals.TryGetValue(key, out var v))
                {
                    if (v.icon != null) Destroy(v.icon.gameObject);
                    if (v.label != null) Destroy(v.label.gameObject);
                    if (v.ring != null) Destroy(v.ring.gameObject);
                    _visuals.Remove(key);
                }
                _estimates.RemoveAt(i);
            }

            foreach (var kv in groups)
            {
                if (kv.Value.Count < minSamples) continue;
                var est = _estimates.Find(e => e.bssid == kv.Key);
                if (est == null)
                {
                    est = new ApEstimate { bssid = kv.Key };
                    _estimates.Add(est);
                }
                FitGroup(kv.Value, est);
                if (!_visuals.ContainsKey(kv.Key))
                    _visuals[kv.Key] = CreateVisual();
            }

            UpdateActive();
            if (HasFix) SendApUpdate();
        }

        // Two-stage fit for ONE AP's samples (centroid init + pattern search).
        private static void FitGroup(System.Collections.Generic.List<Sample> samples,
            ApEstimate est)
        {
            Vector3 num = Vector3.zero;
            double den = 0.0;
            int strongest = -127;
            string strongestSsid = "";
            int frequency = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                double w = Math.Pow(10.0, s.rssiDbm / 10.0); // linear power weight
                num += (float)w * new Vector3(s.posX, s.posY, s.posZ);
                den += w;
                if (s.rssiDbm > strongest)
                {
                    strongest = s.rssiDbm;
                    strongestSsid = s.ssid;
                }
                if (s.frequencyMhz > 0) frequency = s.frequencyMhz;
            }
            est.sampleCount = samples.Count;
            est.strongestRssi = strongest;
            est.ssid = strongestSsid;
            est.frequencyMhz = frequency;
            if (den <= 0.0) return;
            Vector3 centroid = num / (float)den;

            var apXZ = new Vector2(centroid.x, centroid.z);
            float apY = centroid.y;
            float cost = FitCost(samples, apXZ, apY, out float n, out float p1);
            bool fitOk = !float.IsPositiveInfinity(cost);
            if (fitOk)
            {
                float step = 2f;
                for (int iter = 0; iter < 60 && step > 0.05f; iter++)
                {
                    Vector2 best = apXZ;
                    float bestCost = cost;
                    foreach (var dir in SearchDirs)
                    {
                        Vector2 cand = apXZ + dir * step;
                        float c = FitCost(samples, cand, apY, out _, out _);
                        if (c < bestCost) { bestCost = c; best = cand; }
                    }
                    if (bestCost < cost) { apXZ = best; cost = bestCost; }
                    else step *= 0.6f;
                }
                cost = FitCost(samples, apXZ, apY, out n, out p1);

                var pos = new Vector3(apXZ.x, apY, apXZ.y);
                est.posX = pos.x; est.posY = pos.y; est.posZ = pos.z;
                est.pathLossN = n;
                est.powerAt1m = p1;
                // Residual (dB) -> distance uncertainty at the typical range:
                // d ~ 10^((P1-r)/(10n)), so sigma_dB maps to the factor
                // 10^(sigma/(10n)). Radius = mean distance * (factor - 1).
                float sigma = Mathf.Sqrt(cost / samples.Count);
                float meanD = MeanDistance(samples, pos);
                float factor = Mathf.Pow(10f, sigma / (10f * n));
                est.radius = Mathf.Clamp(meanD * (factor - 1f), 0.3f, 8f);
                est.hasFit = true;
            }
            else
            {
                est.posX = centroid.x; est.posY = centroid.y; est.posZ = centroid.z;
                est.hasFit = false; // degenerate data: centroid only
            }
        }

        // Maps the single-AP properties to the ACTIVE estimate: the filtered
        // AP when a per-AP heatmap is selected, else the one with most samples.
        private void UpdateActive()
        {
            ApEstimate active = null;
            if (_bssidFilter.Length > 0)
                active = _estimates.Find(e => e.bssid == _bssidFilter);
            if (active == null)
                foreach (var e in _estimates)
                    if (active == null || e.sampleCount > active.sampleCount)
                        active = e;
            if (active == null)
            {
                HasFix = false;
                HasFit = false;
                ActiveBssid = "";
                return;
            }
            HasFix = true;
            ActiveBssid = active.bssid;
            FixPosition = active.Position;
            FixSsid = active.ssid;
            FixRssi = active.strongestRssi;
            HasFit = active.hasFit;
            FixRadius = active.radius;
            PathLossExponent = active.pathLossN;
            PowerAt1mDbm = active.powerAt1m;
        }

        /// <summary>
        /// SSE of the log-distance model for an AP candidate: (P1m, n) are the
        /// least-squares solution over log10(d) (n clamped to a physical
        /// 1.6…6.0), returned cost is the sum of squared dB residuals.
        /// +Inf when the geometry is degenerate (all distances equal).
        /// </summary>
        private static float FitCost(System.Collections.Generic.IReadOnlyList<Sample> samples,
            Vector2 apXZ, float apY, out float n, out float p1)
        {
            n = 2.7f;
            p1 = -40f;
            int count = samples.Count;
            double sumL = 0, sumL2 = 0, sumR = 0, sumRL = 0, sumR2 = 0;
            for (int i = 0; i < count; i++)
            {
                var s = samples[i];
                float dx = s.posX - apXZ.x;
                float dy = s.posY - apY;
                float dz = s.posZ - apXZ.y;
                float d = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
                double L = Math.Log10(Mathf.Max(d, 0.3f));
                double r = s.rssiDbm;
                sumL += L;
                sumL2 += L * L;
                sumR += r;
                sumRL += r * L;
                sumR2 += r * r;
            }
            double varL = sumL2 - sumL * sumL / count;
            if (varL < 1e-4) return float.PositiveInfinity;

            double slope = (sumRL - sumR * sumL / count) / varL; // ideally < 0
            double nFit = Math.Min(6.0, Math.Max(1.6, -slope / 10.0));
            slope = -10.0 * nFit;
            double intercept = (sumR - slope * sumL) / count;
            double sse = sumR2 - 2.0 * intercept * sumR - 2.0 * slope * sumRL
                + count * intercept * intercept
                + 2.0 * intercept * slope * sumL + slope * slope * sumL2;
            n = (float)nFit;
            p1 = (float)intercept;
            return (float)Math.Max(sse, 0.0);
        }

        private static float MeanDistance(
            System.Collections.Generic.IReadOnlyList<Sample> samples, Vector3 ap)
        {
            double sum = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                sum += Vector3.Distance(new Vector3(s.posX, s.posY, s.posZ), ap);
            }
            return (float)(sum / Mathf.Max(1, samples.Count));
        }

        // Horizontal uncertainty circle around one estimate's icon.
        private static void UpdateRing(LineRenderer ring, Vector3 center,
            ApEstimate est)
        {
            if (ring == null) return;
            ring.enabled = est.hasFit;
            if (!est.hasFit) return;
            for (int i = 0; i <= RingSegments; i++)
            {
                float a = i / (float)RingSegments * Mathf.PI * 2f;
                ring.SetPosition(i, center + new Vector3(
                    Mathf.Cos(a) * est.radius, 0f, Mathf.Sin(a) * est.radius));
            }
        }

        private static void Billboard(Transform icon)
        {
            var cam = Camera.main;
            if (cam == null || icon == null) return;
            Vector3 dir = icon.position - cam.transform.position;
            if (dir.sqrMagnitude > 1e-6f)
                icon.rotation = Quaternion.LookRotation(dir, Vector3.up);
        }

        // ---- Streaming ----

        private void SendApUpdate()
        {
            if (streamer == null || !HasFix) return;
            _resendTimer = 0f;
            var p = new ApFixPacket
            {
                posX = FixPosition.x,
                posY = FixPosition.y,
                posZ = FixPosition.z,
                ssid = FixSsid,
                bssid = _bssidFilter,
                rssi = FixRssi,
                radius = HasFit ? FixRadius : 0f,
                pathLossN = PathLossExponent,
                powerAt1m = PowerAt1mDbm,
                aps = _estimates,
            };
            streamer.SendRaw(JsonUtility.ToJson(p));
        }

        // ---- Visuals ----

        // URP-Unlit clone set up as a double-sided, alpha-blended textured icon.
        private static void MakeIconMaterial(Material m, Texture2D tex)
        {
            m.SetTexture("_BaseMap", tex);
            m.SetColor("_BaseColor", Color.white);
            m.SetFloat("_Cull", 0f); // double-sided so billboard orientation never hides it
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.SetOverrideTag("RenderType", "Transparent");
            m.renderQueue = (int)RenderQueue.Transparent;
        }

        /// <summary>A simple recognizable router glyph: a rounded body with two
        /// antennas and three status LEDs, cyan on a transparent 64×64 texture.</summary>
        private static Texture2D CreateRouterTexture()
        {
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            var clear = new Color32(0, 0, 0, 0);
            var body = new Color32(0, 210, 255, 255);
            var dark = new Color32(10, 40, 55, 255);
            var led = new Color32(255, 255, 255, 255);

            var px = new Color32[size * size];
            for (int i = 0; i < px.Length; i++) px[i] = clear;

            // Antennas (drawn first, under the body): two diagonal bars.
            DrawLine(px, size, 22, 34, 15, 56, dark, 3);
            DrawLine(px, size, 42, 34, 49, 56, dark, 3);

            // Body: rounded rectangle 8..56 x 14..36.
            FillRoundedRect(px, size, 8, 14, 56, 36, 6, dark);   // outline
            FillRoundedRect(px, size, 10, 16, 54, 34, 5, body);  // fill

            // Three LEDs along the lower edge of the body.
            FillDisc(px, size, 20, 21, 2.4f, led);
            FillDisc(px, size, 32, 21, 2.4f, led);
            FillDisc(px, size, 44, 21, 2.4f, led);

            tex.SetPixels32(px);
            tex.Apply(false);
            return tex;
        }

        private static void FillRoundedRect(Color32[] px, int size, int x0, int y0,
            int x1, int y1, int r, Color32 c)
        {
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                if (x < 0 || x >= size || y < 0 || y >= size) continue;
                // Round the corners.
                int cx = x < x0 + r ? x0 + r : (x > x1 - r ? x1 - r : x);
                int cy = y < y0 + r ? y0 + r : (y > y1 - r ? y1 - r : y);
                int dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy > r * r) continue;
                px[y * size + x] = c;
            }
        }

        private static void FillDisc(Color32[] px, int size, float cx, float cy,
            float r, Color32 c)
        {
            int x0 = Mathf.FloorToInt(cx - r), x1 = Mathf.CeilToInt(cx + r);
            int y0 = Mathf.FloorToInt(cy - r), y1 = Mathf.CeilToInt(cy + r);
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                if (x < 0 || x >= size || y < 0 || y >= size) continue;
                float dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy <= r * r) px[y * size + x] = c;
            }
        }

        private static void DrawLine(Color32[] px, int size, int ax, int ay,
            int bx, int by, Color32 c, int thickness)
        {
            int steps = Mathf.Max(Mathf.Abs(bx - ax), Mathf.Abs(by - ay));
            int h = thickness / 2;
            for (int s = 0; s <= steps; s++)
            {
                float t = steps == 0 ? 0f : s / (float)steps;
                int x = Mathf.RoundToInt(Mathf.Lerp(ax, bx, t));
                int y = Mathf.RoundToInt(Mathf.Lerp(ay, by, t));
                for (int oy = -h; oy <= h; oy++)
                for (int ox = -h; ox <= h; ox++)
                {
                    int xx = x + ox, yy = y + oy;
                    if (xx < 0 || xx >= size || yy < 0 || yy >= size) continue;
                    px[yy * size + xx] = c;
                }
            }
        }
    }
}
