using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;

namespace WifiScanner
{
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
        [Range(0.1f, 0.3f)]
        [Tooltip("Vertex-cluster size used after Surface Nets. 0.15 m keeps the " +
                 "runtime mesh compact on Unity 6000.0.")]
        public float decimationCellSize = 0.15f;

        public bool ScanEnabled { get; private set; }
        public bool MeshVisible { get; private set; }
        public int ChunkCount => _chunks.Count;
        public int TriangleCount { get; private set; }

        private readonly Dictionary<int, MeshChunk> _chunks = new();
        private readonly ConcurrentQueue<MeshChunk> _meshQueue = new();
        private CancellationTokenSource _workerCancellation;
        private SemaphoreSlim _meshSemaphore;
        private DynamicOcclusionController _depthCoordinator;
        private RoomMeshDepthSource _depth;
        private RoomMeshVolume _volume;
        private Material _material;
        private Transform _head;
        private float _nextScanTime;
        private bool _ready;
        private bool _pollBusy;
        private int _generation;

        public void Configure(DynamicOcclusionController depthCoordinator)
        {
            _depthCoordinator = depthCoordinator;
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
            MeshVisible = meshVisibleAtStart;
            _ready = true;
            SetScanEnabled(scanOnAtStart);
            Debug.Log("[RoomMesh] Bereit; Raumscan ist standardmäßig " +
                      (scanOnAtStart ? "aktiv." : "aus."));
        }

        private void Update()
        {
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
            TriangleCount = 0;
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
            var decimator = new RoomMeshClusterDecimator();
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
                        decimator.Simplify(scratch, chunk.Mesh, decimationCellSize);
                        chunk.Mesh.RecalculateBounds();
                    }
                    else chunk.Mesh.Clear();
                    chunk.Dirty = false;
                    RecountTriangles();
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
                decimator.Dispose();
                readback.Dispose();
                Destroy(scratch);
            }
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

        private void OnDestroy()
        {
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
