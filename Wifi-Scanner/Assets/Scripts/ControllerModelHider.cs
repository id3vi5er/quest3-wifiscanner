using System;
using UnityEngine;

namespace WifiScanner
{
    /// <summary>
    /// Hides the rendered controller models while keeping everything usable:
    /// only Mesh-/SkinnedMeshRenderers below tracking-space transforms whose
    /// name contains "controller" are disabled. Ray visuals (LineRenderer),
    /// hand meshes, the wrist HUD and the control panel (canvas renderers) are
    /// untouched, so menus keep working as before.
    ///
    /// XRI spawns the controller model prefabs asynchronously once the devices
    /// connect (and again after resume), so this re-scans periodically instead
    /// of hiding once. Added and wired by SceneBootstrap.
    /// </summary>
    public class ControllerModelHider : MonoBehaviour
    {
        [Tooltip("Seconds between scans for newly spawned controller models.")]
        public float rescanInterval = 1f;

        private float _timer;

        private void Update()
        {
            _timer -= Time.deltaTime;
            if (_timer > 0f) return;
            _timer = rescanInterval;

            // Tracking space = Camera Offset (holds camera, controllers, hands).
            var cam = Camera.main;
            if (cam == null || cam.transform.parent == null) return;

            foreach (Transform child in cam.transform.parent)
            {
                if (child.name.IndexOf("controller", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                Hide<MeshRenderer>(child);
                Hide<SkinnedMeshRenderer>(child);
            }
        }

        private static void Hide<T>(Transform root) where T : Renderer
        {
            foreach (var r in root.GetComponentsInChildren<T>(true))
                if (r.enabled) r.enabled = false;
        }
    }
}
