using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Meshia.MeshSimplification;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;

namespace WifiScanner
{
    public enum RoomMeshColorMode
    {
        SurfaceNormals,
        WifiSignal
    }

    /// <summary>
    /// Optional real-time room mesh reconstructed from Quest environment depth.
    /// Scanning and visibility are deliberately independent: stopping releases
    /// the depth request and all workers but retains the last mesh for display
    /// and export. A new survey clears both the TSDF and retained meshes.
    /// </summary>
    public sealed class DynamicRoomMeshController : MonoBehaviour
    {
        [Header("Runtime state")]
        [Tooltip("Start environment-depth scanning on app launch.")]
        public bool scanOnAtStart;
        [Tooltip("Show generated mesh chunks. Independent from scanning.")]
        public bool meshVisibleAtStart = true;

        [Header("Lasertag reconstruction settings")]
        [Range(1f, 30f)] public float updateFrequency = 15f;
        [Min(1)] public int meshWorkers = 2;
        [Min(1)] public int remeshSweptVoxels = 50;

        [Header("Meshia decimation settings")]
        public MeshSimplificationTarget decimationTarget = new()
        {
            Kind = MeshSimplificationTargetKind.ScaledTotalError,
            Value = 0.5f
        };

        public MeshSimplifierOptions decimationOptions = new()
        {
            EnableSmartLink = false,
            MinNormalDot = 0.8f,
            PreserveBorderEdges = true,
            PreserveSurfaceCurvature = false,
            UseBarycentricCoordinateInterpolation = false,
            VertexLinkDistance = 0.0001f,
            VertexLinkMinNormalDot = 0.95f,
            VertexLinkColorDistance = 0.01f,
            VertexLinkUvDistance = 0.001f
        };

        [Header("Signal colour settings")]
        [Tooltip("Initial in-headset colour mode. The PLY export always uses " +
                 "the WiFi signal colours regardless of this setting.")]
        public RoomMeshColorMode colorModeAtStart = RoomMeshColorMode.SurfaceNormals;
        [Range(0f, 1f)] public float meshOpacityAtStart = 1f;
        [Range(0.25f, 5f)] public float signalColorUpdateInterval = 1f;
        [Range(1, 16)] public int colorChunksPerFrame = 4;
        [Range(0.25f, 0.9f)] public float floorNormalThreshold = 0.6f;
        [Range(0.1f, 0.75f)] public float floorHeightTolerance = 0.35f;

        [Header("Web dashboard preview")]
        [Tooltip("Optional embedded dashboard receiving a bounded live mesh preview.")]
        public WebDashboardServer webServer;
        [Min(0.5f)] public float dashboardMeshUpdateInterval = 1.5f;
        [Range(250, 6000)] public int dashboardMeshMaxTriangles = 2500;

        public bool ScanEnabled { get; private set; }
        public bool MeshVisible { get; private set; }
        public RoomMeshColorMode ColorMode { get; private set; }
        public float MeshOpacity { get; private set; } = 1f;
        public int ChunkCount => _chunks.Count;
        public int TriangleCount { get; private set; }

        private readonly Dictionary<int, MeshChunk> _chunks = new();
        private readonly ConcurrentQueue<MeshChunk> _meshQueue = new();
        private readonly Queue<int> _colorQueue = new();
        private CancellationTokenSource _workerCancellation;
        private SemaphoreSlim _meshSemaphore;
        private DynamicOcclusionController _depthCoordinator;
        private SampleCollector _collector;
        private FloorHeatmapRenderer _floorHeatmap;
        private RoomMeshDepthSource _depth;
        private RoomMeshVolume _volume;
        private Material _material;
        private Transform _head;
        private float _nextScanTime;
        private bool _ready;
        private bool _pollBusy;
        private int _generation;
        private int _colorDataRevision = -1;
        private int _colorFieldRevision = -1;
        private float _nextColorUpdate;
        private string _bssidFilter = "";
        private SignalKdTree _signalIndex;
        private float _saveOpacityPrefsAt = -1f;
        private int _dashboardMeshRevision;
        private int _dashboardLastSentRevision = -1;
        private float _nextDashboardMeshUpdate;

        private static readonly int ColorModeId = Shader.PropertyToID("_ColorMode");
        private static readonly int OpacityId = Shader.PropertyToID("_Opacity");
        private const string OpacityPrefsKey = "wifi_room_mesh_opacity";
        private static readonly Color32 UnmeasuredColor = new(110, 110, 110, 255);

