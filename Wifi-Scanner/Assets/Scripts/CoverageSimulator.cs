using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace WifiScanner
{
    /// <summary>
    /// "Was wäre wenn"-coverage prediction. Simulates the RSSI field of one or
    /// more access points over the traced floor plan with a log-distance
    /// path-loss model plus per-wall attenuation:
    ///
    ///   rssi(cell) = P1m − 10·n·log10(d) − Σ (crossed walls × wall loss)
    ///
    /// AP sources: every "Virtueller AP" spatial note (place one, drag the
    /// prediction updates); if none exist, the REAL AP's fitted position.
    /// Model parameters come from the ApLocator fit when available, defaults
    /// otherwise; wall losses come from the WallLossEstimator when it has an
    /// estimate for a wall, else <see cref="defaultWallLossDb"/>. Door/window
    /// openings are ignored (worst-case wall).
    ///
    /// Renders as its own floor quad (same RdYlGn ramp as the measured
    /// heatmap, slightly above it), toggled via the panel ("Prognose",
    /// Ansicht tab). Cells are computed in a budgeted coroutine; the field
    /// refreshes automatically when APs/rooms/fit change.
    /// Created and wired by SceneBootstrap.
    /// </summary>
    public class CoverageSimulator : MonoBehaviour
    {
        [Header("Wired by SceneBootstrap")]
        public RoomTracer roomTracer;
        public SpatialNoteManager spatialNotes; // optional (virtual APs)
        public ApLocator apLocator;             // optional (real AP + fit)
        public FloorManager floorManager;       // optional (active storey)
        [Tooltip("Material ASSET (URP Unlit, transparent) — same as the heatmap.")]
        public Material floorMaterial;

        [Header("Model")]
        [Tooltip("Path-loss exponent when no AP fit exists (free space = 2).")]
        public float defaultPathLossN = 2.7f;
        [Tooltip("Power at 1 m for virtual APs / without fit (dBm).")]
        public float defaultPowerAt1mDbm = -40f;
        [Tooltip("Attenuation per crossed wall without an estimate (dB).")]
        public float defaultWallLossDb = 5f;

        [Header("Raster")]
        [Tooltip("Cell size of the prediction grid (m).")]
        public float cellSize = 0.15f;
        public int maxTextureDim = 384;
        [Tooltip("Margin around the traced rooms (m).")]
        public float padding = 1f;
        [Tooltip("Lift above the floor (m) — slightly above the measured heatmap.")]
        public float floorOffset = 0.035f;
        [Range(0f, 1f)]
        public float opacity = 0.8f;
        [Tooltip("Cells simulated per frame while rebuilding.")]
        public int cellsPerFrame = 6000;
        [Tooltip("Seconds between change checks while visible.")]
        public float updateInterval = 2f;

        public bool IsVisible { get; private set; }
        /// <summary>Why nothing is drawn ("" when the quad is showing).</summary>
        public string StatusHint { get; private set; } = "";

        private WallLossEstimator _wallLoss; // optional, set via SceneBootstrap
        public WallLossEstimator wallLoss
        {
            get => _wallLoss;
            set => _wallLoss = value;
        }

        private GameObject _quadGo;
        private MeshRenderer _renderer;
        private Material _mat;
        private Texture2D _tex;
        private float _timer;
        private int _changeHash;
        private bool _rebuilding;

        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        private struct ApSource
        {
            public Vector3 pos;
            public float p1m;
            public float n;
        }

        private struct Wall
        {
            public Vector2 a, b;
            public float lossDb;
        }

        private void Start()
        {
            // Unit quad in the XZ plane (same pattern as FloorHeatmapRenderer).
            var mesh = new Mesh { name = "CoverageQuad" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, 0f, -0.5f), new Vector3(-0.5f, 0f, 0.5f),
                new Vector3(0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, -0.5f),
            };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f), new Vector2(0f, 1f),
                new Vector2(1f, 1f), new Vector2(1f, 0f),
            };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            _quadGo = new GameObject("CoverageQuad");
            _quadGo.transform.SetParent(transform, false);
            _quadGo.AddComponent<MeshFilter>().sharedMesh = mesh;
            _renderer = _quadGo.AddComponent<MeshRenderer>();
            _renderer.shadowCastingMode = ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.lightProbeUsage = LightProbeUsage.Off;
            _renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            _quadGo.SetActive(false);
        }

        public void Toggle() => SetVisible(!IsVisible);

        public void SetVisible(bool visible)
        {
            IsVisible = visible;
            if (!visible)
            {
                if (_quadGo != null) _quadGo.SetActive(false);
                return;
            }
            _changeHash = 0; // force a rebuild on the next Update
            _timer = updateInterval;
        }

        private void Update()
        {
            if (!IsVisible || _rebuilding || _quadGo == null) return;

            _timer += Time.deltaTime;
            if (_timer < updateInterval) return;
            _timer = 0f;

            int hash = ComputeChangeHash();
            if (hash == _changeHash) return;
            _changeHash = hash;
            StartCoroutine(Rebuild());
        }

        // Cheap fingerprint of everything the prediction depends on.
        private int ComputeChangeHash()
        {
            int h = 17;
            var aps = CollectAps();
            foreach (var ap in aps)
                h = h * 31 + ap.pos.GetHashCode() ^ Mathf.RoundToInt(ap.p1m * 10f);
            if (roomTracer != null)
            {
                h = h * 31 + roomTracer.Rooms.Count;
                foreach (var room in roomTracer.Rooms)
                    h = h * 31 + room.cornersXZ.Count
                        ^ Mathf.RoundToInt(room.floorY * 100f);
            }
            if (floorManager != null) h = h * 31 + floorManager.ActiveIndex;
            return h;
        }

        // All AP sources: every "Virtueller AP" note; fallback = the real fix.
        private List<ApSource> CollectAps()
        {
            var aps = new List<ApSource>();
            float n = apLocator != null && apLocator.HasFit
                ? apLocator.PathLossExponent : defaultPathLossN;
            if (spatialNotes != null)
                foreach (var note in spatialNotes.Notes)
                    if (note.type == "VirtualAp")
                        aps.Add(new ApSource
                        {
                            pos = new Vector3(note.posX, note.posY, note.posZ),
                            p1m = defaultPowerAt1mDbm,
                            n = n,
                        });
            if (aps.Count == 0 && apLocator != null && apLocator.HasFix)
                aps.Add(new ApSource
                {
                    pos = apLocator.FixPosition,
                    p1m = apLocator.HasFit ? apLocator.PowerAt1mDbm : defaultPowerAt1mDbm,
                    n = n,
                });
            return aps;
        }

        private IEnumerator Rebuild()
        {
            _rebuilding = true;

            var aps = CollectAps();
            if (aps.Count == 0)
            {
                StatusHint = "Prognose: virtuellen AP setzen (Notiz) oder AP-Fix abwarten";
                _quadGo.SetActive(false);
                _rebuilding = false;
                yield break;
            }
            if (roomTracer == null || roomTracer.Rooms.Count == 0)
            {
                StatusHint = "Prognose: erst Räume zeichnen";
                _quadGo.SetActive(false);
                _rebuilding = false;
                yield break;
            }

            // --- Walls + bounds of the active storey's rooms ---
            float baseY = floorManager != null && floorManager.Floors.Count > 0
                ? floorManager.ActiveBaseY
                : roomTracer.Rooms[0].floorY;
            var walls = new List<Wall>();
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            var rooms = roomTracer.Rooms;
            for (int r = 0; r < rooms.Count; r++)
            {
                var room = rooms[r];
                if (Mathf.Abs(room.floorY - baseY) > 1f) continue; // other storey
                int corners = room.cornersXZ.Count;
                for (int e = 0; e < corners; e++)
                {
                    Vector2 a = room.cornersXZ[e];
                    Vector2 b = room.cornersXZ[(e + 1) % corners];
                    float loss = defaultWallLossDb;
                    if (_wallLoss != null && _wallLoss.TryGetWallLoss(r, e, out float est))
                        loss = est;
                    walls.Add(new Wall { a = a, b = b, lossDb = loss });
                    minX = Mathf.Min(minX, a.x); maxX = Mathf.Max(maxX, a.x);
                    minZ = Mathf.Min(minZ, a.y); maxZ = Mathf.Max(maxZ, a.y);
                }
            }
            if (walls.Count == 0)
            {
                StatusHint = "Prognose: keine Räume auf dieser Etage";
                _quadGo.SetActive(false);
                _rebuilding = false;
                yield break;
            }
            StatusHint = "";

            minX -= padding; maxX += padding;
            minZ -= padding; maxZ += padding;
            float sizeX = Mathf.Max(maxX - minX, 1f);
            float sizeZ = Mathf.Max(maxZ - minZ, 1f);
            float cell = Mathf.Max(cellSize, sizeX / maxTextureDim, sizeZ / maxTextureDim);
            int w = Mathf.Max(2, Mathf.CeilToInt(sizeX / cell));
            int h = Mathf.Max(2, Mathf.CeilToInt(sizeZ / cell));

            var pixels = new Color32[w * h];
            int budget = Mathf.Max(512, cellsPerFrame);
            float sampleY = baseY + 1.4f; // predict at ~device height

            for (int ty = 0; ty < h; ty++)
            {
                float wz = minZ + (ty + 0.5f) * cell;
                for (int tx = 0; tx < w; tx++)
                {
                    float wx = minX + (tx + 0.5f) * cell;
                    var p = new Vector2(wx, wz);

                    float best = -127f;
                    foreach (var ap in aps)
                    {
                        float d = Vector3.Distance(
                            new Vector3(wx, sampleY, wz), ap.pos);
                        float rssi = ap.p1m
                            - 10f * ap.n * Mathf.Log10(Mathf.Max(d, 0.3f));
                        var apXZ = new Vector2(ap.pos.x, ap.pos.z);
                        foreach (var wall in walls)
                            if (SegmentsIntersect(p, apXZ, wall.a, wall.b))
                                rssi -= wall.lossDb;
                        if (rssi > best) best = rssi;
                    }

                    Color c = HeatmapColorMapper.Map(Mathf.RoundToInt(best));
                    pixels[ty * w + tx] = new Color32(
                        (byte)(c.r * 255f), (byte)(c.g * 255f),
                        (byte)(c.b * 255f), 255);

                    if (--budget <= 0)
                    {
                        budget = Mathf.Max(512, cellsPerFrame);
                        yield return null;
                    }
                }
            }

            // --- Texture + quad placement (axis-aligned world grid) ---
            if (!EnsureMaterial()) { _rebuilding = false; yield break; }
            if (_tex == null || _tex.width != w || _tex.height != h)
            {
                if (_tex != null) Destroy(_tex);
                _tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
                {
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
                _mat.SetTexture(BaseMapId, _tex);
            }
            _tex.SetPixels32(pixels);
            _tex.Apply(false);

            float quadW = w * cell;
            float quadH = h * cell;
            _quadGo.transform.position = new Vector3(
                minX + quadW * 0.5f, baseY + floorOffset, minZ + quadH * 0.5f);
            _quadGo.transform.rotation = Quaternion.identity;
            _quadGo.transform.localScale = new Vector3(quadW, 1f, quadH);
            _quadGo.SetActive(IsVisible);

            _rebuilding = false;
        }

        private bool EnsureMaterial()
        {
            if (_mat != null) return true;
            if (floorMaterial == null)
            {
                Debug.LogError("[CoverageSimulator] floorMaterial not assigned.");
                return false;
            }
            _mat = new Material(floorMaterial); // asset clone keeps its variant
            _mat.SetColor(BaseColorId, new Color(1f, 1f, 1f, Mathf.Clamp01(opacity)));
            _renderer.sharedMaterial = _mat;
            return true;
        }

        // 2D segment intersection (proper crossings; shared endpoints count).
        private static bool SegmentsIntersect(Vector2 p1, Vector2 p2,
            Vector2 q1, Vector2 q2)
        {
            float d1 = Cross(q2 - q1, p1 - q1);
            float d2 = Cross(q2 - q1, p2 - q1);
            float d3 = Cross(p2 - p1, q1 - p1);
            float d4 = Cross(p2 - p1, q2 - p1);
            return ((d1 > 0f && d2 < 0f) || (d1 < 0f && d2 > 0f)) &&
                   ((d3 > 0f && d4 < 0f) || (d3 < 0f && d4 > 0f));
        }

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
    }
}
