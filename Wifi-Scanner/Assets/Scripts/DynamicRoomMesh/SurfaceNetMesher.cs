using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Mathematics.Geometry;
using UnityEngine;
using UnityEngine.Rendering;

namespace WifiScanner
{
    /// <summary>Burst surface-net mesher used by Lasertag, adapted to the local
    /// room-volume type. One vertex is placed at the averaged zero crossings
    /// of each occupied voxel cell.</summary>
    internal sealed class SurfaceNetMesher : IDisposable
    {
        private NativeList<Vertex> _vertices;
        private NativeList<int3> _vertexCoordinates;
        private NativeArray<int> _coordinateVertexMap;
        private NativeList<uint> _triangles;
        private NativeReference<MinMaxAABB> _bounds;
        private bool _busy;

        private static readonly byte[] EdgeA =
            { 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3 };
        private static readonly byte[] EdgeB =
            { 1, 2, 3, 0, 5, 6, 7, 4, 4, 5, 6, 7 };
        private static readonly int3[] Corners =
        {
            new int3(0, 0, 0), new int3(1, 0, 0),
            new int3(1, 0, 1), new int3(0, 0, 1),
            new int3(0, 1, 0), new int3(1, 1, 0),
            new int3(1, 1, 1), new int3(0, 1, 1)
        };
        private const int InvalidVertex = -1;

        [StructLayout(LayoutKind.Sequential)]
        private struct Vertex
        {
            public float3 Position;
            public float3 Normal;

            public Vertex(float3 position, float3 normal)
            {
                Position = position;
                Normal = normal;
            }

            public static readonly VertexAttributeDescriptor[] Layout =
            {
                new VertexAttributeDescriptor(VertexAttribute.Position),
                new VertexAttributeDescriptor(VertexAttribute.Normal)
            };
        }

        [BurstCompile]
        private struct VertexJob : IJob
        {
            [ReadOnly] public NativeArray<RoomMeshVolume.Voxel> Volume;
            [ReadOnly] public int3 VoxelCount;
            [ReadOnly] public float VoxelSize;
            [WriteOnly] public NativeArray<int> CoordinateVertexMap;
            public NativeList<Vertex> Vertices;
            public NativeList<int3> VertexCoordinates;
            public NativeReference<MinMaxAABB> Bounds;

            public void Execute()
            {
                float positiveInfinity = float.PositiveInfinity;
                float negativeInfinity = float.NegativeInfinity;
                var bounds = new MinMaxAABB(
                    new float3(positiveInfinity), new float3(negativeInfinity));
                int voxelCount = VoxelCount.x * VoxelCount.y * VoxelCount.z;

                for (int index = 0; index < voxelCount; index++)
                {
                    int3 coordinate = IndexToCoordinate(index, VoxelCount);
                    if (coordinate.x == VoxelCount.x - 1 ||
                        coordinate.y == VoxelCount.y - 1 ||
                        coordinate.z == VoxelCount.z - 1)
                    {
                        CoordinateVertexMap[index] = InvalidVertex;
                        continue;
                    }

                    float3 crossingSum = default;
                    float3 normal = default;
                    byte crossings = 0;
                    byte invalidCrossings = 0;
                    for (int edge = 0; edge < 12; edge++)
                    {
                        int3 aCoordinate = coordinate + Corners[EdgeA[edge]];
                        int3 bCoordinate = coordinate + Corners[EdgeB[edge]];
                        float a = VoxelAt(aCoordinate, VoxelCount, Volume).NormalizedDistance;
                        float b = VoxelAt(bCoordinate, VoxelCount, Volume).NormalizedDistance;
                        float change = a - b;
                        normal += new float3(aCoordinate - bCoordinate) * change;
                        if ((a < 0f) == (b < 0f)) continue;
                        if (a == 1f || b == 1f) invalidCrossings++;
                        float t = a / change;
                        crossingSum += aCoordinate + t * new float3(bCoordinate - aCoordinate);
                        crossings++;
                    }

                    if (crossings < 3 || crossings == invalidCrossings)
                    {
                        CoordinateVertexMap[index] = InvalidVertex;
                        continue;
                    }

                    float3 position = crossingSum / crossings * VoxelSize + VoxelSize * 0.5f;
                    var vertex = new Vertex(position, math.normalize(normal));
                    bounds.Encapsulate(position);
                    CoordinateVertexMap[index] = Vertices.Length;
                    Vertices.Add(vertex);
                    VertexCoordinates.Add(coordinate);
                }

                Bounds.Value = bounds;
            }
        }

