using System;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.Meta;
using UnityEngine.XR.OpenXR.NativeTypes;

namespace WifiScanner
{
    /// <summary>
    /// Turns the scene into MR passthrough at runtime using the Meta-OpenXR /
    /// AR Foundation stack (com.unity.xr.meta-openxr 2.x). Add this component to
    /// any active GameObject in the scene (e.g. the same one as SceneBootstrap).
    ///
    /// REQUIRED one-time Project Setting — this part cannot be scripted:
    ///   Project Settings → XR Plug-in Management → OpenXR → Android tab →
    ///   OpenXR Feature Groups → enable "Meta Quest", and tick
    ///   "Meta Quest: Camera (Passthrough)".
    ///
    /// Boundaryless mode (no guardian): this component suppresses the guardian
    /// boundary at runtime via the BoundaryVisibilityFeature. That needs the
    ///   "Meta Quest: Boundary Visibility"
    /// OpenXR feature enabled (already done in this project's OpenXR settings;
    /// "Suppress Visibility" stays OFF because we drive it from code instead).
    /// The runtime only accepts suppression WHILE passthrough is actually
    /// rendering, so we retry until it succeeds and re-apply after resume.
    ///
    /// Also remove/disable any opaque environment from the VR template (virtual
    /// room, skybox geometry), otherwise it hides the passthrough view.
    ///
    /// Note: passthrough only appears in an Android build on the headset — not
    /// in Editor Play mode.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class PassthroughSetup : MonoBehaviour
    {
        [Tooltip("Uncheck to keep a normal opaque VR view.")]
        public bool enablePassthrough = true;

        [Tooltip("Hide the guardian boundary so the user can roam freely " +
                 "(only works while passthrough is on).")]
        public bool suppressBoundary = true;

        [Tooltip("How long to keep retrying the suppression request while " +
                 "waiting for passthrough/splash to finish (seconds).")]
        public float suppressTimeout = 15f;

        private BoundaryVisibilityFeature _boundaryFeature;

        private void Awake()
        {
            if (!enablePassthrough) return;

            // 1) An AR Session drives the AR subsystems (incl. the camera).
            if (FindFirstObjectByType<ARSession>() == null)
                new GameObject("AR Session").AddComponent<ARSession>();

            // 2) The AR Camera Manager on the head camera is what actually
            //    switches passthrough on for Meta devices. (AR Camera Background
            //    is NOT needed on Quest and is intentionally omitted.)
            var cam = Camera.main;
            if (cam == null)
            {
                Debug.LogError("[PassthroughSetup] No Camera.main found — " +
                               "is the XR Origin camera tagged MainCamera?");
                return;
            }
            if (cam.GetComponent<ARCameraManager>() == null)
                cam.gameObject.AddComponent<ARCameraManager>();

            // 3) Clear to fully transparent so the real world shows through.
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);

            Debug.Log("[PassthroughSetup] Passthrough scene setup applied " +
                      "(needs the OpenXR Camera (Passthrough) feature enabled).");
        }

        private void Start()
        {
            if (!enablePassthrough || !suppressBoundary) return;

            _boundaryFeature = OpenXRSettings.Instance != null
                ? OpenXRSettings.Instance.GetFeature<BoundaryVisibilityFeature>()
                : null;

            if (_boundaryFeature == null || !_boundaryFeature.enabled)
            {
                Debug.LogWarning("[PassthroughSetup] Boundary Visibility feature " +
                                 "not available/enabled — can't go boundaryless. " +
                                 "Enable 'Meta Quest: Boundary Visibility' under " +
                                 "OpenXR → Android features.");
                return;
            }

            // Re-suppress if the runtime ever restores the boundary on its own
            // (e.g. when the view is briefly fully obscured).
            _boundaryFeature.boundaryVisibilityChanged += OnBoundaryVisibilityChanged;
            _ = SuppressBoundaryAsync();
        }

        private async Awaitable SuppressBoundaryAsync()
        {
            // Suppression is rejected until the splash screen is gone and
            // passthrough is on screen, so poll until it takes (or we time out).
            int attemptsLeft = Mathf.Max(1, Mathf.RoundToInt(suppressTimeout / 0.1f));
            XrResult result = XrResult.Success;

            try
            {
                while (attemptsLeft-- > 0)
                {
                    if (_boundaryFeature == null) return;

                    result = _boundaryFeature.TryRequestBoundaryVisibility(
                        XrBoundaryVisibility.VisibilitySuppressed);

                    if (result.IsUnqualifiedSuccess())
                    {
                        Debug.Log("[PassthroughSetup] Guardian boundary suppressed " +
                                  "— boundaryless mode active.");
                        return;
                    }

                    // XR_BOUNDARY_VISIBILITY_SUPPRESSION_NOT_ALLOWED_META is the
                    // expected 'passthrough not visible yet' case — just retry.
                    if (result.IsError() &&
                        (int)result != BoundaryVisibilityFeature
                            .XR_BOUNDARY_VISIBILITY_SUPPRESSION_NOT_ALLOWED_META)
                    {
                        Debug.LogError($"[PassthroughSetup] Boundary suppression " +
                                       $"failed with error {result}.");
                        return;
                    }

                    await Awaitable.WaitForSecondsAsync(0.1f);
                }

                Debug.LogWarning($"[PassthroughSetup] Boundary suppression timed " +
                                 $"out after {suppressTimeout}s (last result {result}). " +
                                 $"Is passthrough actually rendering?");
            }
            catch (OperationCanceledException)
            {
                // App/scene tearing down — nothing to do.
            }
        }

        private void OnBoundaryVisibilityChanged(object sender, XrBoundaryVisibility visibility)
        {
            Debug.Log($"[PassthroughSetup] Boundary visibility changed -> {visibility}.");
            if (suppressBoundary && visibility == XrBoundaryVisibility.VisibilityNotSuppressed)
                _ = SuppressBoundaryAsync();
        }

        private void OnApplicationPause(bool paused)
        {
            // Coming back from the system menu/sleep can restore the boundary;
            // request suppression again once we're rendering passthrough.
            if (!paused && enablePassthrough && suppressBoundary && _boundaryFeature != null)
                _ = SuppressBoundaryAsync();
        }

        private void OnDestroy()
        {
            if (_boundaryFeature != null)
                _boundaryFeature.boundaryVisibilityChanged -= OnBoundaryVisibilityChanged;
        }
    }
}
