using System;
using Unity.XR.CoreUtils.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace WifiScanner
{
    /// <summary>Turns Meta's stereo environment-depth frames into the world-space
    /// matrices and normal texture consumed by the TSDF room scanner.</summary>
    internal sealed class RoomMeshDepthSource : IDisposable
    {
        private readonly Matrix4x4[] _projection = new Matrix4x4[2];
        private readonly Matrix4x4[] _projectionInverse = new Matrix4x4[2];
        private readonly Matrix4x4[] _view = new Matrix4x4[2];
        private readonly Matrix4x4[] _viewInverse = new Matrix4x4[2];

        internal static readonly int DepthTextureId = Shader.PropertyToID("drmDepthTexture");
        internal static readonly int DepthTextureSizeId = Shader.PropertyToID("drmDepthTextureSize");
        internal static readonly int DepthNormalTextureId = Shader.PropertyToID("drmDepthNormalTexture");
        internal static readonly int ProjectionId = Shader.PropertyToID("drmDepthProjection");
        internal static readonly int ProjectionInverseId = Shader.PropertyToID("drmDepthProjectionInverse");
        internal static readonly int ViewId = Shader.PropertyToID("drmDepthView");
        internal static readonly int ViewInverseId = Shader.PropertyToID("drmDepthViewInverse");

        private readonly AROcclusionManager _manager;
        private readonly Transform _trackingSpace;
        private readonly ComputeShader _normalCompute;
        private readonly RoomMeshComputeKernel _normalKernel;
        private RenderTexture _normalTexture;
        private bool _subscribed;
        private bool _warnedUnexpectedFormat;

        public Texture DepthTexture { get; private set; }
        public RenderTexture NormalTexture => _normalTexture;
        public Matrix4x4[] Projection => _projection;
        public Matrix4x4[] ProjectionInverse => _projectionInverse;
        public Matrix4x4[] View => _view;
        public Matrix4x4[] ViewInverse => _viewInverse;
        public bool DepthAvailable { get; private set; }

        public event Action Updated = delegate { };

        public RoomMeshDepthSource(AROcclusionManager manager, Transform trackingSpace)
        {
            _manager = manager;
            _trackingSpace = trackingSpace;
            _normalCompute = Resources.Load<ComputeShader>("DynamicRoomMesh/DepthNormals");
            if (_normalCompute == null)
                throw new InvalidOperationException(
                    "DynamicRoomMesh/DepthNormals.compute is missing from Resources.");
            _normalKernel = new RoomMeshComputeKernel(_normalCompute, "DepthNormals");
        }

        public void SetActive(bool active)
        {
            if (active == _subscribed) return;
            _subscribed = active;
            DepthAvailable = false;
            if (active) _manager.frameReceived += OnDepthFrame;
            else _manager.frameReceived -= OnDepthFrame;
        }

        private void OnDepthFrame(AROcclusionFrameEventArgs args)
        {
            ReadOnlyList<ARExternalTexture> textures = args.externalTextures;
            if (textures == null || textures.Count < 1 || textures[0].texture == null)
            {
                DepthAvailable = false;
                return;
            }

            Texture rawDepth = textures[0].texture;
            bool questStereoDepth = rawDepth is RenderTexture depthRt &&
                                    depthRt.depthStencilFormat == GraphicsFormat.D16_UNorm &&
                                    depthRt.dimension == TextureDimension.Tex2DArray &&
                                    depthRt.volumeDepth == 2;
            if (!questStereoDepth)
            {
                DepthAvailable = false;
                if (!_warnedUnexpectedFormat)
                {
                    _warnedUnexpectedFormat = true;
                    Debug.LogWarning("[RoomMesh] Unerwartetes Environment-Depth-Format; " +
                                     "der Raumscan wartet auf Quest-Stereo-D16.");
                }
                return;
            }

            ReadOnlyList<XRFov> fovs = null;
            ReadOnlyList<Pose> poses = null;
            XRNearFarPlanes planes = default;
            bool gotMetadata = args.TryGetFovs(out fovs) &&
                               args.TryGetPoses(out poses) &&
                               args.TryGetNearFarPlanes(out planes);
            if (!gotMetadata || fovs.Count < 2 || poses.Count < 2)
            {
                DepthAvailable = false;
                return;
            }

            DepthTexture = rawDepth;
            _warnedUnexpectedFormat = false;
            Matrix4x4 worldToTracking = _trackingSpace != null
                ? _trackingSpace.worldToLocalMatrix
                : Matrix4x4.identity;
            for (int eye = 0; eye < 2; eye++)
            {
                _projection[eye] = CalculateProjection(fovs[eye], planes);
                _projectionInverse[eye] = _projection[eye].inverse;
                Pose pose = poses[eye];
                Matrix4x4 depthPose = Matrix4x4.TRS(
                    pose.position, pose.rotation, new Vector3(1f, 1f, -1f));
                _view[eye] = depthPose.inverse * worldToTracking;
                _viewInverse[eye] = _view[eye].inverse;
            }

            EnsureNormalTexture(rawDepth.width, rawDepth.height);
            _normalCompute.SetMatrixArray(ProjectionId, _projection);
            _normalCompute.SetMatrixArray(ProjectionInverseId, _projectionInverse);
            _normalCompute.SetMatrixArray(ViewId, _view);
            _normalCompute.SetMatrixArray(ViewInverseId, _viewInverse);
            _normalCompute.SetVector(DepthTextureSizeId,
                new Vector2(rawDepth.width, rawDepth.height));
            _normalKernel.Bind(DepthTextureId, rawDepth);
            _normalKernel.Bind("drmDepthNormalTextureRW", _normalTexture);
            _normalKernel.DispatchFit(rawDepth.width, rawDepth.height, 2);

            DepthAvailable = true;
            Updated.Invoke();
        }

        private void EnsureNormalTexture(int width, int height)
        {
            if (_normalTexture != null && _normalTexture.width == width &&
                _normalTexture.height == height) return;
            if (_normalTexture != null)
            {
                _normalTexture.Release();
                UnityEngine.Object.Destroy(_normalTexture);
            }

            _normalTexture = new RenderTexture(width, height, 0,
                GraphicsFormat.R8G8B8A8_SNorm, 1)
            {
                name = "Dynamic Room Mesh Depth Normals",
                dimension = TextureDimension.Tex2DArray,
                volumeDepth = 2,
                useMipMap = false,
                enableRandomWrite = true
            };
            _normalTexture.Create();
        }

        private static Matrix4x4 CalculateProjection(XRFov fov, XRNearFarPlanes planes)
        {
            float left = Mathf.Tan(fov.angleLeft);
            float right = Mathf.Tan(fov.angleRight);
            float bottom = Mathf.Tan(fov.angleDown);
            float top = Mathf.Tan(fov.angleUp);
            float near = planes.nearZ;
            float far = planes.farZ;
            float c = float.IsInfinity(far) ? -1f : -(far + near) / (far - near);
            float d = float.IsInfinity(far) ? -2f * near : -(2f * far * near) / (far - near);

            return new Matrix4x4
            {
                m00 = 2f / (right - left), m02 = (right + left) / (right - left),
                m11 = 2f / (top - bottom), m12 = (top + bottom) / (top - bottom),
                m22 = c, m23 = d,
                m32 = -1f
            };
        }

        public void Dispose()
        {
            SetActive(false);
            if (_normalTexture == null) return;
            _normalTexture.Release();
            UnityEngine.Object.Destroy(_normalTexture);
            _normalTexture = null;
        }
    }
}