        public void Configure(DynamicOcclusionController depthCoordinator,
            SampleCollector collector, FloorHeatmapRenderer floorHeatmap)
        {
            _depthCoordinator = depthCoordinator;
            _collector = collector;
            _floorHeatmap = floorHeatmap;
        }

        private void Awake()
        {
            MeshOpacity = Mathf.Clamp01(PlayerPrefs.GetFloat(
                OpacityPrefsKey, meshOpacityAtStart));
        }

        private void Start()
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                Debug.LogError("[RoomMesh] Main Camera fehlt; Raumscan nicht verfügbar.");
                return;
            }

            _head = camera.transform;
            _depthCoordinator ??= FindFirstObjectByType<DynamicOcclusionController>();
            AROcclusionManager manager = camera.GetComponent<AROcclusionManager>();
            if (manager == null) manager = camera.gameObject.AddComponent<AROcclusionManager>();
            _depth = new RoomMeshDepthSource(manager, camera.transform.parent);

            Shader shader = Resources.Load<Shader>("DynamicRoomMesh/NormalColors");
            if (shader == null)
            {
                Debug.LogError("[RoomMesh] NormalColors.shader fehlt in Resources.");
                return;
            }
            _material = new Material(shader) { name = "Dynamic Room Mesh Normal Colors" };
            _material.renderQueue = (int)RenderQueue.Transparent - 100;
            SetColorMode(colorModeAtStart);
            SetMeshOpacity(MeshOpacity, false);
            MeshVisible = meshVisibleAtStart;
            _ready = true;
            SetScanEnabled(scanOnAtStart);
            Debug.Log("[RoomMesh] Bereit; Raumscan ist standardmäßig " +
                      (scanOnAtStart ? "aktiv." : "aus."));
        }

        private void Update()
        {
            if (_saveOpacityPrefsAt >= 0f &&
                Time.unscaledTime >= _saveOpacityPrefsAt)
            {
                PlayerPrefs.Save();
                _saveOpacityPrefsAt = -1f;
            }
            UpdateSignalColors();
            UpdateDashboardMesh();
            if (!ScanEnabled || _volume == null || _depth == null ||
                !_depth.DepthAvailable || Time.unscaledTime < _nextScanTime) return;
            _nextScanTime = Time.unscaledTime + 1f / Mathf.Max(1f, updateFrequency);
            _volume.Scan(_depth, _head);
            PollVisibleChunks(_generation);
        }

        public void ToggleScan() => SetScanEnabled(!ScanEnabled);

        public void SetScanEnabled(bool enabled)
        {
            ScanEnabled = enabled;
            if (!_ready) return;

            if (enabled)
            {
                _volume ??= new RoomMeshVolume();
                _generation++;
                _nextScanTime = 0f;
                _depthCoordinator?.SetRoomScanDepthRequired(true);
                _depth.SetActive(true);
                StartWorkers(_generation);
            }
            else
            {
                _generation++;
                _depth.SetActive(false);
                _depthCoordinator?.SetRoomScanDepthRequired(false);
                StopWorkers();
            }
            Debug.Log($"[RoomMesh] Raumscan -> {(enabled ? "AN" : "AUS")}");
        }

        public void ToggleMeshVisible() => SetMeshVisible(!MeshVisible);

        public void ToggleColorMode() => SetColorMode(ColorMode ==
            RoomMeshColorMode.SurfaceNormals
                ? RoomMeshColorMode.WifiSignal
                : RoomMeshColorMode.SurfaceNormals);

        public void SetColorMode(RoomMeshColorMode mode)
        {
            ColorMode = mode;
            if (_material != null)
                _material.SetFloat(ColorModeId,
                    mode == RoomMeshColorMode.WifiSignal ? 1f : 0f);
            if (mode == RoomMeshColorMode.WifiSignal)
                ScheduleFullColorRefresh(true);
            _dashboardMeshRevision++;
            Debug.Log($"[RoomMesh] Farbmodus -> {(mode == RoomMeshColorMode.WifiSignal ? "WLAN-Signal" : "Normalen")}");
        }

        public void SetMeshOpacity(float opacity, bool persist = true)
        {
            MeshOpacity = Mathf.Clamp01(opacity);
            if (_material != null) _material.SetFloat(OpacityId, MeshOpacity);
            if (!persist) return;
            PlayerPrefs.SetFloat(OpacityPrefsKey, MeshOpacity);
            _saveOpacityPrefsAt = Time.unscaledTime + 0.75f;
        }

        /// <summary>Uses the same AP selection as the floor heatmap.</summary>
        public void SetBssidFilter(string bssid)
        {
            string value = bssid ?? "";
            if (_bssidFilter == value) return;
            _bssidFilter = value;
            ScheduleFullColorRefresh(true);
        }

        public void SetMeshVisible(bool visible)
        {
            MeshVisible = visible;
            foreach (MeshChunk chunk in _chunks.Values)
                if (chunk.Renderer != null) chunk.Renderer.enabled = visible;
            Debug.Log($"[RoomMesh] Anzeige -> {(visible ? "SICHTBAR" : "AUSGEBLENDET")}");
        }

        /// <summary>Clears only the dynamic room scan. Existing WiFi and tool
        /// data are owned elsewhere and are not touched.</summary>
        public void ClearMesh()
        {
            bool restart = ScanEnabled;
            _generation++;
            StopWorkers();
            _volume?.Clear();
            foreach (MeshChunk chunk in _chunks.Values) chunk.Dispose();
            _chunks.Clear();
            _meshQueue.Clear();
            _colorQueue.Clear();
            _signalIndex = null;
            _colorDataRevision = -1;
            _colorFieldRevision = -1;
            TriangleCount = 0;
            _dashboardMeshRevision++;
            _dashboardLastSentRevision = -1;
            webServer?.Broadcast("{\"type\":\"roommesh\",\"v\":[],\"c\":[]}");
            if (restart) StartWorkers(_generation);
            Debug.Log("[RoomMesh] Dynamisches Raummesh geleert.");
        }

        private async void PollVisibleChunks(int generation)
        {
            if (_pollBusy) return;
            _pollBusy = true;
            try
            {
                RoomMeshVolume.VisibleChunksResult result =
                    await _volume.ReadbackVisibleChunks();
                if (!result.Valid || !ScanEnabled || generation != _generation) return;

                for (int i = 0; i < result.Count; i++)
                {
                    int index = result.Chunks[i];
                    MeshChunk chunk = GetOrCreateChunk(index);
                    uint changes = result.ChangeSums[i];
                    if (chunk.Dirty ||
                        changes - chunk.LastMeshingChangeSum <
                        (uint)(remeshSweptVoxels * 254)) continue;
                    chunk.LastMeshingChangeSum = changes;
                    chunk.Dirty = true;
                    _meshQueue.Enqueue(chunk);
                    _meshSemaphore?.Release();
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[RoomMesh] Chunk-Abfrage fehlgeschlagen: {exception.Message}");
            }
            finally
            {
                _pollBusy = false;
            }
        }

        private MeshChunk GetOrCreateChunk(int index)
        {
            if (_chunks.TryGetValue(index, out MeshChunk chunk)) return chunk;
            float3 corner = _volume.ChunkCoordinateToCorner(
                _volume.ChunkIndexToCoordinate(index));
            var go = new GameObject($"Room Mesh Chunk {index}");
            go.transform.SetParent(transform, false);
            go.transform.position = corner;
            var filter = go.AddComponent<MeshFilter>();
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            renderer.enabled = MeshVisible;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            var mesh = new Mesh { name = $"Dynamic Room Chunk {index}", indexFormat = IndexFormat.UInt32 };
            mesh.MarkDynamic();
            filter.sharedMesh = mesh;
            chunk = new MeshChunk(index, go, renderer, mesh);
            _chunks.Add(index, chunk);
            return chunk;
        }

        private void StartWorkers(int generation)
        {
            StopWorkers();
            _workerCancellation = new CancellationTokenSource();
            _meshSemaphore = new SemaphoreSlim(0);
            for (int i = 0; i < Mathf.Max(1, meshWorkers); i++)
                _ = RunWorker(generation, _meshSemaphore, _workerCancellation.Token);
        }

        private void StopWorkers()
        {
            _workerCancellation?.Cancel();
            _workerCancellation = null;
            _meshQueue.Clear();
            foreach (MeshChunk chunk in _chunks.Values) chunk.Dirty = false;
        }

        private async Task RunWorker(int generation, SemaphoreSlim semaphore,
            CancellationToken token)
        {
            var surfaceMesher = new SurfaceNetMesher();
            RoomMeshVolume.ChunkReadbackBuffer readback =
                _volume.CreateChunkReadbackBuffer();
            var scratch = new Mesh { name = "Room Mesh Worker Scratch", indexFormat = IndexFormat.UInt32 };
            scratch.MarkDynamic();
            int dimension = _volume.VoxelsPerChunk;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    await semaphore.WaitAsync(token);
                    if (!_meshQueue.TryDequeue(out MeshChunk chunk)) continue;
                    bool success = await _volume.ReadbackChunkInto(chunk.Index, readback);
                    token.ThrowIfCancellationRequested();
                    if (!success || generation != _generation)
                    {
                        chunk.Dirty = false;
                        continue;
                    }

                    bool populated = await surfaceMesher.CreateMesh(readback.Data,
                        new int3(dimension), RoomMeshVolume.VoxelSize, scratch, token);
                    token.ThrowIfCancellationRequested();
                    if (populated)
                    {
                        await MeshSimplifier.SimplifyAsync(scratch, decimationTarget,
                            decimationOptions, chunk.Mesh, token);
                        token.ThrowIfCancellationRequested();
                        chunk.Mesh.RecalculateBounds();
                        EnsureSignalIndex();
                        ApplySignalColors(chunk);
                    }
                    else chunk.Mesh.Clear();
                    chunk.Dirty = false;
                    RecountTriangles();
                    _dashboardMeshRevision++;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                Debug.LogError($"[RoomMesh] Meshing-Worker beendet: {exception}");
            }
            finally
            {
                surfaceMesher.Dispose();
                readback.Dispose();
                Destroy(scratch);
            }
        }

        private void UpdateSignalColors()
        {
            if (ColorMode != RoomMeshColorMode.WifiSignal) return;

            int dataRevision = _collector != null ? _collector.DataRevision : -1;
            int fieldRevision = _floorHeatmap != null
                ? _floorHeatmap.FieldRevision : -1;
            if ((dataRevision != _colorDataRevision ||
                 fieldRevision != _colorFieldRevision) &&
                Time.unscaledTime >= _nextColorUpdate)
                ScheduleFullColorRefresh(true);

            int budget = Mathf.Max(1, colorChunksPerFrame);
            while (budget-- > 0 && _colorQueue.Count > 0)
            {
                int index = _colorQueue.Dequeue();
                if (_chunks.TryGetValue(index, out MeshChunk chunk) && !chunk.Dirty)
                {
                    ApplySignalColors(chunk);
                    _dashboardMeshRevision++;
                }
            }
        }

        private void ScheduleFullColorRefresh(bool rebuildIndex)
        {
            if (rebuildIndex)
            {
                _signalIndex = BuildSignalIndex();
                _colorDataRevision = _collector != null
                    ? _collector.DataRevision : -1;
                _colorFieldRevision = _floorHeatmap != null
                    ? _floorHeatmap.FieldRevision : -1;
            }
            _nextColorUpdate = Time.unscaledTime +
                               Mathf.Max(0.25f, signalColorUpdateInterval);
            _colorQueue.Clear();
            var indices = new List<int>(_chunks.Keys);
            indices.Sort();
            foreach (int index in indices) _colorQueue.Enqueue(index);
        }

        private void EnsureSignalIndex()
        {
            int revision = _collector != null ? _collector.DataRevision : -1;
            if (_signalIndex == null || revision != _colorDataRevision)
            {
                _signalIndex = BuildSignalIndex();
                _colorDataRevision = revision;
            }
        }

        private SignalKdTree BuildSignalIndex()
        {
            if (_collector == null || _collector.SampleCount == 0) return null;
            var points = new List<SignalPoint>(_collector.SampleCount);
            bool filter = !string.IsNullOrEmpty(_bssidFilter);
            foreach (Sample sample in _collector.Samples)
            {
                if (filter && sample.bssid != _bssidFilter) continue;
                points.Add(new SignalPoint(
                    new Vector3(sample.posX, sample.posY, sample.posZ),
                    sample.rssiDbm));
            }
            return points.Count > 0 ? new SignalKdTree(points) : null;
        }

        private void ApplySignalColors(MeshChunk chunk)
        {
            Mesh mesh = chunk.Mesh;
            if (mesh == null || mesh.vertexCount == 0) return;

            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            if (normals.Length != vertices.Length)
            {
                mesh.RecalculateNormals();
                normals = mesh.normals;
            }
            var colors = new Color32[vertices.Length];
            float floorY = 0f;
            bool hasFloor = _floorHeatmap != null &&
                            _floorHeatmap.TryGetActiveFloorY(out floorY);

            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 world = chunk.GameObject.transform.TransformPoint(vertices[i]);
                Vector3 normal = i < normals.Length
                    ? chunk.GameObject.transform.TransformDirection(normals[i]).normalized
                    : Vector3.zero;
                bool floorSurface = hasFloor &&
                    Mathf.Abs(normal.y) >= floorNormalThreshold &&
                    Mathf.Abs(world.y - floorY) <= floorHeightTolerance;

                float rssi = 0f;
                bool measured;
                if (floorSurface)
                    measured = _floorHeatmap.TrySampleRssi(world, out rssi);
                else if (_signalIndex != null)
                    measured = _signalIndex.TryFindNearest(world, out rssi);
                else
                    measured = false;
                colors[i] = UnmeasuredColor;
                if (measured)
                    colors[i] = (Color32)HeatmapColorMapper.Map(
                        Mathf.RoundToInt(rssi));
            }
            mesh.colors32 = colors;
        }

        private void RecountTriangles()
        {
            int total = 0;
            foreach (MeshChunk chunk in _chunks.Values)
                if (chunk.Mesh != null && chunk.Mesh.subMeshCount > 0)
                {
                    total += (int)chunk.Mesh.GetIndexCount(0) / 3;
                }
            TriangleCount = total;
        }

        // The dashboard receives a deliberately bounded triangle soup rather
        // than the complete export mesh. This keeps JSON generation, WebSocket
        // traffic and browser painting stable while the scan keeps growing.
        private void UpdateDashboardMesh()
        {
            if (webServer == null || !webServer.IsRunning) return;
            if (!webServer.HasWebSocketClients)
            {
                // A future first client needs a complete preview even when the
                // geometry itself has not changed since the previous client.
                _dashboardLastSentRevision = -1;
                return;
            }
            if (_dashboardLastSentRevision == _dashboardMeshRevision ||
                Time.unscaledTime < _nextDashboardMeshUpdate) return;

            _nextDashboardMeshUpdate = Time.unscaledTime +
                Mathf.Max(0.5f, dashboardMeshUpdateInterval);
            webServer.Broadcast(BuildDashboardMeshPacket());
            _dashboardLastSentRevision = _dashboardMeshRevision;
        }

        private string BuildDashboardMeshPacket()
        {
            int limit = Mathf.Max(250, dashboardMeshMaxTriangles);
            int stride = Mathf.Max(1, Mathf.CeilToInt(
                Mathf.Max(1, TriangleCount) / (float)limit));
            var verticesJson = new StringBuilder(limit * 72);
            var colorsJson = new StringBuilder(limit * 36);
            int visited = 0;
            int emitted = 0;
            bool firstVertex = true;
            bool firstColor = true;
            var indices = new List<int>(_chunks.Keys);
            indices.Sort();

            foreach (int index in indices)
            {
                MeshChunk chunk = _chunks[index];
                Mesh mesh = chunk.Mesh;
                if (mesh == null || mesh.vertexCount == 0 ||
                    mesh.subMeshCount == 0) continue;
                Vector3[] localVertices = mesh.vertices;
                int[] triangles = mesh.triangles;
                Color32[] colors = mesh.colors32;
                for (int i = 0; i + 2 < triangles.Length && emitted < limit; i += 3)
                {
                    int triangleNumber = visited++;
                    if (triangleNumber % stride != 0) continue;
                    int ia = triangles[i];
                    int ib = triangles[i + 1];
                    int ic = triangles[i + 2];
                    if ((uint)ia >= (uint)localVertices.Length ||
                        (uint)ib >= (uint)localVertices.Length ||
                        (uint)ic >= (uint)localVertices.Length) continue;

                    Vector3 a = chunk.GameObject.transform.TransformPoint(localVertices[ia]);
                    Vector3 b = chunk.GameObject.transform.TransformPoint(localVertices[ib]);
                    Vector3 c = chunk.GameObject.transform.TransformPoint(localVertices[ic]);
                    AppendDashboardVector(verticesJson, a, ref firstVertex);
                    AppendDashboardVector(verticesJson, b, ref firstVertex);
                    AppendDashboardVector(verticesJson, c, ref firstVertex);

                    if (ColorMode == RoomMeshColorMode.WifiSignal)
                    {
                        AppendDashboardColor(colorsJson,
                            ia < colors.Length ? colors[ia] : UnmeasuredColor,
                            ref firstColor);
                        AppendDashboardColor(colorsJson,
                            ib < colors.Length ? colors[ib] : UnmeasuredColor,
                            ref firstColor);
                        AppendDashboardColor(colorsJson,
                            ic < colors.Length ? colors[ic] : UnmeasuredColor,
                            ref firstColor);
                    }
                    else
                    {
                        Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
                        var normalColor = new Color32(
                            (byte)Mathf.Clamp(Mathf.RoundToInt((normal.x * 0.5f + 0.5f) * 255f), 0, 255),
                            (byte)Mathf.Clamp(Mathf.RoundToInt((normal.y * 0.5f + 0.5f) * 255f), 0, 255),
                            (byte)Mathf.Clamp(Mathf.RoundToInt((normal.z * 0.5f + 0.5f) * 255f), 0, 255),
                            255);
                        AppendDashboardColor(colorsJson, normalColor, ref firstColor);
                        AppendDashboardColor(colorsJson, normalColor, ref firstColor);
                        AppendDashboardColor(colorsJson, normalColor, ref firstColor);
                    }
                    emitted++;
                }
                if (emitted >= limit) break;
            }

            return "{\"type\":\"roommesh\",\"triangles\":" + emitted +
                ",\"v\":[" + verticesJson + "],\"c\":[" + colorsJson + "]}";
        }

        private static void AppendDashboardVector(StringBuilder builder,
            Vector3 value, ref bool first)
        {
            AppendDashboardFloat(builder, value.x, ref first);
            AppendDashboardFloat(builder, value.y, ref first);
            AppendDashboardFloat(builder, value.z, ref first);
        }

        private static void AppendDashboardFloat(StringBuilder builder,
            float value, ref bool first)
        {
            if (!first) builder.Append(',');
            first = false;
            builder.Append(value.ToString("0.###", CultureInfo.InvariantCulture));
        }

        private static void AppendDashboardColor(StringBuilder builder,
            Color32 value, ref bool first)
        {
            if (!first) builder.Append(',');
            first = false;
            builder.Append(value.r).Append(',').Append(value.g).Append(',')
                .Append(value.b);
        }

        /// <summary>Writes a geometry-only sidecar named &lt;stem&gt;_roommesh.obj.
        /// Coordinates use the same right-handed conversion as the existing
        /// traced-room OBJ export.</summary>
        public string ExportObj(string stem)
        {
            if (_chunks.Count == 0 || TriangleCount == 0) return null;
            string path = Path.Combine(Application.persistentDataPath,
                $"{stem}_roommesh.obj");
            var builder = new StringBuilder(Mathf.Max(4096, TriangleCount * 48));
            builder.AppendLine("# Quest3 WiFi-Scanner dynamic environment-depth room mesh");
            builder.AppendLine("# Unity world X is negated for right-handed OBJ coordinates.");
            int vertexOffset = 1;
            var indices = new List<int>(_chunks.Keys);
            indices.Sort();
            CultureInfo invariant = CultureInfo.InvariantCulture;

            foreach (int index in indices)
            {
                MeshChunk chunk = _chunks[index];
                Mesh mesh = chunk.Mesh;
                if (mesh == null || mesh.vertexCount == 0 || mesh.subMeshCount == 0 ||
                    mesh.GetIndexCount(0) < 3) continue;
                Vector3[] vertices = mesh.vertices;
                Vector3[] normals = mesh.normals;
                int[] triangles = mesh.triangles;
                builder.Append("o chunk_").Append(index).AppendLine();
                foreach (Vector3 local in vertices)
                {
                    Vector3 world = chunk.GameObject.transform.TransformPoint(local);
                    builder.Append("v ").Append((-world.x).ToString("R", invariant)).Append(' ')
                        .Append(world.y.ToString("R", invariant)).Append(' ')
                        .Append(world.z.ToString("R", invariant)).AppendLine();
                }
                for (int i = 0; i < normals.Length; i++)
                {
                    Vector3 world = chunk.GameObject.transform.TransformDirection(normals[i]).normalized;
                    builder.Append("vn ").Append((-world.x).ToString("R", invariant)).Append(' ')
                        .Append(world.y.ToString("R", invariant)).Append(' ')
                        .Append(world.z.ToString("R", invariant)).AppendLine();
                }
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                {
                    int a = vertexOffset + triangles[i];
                    int b = vertexOffset + triangles[i + 1];
                    int c = vertexOffset + triangles[i + 2];
                    builder.Append("f ").Append(a).Append("//").Append(a).Append(' ')
                        .Append(c).Append("//").Append(c).Append(' ')
                        .Append(b).Append("//").Append(b).AppendLine();
                }
                vertexOffset += vertices.Length;
            }

            File.WriteAllText(path, builder.ToString());
            Debug.Log($"[RoomMesh] OBJ exportiert: {path}");
            return path;
        }

        /// <summary>Writes the complete room mesh as an ASCII PLY with per-vertex
        /// RSSI colours. Signal colours are exported regardless of the current
        /// in-headset colour mode.</summary>
        public string ExportPly(string stem)
        {
            if (_chunks.Count == 0 || TriangleCount == 0) return null;

            EnsureSignalIndex();
            var indices = new List<int>(_chunks.Keys);
            indices.Sort();
            int vertexCount = 0;
            int faceCount = 0;
            foreach (int index in indices)
            {
                MeshChunk chunk = _chunks[index];
                Mesh mesh = chunk.Mesh;
                if (mesh == null || mesh.vertexCount == 0 || mesh.subMeshCount == 0)
                    continue;
                ApplySignalColors(chunk);
                vertexCount += mesh.vertexCount;
                faceCount += (int)mesh.GetIndexCount(0) / 3;
            }
            if (vertexCount == 0 || faceCount == 0) return null;

            string path = Path.Combine(Application.persistentDataPath,
                $"{stem}_roommesh.ply");
            var builder = new StringBuilder(Mathf.Max(8192, vertexCount * 72));
            builder.AppendLine("ply");
            builder.AppendLine("format ascii 1.0");
            builder.AppendLine("comment Quest3 WiFi-Scanner RSSI-coloured dynamic room mesh");
            builder.AppendLine("comment World X is negated for right-handed coordinates");
            builder.Append("element vertex ").Append(vertexCount).AppendLine();
            builder.AppendLine("property float x");
            builder.AppendLine("property float y");
            builder.AppendLine("property float z");
            builder.AppendLine("property float nx");
            builder.AppendLine("property float ny");
            builder.AppendLine("property float nz");
            builder.AppendLine("property uchar red");
            builder.AppendLine("property uchar green");
            builder.AppendLine("property uchar blue");
            builder.AppendLine("property uchar alpha");
            builder.Append("element face ").Append(faceCount).AppendLine();
            builder.AppendLine("property list uchar int vertex_indices");
            builder.AppendLine("end_header");

            CultureInfo invariant = CultureInfo.InvariantCulture;
            foreach (int index in indices)
            {
                MeshChunk chunk = _chunks[index];
                Mesh mesh = chunk.Mesh;
                if (mesh == null || mesh.vertexCount == 0 || mesh.subMeshCount == 0)
                    continue;
                Vector3[] vertices = mesh.vertices;
                Vector3[] normals = mesh.normals;
                Color32[] colors = mesh.colors32;
                for (int i = 0; i < vertices.Length; i++)
                {
                    Vector3 world = chunk.GameObject.transform.TransformPoint(vertices[i]);
                    Vector3 normal = i < normals.Length
                        ? chunk.GameObject.transform.TransformDirection(normals[i]).normalized
                        : Vector3.up;
                    Color32 color = i < colors.Length ? colors[i] : UnmeasuredColor;
                    builder.Append((-world.x).ToString("R", invariant)).Append(' ')
                        .Append(world.y.ToString("R", invariant)).Append(' ')
                        .Append(world.z.ToString("R", invariant)).Append(' ')
                        .Append((-normal.x).ToString("R", invariant)).Append(' ')
                        .Append(normal.y.ToString("R", invariant)).Append(' ')
                        .Append(normal.z.ToString("R", invariant)).Append(' ')
                        .Append(color.r).Append(' ').Append(color.g).Append(' ')
                        .Append(color.b).Append(' ').Append(color.a).AppendLine();
                }
            }

            int vertexOffset = 0;
            foreach (int index in indices)
            {
                Mesh mesh = _chunks[index].Mesh;
                if (mesh == null || mesh.vertexCount == 0 || mesh.subMeshCount == 0)
                    continue;
                int[] triangles = mesh.triangles;
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                {
                    int a = vertexOffset + triangles[i];
                    int b = vertexOffset + triangles[i + 1];
                    int c = vertexOffset + triangles[i + 2];
                    builder.Append("3 ").Append(a).Append(' ')
                        .Append(c).Append(' ').Append(b).AppendLine();
                }
                vertexOffset += mesh.vertexCount;
            }

            File.WriteAllText(path, builder.ToString());
            Debug.Log($"[RoomMesh] Farbiges PLY exportiert: {path}");
            return path;
        }

        private void OnDestroy()
        {
            if (_saveOpacityPrefsAt >= 0f) PlayerPrefs.Save();
            ScanEnabled = false;
            _generation++;
            StopWorkers();
            _depthCoordinator?.SetRoomScanDepthRequired(false);
            _depth?.Dispose();
            _volume?.Dispose();
            foreach (MeshChunk chunk in _chunks.Values) chunk.Dispose();
            _chunks.Clear();
            if (_material != null) Destroy(_material);
        }

        private readonly struct SignalPoint
        {
            public readonly Vector3 Position;
            public readonly float Rssi;

            public SignalPoint(Vector3 position, float rssi)
            {
                Position = position;
                Rssi = rssi;
            }
        }

        /// <summary>Balanced 3D nearest-neighbour index rebuilt from the current
        /// AP-filtered sample snapshot. Queries are exact and allow vertical
        /// signal gradients on walls.</summary>
        private sealed class SignalKdTree
        {
            private struct Node
            {
                public SignalPoint Point;
                public int Left;
                public int Right;
                public byte Axis;
            }

            private sealed class AxisComparer : IComparer<SignalPoint>
            {
                private readonly int _axis;
                public AxisComparer(int axis) => _axis = axis;
                public int Compare(SignalPoint a, SignalPoint b) =>
                    a.Position[_axis].CompareTo(b.Position[_axis]);
            }

            private static readonly IComparer<SignalPoint>[] Comparers =
            {
                new AxisComparer(0), new AxisComparer(1), new AxisComparer(2)
            };

            private readonly SignalPoint[] _points;
            private readonly Node[] _nodes;
            private readonly int _root;
            private int _nodeCount;

            public SignalKdTree(List<SignalPoint> points)
            {
                _points = points.ToArray();
                _nodes = new Node[_points.Length];
                _root = Build(0, _points.Length, 0);
            }

            public bool TryFindNearest(Vector3 position, out float rssi)
            {
                rssi = 0f;
                if (_root < 0) return false;
                float bestDistance = float.PositiveInfinity;
                SignalPoint best = default;
                Search(_root, position, ref bestDistance, ref best);
                rssi = best.Rssi;
                return true;
            }

            private int Build(int start, int length, int depth)
            {
                if (length <= 0) return -1;
                int axis = depth % 3;
                Array.Sort(_points, start, length, Comparers[axis]);
                int middle = start + length / 2;
                int nodeIndex = _nodeCount++;
                _nodes[nodeIndex] = new Node
                {
                    Point = _points[middle],
                    Axis = (byte)axis,
                    Left = Build(start, middle - start, depth + 1),
                    Right = Build(middle + 1, start + length - middle - 1,
                        depth + 1)
                };
                return nodeIndex;
            }

            private void Search(int nodeIndex, Vector3 position,
                ref float bestDistance, ref SignalPoint best)
            {
                if (nodeIndex < 0) return;
                Node node = _nodes[nodeIndex];
                float distance = (node.Point.Position - position).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = node.Point;
                }

                float delta = position[node.Axis] - node.Point.Position[node.Axis];
                int near = delta <= 0f ? node.Left : node.Right;
                int far = delta <= 0f ? node.Right : node.Left;
                Search(near, position, ref bestDistance, ref best);
                if (delta * delta < bestDistance)
                    Search(far, position, ref bestDistance, ref best);
            }
        }

        private sealed class MeshChunk
        {
            public readonly int Index;
            public readonly GameObject GameObject;
            public readonly MeshRenderer Renderer;
            public readonly Mesh Mesh;
            public bool Dirty;
            public uint LastMeshingChangeSum;

            public MeshChunk(int index, GameObject gameObject,
                MeshRenderer renderer, Mesh mesh)
            {
                Index = index;
                GameObject = gameObject;
                Renderer = renderer;
                Mesh = mesh;
            }

            public void Dispose()
            {
                if (Mesh != null) UnityEngine.Object.Destroy(Mesh);
                if (GameObject != null) UnityEngine.Object.Destroy(GameObject);
            }
        }
    }
}
