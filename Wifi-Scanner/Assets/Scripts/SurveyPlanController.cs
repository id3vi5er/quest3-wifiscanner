using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace WifiScanner
{
    /// <summary>
    /// Guided, room-based survey plan and its independent AR overlay. It never
    /// mutates or recolours FloorHeatmapRenderer; live display and export both
    /// consume SurveyAnalyzer.BuildSurvey with the same configuration.
    /// </summary>
    public class SurveyPlanController : MonoBehaviour
    {
        public SampleCollector collector;
        public RoomTracer roomTracer;
        public FloorManager floorManager;
        public FloorHeatmapRenderer floorHeatmap;
        public MultiApScanner multiAp;
        public Material overlayMaterial;

        public SurveyAnalyzer.QualityConfig quality = new SurveyAnalyzer.QualityConfig();
        public float updateInterval = 1f;
        public float floorOffset = 0.035f;

        public SurveyAnalyzer.SurveyLayerMode Mode { get; private set; }
        public bool IsRunning { get; private set; }
        public bool IsCompleted { get; private set; }
        public bool ManuallyReleased { get; private set; }
        public string ManualReleaseReason { get; private set; } = "";
        public SurveyAnalyzer.SurveyFloorResult CurrentResult { get; private set; }
        public int Revision { get; private set; }

        public float ProgressOpacity { get; private set; }
        public float ConfidenceOpacity { get; private set; }
        public float CurrentOpacity => Mode == SurveyAnalyzer.SurveyLayerMode.Progress
            ? ProgressOpacity : ConfidenceOpacity;

        private readonly HashSet<string> _unwalkable = new HashSet<string>();
        private GameObject _quad;
        private MeshRenderer _renderer;
        private Material _material;
        private Texture2D _texture;
        private LineRenderer _targetMarker;
        private float _timer;
        private int _lastDataRevision = -1;
        private int _lastFloorVersion = -1;
        private int _lastRoomHash;
        private float _lastGridYaw = float.NaN;
        private string _lastSurveyBssid;
        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private const string ModePrefs = "wifi_survey_layer_mode";
        private const string ProgressOpacityPrefs = "wifi_survey_progress_opacity";
        private const string ConfidenceOpacityPrefs = "wifi_survey_confidence_opacity";
        private const int PixelsPerCell = 4;

        [Serializable]
        private class SurveyExport
        {
            public int schemaVersion = 1;
            public long exportedTimestampMs;
            public bool surveyCompleted;
            public bool manuallyReleased;
            public string manualReleaseReason;
            public SurveyAnalyzer.QualityConfig criteria;
            public SurveyAnalyzer.SurveyFloorResult floor;
        }

        private void Awake()
        {
            Mode = (SurveyAnalyzer.SurveyLayerMode)Mathf.Clamp(
                PlayerPrefs.GetInt(ModePrefs, 0), 0, 2);
            ProgressOpacity = Mathf.Clamp01(PlayerPrefs.GetFloat(
                ProgressOpacityPrefs, 0.62f));
            ConfidenceOpacity = Mathf.Clamp01(PlayerPrefs.GetFloat(
                ConfidenceOpacityPrefs, 0.62f));
            CreateQuad();
        }

        private void CreateQuad()
        {
            var mesh = new Mesh { name = "SurveyQualityQuad" };
            mesh.vertices = new[]
            {
                new Vector3(-.5f,0f,-.5f), new Vector3(-.5f,0f,.5f),
                new Vector3(.5f,0f,.5f), new Vector3(.5f,0f,-.5f),
            };
            mesh.uv = new[] { Vector2.zero, Vector2.up, Vector2.one, Vector2.right };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateNormals();
            _quad = new GameObject("SurveyQualityOverlay");
            _quad.transform.SetParent(transform, false);
            _quad.AddComponent<MeshFilter>().sharedMesh = mesh;
            _renderer = _quad.AddComponent<MeshRenderer>();
            _renderer.shadowCastingMode = ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _quad.SetActive(false);

            var markerGo = new GameObject("SurveyNextAreaMarker");
            markerGo.transform.SetParent(transform, false);
            _targetMarker = markerGo.AddComponent<LineRenderer>();
            _targetMarker.positionCount = 4;
            _targetMarker.loop = true;
            _targetMarker.startWidth = _targetMarker.endWidth = 0.018f;
            _targetMarker.shadowCastingMode = ShadowCastingMode.Off;
            markerGo.SetActive(false);
        }

        private bool EnsureMaterial()
        {
            if (_material != null) return true;
            if (overlayMaterial == null) return false;
            _material = new Material(overlayMaterial);
            _renderer.sharedMaterial = _material;
            _targetMarker.sharedMaterial = _material;
            ApplyOpacity();
            return true;
        }

        private void Update()
        {
            if (collector == null || roomTracer == null || !EnsureMaterial()) return;
            _timer += Time.deltaTime;
            int roomHash = ComputeRoomHash();
            int floorVersion = floorManager != null ? floorManager.Version : 0;
            float gridYaw = floorHeatmap != null ? floorHeatmap.gridYawDegrees : 0f;
            string surveyBssid = EffectiveBssidFilter();
            if (_timer < updateInterval && collector.DataRevision == _lastDataRevision &&
                roomHash == _lastRoomHash && floorVersion == _lastFloorVersion &&
                Mathf.Abs(gridYaw - _lastGridYaw) < 0.001f &&
                string.Equals(surveyBssid, _lastSurveyBssid,
                    StringComparison.Ordinal))
                return;
            _timer = 0f;
            _lastDataRevision = collector.DataRevision;
            _lastRoomHash = roomHash;
            _lastFloorVersion = floorVersion;
            _lastGridYaw = gridYaw;
            _lastSurveyBssid = surveyBssid;
            RebuildActiveFloor();
        }

        public void SetMode(SurveyAnalyzer.SurveyLayerMode mode)
        {
            Mode = mode;
            PlayerPrefs.SetInt(ModePrefs, (int)Mode);
            ApplyOpacity();
            RebuildTexture();
            RefreshVisibility();
            Revision++;
        }

        public void CycleMode() => SetMode((SurveyAnalyzer.SurveyLayerMode)
            (((int)Mode + 1) % 3));

        public void SetOpacity(float value01)
        {
            value01 = Mathf.Clamp01(value01);
            if (Mode == SurveyAnalyzer.SurveyLayerMode.Progress)
            {
                ProgressOpacity = value01;
                PlayerPrefs.SetFloat(ProgressOpacityPrefs, value01);
            }
            else
            {
                ConfidenceOpacity = value01;
                PlayerPrefs.SetFloat(ConfidenceOpacityPrefs, value01);
            }
            ApplyOpacity();
            Revision++;
        }

        private void ApplyOpacity()
        {
            if (_material != null)
                _material.SetColor(BaseColorId, new Color(1f, 1f, 1f,
                    Mathf.Clamp01(CurrentOpacity)));
        }

        public void StartSurvey()
        {
            IsRunning = true;
            IsCompleted = false;
            ManuallyReleased = false;
            ManualReleaseReason = "";
            if (Mode == SurveyAnalyzer.SurveyLayerMode.Off)
                SetMode(SurveyAnalyzer.SurveyLayerMode.Progress);
            RefreshTargetMarker();
        }

        public void StopSurvey()
        {
            IsRunning = false;
            RefreshTargetMarker();
        }

        public bool CompleteSurvey(string manualReason = null)
        {
            bool naturallyValid = AllFloorsValid();
            if (!naturallyValid && string.IsNullOrWhiteSpace(manualReason)) return false;
            IsRunning = false;
            IsCompleted = true;
            ManuallyReleased = !naturallyValid;
            ManualReleaseReason = ManuallyReleased ? manualReason.Trim() : "";
            RefreshTargetMarker();
            Revision++;
            return true;
        }

        public void SetTargetSpacing(float metres)
        {
            quality.targetMeasurementSpacing = Mathf.Clamp(metres, 0.25f, 1.5f);
            ForceRebuild();
        }

        public bool IsCellUnwalkable(string cellId) =>
            !string.IsNullOrEmpty(cellId) && _unwalkable.Contains(cellId);

        /// <summary>Returns true when the cell is unwalkable after toggling.</summary>
        public bool ToggleCellUnwalkable(string cellId)
        {
            if (string.IsNullOrEmpty(cellId)) return false;
            bool unwalkable;
            if (_unwalkable.Remove(cellId)) unwalkable = false;
            else { _unwalkable.Add(cellId); unwalkable = true; }
            ForceRebuild();
            return unwalkable;
        }

        /// <summary>
        /// Intersects the right-controller ray with the active floor and maps
        /// the hit to the exact rotated Survey grid cell.
        /// </summary>
        public bool TryRaycastCell(Vector3 origin, Vector3 direction,
            out SurveyAnalyzer.SurveyCell cell, out float floorY)
        {
            cell = null;
            floorY = floorManager != null ? floorManager.ActiveBaseY : ResolveFloorY();
            if (CurrentResult == null || CurrentResult.cells.Count == 0) return false;
            float denominator = direction.y;
            if (Mathf.Abs(denominator) < 0.0001f) return false;
            float distance = (floorY + floorOffset - origin.y) / denominator;
            if (distance < 0f || distance > 20f) return false;
            Vector3 hit = origin + direction * distance;
            Vector3 grid = Quaternion.Euler(0f, -CurrentResult.gridYawDegrees, 0f) *
                new Vector3(hit.x, 0f, hit.z);
            float size = Mathf.Max(0.1f, quality.cellSize);
            int x = Mathf.FloorToInt(grid.x / size);
            int z = Mathf.FloorToInt(grid.z / size);
            string id = $"{CurrentResult.floor}:{x}:{z}";
            for (int i = 0; i < CurrentResult.cells.Count; i++)
                if (CurrentResult.cells[i].id == id)
                {
                    cell = CurrentResult.cells[i];
                    return true;
                }
            return false;
        }

        public void ResetForNewSession()
        {
            _unwalkable.Clear();
            IsRunning = false;
            IsCompleted = false;
            ManuallyReleased = false;
            ManualReleaseReason = "";
            CurrentResult = null;
            _lastDataRevision = -1;
            if (_quad != null) _quad.SetActive(false);
            if (_targetMarker != null) _targetMarker.gameObject.SetActive(false);
            Revision++;
        }

        public void OnFloorInserted(int atIndex)
        {
            if (_unwalkable.Count == 0) return;
            var moved = new HashSet<string>();
            foreach (string id in _unwalkable)
            {
                string[] parts = id.Split(':');
                if (parts.Length == 3 && int.TryParse(parts[0], out int floor))
                {
                    if (floor >= atIndex) floor++;
                    moved.Add($"{floor}:{parts[1]}:{parts[2]}");
                }
            }
            _unwalkable.Clear();
            foreach (string id in moved) _unwalkable.Add(id);
            ForceRebuild();
        }

        public void OnSessionAligned()
        {
            // Cell IDs are tied to the world-space 0.5 m grid. A rigid
            // re-alignment changes that grid, so old manual exclusions cannot
            // be mapped without ambiguity and must be documented again.
            _unwalkable.Clear();
            IsCompleted = false;
            ManuallyReleased = false;
            ManualReleaseReason = "";
            ForceRebuild();
        }

        public void ForceRebuild()
        {
            _lastDataRevision = -1;
            _timer = updateInterval;
        }

        private void RebuildActiveFloor()
        {
            int index = floorManager != null ? floorManager.ActiveIndex : 0;
            string label = floorManager != null && floorManager.Floors.Count > index
                ? floorManager.Floors[index].label : "EG";
            float baseY = floorManager != null ? floorManager.ActiveBaseY : ResolveFloorY();
            CurrentResult = SurveyAnalyzer.BuildSurvey(collector.Samples,
                roomTracer.Rooms, index, label, baseY, quality, _unwalkable,
                EffectiveBssidFilter(), 0,
                floorHeatmap != null ? floorHeatmap.gridYawDegrees : 0f);
            RebuildTexture();
            RefreshTargetMarker();
            Revision++;
        }

        private float ResolveFloorY()
        {
            return floorHeatmap != null && floorHeatmap.TryGetActiveFloorY(out float y)
                ? y : 0f;
        }

        /// <summary>
        /// Survey follows the connected BSSID while Multi-AP scanning is off.
        /// With Multi-AP enabled it follows the existing AP selection; an empty
        /// selection then means the analyzer's per-cell dominant BSSID.
        /// </summary>
        private string EffectiveBssidFilter()
        {
            if (multiAp == null || !multiAp.IsEnabled)
                return collector != null && !string.IsNullOrEmpty(collector.LastBssid)
                    ? collector.LastBssid : null;
            return floorHeatmap != null ? floorHeatmap.BssidFilter : null;
        }

        private void RebuildTexture()
        {
            if (CurrentResult == null || CurrentResult.cells.Count == 0 ||
                Mode == SurveyAnalyzer.SurveyLayerMode.Off)
            { RefreshVisibility(); return; }
            CreateRaster(CurrentResult, Mode, out Color32[] pixels,
                out Vector2 min, out int w, out int h);
            if (_texture == null || _texture.width != w || _texture.height != h)
            {
                if (_texture != null) Destroy(_texture);
                _texture = new Texture2D(w, h, TextureFormat.RGBA32, false)
                { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            }
            _texture.SetPixels32(pixels);
            _texture.Apply(false);
            _material.SetTexture(BaseMapId, _texture);
            float worldW = w * quality.cellSize / PixelsPerCell;
            float worldH = h * quality.cellSize / PixelsPerCell;
            float y = floorManager != null ? floorManager.ActiveBaseY : ResolveFloorY();
            Quaternion gridToWorld = Quaternion.Euler(0f,
                CurrentResult.gridYawDegrees, 0f);
            Vector3 centerWorld = gridToWorld * new Vector3(
                min.x + worldW * .5f, 0f, min.y + worldH * .5f);
            _quad.transform.SetPositionAndRotation(new Vector3(
                centerWorld.x, y + floorOffset, centerWorld.z), gridToWorld);
            _quad.transform.localScale = new Vector3(worldW, 1f, worldH);
            RefreshVisibility();
        }

        private void RefreshVisibility()
        {
            if (_quad != null) _quad.SetActive(Mode !=
                SurveyAnalyzer.SurveyLayerMode.Off && CurrentResult != null &&
                CurrentResult.cells.Count > 0);
        }

        private void RefreshTargetMarker()
        {
            if (_targetMarker == null) return;
            var target = IsRunning ? FindNextCell() : null;
            if (target == null)
            { _targetMarker.gameObject.SetActive(false); return; }
            float half = quality.cellSize * .42f;
            float y = (floorManager != null ? floorManager.ActiveBaseY : ResolveFloorY())
                      + floorOffset + .01f;
            Quaternion r = Quaternion.Euler(0f,
                CurrentResult != null ? CurrentResult.gridYawDegrees : 0f, 0f);
            Vector3 center = new Vector3(target.centerX, y, target.centerZ);
            _targetMarker.SetPosition(0, center + r * new Vector3(-half,0f,-half));
            _targetMarker.SetPosition(1, center + r * new Vector3(-half,0f, half));
            _targetMarker.SetPosition(2, center + r * new Vector3( half,0f, half));
            _targetMarker.SetPosition(3, center + r * new Vector3( half,0f,-half));
            _targetMarker.gameObject.SetActive(true);
        }

        public SurveyAnalyzer.SurveyCell FindNextCell()
        {
            if (CurrentResult == null) return null;
            Vector3 head = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            SurveyAnalyzer.SurveyCell best = null;
            float bestCost = float.MaxValue;
            foreach (var c in CurrentResult.cells)
            {
                if (c.status == SurveyAnalyzer.SurveyCellStatus.Sufficient ||
                    c.status == SurveyAnalyzer.SurveyCellStatus.Unwalkable) continue;
                float dx = c.centerX-head.x, dz = c.centerZ-head.z;
                float priority = c.status == SurveyAnalyzer.SurveyCellStatus.Open ? 0f : 1000f;
                float cost = priority + dx*dx + dz*dz;
                if (cost < bestCost) { bestCost = cost; best = c; }
            }
            return best;
        }

        public bool TryGetCurrent(out SurveyAnalyzer.SurveyFloorResult result)
        { result = CurrentResult; return result != null && result.cells.Count > 0; }

        public void Export(string stem)
        {
            int floorCount = floorManager != null ? floorManager.Floors.Count : 1;
            for (int i = 0; i < floorCount; i++)
            {
                string label = floorManager != null ? floorManager.Floors[i].label : "EG";
                float y = floorManager != null ? floorManager.Floors[i].baseY : ResolveFloorY();
                var result = SurveyAnalyzer.BuildSurvey(collector.Samples,
                    roomTracer.Rooms, i, label, y, quality, _unwalkable,
                    EffectiveBssidFilter(), 0,
                    floorHeatmap != null ? floorHeatmap.gridYawDegrees : 0f);
                if (result.cells.Count == 0) continue;
                string safe = Sanitize(label);
                string basePath = Path.Combine(Application.persistentDataPath,
                    $"{stem}_survey_{safe}");
                var wrapper = new SurveyExport
                {
                    exportedTimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    surveyCompleted = IsCompleted,
                    manuallyReleased = ManuallyReleased,
                    manualReleaseReason = ManualReleaseReason,
                    criteria = quality,
                    floor = result,
                };
                File.WriteAllText(basePath + ".json", JsonUtility.ToJson(wrapper, true));
                var exportMode = Mode == SurveyAnalyzer.SurveyLayerMode.Off
                    ? SurveyAnalyzer.SurveyLayerMode.Confidence : Mode;
                CreateRaster(result, exportMode, out Color32[] pixels,
                    out _, out int w, out int h);
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.SetPixels32(pixels); tex.Apply(false);
                File.WriteAllBytes(basePath + ".png", tex.EncodeToPNG());
                Destroy(tex);
            }
        }

        public bool AllFloorsValid()
        {
            if (collector == null || roomTracer == null) return false;
            int count = floorManager != null ? floorManager.Floors.Count : 1;
            bool any = false;
            for (int i = 0; i < count; i++)
            {
                string label = floorManager != null ? floorManager.Floors[i].label : "EG";
                float y = floorManager != null ? floorManager.Floors[i].baseY : ResolveFloorY();
                var r = SurveyAnalyzer.BuildSurvey(collector.Samples, roomTracer.Rooms,
                    i, label, y, quality, _unwalkable,
                    EffectiveBssidFilter(), 0,
                    floorHeatmap != null ? floorHeatmap.gridYawDegrees : 0f);
                if (r.cells.Count == 0) continue;
                any = true;
                if (!r.valid) return false;
            }
            return any;
        }

        public List<SurveyAnalyzer.SurveyFloorResult> AnalyzeAllFloors()
        {
            var results = new List<SurveyAnalyzer.SurveyFloorResult>();
            if (collector == null || roomTracer == null) return results;
            int count = floorManager != null ? floorManager.Floors.Count : 1;
            for (int i = 0; i < count; i++)
            {
                string label = floorManager != null ? floorManager.Floors[i].label : "EG";
                float y = floorManager != null ? floorManager.Floors[i].baseY : ResolveFloorY();
                var result = SurveyAnalyzer.BuildSurvey(collector.Samples,
                    roomTracer.Rooms, i, label, y, quality, _unwalkable,
                    EffectiveBssidFilter(), 0,
                    floorHeatmap != null ? floorHeatmap.gridYawDegrees : 0f);
                if (result.cells.Count > 0) results.Add(result);
            }
            return results;
        }

        public static Color32 ColorFor(SurveyAnalyzer.SurveyCell cell,
            SurveyAnalyzer.SurveyLayerMode mode, int x = 0, int y = 0)
        {
            Color c;
            // Manual walkability must remain recognizable in either layer mode.
            if (cell.status == SurveyAnalyzer.SurveyCellStatus.Unwalkable)
            {
                c = new Color(.16f, .16f, .18f, .9f);
                if (((x / 2) + (y / 2)) % 3 == 0)
                    c = new Color(.42f, .18f, .14f, .95f);
            }
            else if (mode == SurveyAnalyzer.SurveyLayerMode.Progress)
            {
                c = cell.status switch
                {
                    SurveyAnalyzer.SurveyCellStatus.Open => new Color(.38f,.40f,.44f,1f),
                    SurveyAnalyzer.SurveyCellStatus.Measured => new Color(.12f,.48f,.95f,1f),
                    SurveyAnalyzer.SurveyCellStatus.Sufficient => new Color(.12f,.78f,.28f,1f),
                    SurveyAnalyzer.SurveyCellStatus.Critical => new Color(.9f,.15f,.12f,1f),
                    _ => Color.clear,
                };
            }
            else
            {
                float t = Mathf.Clamp01(cell.quality.score / 100f);
                c = t < .5f
                    ? Color.Lerp(new Color(.9f,.1f,.08f), new Color(1f,.82f,.08f), t*2f)
                    : Color.Lerp(new Color(1f,.82f,.08f), new Color(.1f,.8f,.24f), (t-.5f)*2f);
            }
            if ((cell.quality.interpolated || cell.quality.stale) && ((x + y) & 1) == 0)
                c = Color.Lerp(c, Color.black, .42f);
            return c;
        }

        private void CreateRaster(SurveyAnalyzer.SurveyFloorResult result,
            SurveyAnalyzer.SurveyLayerMode mode, out Color32[] pixels,
            out Vector2 min, out int w, out int h)
        {
            float cell = Mathf.Max(.1f, result.cellSize);
            float minX=float.MaxValue,minZ=float.MaxValue,maxX=float.MinValue,maxZ=float.MinValue;
            foreach (var c in result.cells)
            { minX=Mathf.Min(minX,c.gridX-cell*.5f); maxX=Mathf.Max(maxX,c.gridX+cell*.5f);
              minZ=Mathf.Min(minZ,c.gridZ-cell*.5f); maxZ=Mathf.Max(maxZ,c.gridZ+cell*.5f); }
            int gridW=Mathf.Max(1,Mathf.CeilToInt((maxX-minX)/cell));
            int gridH=Mathf.Max(1,Mathf.CeilToInt((maxZ-minZ)/cell));
            w=gridW*PixelsPerCell; h=gridH*PixelsPerCell;
            min=new Vector2(minX,minZ); pixels=new Color32[w*h];
            foreach (var c in result.cells)
            {
                int gx=Mathf.Clamp(Mathf.FloorToInt((c.gridX-minX)/cell),0,gridW-1);
                int gy=Mathf.Clamp(Mathf.FloorToInt((c.gridZ-minZ)/cell),0,gridH-1);
                for(int py=0;py<PixelsPerCell;py++)
                for(int px=0;px<PixelsPerCell;px++)
                {
                    int x=gx*PixelsPerCell+px, y=gy*PixelsPerCell+py;
                    pixels[y*w+x]=ColorFor(c,mode,px,py);
                }
            }
        }

        private int ComputeRoomHash()
        {
            unchecked
            {
                int h = 17;
                foreach (var r in roomTracer.Rooms)
                { h=h*31+r.cornersXZ.Count; h=h*31+Mathf.RoundToInt(r.floorY*100f);
                  foreach(var c in r.cornersXZ){h=h*31+Mathf.RoundToInt(c.x*100f);h=h*31+Mathf.RoundToInt(c.y*100f);} }
                return h;
            }
        }

        private static string Sanitize(string value)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) value=value.Replace(c,'_');
            return string.IsNullOrWhiteSpace(value) ? "floor" : value;
        }
    }
}
