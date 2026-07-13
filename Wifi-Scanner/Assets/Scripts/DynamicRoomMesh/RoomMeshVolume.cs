using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace WifiScanner
{
    /// <summary>GPU sparse TSDF volume ported from Lasertag's EnvScanner.</summary>
    internal sealed class RoomMeshVolume : IDisposable
    {
        private const int VoxelsPerChunkDimension = 32;
        private const int MaxVisibleChunks = 256;
        private static readonly int3 ChunkTableDimensions = new int3(100, 15, 100);
        private static readonly int3 ChunkDataDimensions = new int3(16, 8, 16);

        public const float VoxelSize = 0.1f;
        public const float ChunkWorldSize = VoxelSize * (VoxelsPerChunkDimension - 2);
        public int ChunkTableLength { get; }
        public int VoxelsPerChunk => VoxelsPerChunkDimension;

        private readonly ComputeShader _compute;
        private readonly ComputeBuffer _reservedChunkCounter;
        private readonly ComputeBuffer _chunkTable;
        private readonly ComputeBuffer _chunkChangeSums;
        private readonly ComputeBuffer _visibleChunks;
        private readonly ComputeBuffer _visibleChangeSumsReadback;
        private readonly ComputeBuffer _integrateDispatchDimensions;
        private readonly RenderTexture _chunkData;
        private readonly RoomMeshComputeKernel _clearKernel;
        private readonly RoomMeshComputeKernel _markKernel;
        private readonly RoomMeshComputeKernel _integrateKernel;
        private readonly RoomMeshComputeKernel _visibleChangesKernel;
        private readonly RoomMeshComputeKernel _dataReadbackKernel;
        private readonly Vector4[] _playerHeads = new Vector4[64];

        private static readonly int ReadbackBufferId = Shader.PropertyToID("readbackBuffer");
        private static readonly int ReadbackChunkIndexId = Shader.PropertyToID("readbackChunkIndex");

        public RoomMeshVolume()
        {
            _compute = Resources.Load<ComputeShader>("DynamicRoomMesh/RoomVolume");
            if (_compute == null)
                throw new InvalidOperationException(
                    "DynamicRoomMesh/RoomVolume.compute is missing from Resources.");

            int maxChunks = ChunkDataDimensions.x * ChunkDataDimensions.y * ChunkDataDimensions.z;
            ChunkTableLength = ChunkTableDimensions.x * ChunkTableDimensions.y * ChunkTableDimensions.z;
            _reservedChunkCounter = new ComputeBuffer(1, sizeof(int));
            _visibleChunks = new ComputeBuffer(MaxVisibleChunks, sizeof(int), ComputeBufferType.Append);
            _visibleChangeSumsReadback = new ComputeBuffer(MaxVisibleChunks, sizeof(uint));
            _chunkTable = new ComputeBuffer(ChunkTableLength, sizeof(int));
            _chunkChangeSums = new ComputeBuffer(ChunkTableLength, sizeof(uint));

            var descriptor = new RenderTextureDescriptor
            {
                width = ChunkDataDimensions.x * VoxelsPerChunkDimension,
                height = ChunkDataDimensions.y * VoxelsPerChunkDimension,
                volumeDepth = ChunkDataDimensions.z * VoxelsPerChunkDimension,
                msaaSamples = 1,
                useMipMap = false,
                graphicsFormat = GraphicsFormat.R8G8_SNorm,
                dimension = TextureDimension.Tex3D,
                enableRandomWrite = true
            };
            _chunkData = new RenderTexture(descriptor) { name = "Dynamic Room TSDF" };
            _chunkData.Create();

            _compute.SetFloat("minDist", 1f);
            _compute.SetFloat("maxDist", 6f);
            _compute.SetFloat("minDot", 0.3f);
            _compute.SetFloat("voxSize", VoxelSize);
            _compute.SetFloat("chunkWorldSizeDim", ChunkWorldSize);
            _compute.SetFloat("distanceTruncationBand", 0.2f);
            _compute.SetInt("voxPerChunkDim", VoxelsPerChunkDimension);
            _compute.SetInts("chunkTableDims", ChunkTableDimensions.x,
                ChunkTableDimensions.y, ChunkTableDimensions.z);
            _compute.SetInt("chunkTableLength", ChunkTableLength);
            _compute.SetInts("chunkDataDims", ChunkDataDimensions.x,
                ChunkDataDimensions.y, ChunkDataDimensions.z);
            _compute.SetInt("maxNumVisibleChunks", MaxVisibleChunks);
            _compute.SetInt("maxNumChunks", maxChunks);
            _compute.SetFloat("playerMaskRadius", 0.8f);
            _compute.SetFloat("playerMaskAbove", 0.5f);
            _compute.SetFloat("playerMaskBelow", 2f);

            _markKernel = new RoomMeshComputeKernel(_compute, "Mark");
            _markKernel.Bind("reservedChunkCounter", _reservedChunkCounter);
            _markKernel.Bind("chunkTable", _chunkTable);
            _markKernel.Bind("chunkData", _chunkData);
            _markKernel.Bind("visibleChunksAppend", _visibleChunks);

            _integrateKernel = new RoomMeshComputeKernel(_compute, "Integrate");
            _integrateKernel.Bind("chunkTable", _chunkTable);
            _integrateKernel.Bind("chunkData", _chunkData);
            _integrateKernel.Bind("chunkChangeSums", _chunkChangeSums);
            _integrateKernel.Bind("visibleChunks", _visibleChunks);
            _integrateDispatchDimensions = new ComputeBuffer(
                3, sizeof(uint), ComputeBufferType.IndirectArguments);
            int groupsPerChunkDimension = VoxelsPerChunkDimension / 8;
            _compute.SetInt("groupsPerChunkDim", groupsPerChunkDimension);
            uint groupsPerChunk = (uint)(groupsPerChunkDimension *
                                         groupsPerChunkDimension *
                                         groupsPerChunkDimension);
            _integrateDispatchDimensions.SetData(new[] { 0u, groupsPerChunk, 1u });

            _clearKernel = new RoomMeshComputeKernel(_compute, "Clear");
            _clearKernel.Bind("chunkData", _chunkData);
            _dataReadbackKernel = new RoomMeshComputeKernel(_compute, "ChunkDataReadback");
            _dataReadbackKernel.Bind("chunkTable", _chunkTable);
            _dataReadbackKernel.Bind("chunkData", _chunkData);
            _visibleChangesKernel = new RoomMeshComputeKernel(
                _compute, "VisibleChunkChangeSumsReadback");
            _visibleChangesKernel.Bind("visibleChunks", _visibleChunks);
            _visibleChangesKernel.Bind("chunkChangeSums", _chunkChangeSums);
            _visibleChangesKernel.Bind("visibleChangeSumsReadback", _visibleChangeSumsReadback);

            Clear();
        }

        public void Clear()
        {
            _reservedChunkCounter.SetData(new int[1]);
            _chunkTable.SetData(new int[ChunkTableLength]);
            _chunkChangeSums.SetData(new uint[ChunkTableLength]);
            _visibleChunks.SetData(new int[MaxVisibleChunks]);
            _clearKernel.DispatchFit(_chunkData);
        }

        public void Scan(RoomMeshDepthSource depth, Transform head)
        {
            if (!depth.DepthAvailable) return;

            _compute.SetMatrixArray(RoomMeshDepthSource.ViewId, depth.View);
            _compute.SetMatrixArray(RoomMeshDepthSource.ProjectionId, depth.Projection);
            _compute.SetMatrixArray(RoomMeshDepthSource.ViewInverseId, depth.ViewInverse);
            _compute.SetMatrixArray(RoomMeshDepthSource.ProjectionInverseId,
                depth.ProjectionInverse);
            _compute.SetVector(RoomMeshDepthSource.DepthTextureSizeId,
                new Vector2(depth.DepthTexture.width, depth.DepthTexture.height));
            _compute.SetInt("numPlayerHeads", head != null ? 1 : 0);
            if (head != null) _playerHeads[0] = head.position;
            _compute.SetVectorArray("playerHeads", _playerHeads);

            _visibleChunks.SetCounterValue(0);
            _markKernel.Bind(RoomMeshDepthSource.DepthTextureId, depth.DepthTexture);
            _markKernel.DispatchFit(depth.DepthTexture, 1);
            _integrateKernel.Bind(RoomMeshDepthSource.DepthTextureId, depth.DepthTexture);
            _integrateKernel.Bind(RoomMeshDepthSource.DepthNormalTextureId,
                depth.NormalTexture);
            ComputeBuffer.CopyCount(_visibleChunks, _integrateDispatchDimensions, 0);
            _integrateKernel.DispatchIndirect(_integrateDispatchDimensions);
        }

        public async Awaitable<VisibleChunksResult> ReadbackVisibleChunks()
        {
            _visibleChangesKernel.DispatchFit(MaxVisibleChunks, 1, 1);
            Awaitable<AsyncGPUReadbackRequest> chunksWait =
                AsyncGPUReadback.RequestAsync(_visibleChunks);
            Awaitable<AsyncGPUReadbackRequest> countWait =
                AsyncGPUReadback.RequestAsync(_integrateDispatchDimensions);
            Awaitable<AsyncGPUReadbackRequest> changesWait =
                AsyncGPUReadback.RequestAsync(_visibleChangeSumsReadback);
            AsyncGPUReadbackRequest chunks = await chunksWait;
            AsyncGPUReadbackRequest count = await countWait;
            AsyncGPUReadbackRequest changes = await changesWait;
            if (chunks.hasError || count.hasError || changes.hasError)
                return default;

            int visibleCount = math.min(count.GetData<int>()[0], MaxVisibleChunks);
            return new VisibleChunksResult(visibleCount,
                chunks.GetData<int>(), changes.GetData<uint>());
        }

        public ChunkReadbackBuffer CreateChunkReadbackBuffer()
        {
            int voxels = VoxelsPerChunkDimension;
            return new ChunkReadbackBuffer(
                new ComputeBuffer(voxels / 4 * voxels * voxels, sizeof(uint)),
                new NativeArray<Voxel>(voxels * voxels * voxels, Allocator.Persistent),
                voxels);
        }

        public async Awaitable<bool> ReadbackChunkInto(
            int chunkIndex, ChunkReadbackBuffer buffer)
        {
            if (buffer.VoxelsPerDimension != VoxelsPerChunkDimension)
                throw new ArgumentException("Chunk readback dimensions do not match.");
            if (chunkIndex < 0 || chunkIndex >= ChunkTableLength) return false;

            _compute.SetInt(ReadbackChunkIndexId, chunkIndex);
            _compute.SetBuffer(_compute.FindKernel("ChunkDataReadback"),
                ReadbackBufferId, buffer.GpuBuffer);
            _dataReadbackKernel.DispatchFit(VoxelsPerChunkDimension / 4,
                VoxelsPerChunkDimension, VoxelsPerChunkDimension);
            AsyncGPUReadbackRequest request =
                await AsyncGPUReadback.RequestAsync(buffer.GpuBuffer);
            if (request.hasError) return false;
            request.GetData<byte>().Reinterpret<Voxel>(1).CopyTo(buffer.Data);
            return true;
        }

        public int3 ChunkIndexToCoordinate(int index) => new int3(
            index % ChunkTableDimensions.x,
            index / ChunkTableDimensions.x % ChunkTableDimensions.y,
            index / (ChunkTableDimensions.x * ChunkTableDimensions.y));

        public float3 ChunkCoordinateToCorner(int3 coordinate)
        {
            int3 uncentered = coordinate - ChunkTableDimensions / 2;
            return (float3)uncentered * ChunkWorldSize - new float3(VoxelSize);
        }

        public void Dispose()
        {
            _reservedChunkCounter.Dispose();
            _chunkTable.Dispose();
            _chunkChangeSums.Dispose();
            _visibleChunks.Dispose();
            _visibleChangeSumsReadback.Dispose();
            _integrateDispatchDimensions.Dispose();
            _chunkData.Release();
            UnityEngine.Object.Destroy(_chunkData);
        }

        internal readonly struct VisibleChunksResult
        {
            public readonly int Count;
            public readonly NativeArray<int> Chunks;
            public readonly NativeArray<uint> ChangeSums;
            public readonly bool Valid;

            public VisibleChunksResult(int count, NativeArray<int> chunks,
                NativeArray<uint> changeSums)
            {
                Count = count;
                Chunks = chunks;
                ChangeSums = changeSums;
                Valid = true;
            }
        }

        internal readonly struct ChunkReadbackBuffer : IDisposable
        {
            public readonly ComputeBuffer GpuBuffer;
            public readonly NativeArray<Voxel> Data;
            public readonly int VoxelsPerDimension;

            public ChunkReadbackBuffer(ComputeBuffer gpuBuffer,
                NativeArray<Voxel> data, int voxelsPerDimension)
            {
                GpuBuffer = gpuBuffer;
                Data = data;
                VoxelsPerDimension = voxelsPerDimension;
            }

            public void Dispose()
            {
                GpuBuffer.Dispose();
                Data.Dispose();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal readonly struct Voxel
        {
            public readonly sbyte Value;
            public float NormalizedDistance => Value / (float)sbyte.MaxValue;
        }
    }
}