        [BurstCompile]
        private struct IndexJob : IJob
        {
            [ReadOnly] public NativeArray<RoomMeshVolume.Voxel> Volume;
            [ReadOnly] public int3 VoxelCount;
            [ReadOnly] public NativeList<int3> VertexCoordinates;
            [ReadOnly] public NativeArray<int> CoordinateVertexMap;
            public NativeList<uint> Triangles;

            public void Execute()
            {
                foreach (int3 coordinate in VertexCoordinates)
                {
                    TrianglesForAxis(coordinate, new int3(1, 0, 0),
                        new int3(0, 0, 1), new int3(0, 1, 0));
                    TrianglesForAxis(coordinate, new int3(0, 1, 0),
                        new int3(1, 0, 0), new int3(0, 0, 1));
                    TrianglesForAxis(coordinate, new int3(0, 0, 1),
                        new int3(0, 1, 0), new int3(1, 0, 0));
                }
            }

            private void TrianglesForAxis(int3 coordinate, int3 axis, int3 d1, int3 d2)
            {
                if (math.any(coordinate - d1 < int3.zero) ||
                    math.any(coordinate - d2 < int3.zero)) return;
                float aDistance = VoxelAt(coordinate, VoxelCount, Volume).NormalizedDistance;
                float bDistance = VoxelAt(coordinate + axis, VoxelCount, Volume)
                    .NormalizedDistance;
                if ((aDistance < 0f) == (bDistance < 0f)) return;

                int a = CoordinateVertexMap[Flatten(coordinate, VoxelCount)];
                int b = CoordinateVertexMap[Flatten(coordinate - d1, VoxelCount)];
                int c = CoordinateVertexMap[Flatten(coordinate - d1 - d2, VoxelCount)];
                int d = CoordinateVertexMap[Flatten(coordinate - d2, VoxelCount)];
                if (a == InvalidVertex || b == InvalidVertex ||
                    c == InvalidVertex || d == InvalidVertex) return;

                if (aDistance < 0f)
                {
                    Add(c, b, a);
                    Add(d, c, a);
                }
                else
                {
                    Add(a, c, d);
                    Add(a, b, c);
                }
            }

            private void Add(int a, int b, int c)
            {
                Triangles.AddNoResize((uint)a);
                Triangles.AddNoResize((uint)b);
                Triangles.AddNoResize((uint)c);
            }
        }

