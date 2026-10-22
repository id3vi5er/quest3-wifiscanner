using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace WifiScanner
{
    /// <summary>
    /// Left-wrist selectable tool for toggling individual Survey cells between
    /// walkable and unwalkable. The right pointer ray selects one exact cell on
    /// the active floor; a single trigger press toggles it.
    /// </summary>
    public sealed class SurveyCellTool : MonoBehaviour, IExclusiveTool
    {
        [Header("Wired by SceneBootstrap")]
        public SurveyPlanController surveyPlan;
        public Material visualMaterial;

        public bool IsActive { get; private set; }

        private InputAction _posAction;
        private InputAction _rotAction;
        private InputAction _toggleAction;
        private LineRenderer _outline;
        private Material _outlineMaterial;
        private SurveyAnalyzer.SurveyCell _hoverCell;

        private static readonly Color WalkableColor =
            new Color(0.10f, 0.85f, 1f, 1f);
        private static readonly Color UnwalkableColor =
            new Color(1f, 0.25f, 0.18f, 1f);
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        private void Start()
        {
            var root = new GameObject("SurveyCellToolVisuals");
            root.transform.SetParent(transform, false);
            _outline = root.AddComponent<LineRenderer>();
            _outline.useWorldSpace = true;
            _outline.loop = true;
            _outline.positionCount = 4;
            _outline.startWidth = _outline.endWidth = 0.025f;
            _outline.shadowCastingMode = ShadowCastingMode.Off;
            _outline.receiveShadows = false;
            if (visualMaterial != null)
            {
                _outlineMaterial = new Material(visualMaterial);
                _outline.sharedMaterial = _outlineMaterial;
            }
            _outline.enabled = false;
        }

        private void OnEnable()
        {
            _posAction = new InputAction("SurveyCellPos", InputActionType.Value,
                "<XRController>{RightHand}/pointerPosition",
                expectedControlType: "Vector3");
            _posAction.AddBinding("<XRController>{RightHand}/devicePosition");
            _rotAction = new InputAction("SurveyCellRot", InputActionType.Value,
                "<XRController>{RightHand}/pointerRotation",
                expectedControlType: "Quaternion");
            _rotAction.AddBinding("<XRController>{RightHand}/deviceRotation");
            _toggleAction = new InputAction("SurveyCellToggle", InputActionType.Button,
                "<XRController>{RightHand}/triggerPressed");
            _posAction.Enable();
            _rotAction.Enable();
            _toggleAction.Enable();
            ExclusiveTools.Register(this);
        }

        private void OnDisable()
        {
            SetActive(false);
            _posAction?.Disable();
            _rotAction?.Disable();
            _toggleAction?.Disable();
            ExclusiveTools.Unregister(this);
        }

        private void OnDestroy()
        {
            if (_outlineMaterial != null) Destroy(_outlineMaterial);
        }

        public void SetActive(bool active)
        {
            // This tool has no useful or safe target outside a running Survey.
            active &= surveyPlan != null && surveyPlan.IsRunning;
            if (IsActive == active) return;
            IsActive = active;
            if (active) ExclusiveTools.DeactivateOthers(this);
            else ClearHover();
        }

        private void Update()
        {
            if (!IsActive) return;
            if (surveyPlan == null || !surveyPlan.IsRunning)
            {
                SetActive(false);
                return;
            }

            GetPointer(out Vector3 origin, out Vector3 direction);
            float floorY = 0f;
            bool hit = !ControlPanel.PointerOverPanel &&
                surveyPlan.TryRaycastCell(origin, direction, out _hoverCell,
                    out floorY);
            if (!hit)
            {
                ClearHover();
                return;
            }

            ShowHover(_hoverCell, floorY);
            if (!_toggleAction.WasPressedThisFrame()) return;

            bool nowUnwalkable = surveyPlan.ToggleCellUnwalkable(_hoverCell.id);
            SetColor(nowUnwalkable ? UnwalkableColor : WalkableColor);
            Haptics.PlaceRight();
        }

        private void ShowHover(SurveyAnalyzer.SurveyCell cell, float floorY)
        {
            if (_outline == null || cell == null) return;
            float half = surveyPlan.quality.cellSize * 0.46f;
            float y = floorY + surveyPlan.floorOffset + 0.02f;
            float yaw = surveyPlan.CurrentResult != null
                ? surveyPlan.CurrentResult.gridYawDegrees : 0f;
            Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
            Vector3 center = new Vector3(cell.centerX, y, cell.centerZ);
            _outline.SetPosition(0, center + rotation * new Vector3(-half, 0f, -half));
            _outline.SetPosition(1, center + rotation * new Vector3(-half, 0f,  half));
            _outline.SetPosition(2, center + rotation * new Vector3( half, 0f,  half));
            _outline.SetPosition(3, center + rotation * new Vector3( half, 0f, -half));
            SetColor(surveyPlan.IsCellUnwalkable(cell.id)
                ? UnwalkableColor : WalkableColor);
            _outline.enabled = true;
        }

        private void ClearHover()
        {
            _hoverCell = null;
            if (_outline != null) _outline.enabled = false;
        }

        private void SetColor(Color color)
        {
            if (_outline == null) return;
            _outline.startColor = _outline.endColor = color;
            if (_outlineMaterial != null &&
                _outlineMaterial.HasProperty(BaseColorId))
                _outlineMaterial.SetColor(BaseColorId, color);
        }

        private void GetPointer(out Vector3 worldPos, out Vector3 worldFwd)
        {
            Vector3 pos = _posAction.ReadValue<Vector3>();
            Quaternion rot = _rotAction.ReadValue<Quaternion>();
            var cam = Camera.main;
            Transform space = cam != null ? cam.transform.parent : null;
            if (space != null)
            {
                worldPos = space.TransformPoint(pos);
                worldFwd = space.rotation * rot * Vector3.forward;
            }
            else
            {
                worldPos = pos;
                worldFwd = rot * Vector3.forward;
            }
        }
    }
}
