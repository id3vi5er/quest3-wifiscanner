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

        private GameObject _root;
        private Transform _icon;
        private WorldLabel _label;
        private Material _iconMat;
        private Mesh _quadMesh;
        private LineRenderer _ring;

        private int _lastRevision = -1;
        private float _refitTimer;
        private bool _dirty;
        private Vector3 _target;
        private Vector3 _velocity;
        private bool _hasTarget;
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
            public float posX, posY, posZ;
            public string ssid;
            public int rssi;
            public float radius;       // uncertainty (m); 0 = centroid only
            public float pathLossN;    // fitted exponent
            public float powerAt1m;    // fitted P1m (dBm)
        }

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
            }
            else
            {
                Debug.LogError("[ApLocator] visualMaterial not assigned — assign the " +
                               "point material in SceneBootstrap.");
            }

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
            _icon = iconGo.transform;

            _label = WorldLabel.Create(_root.transform);

            // Uncertainty ring (horizontal circle around the icon).
            var ringGo = new GameObject("UncertaintyRing");
            ringGo.transform.SetParent(_root.transform, false);
            _ring = ringGo.AddComponent<LineRenderer>();
            _ring.positionCount = RingSegments + 1;
            _ring.startWidth = _ring.endWidth = 0.012f;
            _ring.loop = false;
            _ring.shadowCastingMode = ShadowCastingMode.Off;
            _ring.receiveShadows = false;
            if (visualMaterial != null)
            {
                var ringMat = new Material(visualMaterial); // asset clone
                ringMat.SetColor("_BaseColor", new Color(0f, 0.86f, 1f, 0.55f));
                _ring.sharedMaterial = ringMat;
            }
            _ring.enabled = false;

            _root.SetActive(visibleAtStart);
        }

        // ---- Panel control ----

        public void Toggle() => SetVisible(!IsVisible);

        public void SetVisible(bool visible)
        {
            if (_root != null) _root.SetActive(visible);
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

            if (_hasTarget)
            {
                Vector3 pos = _icon != null ? _icon.position : _target;
                pos = Vector3.SmoothDamp(pos, _target, ref _velocity, smoothTime);
                if (_icon != null)
                {
                    _icon.position = pos;
                    BillboardIcon();
                }
                UpdateRing(pos);
                _label.SetWorldPosition(pos + Vector3.up * (iconSize * 0.75f));
                string unc = HasFit ? $"\n±{FixRadius:F1} m" : "";
                _label.SetText(HasFix
                    ? $"{(string.IsNullOrEmpty(FixSsid) ? "AP" : FixSsid)}\n~{FixRssi} dBm{unc}"
                    : "AP: (zu wenig Daten)");
            }
            _label.SetVisible(_hasTarget && HasFix);
            if (_ring != null) _ring.enabled = _hasTarget && HasFit;
        }

        private void Recompute()
        {
            var samples = collector.Samples;
            if (samples.Count < minSamples)
            {
                HasFix = false;
                HasFit = false;
                _hasTarget = false;
                return;
            }

            // --- Stage 1: weighted centroid (linear power) as initial guess ---
            Vector3 num = Vector3.zero;
            double den = 0.0;
            int strongest = -127;
            string strongestSsid = "";
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
            }
            if (den <= 0.0) { HasFix = false; _hasTarget = false; return; }
            Vector3 centroid = num / (float)den;

            // --- Stage 2: XZ pattern search minimising the path-loss SSE ---
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
            }

            if (fitOk)
            {
                FixPosition = new Vector3(apXZ.x, apY, apXZ.y);
                PathLossExponent = n;
                PowerAt1mDbm = p1;
                // Residual (dB) -> distance uncertainty at the typical range:
                // d ~ 10^((P1-r)/(10n)), so sigma_dB maps to the factor
                // 10^(sigma/(10n)). Radius = mean distance * (factor - 1).
                float sigma = Mathf.Sqrt(cost / samples.Count);
                float meanD = MeanDistance(samples, FixPosition);
                float factor = Mathf.Pow(10f, sigma / (10f * n));
                FixRadius = Mathf.Clamp(meanD * (factor - 1f), 0.3f, 8f);
                HasFit = true;
            }
            else
            {
                FixPosition = centroid; // degenerate data: centroid only
                HasFit = false;
            }

            FixSsid = strongestSsid;
            FixRssi = strongest;
            _target = FixPosition + Vector3.up * heightOffset;
            if (!_hasTarget && _icon != null) _icon.position = _target; // snap first time
            _hasTarget = true;
            HasFix = true;
            SendApUpdate();
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

        // Horizontal circle of FixRadius around the (smoothed) icon position.
        private void UpdateRing(Vector3 center)
        {
            if (_ring == null || !HasFit) return;
            for (int i = 0; i <= RingSegments; i++)
            {
                float a = i / (float)RingSegments * Mathf.PI * 2f;
                _ring.SetPosition(i, center + new Vector3(
                    Mathf.Cos(a) * FixRadius, 0f, Mathf.Sin(a) * FixRadius));
            }
        }

        private void BillboardIcon()
        {
            var cam = Camera.main;
            if (cam == null || _icon == null) return;
            Vector3 dir = _icon.position - cam.transform.position;
            if (dir.sqrMagnitude > 1e-6f)
                _icon.rotation = Quaternion.LookRotation(dir, Vector3.up);
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
                rssi = FixRssi,
                radius = HasFit ? FixRadius : 0f,
                pathLossN = PathLossExponent,
                powerAt1m = PowerAt1mDbm,
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