        public async Task<bool> CreateMesh(NativeArray<RoomMeshVolume.Voxel> volume,
            int3 voxelCount, float voxelSize, Mesh mesh, CancellationToken token)
        {
            if (_busy) return false;
            _busy = true;
            EnsureStorage(volume.Length);
            _vertices.Clear();
            _vertexCoordinates.Clear();
            _triangles.Clear();

            try
            {
                var vertexJob = new VertexJob
                {
                    Volume = volume,
                    VoxelCount = voxelCount,
                    VoxelSize = voxelSize,
                    CoordinateVertexMap = _coordinateVertexMap,
                    Vertices = _vertices,
                    VertexCoordinates = _vertexCoordinates,
                    Bounds = _bounds
                };
                JobHandle vertexHandle = vertexJob.Schedule();
                while (!vertexHandle.IsCompleted) await Awaitable.NextFrameAsync();
                vertexHandle.Complete();
                token.ThrowIfCancellationRequested();
                if (_vertices.Length < 3) return false;

                var indexJob = new IndexJob
                {
                    Volume = volume,
                    VoxelCount = voxelCount,
                    VertexCoordinates = _vertexCoordinates,
                    CoordinateVertexMap = _coordinateVertexMap,
                    Triangles = _triangles
                };
                JobHandle indexHandle = indexJob.Schedule();
                while (!indexHandle.IsCompleted) await Awaitable.NextFrameAsync();
                indexHandle.Complete();
                token.ThrowIfCancellationRequested();

                MinMaxAABB bounds = _bounds.Value;
                ApplyToMesh(_vertices.AsArray(), _triangles.AsArray(),
                    new Bounds(bounds.Center, bounds.Extents * 2f), mesh);
                return _triangles.Length > 0;
            }
            finally
            {
                _busy = false;
            }
        }

        private void EnsureStorage(int voxelCount)
        {
            if (!_vertices.IsCreated)
            {
                int vertexEstimate = voxelCount / 3;
                _vertices = new NativeList<Vertex>(vertexEstimate, Allocator.Persistent);
                _vertexCoordinates = new NativeList<int3>(vertexEstimate, Allocator.Persistent);
                _triangles = new NativeList<uint>(vertexEstimate * 6, Allocator.Persistent);
                _bounds = new NativeReference<MinMaxAABB>(Allocator.Persistent);
                _coordinateVertexMap = new NativeArray<int>(voxelCount, Allocator.Persistent);
            }
            else if (_coordinateVertexMap.Length < voxelCount)
            {
                _coordinateVertexMap.Dispose();
                _coordinateVertexMap = new NativeArray<int>(voxelCount, Allocator.Persistent);
            }
        }

        private static int Flatten(int3 coordinate, int3 size) =>
            coordinate.x + coordinate.y * size.x + coordinate.z * size.x * size.y;

        private static int3 IndexToCoordinate(int index, int3 size) => new int3(
            index % size.x, index / size.x % size.y, index / (size.x * size.y));

        private static RoomMeshVolume.Voxel VoxelAt(int3 coordinate, int3 size,
            NativeArray<RoomMeshVolume.Voxel> volume) => volume[Flatten(coordinate, size)];

        private static void ApplyToMesh(NativeArray<Vertex> vertices,
            NativeArray<uint> triangles, Bounds bounds, Mesh mesh)
        {
            Mesh.MeshDataArray array = Mesh.AllocateWritableMeshData(1);
            Mesh.MeshData data = array[0];
            data.SetVertexBufferParams(vertices.Length, Vertex.Layout);
            data.SetIndexBufferParams(triangles.Length, IndexFormat.UInt32);
            data.GetVertexData<Vertex>().CopyFrom(vertices);
            data.GetIndexData<uint>().CopyFrom(triangles);
            const MeshUpdateFlags flags = MeshUpdateFlags.DontNotifyMeshUsers |
                                          MeshUpdateFlags.DontRecalculateBounds |
                                          MeshUpdateFlags.DontValidateIndices;
            data.subMeshCount = 1;
            data.SetSubMesh(0, new SubMeshDescriptor(0, triangles.Length), flags);
            Mesh.ApplyAndDisposeWritableMeshData(array, mesh);
            mesh.bounds = bounds;
            mesh.MarkModified();
        }

        public void Dispose()
        {
            if (_vertices.IsCreated) _vertices.Dispose();
            if (_vertexCoordinates.IsCreated) _vertexCoordinates.Dispose();
            if (_coordinateVertexMap.IsCreated) _coordinateVertexMap.Dispose();
            if (_triangles.IsCreated) _triangles.Dispose();
            if (_bounds.IsCreated) _bounds.Dispose();
        }
    }
}
