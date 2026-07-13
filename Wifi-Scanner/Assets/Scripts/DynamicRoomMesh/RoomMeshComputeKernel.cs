using Unity.Mathematics;
using UnityEngine;

namespace WifiScanner
{
    internal readonly struct RoomMeshComputeKernel
    {
        private readonly ComputeShader _shader;
        private readonly int _index;
        private readonly int3 _groupSize;

        public RoomMeshComputeKernel(ComputeShader shader, string kernel)
        {
            _shader = shader;
            _index = shader.FindKernel(kernel);
            shader.GetKernelThreadGroupSizes(_index, out uint x, out uint y, out uint z);
            _groupSize = new int3((int)x, (int)y, (int)z);
        }

        public void Bind(int id, Texture texture) => _shader.SetTexture(_index, id, texture);
        public void Bind(string id, Texture texture) => _shader.SetTexture(_index, id, texture);
        public void Bind(string id, ComputeBuffer buffer) => _shader.SetBuffer(_index, id, buffer);

        public void DispatchFit(int x, int y, int z = 1)
        {
            _shader.Dispatch(_index,
                Mathf.CeilToInt(x / (float)_groupSize.x),
                Mathf.CeilToInt(y / (float)_groupSize.y),
                Mathf.CeilToInt(z / (float)_groupSize.z));
        }

        public void DispatchFit(RenderTexture texture) =>
            DispatchFit(texture.width, texture.height, texture.volumeDepth);

        public void DispatchFit(Texture texture, int depth = 1) =>
            DispatchFit(texture.width, texture.height, depth);

        public void DispatchIndirect(ComputeBuffer arguments) =>
            _shader.DispatchIndirect(_index, arguments);
    }
}
