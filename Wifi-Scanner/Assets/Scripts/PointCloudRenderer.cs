using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace WifiScanner
{
    /// <summary>
    /// Renders the WiFi cloud as round sphere GameObjects. Each sphere reuses the
    /// built-in sphere MESH and one of a set of pre-built gradient materials
    /// (clones of the assigned URP material, tinted along the RdYlGn ramp). Using
    /// shared materials instead of a per-renderer MaterialPropertyBlock lets the
    /// SRP Batcher group draws, so many thousands of points stay performant while
    /// still looking like a smooth gradient (48 steps ≈ continuous).
    ///
    /// Points beyond <see cref="maxPoints"/> reuse the oldest (ring buffer).
    /// </summary>
    public class PointCloudRenderer : MonoBehaviour
    {
        [Tooltip("Diameter of each point sphere, in metres.")]
        public float pointSize = 0.06f;

        [Tooltip("Max simultaneous point objects; the oldest is reused beyond this.")]
        public int maxPoints = 6000;

        [Tooltip("Number of pre-built gradient materials (visual colour resolution).")]
        public int gradientSteps = 48;

        [Tooltip("URP material asset cloned per gradient step. Assigned by SceneBootstrap.")]
        public Material pointMaterial;

        [Range(0f, 1f)]
        [Tooltip("Global opacity of all points (0 = invisible, 1 = fully opaque). " +
                 "Controlled at runtime by the left thumbstick via OpacityControls.")]
        public float opacity = 0.6f;

        private Mesh _mesh;
        private Material[] _gradientMats;
        private bool _warnedNoMaterial;
        private static readonly int ColorId = Shader.PropertyToID("_BaseColor");
        private readonly List<Renderer> _pool = new List<Renderer>();
        // RSSI per pool slot, so the dBm range filter can hide points later.
        private readonly List<int> _rssis = new List<int>();
        private int _filterMinDbm = -127;
        private int _filterMaxDbm = 0;
        private int _ring;

        private void Awake()
        {
            // Borrow the built-in sphere MESH; material comes from the URP asset.
            var temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _mesh = temp.GetComponent<MeshFilter>().sharedMesh;
            Destroy(temp);
        }

        private void BuildGradientMaterials()
        {
            if (pointMaterial == null)
            {
                if (!_warnedNoMaterial)
                {
                    _warnedNoMaterial = true;
                    Debug.LogError("[PointCloudRenderer] pointMaterial is not assigned — " +
                                   "assign a URP material (SceneBootstrap.pointMaterialOverride).");
                }
                return;
            }

            gradientSteps = Mathf.Max(2, gradientSteps);
            _gradientMats = new Material[gradientSteps];
            for (int i = 0; i < gradientSteps; i++)
            {
                float t = i / (float)(gradientSteps - 1); // 0 = red/weak .. 1 = green/strong
                var m = new Material(pointMaterial);
                m.enableInstancing = true;
                MakeTransparent(m);
                Color c = HeatmapColorMapper.Evaluate(t);
                c.a = opacity;
                m.SetColor(ColorId, c);
                _gradientMats[i] = m;
            }
        }

        /// <summary>
        /// Switch a URP Unlit material to alpha-blended transparent rendering so
        /// its <c>_BaseColor</c> alpha actually fades the point instead of being
        /// ignored (the shader's default surface type is Opaque).
        /// </summary>
        private static void MakeTransparent(Material m)
        {
            m.SetFloat("_Surface", 1f);   // 0 = Opaque, 1 = Transparent
            m.SetFloat("_Blend", 0f);     // Alpha blend
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.SetOverrideTag("RenderType", "Transparent");
            m.renderQueue = (int)RenderQueue.Transparent; // 3000
        }

        /// <summary>
        /// Set the global opacity of all points in [0,1]. Because every sphere
        /// shares one of the ~<see cref="gradientSteps"/> gradient materials, this
        /// writes only that many material alphas and takes effect immediately,
        /// independent of the number of points.
        /// </summary>
        public void SetOpacity(float value01)
        {
            opacity = Mathf.Clamp01(value01);
            if (_gradientMats == null) return;
            for (int i = 0; i < _gradientMats.Length; i++)
            {
                Color c = _gradientMats[i].GetColor(ColorId);
                c.a = opacity;
                _gradientMats[i].SetColor(ColorId, c);
            }
        }

        private Material MaterialForRssi(int rssiDbm)
        {
            float t = Mathf.Clamp01(Mathf.InverseLerp(
                HeatmapColorMapper.WeakDbm, HeatmapColorMapper.StrongDbm, rssiDbm));
            int idx = Mathf.Clamp(Mathf.RoundToInt(t * (gradientSteps - 1)), 0, gradientSteps - 1);
            return _gradientMats[idx];
        }

        private Renderer CreatePoint()
        {
            var go = new GameObject("point");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return mr;
        }

        /// <summary>
        /// Add a point and return a handle (its pool index) that can later be
        /// passed to <see cref="UpdatePoint"/> to recolour it. Returns -1 if no
        /// material is assigned.
        /// </summary>
        public int AddPoint(Vector3 worldPos, int rssiDbm)
        {
            if (_gradientMats == null)
            {
                BuildGradientMaterials();
                if (_gradientMats == null) return -1; // no material assigned
            }

            Renderer mr;
            int handle;
            if (_pool.Count < maxPoints)
            {
                mr = CreatePoint();
                _pool.Add(mr);
                _rssis.Add(rssiDbm);
                handle = _pool.Count - 1;
            }
            else
            {
                handle = _ring;
                mr = _pool[_ring];
                _rssis[_ring] = rssiDbm;
                _ring = (_ring + 1) % maxPoints;
            }

            var tr = mr.transform;
            tr.position = worldPos;
            tr.localScale = Vector3.one * pointSize;
            mr.sharedMaterial = MaterialForRssi(rssiDbm);
            mr.enabled = PassesFilter(rssiDbm);
            return handle;
        }

        /// <summary>
        /// Recolour an existing point (obtained from <see cref="AddPoint"/>) to a
        /// new RSSI value without moving it. Used when a revisited spot updates
        /// its running-average reading instead of spawning a duplicate.
        /// </summary>
        public void UpdatePoint(int handle, int rssiDbm)
        {
            if (_gradientMats == null) return;
            if (handle < 0 || handle >= _pool.Count) return;
            var mr = _pool[handle];
            if (mr == null) return;
            _rssis[handle] = rssiDbm;
            mr.sharedMaterial = MaterialForRssi(rssiDbm);
            mr.enabled = PassesFilter(rssiDbm);
        }

        /// <summary>
        /// Show only points whose RSSI lies in [minDbm, maxDbm]; points outside
        /// keep their data but their renderer is disabled. Reset with
        /// (-127, 0) to show everything.
        /// </summary>
        public void SetRssiFilter(int minDbm, int maxDbm)
        {
            _filterMinDbm = minDbm;
            _filterMaxDbm = maxDbm;
            for (int i = 0; i < _pool.Count; i++)
            {
                var mr = _pool[i];
                if (mr != null) mr.enabled = PassesFilter(_rssis[i]);
            }
        }

        private bool PassesFilter(int rssiDbm) =>
            rssiDbm >= _filterMinDbm && rssiDbm <= _filterMaxDbm;

        public void Clear()
        {
            foreach (var mr in _pool)
                if (mr != null) Destroy(mr.gameObject);
            _pool.Clear();
            _rssis.Clear();
            _ring = 0;
        }

        public int Count => _pool.Count;
    }
}
