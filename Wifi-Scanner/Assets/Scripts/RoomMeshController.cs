using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.OpenXR.Features.Meta;

#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace WifiScanner
{
    /// <summary>
    /// 3D room reconstruction alongside the WiFi measurement, via the Quest's
    /// depth-based scene mesh (Meta-OpenXR "Meta Quest: Meshing" feature +
    /// AR Foundation ARMeshManager). The mesh comes from the system's Space
    /// Setup scan — per room, not continuous live SLAM; for a whole building,
    /// scan room by room ("Raum scannen" launches Space Setup from the app).
    ///
    /// The mesh renders as a subtle transparent ghost over passthrough
    /// (toggleable) and exports as OBJ next to the CSV/JSON WiFi export, in the
    /// same tracking-space coordinates, so building geometry and WiFi cloud load
    /// up congruently on the PC.
    ///
    /// REQUIRED one-time Project Setting (mirrors PassthroughSetup):
    ///   OpenXR → Android → enable "Meta Quest: Meshing". The package then
    ///   injects the USE_SCENE manifest permission at build time; the runtime
    ///   permission is requested here.
    ///
    /// Created and wired by SceneBootstrap.
    /// </summary>
    public enum RoomMeshMode
    {
        /// <summary>Mesh not rendered at all.</summary>
        Hidden,
        /// <summary>Subtle transparent ghost overlay (see the scan).</summary>
        Ghost,
        /// <summary>Invisible occluder: writes only depth, so real walls and
        /// furniture hide the WiFi points/heatmap behind them — you only see
        /// the data of the room you are in. Passthrough itself stays visible
        /// (the material draws no colour, render queue just before the
        /// transparent WiFi visuals).</summary>
        Occluder,
    }

    public class RoomMeshController : MonoBehaviour
    {
        [Tooltip("URP Unlit asset (transparent ghost). SceneBootstrap.roomMeshMaterialOverride.")]
        public Material meshMaterial;
        [Tooltip("URP Unlit asset (alpha 0, ZWrite on, queue 2999). " +
                 "SceneBootstrap.roomMeshOccluderMaterialOverride.")]
        public Material occluderMaterial;
        public RoomMeshMode startMode = RoomMeshMode.Hidden;

        public RoomMeshMode Mode { get; private set; } = RoomMeshMode.Hidden;

        public string ModeLabel => Mode switch
        {
            RoomMeshMode.Ghost => "Geist",
            RoomMeshMode.Occluder => "verdeckt",
            _ => "aus",
        };

        private ARMeshManager _meshManager;
        private MeshRenderer _templateRenderer;

        private void Start()
        {
            RequestScenePermission();

            var origin = FindFirstObjectByType<XROrigin>();
            if (origin == null)
            {
                Debug.LogError("[RoomMesh] No XROrigin in scene — cannot create " +
                               "the ARMeshManager (it must live under the XR Origin).");
                return;
            }
            if (meshMaterial == null)
            {
                Debug.LogError("[RoomMesh] meshMaterial not assigned — assign a URP " +
                               "Unlit asset (SceneBootstrap.roomMeshMaterialOverride).");
                return;
            }

            // Template whose MeshFilter the ARMeshManager instantiates per scene
            // mesh. Uses the material asset directly (magenta trap: keep the
            // variant that is guaranteed to be in the build).
            var template = new GameObject("RoomMeshTemplate");
            template.transform.SetParent(transform, false);
            var mf = template.AddComponent<MeshFilter>();
            _templateRenderer = template.AddComponent<MeshRenderer>();
            _templateRenderer.sharedMaterial = meshMaterial;
            _templateRenderer.shadowCastingMode = ShadowCastingMode.Off;
            _templateRenderer.receiveShadows = false;
            _templateRenderer.lightProbeUsage = LightProbeUsage.Off;
            _templateRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

            // ARMeshManager must sit on a CHILD of the XR Origin.
            var managerGo = new GameObject("RoomMeshManager");
            managerGo.transform.SetParent(origin.transform, false);
            _meshManager = managerGo.AddComponent<ARMeshManager>();
            _meshManager.meshPrefab = mf;

            SetMode(startMode);
            Debug.Log("[RoomMesh] ARMeshManager created (needs the OpenXR " +
                      "'Meta Quest: Meshing' feature enabled).");
        }

        /// <summary>
        /// Launches the system Space Setup so the user can (re)scan the current
        /// room; the resulting scene mesh then streams in automatically.
        /// </summary>
        public void RequestSceneCapture()
        {
            var session = FindFirstObjectByType<ARSession>();
            var meta = session != null
                ? session.subsystem as MetaOpenXRSessionSubsystem
                : null;
            if (meta == null)
            {
                Debug.LogWarning("[RoomMesh] Scene capture unavailable (no Meta " +
                                 "OpenXR ARSession — device build only).");
                return;
            }
            bool ok = meta.TryRequestSceneCapture();
            Debug.Log($"[RoomMesh] Scene capture request -> {(ok ? "OK" : "FAILED")}.");
        }

        public void CycleMode() =>
            SetMode((RoomMeshMode)(((int)Mode + 1) % 3));

        /// <summary>
        /// Off / ghost overlay / invisible occluder. Mesh data keeps updating
        /// in every mode; only the rendering changes.
        /// </summary>
        public void SetMode(RoomMeshMode mode)
        {
            if (mode == RoomMeshMode.Occluder && occluderMaterial == null)
            {
                Debug.LogWarning("[RoomMesh] occluderMaterial not assigned — " +
                                 "skipping occluder mode.");
                mode = RoomMeshMode.Hidden;
            }
            Mode = mode;
            Apply(_templateRenderer);
            if (_meshManager == null) return;
            foreach (var mf in _meshManager.meshes)
            {
                if (mf == null) continue;
                Apply(mf.GetComponent<MeshRenderer>());
            }
            Debug.Log($"[RoomMesh] Mesh-Modus -> {ModeLabel}");
        }

        private void Apply(MeshRenderer mr)
        {
            if (mr == null) return;
            switch (Mode)
            {
                case RoomMeshMode.Ghost:
                    mr.sharedMaterial = meshMaterial;
                    mr.enabled = true;
                    break;
                case RoomMeshMode.Occluder:
                    mr.sharedMaterial = occluderMaterial;
                    mr.enabled = true;
                    break;
                default:
                    mr.enabled = false;
                    break;
            }
        }

        private void Update()
        {
            // Instances spawned after a mode change inherit the template's
            // state, but re-assert periodically in case the manager resets
            // renderers; cheap for the handful of room meshes.
            if (_meshManager == null) return;
            foreach (var mf in _meshManager.meshes)
            {
                if (mf == null) continue;
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr == null) continue;
                bool wantEnabled = Mode != RoomMeshMode.Hidden;
                if (mr.enabled != wantEnabled) Apply(mr);
                else if (wantEnabled)
                {
                    var want = Mode == RoomMeshMode.Ghost ? meshMaterial : occluderMaterial;
                    if (mr.sharedMaterial != want) Apply(mr);
                }
            }
        }

        /// <summary>
        /// Writes all current scene meshes into one OBJ (world/tracking-space
        /// coordinates — congruent with the CSV/JSON WiFi samples).
        /// </summary>
        public void ExportObj(string stem)
        {
            if (_meshManager == null || _meshManager.meshes.Count == 0)
            {
                Debug.Log("[RoomMesh] No scene meshes to export (run Space Setup?).");
                return;
            }
            DataExporter.ExportMeshesObj(_meshManager.meshes, stem);
        }

        private static void RequestScenePermission()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            const string useScene = "com.oculus.permission.USE_SCENE";
            if (!Permission.HasUserAuthorizedPermission(useScene))
                Permission.RequestUserPermission(useScene);
#endif
        }
    }
}
