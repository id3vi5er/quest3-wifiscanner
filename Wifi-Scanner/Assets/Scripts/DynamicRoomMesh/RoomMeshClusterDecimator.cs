using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace WifiScanner
{
    /// <summary>
    /// Lightweight Unity-6000.0-compatible final reduction stage. Vertices in
    /// the same spatial cell are averaged and degenerate remapped triangles are
    /// removed. The Surface Nets topology and chunk boundaries stay intact.
    /// </summary>
    internal sealed class RoomMeshClusterDecimator : IDisposable
    {
        private readonly Dictionary<Vector3Int, int> _clusterMap = new();
        private readonly List<Cluster> _clusters = new();
        private readonly List<Vector3> _positions = new();
        private readonly List<Vector3> _normals = new();
        private readonly List<int> _triangles = new();
        private int[] _remap = Array.Empty<int>();

        public void Simplify(Mesh source, Mesh destination, float cellSize)
        {
            Vector3[] sourcePositions = source.vertices;
            Vector3[] sourceNormals = source.normals;
            int[] sourceTriangles = source.triangles;
            if (_remap.Length < sourcePositions.Length)
                _remap = new int[sourcePositions.Length];
            _clusterMap.Clear();
            _clusters.Clear();
            _positions.Clear();
            _normals.Clear();
            _triangles.Clear();

            float inverseCell = 1f / Mathf.Max(0.001f, cellSize);
            for (int i = 0; i < sourcePositions.Length; i++)
            {
                Vector3 position = sourcePositions[i];
                var key = new Vector3Int(
                    Mathf.FloorToInt(position.x * inverseCell),
                    Mathf.FloorToInt(position.y * inverseCell),
                    Mathf.FloorToInt(position.z * inverseCell));
                if (!_clusterMap.TryGetValue(key, out int clusterIndex))
                {
                    clusterIndex = _clusters.Count;
                    _clusterMap.Add(key, clusterIndex);
                    _clusters.Add(default);
                }
                Cluster cluster = _clusters[clusterIndex];
                cluster.PositionSum += position;
                if (i < sourceNormals.Length) cluster.NormalSum += sourceNormals[i];
                cluster.Count++;
                _clusters[clusterIndex] = cluster;
                _remap[i] = clusterIndex;
            }

            foreach (Cluster cluster in _clusters)
            {
                _positions.Add(cluster.PositionSum / cluster.Count);
                _normals.Add(cluster.NormalSum.sqrMagnitude > 0.000001f
                    ? cluster.NormalSum.normalized
                    : Vector3.up);
            }

            for (int i = 0; i + 2 < sourceTriangles.Length; i += 3)
            {
                int a = _remap[sourceTriangles[i]];
                int b = _remap[sourceTriangles[i + 1]];
                int c = _remap[sourceTriangles[i + 2]];
                if (a == b || b == c || c == a) continue;
                _triangles.Add(a);
                _triangles.Add(b);
                _triangles.Add(c);
            }

            destination.Clear();
            destination.indexFormat = _positions.Count > ushort.MaxValue
                ? IndexFormat.UInt32
                : IndexFormat.UInt16;
            destination.SetVertices(_positions);
            destination.SetNormals(_normals);
            destination.SetTriangles(_triangles, 0, false);
        }

        public void Dispose()
        {
            _clusterMap.Clear();
            _clusters.Clear();
            _positions.Clear();
            _normals.Clear();
            _triangles.Clear();
            _remap = Array.Empty<int>();
        }

        private struct Cluster
        {
            public Vector3 PositionSum;
            public Vector3 NormalSum;
            public int Count;
        }
    }
}
