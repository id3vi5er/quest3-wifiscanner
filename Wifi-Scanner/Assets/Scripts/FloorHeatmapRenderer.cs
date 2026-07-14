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
        [Tooltip("Optional: multi-storey manager. The heatmap shows the active " +
                 "storey only (single plane) at that storey's base height.")]
        public FloorManager floorManager;

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
        [Tooltip("Yaw of the heatmap grid (deg) so the raster runs parallel to " +
                 "the walls. Set via GridCalibrator (two-controller wall " +
                 "calibration from the panel).")]
        public float gridYawDegrees;

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
        // XZ of texel (0,0) corner in GRID space (world rotated by -gridYaw
        // around Y, so the raster axes run parallel to the calibrated wall).
        private Vector2 _worldMin;
        private float _floorY;
        private float _timer;
        private int _lastRevision = -1;
        private int _lastFloorVersion = -1;
        private bool _rebuilding;
        private bool _warnedNoMaterial;
        private bool _hasFloorOverride;
        private float _floorOverrideY;
        // Whether the floor quad should be shown (view mode). Deliberately
        // decoupled from this component being active: the interpolation keeps
        // running in every view mode so the minimap and PNG export stay fresh.
        private bool _quadVisible = true;
        private int _filterMinDbm = -127;
        private int _filterMaxDbm = 0;
        // Diff view: loaded reference scan + its field on the current grid.
        private System.Collections.Generic.List<Sample> _diffSamples;
        private float[] _refCellRssi;
        // Per-AP view: only samples of this BSSID ("" = all APs in parallel).
        private string _bssidFilter = "";

        /// <summary>Bumped whenever the cached RSSI field or its colour mapping
        /// changes. Consumers such as the dynamic room mesh use this to refresh
        /// their vertex colours without polling the texture itself.</summary>
        public int FieldRevision { get; private set; }

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
            // Floor opacity lives in the material tint, NOT in the texture —
            // the texture stays fully opaque so the minimap (which shows the
            // same texture) can fade independently.
            _mat.SetColor(BaseColorId, new Color(1f, 1f, 1f, Mathf.Clamp01(opacity)));
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
            if (_hasFloorOverride)
                return _floorOverrideY + floorOffset;
            // Multi-storey: each storey has its own base plane.
            if (floorManager != null && floorManager.Floors.Count > 0)
                return floorManager.ActiveBaseY + floorOffset;
            if (TryGetTrackedFloorY(_xrOrigin, out float floorY))
                return floorY + floorOffset;
            return minSampleY - 1.2f + floorOffset;
        }

        /// <summary>Manually calibrated floor height (controller held to the
        /// floor, see RoomTracer) — overrides the tracked origin, shared by
        /// heatmap placement and room tracing. Triggers a rebuild.</summary>
        public void SetFloorOverride(float y)
        {
            // With multiple storeys, correct the ACTIVE storey's base instead of
            // a single global override (so each floor keeps its own plane).
            if (floorManager != null && floorManager.Floors.Count > 0)
                floorManager.SetActiveBaseY(y);
            else
            {
                _floorOverrideY = y;
                _hasFloorOverride = true;
            }
            _lastRevision = -1;
            _timer = updateInterval;
        }

        /// <summary>Clears calibration, filters, diff data and cached geometry
        /// for a genuinely blank survey session.</summary>
        public void ResetForNewSession()
        {
            StopAllCoroutines();
            _rebuilding = false;
            _hasFloorOverride = false;
            _floorOverrideY = 0f;
            gridYawDegrees = 0f;
            _bssidFilter = "";
            _diffSamples = null;
            DiffStem = "";
            _refCellRssi = null;
            _cellRssi = null;
            _lastRevision = -1;
            _timer = updateInterval;
            if (_quadGo != null) _quadGo.SetActive(false);
        }

        /// <summary>Best known floor height: manual override, else the
        /// floor-based tracking origin. False if neither exists (Editor).</summary>
        public bool TryGetFloorY(out float y)
        {
            if (_hasFloorOverride)
            {
                y = _floorOverrideY;
                return true;
            }
            return TryGetTrackedFloorY(_xrOrigin, out y);
        }

        /// <summary>Best floor height for the currently displayed storey.</summary>
        public bool TryGetActiveFloorY(out float y)
        {
            if (floorManager != null && floorManager.Floors.Count > 0)
            {
                y = floorManager.ActiveBaseY;
                return true;
            }
            return TryGetFloorY(out y);
        }

        /// <summary>
        /// True (with the physical floor's world Y) when the XR Origin runs a
        /// floor-based tracking origin — the Quest's default, calibrated by the
        /// system. Shared by the heatmap and the room tracer.
        /// </summary>
        public static bool TryGetTrackedFloorY(XROrigin origin, out float floorY)
        {
            if (origin != null &&
                origin.CurrentTrackingOriginMode == TrackingOriginModeFlags.Floor)
            {
                floorY = origin.transform.position.y;
                return true;
            }
            floorY = 0f;
            return false;
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

            // A storey switch forces a rebuild even without new data.
            if (floorManager != null && floorManager.Version != _lastFloorVersion)
            {
                _lastFloorVersion = floorManager.Version;
                _lastRevision = -1;
                _timer = updateInterval;
            }

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

        /// <summary>Floor-quad opacity only (material tint) — the texture and
        /// therefore the minimap are unaffected.</summary>
        public void SetOpacity(float value01)
        {
            opacity = Mathf.Clamp01(value01);
            if (_mat != null)
                _mat.SetColor(BaseColorId, new Color(1f, 1f, 1f, opacity));
        }

        /// <summary>Cells outside [minDbm, maxDbm] become fully transparent.</summary>
        public void SetRssiFilter(int minDbm, int maxDbm)
        {
            _filterMinDbm = minDbm;
            _filterMaxDbm = maxDbm;
            Colorize();
        }

        /// <summary>Currently selected AP ("" = all BSSIDs in parallel).</summary>
        public string BssidFilter => _bssidFilter;

        /// <summary>
        /// Restricts the heatmap to ONE access point's samples (its own map —
        /// samples are BSSID-tagged, so every AP has a clean separate field).
        /// "" or null shows all APs in parallel. Triggers a rebuild.
        /// </summary>
        public void SetBssidFilter(string bssid)
        {
            _bssidFilter = bssid ?? "";
            _lastRevision = -1; // force a rebuild
            _timer = updateInterval;
        }

        /// <summary>True while the diff view (current − reference) is showing.</summary>
        public bool DiffEnabled => _diffSamples != null;
        /// <summary>Stem of the loaded diff reference ("" when disabled).</summary>
        public string DiffStem { get; private set; } = "";

        /// <summary>
        /// Enables the diff view against a loaded reference scan (pass null to
        /// disable): cells show current − reference in a red↔grey↔green ramp
        /// (±20 dB). The reference must have been aligned to the current
        /// tracking origin (ScanAligner) to be meaningful.
        /// </summary>
        public void SetDiffReference(string stem, System.Collections.Generic.List<Sample> samples)
        {
            _diffSamples = samples;
            DiffStem = samples != null ? (stem ?? "") : "";
            _refCellRssi = null;
            _lastRevision = -1; // force a rebuild
            _timer = updateInterval;
        }

        // Result of one IDW interpolation pass (grid-space georeference).
        private class FieldResult
        {
            public bool ok;
            public float[] cellRssi;
            public Vector2 worldMin; // XZ of texel (0,0) corner, grid space
            public float cell;
            public int w, h;
            public float minSampleY;
        }

        private IEnumerator Rebuild()
        {
            _rebuilding = true;
            _lastRevision = collector.DataRevision;

            // With multiple storeys the heatmap shows the active storey only —
            // a heatmap is inherently one horizontal plane. (The AP locator, by
            // contrast, always uses ALL samples across every floor.)
            var result = new FieldResult();
            var pass = ComputeField(collector.Samples, floorManager != null,
                floorManager != null ? floorManager.ActiveIndex : 0, result,
                _bssidFilter);
            while (pass.MoveNext()) yield return pass.Current;

            // No measurements on the active storey → nothing to draw.
            if (!result.ok)
            {
                _quadGo.SetActive(false);
                _rebuilding = false;
                yield break;
            }

            _cellRssi = result.cellRssi;
            _worldMin = result.worldMin;
            _worldCell = result.cell;
            _texW = result.w;
            _texH = result.h;
            _floorY = ResolveFloorY(result.minSampleY);

            // Diff view: rasterise the loaded reference scan onto the SAME
            // grid so the cells subtract 1:1. NaN handling stays per cell.
            _refCellRssi = null;
            if (_diffSamples != null)
            {
                var refCell = new float[_texW * _texH];
                var refPass = RasterizeSamplesOnGrid(_diffSamples,
                    floorManager != null,
                    floorManager != null ? floorManager.ActiveIndex : 0,
                    _worldMin, _worldCell, _texW, _texH, refCell, _bssidFilter);
                while (refPass.MoveNext()) yield return refPass.Current;
                _refCellRssi = refCell;
            }

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

            // --- Place the quad (texel-exact so texture and world line up),
            //     rotated back from grid space into the world ---
            float quadW = _texW * _worldCell;
            float quadH = _texH * _worldCell;
            Quaternion gridToWorld = Quaternion.Euler(0f, gridYawDegrees, 0f);
            Vector3 centerWorld = gridToWorld * new Vector3(
                _worldMin.x + quadW * 0.5f, 0f, _worldMin.y + quadH * 0.5f);
            _quadGo.transform.SetPositionAndRotation(
                new Vector3(centerWorld.x, _floorY, centerWorld.z), gridToWorld);
            _quadGo.transform.localScale = new Vector3(quadW, 1f, quadH);
            _quadGo.SetActive(_quadVisible);

            _rebuilding = false;
        }

        // IDW interpolation for one storey (or all samples when filterFloor is
        // false), spread over frames via texelsPerFrame. Uses only locals and
        // the result object, so an export pass can drain it synchronously even
        // while a live Rebuild coroutine is in flight. The sample source is a
        // parameter so the diff view can rasterise a LOADED reference scan.
        private IEnumerator ComputeField(
            System.Collections.Generic.IReadOnlyList<Sample> samples,
            bool filterFloor, int floorFilter, FieldResult result,
            string bssidFilter = null)
        {
            // --- Snapshot the samples (the list only grows / updates in place) ---
            bool filterBssid = !string.IsNullOrEmpty(bssidFilter);
            int total = samples.Count;
            var px = new float[total];
            var pz = new float[total];
            var prssi = new float[total];
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            float minY = float.MaxValue;
            // Everything below rasterises in GRID space (world rotated by
            // -gridYaw); distances are rotation-invariant, so the IDW result
            // is unchanged — only the raster axes align with the wall.
            Quaternion worldToGrid = Quaternion.Euler(0f, -gridYawDegrees, 0f);
            int n = 0;
            for (int i = 0; i < total; i++)
            {
                var s = samples[i];
                if (filterFloor && s.floor != floorFilter) continue;
                if (filterBssid && s.bssid != bssidFilter) continue;
                Vector3 g = worldToGrid * new Vector3(s.posX, 0f, s.posZ);
                px[n] = g.x;
                pz[n] = g.z;
                prssi[n] = s.rssiDbm;
                if (g.x < minX) minX = g.x;
                if (g.x > maxX) maxX = g.x;
                if (g.z < minZ) minZ = g.z;
                if (g.z > maxZ) maxZ = g.z;
                if (s.posY < minY) minY = s.posY;
                n++;
            }

            // No measurements on that storey → nothing to interpolate.
            if (n == 0)
            {
                result.ok = false;
                yield break;
            }

            minX -= padding; maxX += padding;
            minZ -= padding; maxZ += padding;
            result.minSampleY = minY;

            float sizeX = Mathf.Max(maxX - minX, 0.5f);
            float sizeZ = Mathf.Max(maxZ - minZ, 0.5f);
            float worldCell = Mathf.Max(cellSize, sizeX / maxTextureDim, sizeZ / maxTextureDim);
            int texW = Mathf.Max(2, Mathf.CeilToInt(sizeX / worldCell));
            int texH = Mathf.Max(2, Mathf.CeilToInt(sizeZ / worldCell));
            var worldMin = new Vector2(minX, minZ);

            var cellRssi = new float[texW * texH];
            var raster = RasterizeOnGrid(px, pz, prssi, n,
                worldMin, worldCell, texW, texH, cellRssi);
            while (raster.MoveNext()) yield return raster.Current;

            result.cellRssi = cellRssi;
            result.worldMin = worldMin;
            result.cell = worldCell;
            result.w = texW;
            result.h = texH;
            result.ok = true;
        }

        // The IDW core: rasterises pre-projected grid-space points onto a
        // FIXED grid (shared by the live field and the diff reference).
        private IEnumerator RasterizeOnGrid(float[] px, float[] pz, float[] prssi,
            int n, Vector2 worldMin, float worldCell, int texW, int texH,
            float[] cellRssi)
        {
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

            float maxDistSqr = maxInterpolationDistance * maxInterpolationDistance;
            int budget = Mathf.Max(256, texelsPerFrame);

            for (int ty = 0; ty < texH; ty++)
            {
                float wz = worldMin.y + (ty + 0.5f) * worldCell;
                int hz = Mathf.FloorToInt(wz / hashCell);
                for (int tx = 0; tx < texW; tx++)
                {
                    float wx = worldMin.x + (tx + 0.5f) * worldCell;
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

                    cellRssi[ty * texW + tx] =
                        nearestSqr <= maxDistSqr ? vSum / wSum : float.NaN;

                    if (--budget <= 0)
                    {
                        budget = Mathf.Max(256, texelsPerFrame);
                        yield return null;
                    }
                }
            }
        }

        // Projects LOADED reference samples into grid space and rasterises
        // them onto the current grid (diff view). Fills cellRssi with NaN when
        // the reference has no samples on this storey.
        private IEnumerator RasterizeSamplesOnGrid(
            System.Collections.Generic.IReadOnlyList<Sample> samples,
            bool filterFloor, int floorFilter, Vector2 worldMin, float worldCell,
            int texW, int texH, float[] cellRssi, string bssidFilter = null)
        {
            bool filterBssid = !string.IsNullOrEmpty(bssidFilter);
            int total = samples.Count;
            var px = new float[total];
            var pz = new float[total];
            var prssi = new float[total];
            Quaternion worldToGrid = Quaternion.Euler(0f, -gridYawDegrees, 0f);
            int n = 0;
            for (int i = 0; i < total; i++)
            {
                var s = samples[i];
                if (filterFloor && s.floor != floorFilter) continue;
                if (filterBssid && s.bssid != bssidFilter) continue;
                Vector3 g = worldToGrid * new Vector3(s.posX, 0f, s.posZ);
                px[n] = g.x;
                pz[n] = g.z;
                prssi[n] = s.rssiDbm;
                n++;
            }
            if (n == 0)
            {
                for (int i = 0; i < cellRssi.Length; i++) cellRssi[i] = float.NaN;
                yield break;
            }
            var raster = RasterizeOnGrid(px, pz, prssi, n,
                worldMin, worldCell, texW, texH, cellRssi);
            while (raster.MoveNext()) yield return raster.Current;
        }

        // Maps the cached RSSI field to pixels: gradient inside the filter range,
        // transparent outside it, grey grid pattern for unmeasured gap cells.
        // In diff mode: current − reference in a red↔grey↔green ramp instead.
        private void Colorize()
        {
            if (_tex == null || _cellRssi == null || _pixels == null) return;
            bool diff = _refCellRssi != null &&
                        _refCellRssi.Length == _cellRssi.Length;

            for (int ty = 0; ty < _texH; ty++)
            {
                float wz = _worldMin.y + (ty + 0.5f) * _worldCell;
                bool gridZ = Mathf.Repeat(wz, GapGridSpacing) < _worldCell;
                for (int tx = 0; tx < _texW; tx++)
                {
                    int idx = ty * _texW + tx;
                    float rssi = _cellRssi[idx];
                    if (float.IsNaN(rssi) || (diff && float.IsNaN(_refCellRssi[idx])))
                    {
                        float wx = _worldMin.x + (tx + 0.5f) * _worldCell;
                        bool grid = gridZ || Mathf.Repeat(wx, GapGridSpacing) < _worldCell;
                        _pixels[idx] = grid ? GapGridColor : GapColor;
                    }
                    else if (diff)
                    {
                        _pixels[idx] = DiffColor(rssi - _refCellRssi[idx]);
                    }
                    else if (rssi < _filterMinDbm || rssi > _filterMaxDbm)
                    {
                        _pixels[idx] = new Color32(0, 0, 0, 0);
                    }
                    else
                    {
                        // Full alpha — the floor quad fades via its material
                        // tint, the minimap via its RawImage colour.
                        Color c = HeatmapColorMapper.Map(Mathf.RoundToInt(rssi));
                        _pixels[idx] = new Color32(
                            (byte)(c.r * 255f), (byte)(c.g * 255f),
                            (byte)(c.b * 255f), 255);
                    }
                }
            }
            _tex.SetPixels32(_pixels);
            _tex.Apply(false);
            FieldRevision++;
        }

        // Diverging ramp for the diff view: −20 dB (worse) = red, 0 = grey,
        // +20 dB (better) = green.
        private static Color32 DiffColor(float deltaDb)
        {
            float t = Mathf.Clamp(deltaDb / 20f, -1f, 1f);
            Color c = t < 0f
                ? Color.Lerp(new Color(0.55f, 0.55f, 0.55f), new Color(0.85f, 0.15f, 0.1f), -t)
                : Color.Lerp(new Color(0.55f, 0.55f, 0.55f), new Color(0.1f, 0.8f, 0.25f), t);
            return new Color32((byte)(c.r * 255f), (byte)(c.g * 255f),
                (byte)(c.b * 255f), 255);
        }

        /// <summary>
        /// Shows/hides the floor quad only — computation keeps running so the
        /// minimap texture and PNG export stay current in every view mode.
        /// </summary>
        public void SetVisible(bool visible)
        {
            _quadVisible = visible;
            if (_quadGo != null)
                _quadGo.SetActive(_quadVisible && _cellRssi != null);
        }

        /// <summary>Live heatmap texture (recreated when the area grows —
        /// consumers must re-read this reference periodically).</summary>
        public Texture2D HeatmapTexture => _tex;

        /// <summary>Georeference of the current texture (grid space — convert
        /// world positions with <see cref="WorldToMapXZ"/>). False until the
        /// first interpolation pass has run.</summary>
        public bool TryGetMapInfo(out Vector2 worldMinXZ, out float cellSize,
            out int width, out int height)
        {
            worldMinXZ = _worldMin;
            cellSize = _worldCell;
            width = _texW;
            height = _texH;
            return _cellRssi != null && _tex != null;
        }

        /// <summary>World position → grid-space XZ (the texture's coordinate
        /// frame, rotated by -gridYaw so it matches TryGetMapInfo).</summary>
        public Vector2 WorldToMapXZ(Vector3 world)
        {
            Vector3 g = Quaternion.Euler(0f, -gridYawDegrees, 0f) * world;
            return new Vector2(g.x, g.z);
        }

        /// <summary>
        /// Samples the generated IDW heatmap at a world position. Bilinear
        /// interpolation ignores unmeasured (NaN) neighbours and returns false
        /// outside the generated field or inside a complete gap.
        /// </summary>
        public bool TrySampleRssi(Vector3 world, out float rssiDbm)
        {
            rssiDbm = 0f;
            if (_cellRssi == null || _texW < 1 || _texH < 1 || _worldCell <= 0f)
                return false;

            Vector2 map = WorldToMapXZ(world);
            float fx = (map.x - _worldMin.x) / _worldCell - 0.5f;
            float fy = (map.y - _worldMin.y) / _worldCell - 0.5f;
            if (fx < -0.5f || fy < -0.5f ||
                fx > _texW - 0.5f || fy > _texH - 0.5f)
                return false;

            int x0 = Mathf.FloorToInt(fx);
            int y0 = Mathf.FloorToInt(fy);
            float tx = fx - x0;
            float ty = fy - y0;
            float weighted = 0f;
            float weightSum = 0f;

            Add(x0, y0, (1f - tx) * (1f - ty));
            Add(x0 + 1, y0, tx * (1f - ty));
            Add(x0, y0 + 1, (1f - tx) * ty);
            Add(x0 + 1, y0 + 1, tx * ty);

            if (weightSum <= 0f) return false;
            rssiDbm = weighted / weightSum;
            return true;

            void Add(int x, int y, float weight)
            {
                if (weight <= 0f || x < 0 || y < 0 || x >= _texW || y >= _texH)
                    return;
                float value = _cellRssi[y * _texW + x];
                if (float.IsNaN(value)) return;
                weighted += value * weight;
                weightSum += weight;
            }
        }

        /// <summary>
        /// Rotates the heatmap raster so its axes run parallel to a wall
        /// (two-controller calibration). Normalised to (-90, 90] — the grid is
        /// symmetric under 90° steps — and triggers a full rebuild.
        /// </summary>
        public void SetGridYaw(float yawDegrees)
        {
            float yaw = Mathf.DeltaAngle(0f, yawDegrees); // -> (-180, 180]
            if (yaw > 90f) yaw -= 180f;
            else if (yaw <= -90f) yaw += 180f;
            gridYawDegrees = yaw;
            _lastRevision = -1;          // force a rebuild
            _timer = updateInterval;
            Debug.Log($"[FloorHeatmap] Grid-Ausrichtung -> {yaw:F1}°");
        }

        [System.Serializable]
        private class HeatmapMeta
        {
            public float worldMinX, worldMinZ;
            public float cellSizeMeters;
            public int width, height;
            public float floorY;
            public float gridYawDegrees;
            public int weakDbm, strongDbm;
            public string floorLabel;
            public string note;
        }

        /// <summary>
        /// Writes the current heatmap as PNG (full opacity, unfiltered) plus a
        /// small JSON sidecar with the world georeference, so PC tools can lay
        /// the image congruently under the CSV/OBJ data. If the cached field is
        /// stale or missing (e.g. point-cloud-only view mode), the interpolation
        /// runs synchronously first — acceptable for a deliberate export.
        /// With multiple storeys, each storey with data additionally gets its
        /// own pair ({stem}_heatmap_EG.png, _OG1.png, … + matching JSON),
        /// interpolated offline with that storey's floor filter.
        /// Returns the active storey's PNG bytes (reused for the combined SVG),
        /// or null.
        /// </summary>
        public byte[] ExportPng(string stem)
        {
            if (collector == null || collector.SampleCount == 0 || floorMaterial == null)
                return null;

            // Computation now runs in every view mode, so the cached field is
            // at most updateInterval old — only an initial pass may be missing.
            if (!_rebuilding && _cellRssi == null)
            {
                var pass = Rebuild();
                while (pass.MoveNext()) { } // drain synchronously
            }
            if (_cellRssi == null) return null;

            bool multiStorey = floorManager != null && floorManager.Floors.Count > 1;
            string activeLabel = floorManager != null && floorManager.Floors.Count > 0
                ? floorManager.Floors[floorManager.ActiveIndex].label
                : "";

            // Main file: the active storey's cached field (single-storey scans
            // keep their familiar {stem}_heatmap.png name).
            byte[] png = EncodeField(_cellRssi, _texW, _texH);
            WriteHeatmapFiles(stem, "", png, _worldMin, _worldCell, _texW, _texH,
                _floorY, activeLabel);

            // Multi-storey: one PNG + JSON per storey with data, computed
            // offline with that storey's filter (synchronous drain). Exports
            // are deliberately UNfiltered by BSSID (full record per storey).
            if (multiStorey)
            {
                for (int i = 0; i < floorManager.Floors.Count; i++)
                {
                    var result = new FieldResult();
                    var pass = ComputeField(collector.Samples, true, i, result);
                    while (pass.MoveNext()) { }
                    if (!result.ok) continue; // no samples on that storey

                    var floorLevel = floorManager.Floors[i];
                    byte[] floorPng = EncodeField(result.cellRssi, result.w, result.h);
                    WriteHeatmapFiles(stem, "_" + floorLevel.label, floorPng,
                        result.worldMin, result.cell, result.w, result.h,
                        floorLevel.baseY + floorOffset, floorLevel.label);
                }
            }

            // Multi-AP: one PNG + JSON per access point (BSSID) on the ACTIVE
            // storey — each AP has its own clean heatmap since roaming.
            var bssids = collector.KnownBssids;
            if (bssids.Count > 1)
            {
                int activeFloor = floorManager != null ? floorManager.ActiveIndex : 0;
                for (int i = 0; i < bssids.Count; i++)
                {
                    // Sparse neighbour networks (multi-AP scan) produce
                    // meaningless maps — only export densely measured APs.
                    if (bssids[i].sampleCount < 30) continue;
                    var result = new FieldResult();
                    var pass = ComputeField(collector.Samples,
                        floorManager != null, activeFloor, result, bssids[i].bssid);
                    while (pass.MoveNext()) { }
                    if (!result.ok) continue;

                    byte[] apPng = EncodeField(result.cellRssi, result.w, result.h);
                    string band = Sample.BandLabel(bssids[i].frequencyMhz)
                        .Replace(",", "_").Replace(" ", "");
                    WriteHeatmapFiles(stem, $"_AP{i + 1}_{band}", apPng,
                        result.worldMin, result.cell, result.w, result.h,
                        _floorY, $"AP {i + 1} ({bssids[i].bssid})");
                }
            }
            return png;
        }

        // RSSI field -> fully opaque PNG (gradient, grey = unmeasured); the
        // RSSI display filter is deliberately NOT applied to exports.
        private static byte[] EncodeField(float[] cellRssi, int w, int h)
        {
            var pixels = new Color32[w * h];
            for (int i = 0; i < cellRssi.Length; i++)
            {
                float rssi = cellRssi[i];
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

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.SetPixels32(pixels);
            tex.Apply(false);
            byte[] png = tex.EncodeToPNG();
            Destroy(tex);
            return png;
        }

        // {stem}_heatmap{suffix}.png + .json (georeference sidecar).
        private void WriteHeatmapFiles(string stem, string suffix, byte[] png,
            Vector2 worldMin, float cell, int w, int h, float floorY,
            string floorLabel)
        {
            string dir = Application.persistentDataPath;
            string pngPath = System.IO.Path.Combine(dir, $"{stem}_heatmap{suffix}.png");
            System.IO.File.WriteAllBytes(pngPath, png);

            var meta = new HeatmapMeta
            {
                worldMinX = worldMin.x,
                worldMinZ = worldMin.y,
                cellSizeMeters = cell,
                width = w,
                height = h,
                floorY = floorY,
                gridYawDegrees = gridYawDegrees,
                weakDbm = HeatmapColorMapper.WeakDbm,
                strongDbm = HeatmapColorMapper.StrongDbm,
                floorLabel = floorLabel,
                note = "pixel(x,y) center = grid(worldMinX+(x+0.5)*cell, floorY, " +
                       "worldMinZ+(y+0.5)*cell); world = rotate grid point by " +
                       "+gridYawDegrees around Y; PNG row 0 = worldMinZ edge; " +
                       "grey = unmeasured; world coords match wifi CSV posX/Y/Z",
            };
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(dir, $"{stem}_heatmap{suffix}.json"),
                JsonUtility.ToJson(meta, true));

            Debug.Log($"[FloorHeatmap] Wrote heatmap PNG to {pngPath}");
        }

        private static long HashKey(int x, int z) => ((long)x << 32) ^ (uint)z;
    }
}
