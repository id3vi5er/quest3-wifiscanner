using System.Collections;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace WifiScanner
{
    /// <summary>
    /// Classic 2D WiFi heatmap: a horizontal quad at floor height whose texture
    /// is an IDW-interpolated (inverse distance weighting, power 2) field of the
    /// recorded RSSI points, coloured with the same RdYlGn ramp as the point
    /// cloud. Cells whose nearest measurement is farther than
    /// <see cref="maxInterpolationDistance"/> are NOT extrapolated — they render
    /// as neutral grey with a subtle world-aligned grid, so unmeasured areas are
    /// immediately visible while walking the room (gap display).
    ///
    /// The expensive interpolation pass is throttled (<see cref="updateInterval"/>)
    /// and spread over frames via a coroutine; opacity and RSSI-filter changes
    /// only re-colourise the cached field, which is cheap and instant.
    ///
    /// Created and wired by SceneBootstrap. The material must be a URP Unlit
    /// material ASSET (transparent, cull off) assigned via
    /// SceneBootstrap.floorMaterialOverride — see the magenta-trap note there.
    /// </summary>
    public class FloorHeatmapRenderer : MonoBehaviour
    {
        [Header("Data source")]
        public SampleCollector collector;

        [Header("Material (URP Unlit asset, transparent)")]
        public Material floorMaterial;

        [Header("Raster")]
        [Tooltip("Texel size in metres. Effective size grows if the scanned area " +
                 "would exceed maxTextureDim.")]
        public float cellSize = 0.1f;
        [Tooltip("Upper bound for the heatmap texture dimensions.")]
        public int maxTextureDim = 512;
        [Tooltip("Margin around the scanned area, in metres.")]
        public float padding = 1.0f;

        [Header("Interpolation")]
        [Tooltip("Cells farther than this from any measurement are shown as " +
                 "unmeasured (grey gap cells) instead of being extrapolated.")]
        public float maxInterpolationDistance = 1.5f;

        [Header("Update")]
        [Tooltip("Minimum seconds between interpolation passes.")]
        public float updateInterval = 2f;
        [Tooltip("Texels interpolated per frame while rebuilding.")]
        public int texelsPerFrame = 8192;

        [Header("Placement / look")]
        [Tooltip("Lift above the detected floor to avoid z-fighting (m).")]
        public float floorOffset = 0.02f;
        [Range(0f, 1f)]
        public float opacity = 0.75f;

        private GameObject _quadGo;
        private XROrigin _xrOrigin;
        private MeshRenderer _quadRenderer;
        private Material _mat;
        private Texture2D _tex;
        private Color32[] _pixels;
        // Cached interpolation result per texel: RSSI in dBm, NaN = unmeasured gap.
        private float[] _cellRssi;
        private int _texW, _texH;
        private float _worldCell;
        private Vector2 _worldMin;     // XZ of texel (0,0) corner
        private float _floorY;
        private float _timer;
        private int _lastRevision = -1;
        private bool _rebuilding;
        private bool _warnedNoMaterial;
        private int _filterMinDbm = -127;
        private int _filterMaxDbm = 0;

        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly Color32 GapColor = new Color32(110, 110, 110, 70);
        private static readonly Color32 GapGridColor = new Color32(210, 210, 210, 110);
        private const float GapGridSpacing = 0.5f; // world-aligned grid lines (m)

        private void Awake()
        {
            // Unit quad in the XZ plane, scaled/positioned per rebuild.
            var mesh = new Mesh { name = "FloorHeatmapQuad" };
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

            _quadGo = new GameObject("FloorHeatmapQuad");
            _quadGo.transform.SetParent(transform, false);
            _quadGo.AddComponent<MeshFilter>().sharedMesh = mesh;
            _quadRenderer = _quadGo.AddComponent<MeshRenderer>();
            _quadRenderer.shadowCastingMode = ShadowCastingMode.Off;
            _quadRenderer.receiveShadows = false;
            _quadRenderer.lightProbeUsage = LightProbeUsage.Off;
            _quadRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

            _quadGo.SetActive(false);
            _xrOrigin = FindFirstObjectByType<XROrigin>();
        }

        /// <summary>
        /// Lazy material setup. Must NOT run in Awake: AddComponent (from
        /// SceneBootstrap) invokes Awake before the floorMaterial field is
        /// assigned, which would leave the heatmap permanently invisible.
        /// </summary>
        private bool EnsureMaterial()
        {
            if (_mat != null) return true;
            if (floorMaterial == null)
            {
                if (!_warnedNoMaterial)
                {
                    _warnedNoMaterial = true;
                    Debug.LogError("[FloorHeatmap] floorMaterial not assigned — assign " +
                                   "a URP Unlit asset (SceneBootstrap.floorMaterialOverride).");
                }
                return false;
            }
            // Clone so setting the runtime texture never dirties the asset.
            // Clones of a material ASSET keep their shader variant in the
            // build (proven by the 48 point-gradient clones).
            _mat = new Material(floorMaterial);
            _mat.SetColor(BaseColorId, Color.white);
            _quadRenderer.sharedMaterial = _mat;
            return true;
        }

        /// <summary>
        /// With tracking-origin mode "Floor" (the Quest's default, calibrated by
        /// the system's space setup) y == 0 of the XR Origin IS the physical
        /// floor — use it directly. Only without a floor-based origin (Editor
        /// Play mode, Device mode) fall back to estimating from the lowest head
        /// sample minus a typical head height.
        /// </summary>
        private float ResolveFloorY(float minSampleY)
        {
            if (_xrOrigin != null &&
                _xrOrigin.CurrentTrackingOriginMode == TrackingOriginModeFlags.Floor)
                return _xrOrigin.transform.position.y + floorOffset;
            return minSampleY - 1.2f + floorOffset;
        }

        private void OnDisable()
        {
            // A mode switch can deactivate us mid-rebuild, killing the coroutine;
            // clear the flag (and the revision marker) so the next enable rebuilds.
            _rebuilding = false;
            _lastRevision = -1;
        }

        private void Update()
        {
            if (collector == null || _rebuilding || !EnsureMaterial()) return;

            _timer += Time.deltaTime;
            if (_timer < updateInterval) return;

            // DataRevision also bumps on running-average updates of existing
            // points, so revisited areas recolour without new points appearing.
            if (collector.DataRevision == _lastRevision) return;
            _timer = 0f;

            if (collector.SampleCount == 0)
            {
                _lastRevision = collector.DataRevision;
                _quadGo.SetActive(false);
                return;
            }
            StartCoroutine(Rebuild());
        }

        /// <summary>Cheap: re-colourises the cached field with the new opacity.</summary>
        public void SetOpacity(float value01)
        {
            opacity = Mathf.Clamp01(value01);
            Colorize();
        }

        /// <summary>Cells outside [minDbm, maxDbm] become fully transparent.</summary>
        public void SetRssiFilter(int minDbm, int maxDbm)
        {
            _filterMinDbm = minDbm;
            _filterMaxDbm = maxDbm;
            Colorize();
        }

        private IEnumerator Rebuild()
        {
            _rebuilding = true;
            _lastRevision = collector.DataRevision;

            // --- Snapshot the samples (the list only grows / updates in place) ---
            var samples = collector.Samples;
            int n = samples.Count;
            var px = new float[n];
            var pz = new float[n];
            var prssi = new float[n];
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            float minY = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var s = samples[i];
                px[i] = s.posX;
                pz[i] = s.posZ;
                prssi[i] = s.rssiDbm;
                if (s.posX < minX) minX = s.posX;
                if (s.posX > maxX) maxX = s.posX;
                if (s.posZ < minZ) minZ = s.posZ;
                if (s.posZ > maxZ) maxZ = s.posZ;
                if (s.posY < minY) minY = s.posY;
            }
            minX -= padding; maxX += padding;
            minZ -= padding; maxZ += padding;
            _floorY = ResolveFloorY(minY);

            float sizeX = Mathf.Max(maxX - minX, 0.5f);
            float sizeZ = Mathf.Max(maxZ - minZ, 0.5f);
            _worldCell = Mathf.Max(cellSize, sizeX / maxTextureDim, sizeZ / maxTextureDim);
            _texW = Mathf.Max(2, Mathf.CeilToInt(sizeX / _worldCell));
            _texH = Mathf.Max(2, Mathf.CeilToInt(sizeZ / _worldCell));
            _worldMin = new Vector2(minX, minZ);

            // --- 2D spatial hash over the samples (cell = interpolation radius) ---
            float hashCell = maxInterpolationDistance;
            var hash = new System.Collections.Generic.Dictionary<long,
                System.Collections.Generic.List<int>>();
            for (int i = 0; i < n; i++)
            {
                long key = HashKey(Mathf.FloorToInt(px[i] / hashCell),
                                   Mathf.FloorToInt(pz[i] / hashCell));
                if (!hash.TryGetValue(key, out var list))
                    hash[key] = list = new System.Collections.Generic.List<int>();
                list.Add(i);
            }

            var cellRssi = new float[_texW * _texH];
            float maxDistSqr = maxInterpolationDistance * maxInterpolationDistance;
            int budget = Mathf.Max(256, texelsPerFrame);

            for (int ty = 0; ty < _texH; ty++)
            {
                float wz = _worldMin.y + (ty + 0.5f) * _worldCell;
                int hz = Mathf.FloorToInt(wz / hashCell);
                for (int tx = 0; tx < _texW; tx++)
                {
                    float wx = _worldMin.x + (tx + 0.5f) * _worldCell;
                    int hx = Mathf.FloorToInt(wx / hashCell);

                    float wSum = 0f, vSum = 0f, nearestSqr = float.MaxValue;
                    for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (!hash.TryGetValue(HashKey(hx + dx, hz + dz), out var list))
                            continue;
                        foreach (int i in list)
                        {
                            float ddx = px[i] - wx;
                            float ddz = pz[i] - wz;
                            float sqr = ddx * ddx + ddz * ddz;
                            if (sqr < nearestSqr) nearestSqr = sqr;
                            if (sqr > maxDistSqr) continue;
                            float w = 1f / (sqr + 0.01f); // IDW, power 2
                            wSum += w;
                            vSum += w * prssi[i];
                        }
                    }

                    cellRssi[ty * _texW + tx] =
                        nearestSqr <= maxDistSqr ? vSum / wSum : float.NaN;

                    if (--budget <= 0)
                    {
                        budget = Mathf.Max(256, texelsPerFrame);
                        yield return null;
                    }
                }
            }

            _cellRssi = cellRssi;

            // --- (Re)allocate texture and colourise ---
            if (_tex == null || _tex.width != _texW || _tex.height != _texH)
            {
                if (_tex != null) Destroy(_tex);
                _tex = new Texture2D(_texW, _texH, TextureFormat.RGBA32, false)
                {
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
                _pixels = new Color32[_texW * _texH];
                _mat.SetTexture(BaseMapId, _tex);
            }
            Colorize();

            // --- Place the quad (texel-exact so texture and world line up) ---
            float quadW = _texW * _worldCell;
            float quadH = _texH * _worldCell;
            _quadGo.transform.position = new Vector3(
                _worldMin.x + quadW * 0.5f, _floorY, _worldMin.y + quadH * 0.5f);
            _quadGo.transform.localScale = new Vector3(quadW, 1f, quadH);
            _quadGo.SetActive(true);

            _rebuilding = false;
        }

        // Maps the cached RSSI field to pixels: gradient inside the filter range,
        // transparent outside it, grey grid pattern for unmeasured gap cells.
        private void Colorize()
        {
            if (_tex == null || _cellRssi == null || _pixels == null) return;

            byte alpha = (byte)Mathf.RoundToInt(Mathf.Clamp01(opacity) * 255f);
            for (int ty = 0; ty < _texH; ty++)
            {
                float wz = _worldMin.y + (ty + 0.5f) * _worldCell;
                bool gridZ = Mathf.Repeat(wz, GapGridSpacing) < _worldCell;
                for (int tx = 0; tx < _texW; tx++)
                {
                    int idx = ty * _texW + tx;
                    float rssi = _cellRssi[idx];
                    if (float.IsNaN(rssi))
                    {
                        float wx = _worldMin.x + (tx + 0.5f) * _worldCell;
                        bool grid = gridZ || Mathf.Repeat(wx, GapGridSpacing) < _worldCell;
                        _pixels[idx] = grid ? GapGridColor : GapColor;
                    }
                    else if (rssi < _filterMinDbm || rssi > _filterMaxDbm)
                    {
                        _pixels[idx] = new Color32(0, 0, 0, 0);
                    }
                    else
                    {
                        Color c = HeatmapColorMapper.Map(Mathf.RoundToInt(rssi));
                        _pixels[idx] = new Color32(
                            (byte)(c.r * 255f), (byte)(c.g * 255f),
                            (byte)(c.b * 255f), alpha);
                    }
                }
            }
            _tex.SetPixels32(_pixels);
            _tex.Apply(false);
        }

        public void SetVisible(bool visible) => gameObject.SetActive(visible);

        [System.Serializable]
        private class HeatmapMeta
        {
            public float worldMinX, worldMinZ;
            public float cellSizeMeters;
            public int width, height;
            public float floorY;
            public int weakDbm, strongDbm;
            public string note;
        }

        /// <summary>
        /// Writes the current heatmap as PNG (full opacity, unfiltered) plus a
        /// small JSON sidecar with the world georeference, so PC tools can lay
        /// the image congruently under the CSV/OBJ data. If the cached field is
        /// stale or missing (e.g. point-cloud-only view mode), the interpolation
        /// runs synchronously first — acceptable for a deliberate export.
        /// </summary>
        public void ExportPng(string stem)
        {
            if (collector == null || collector.SampleCount == 0 || floorMaterial == null)
                return;

            // When the heatmap view is active the cached field is at most
            // updateInterval old — good enough. Compute synchronously only when
            // there is no field yet or the view is hidden (field goes stale).
            bool stale = _cellRssi == null ||
                (!isActiveAndEnabled && _lastRevision != collector.DataRevision);
            if (!_rebuilding && stale)
            {
                var pass = Rebuild();
                while (pass.MoveNext()) { } // drain synchronously
            }
            if (_cellRssi == null) return;

            var pixels = new Color32[_texW * _texH];
            for (int i = 0; i < _cellRssi.Length; i++)
            {
                float rssi = _cellRssi[i];
                if (float.IsNaN(rssi))
                {
                    pixels[i] = new Color32(110, 110, 110, 255);
                }
                else
                {
                    Color c = HeatmapColorMapper.Map(Mathf.RoundToInt(rssi));
                    pixels[i] = new Color32((byte)(c.r * 255f), (byte)(c.g * 255f),
                        (byte)(c.b * 255f), 255);
                }
            }

            var tex = new Texture2D(_texW, _texH, TextureFormat.RGBA32, false);
            tex.SetPixels32(pixels);
            tex.Apply(false);
            byte[] png = tex.EncodeToPNG();
            Destroy(tex);

            string dir = Application.persistentDataPath;
            string pngPath = System.IO.Path.Combine(dir, $"{stem}_heatmap.png");
            System.IO.File.WriteAllBytes(pngPath, png);

            var meta = new HeatmapMeta
            {
                worldMinX = _worldMin.x,
                worldMinZ = _worldMin.y,
                cellSizeMeters = _worldCell,
                width = _texW,
                height = _texH,
                floorY = _floorY,
                weakDbm = HeatmapColorMapper.WeakDbm,
                strongDbm = HeatmapColorMapper.StrongDbm,
                note = "pixel(x,y) center = world(worldMinX+(x+0.5)*cell, floorY, " +
                       "worldMinZ+(y+0.5)*cell); PNG row 0 = worldMinZ edge; " +
                       "grey = unmeasured; coords match wifi CSV posX/Y/Z",
            };
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(dir, $"{stem}_heatmap.json"),
                JsonUtility.ToJson(meta, true));

            Debug.Log($"[FloorHeatmap] Wrote heatmap PNG to {pngPath}");
        }

        private static long HashKey(int x, int z) => ((long)x << 32) ^ (uint)z;
    }
}
