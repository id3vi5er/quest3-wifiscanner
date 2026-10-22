using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace WifiScanner
{
    /// <summary>
    /// Clickable tool palette on the LEFT wrist — replaces the old info HUD.
    /// A 3-column grid of buttons, one per exclusive tool (room tracer, doors/
    /// windows, drill planner, distance, notes, ping, delete, scan aligner)
    /// plus "✕ Aus", clicked with the RIGHT controller's ray: glance at your
    /// left wrist, tap a tool, keep working. The active tool's button is
    /// highlighted and a short usage hint for it is shown below the grid —
    /// visible while working, without opening the main panel.
    ///
    /// Registers its hover state with <see cref="XrUiHover"/> so the tools'
    /// trigger/A/B stay quiet while the ray is over the palette (same guard as
    /// the main panel). Created and wired by SceneBootstrap.
    /// </summary>
    public class ToolPalette : MonoBehaviour
    {
        [Header("Wired by SceneBootstrap")]
        public RoomTracer roomTracer;
        public OpeningTool openingTool;
        public DrillPlanner drillPlanner;
        public DistanceMeasurer distanceMeasurer;
        public SpatialNoteManager spatialNotes;
        public PingTester pingTester;
        public DeleteController deleteController;
        public ScanAligner scanAligner;
        public SurveyPlanController surveyPlan;
        public SurveyCellTool surveyCellTool;

        [Header("Wrist panel placement (relative to the left controller)")]
        [Tooltip("Offset of the panel from the controller origin, in metres.")]
        public Vector3 panelLocalPosition = new Vector3(0f, 0.04f, -0.06f);
        [Tooltip("Tilt of the panel. 90 = lying flat on top of the controller " +
                 "(face up); smaller values tilt it towards you.")]
        public Vector3 panelLocalEuler = new Vector3(90f, 0f, 0f);
        [Tooltip("Metres per canvas unit (panel size = sizeDelta * this).")]
        public float panelScale = 0.00045f;

        // One palette button bound to one exclusive tool.
        private class Entry
        {
            public IExclusiveTool tool;
            public Image image;
            public TextMeshProUGUI label;
            public string labelKey;
            public bool deleteStyle; // delete mode highlights reddish
            public GameObject root;
        }

        private static readonly Color DeleteActiveColor = new Color(0.85f, 0.25f, 0.2f, 1f);

        private readonly List<Entry> _entries = new List<Entry>();
        private RectTransform _canvasRect;
        private TextMeshProUGUI _hint;
        private TextMeshProUGUI _header;
        private TextMeshProUGUI _undoLabel;
        private GameObject _optionsRoot;
        private LayoutElement _optionsLayout;
        private IExclusiveTool _optionsTool;
        private Image[] _optionImages;
        private Image _undoImage;
        private Entry _surveyCellEntry;
        private InputAction _posAction;
        private InputAction _rotAction;
        private float _refreshTimer;

        private void Start()
        {
            XrUiHover.EnsureXrUiInputModule();

            Transform trackingSpace = ResolveTrackingSpace();

            // --- Tracked anchor that follows the left controller pose ---
            var anchorGo = new GameObject("ToolPaletteAnchor");
            anchorGo.transform.SetParent(trackingSpace, false);
            var driver = anchorGo.AddComponent<TrackedPoseDriver>();
            driver.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
            driver.positionInput = new InputActionProperty(new InputAction(
                "PalettePos", InputActionType.Value,
                "<XRController>{LeftHand}/devicePosition", expectedControlType: "Vector3"));
            driver.rotationInput = new InputActionProperty(new InputAction(
                "PaletteRot", InputActionType.Value,
                "<XRController>{LeftHand}/deviceRotation", expectedControlType: "Quaternion"));

            // --- World-space canvas sitting on the wrist, ray-clickable ---
            var canvasGo = new GameObject("ToolPaletteCanvas",
                typeof(RectTransform), typeof(Canvas));
            var canvasTr = canvasGo.transform;
            canvasTr.SetParent(anchorGo.transform, false);
            canvasTr.localPosition = panelLocalPosition;
            canvasTr.localRotation = Quaternion.Euler(panelLocalEuler);
            canvasTr.localScale = Vector3.one * panelScale;

            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.overrideSorting = true;
            canvas.sortingOrder = 210;
            _canvasRect = (RectTransform)canvasTr;
            _canvasRect.sizeDelta = new Vector2(360f, 610f); // 4 grid rows + hint
            canvasGo.AddComponent<GraphicRaycaster>();            // editor mouse
            canvasGo.AddComponent<TrackedDeviceGraphicRaycaster>(); // XR rays

            var bg = new GameObject("Background", typeof(RectTransform))
                .AddComponent<Image>();
            bg.transform.SetParent(canvasTr, false);
            bg.color = new Color(0.05f, 0.05f, 0.08f, 0.94f);
            UiBuilder.Stretch(bg.rectTransform);

            var content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(canvasTr, false);
            UiBuilder.Stretch((RectTransform)content.transform);
            var layout = content.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(14, 14, 12, 12);
            layout.spacing = 8f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            _header = UiBuilder.AddText(content.transform,
                LocalizationManager.Get("tool.header").ToUpperInvariant(), 17f,
                FontStyles.Bold, TextAlignmentOptions.BottomLeft);
            _header.color = new Color(1f, 1f, 1f, 0.45f);
            _header.characterSpacing = 6f;
            _header.gameObject.AddComponent<LayoutElement>().preferredHeight = 34f;

            var gridGo = new GameObject("ToolGrid", typeof(RectTransform));
            gridGo.transform.SetParent(content.transform, false);
            var grid = gridGo.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(104f, 88f);
            grid.spacing = new Vector2(8f, 8f);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = 3;
            // The grid computes its own preferred height from the rows.

            AddToolButton(gridGo.transform, "tool.room", roomTracer);
            AddToolButton(gridGo.transform, "tool.opening", openingTool);
            AddToolButton(gridGo.transform, "tool.drill", drillPlanner);
            AddToolButton(gridGo.transform, "tool.distance", distanceMeasurer);
            AddToolButton(gridGo.transform, "tool.note", spatialNotes);
            AddToolButton(gridGo.transform, "tool.ping", pingTester);
            AddToolButton(gridGo.transform, "tool.delete", deleteController, deleteStyle: true);
            AddToolButton(gridGo.transform, "tool.align", scanAligner);
            _surveyCellEntry = AddToolButton(gridGo.transform,
                "tool.survey_cells", surveyCellTool);
            AddToolButton(gridGo.transform, "tool.off", null);
            AddUndoButton(gridGo.transform);
            if (_surveyCellEntry != null)
                _surveyCellEntry.root.SetActive(
                    surveyPlan != null && surveyPlan.IsRunning);

            _optionsRoot = new GameObject("ToolOptions", typeof(RectTransform));
            _optionsRoot.transform.SetParent(content.transform, false);
            _optionsLayout = _optionsRoot.AddComponent<LayoutElement>();
            var optionsColumn = _optionsRoot.AddComponent<VerticalLayoutGroup>();
            optionsColumn.spacing = 6f;
            optionsColumn.childControlWidth = true;
            optionsColumn.childControlHeight = true;
            optionsColumn.childForceExpandWidth = true;
            optionsColumn.childForceExpandHeight = false;
            _optionsRoot.SetActive(false);

            _hint = UiBuilder.AddText(content.transform, "", 16f,
                FontStyles.Normal, TextAlignmentOptions.TopLeft);
            _hint.textWrappingMode = TextWrappingModes.Normal;
        }

        private void OnEnable()
        {
            LocalizationManager.LanguageChanged += OnLanguageChanged;
            _posAction = new InputAction("PaletteRayPos", InputActionType.Value,
                "<XRController>{RightHand}/pointerPosition", expectedControlType: "Vector3");
            _posAction.AddBinding("<XRController>{RightHand}/devicePosition"); // fallback
            _rotAction = new InputAction("PaletteRayRot", InputActionType.Value,
                "<XRController>{RightHand}/pointerRotation", expectedControlType: "Quaternion");
            _rotAction.AddBinding("<XRController>{RightHand}/deviceRotation"); // fallback
            _posAction.Enable();
            _rotAction.Enable();
        }

        private void OnDisable()
        {
            LocalizationManager.LanguageChanged -= OnLanguageChanged;
            _posAction?.Disable();
            _rotAction?.Disable();
            CloseToolOptions();
            XrUiHover.Clear(this);
        }

        private void OnLanguageChanged()
        {
            if (_header != null)
                _header.text = LocalizationManager.Get("tool.header").ToUpperInvariant();
            foreach (Entry entry in _entries)
                if (entry.label != null)
                    entry.label.text = LocalizationManager.Get(entry.labelKey);
            if (_undoLabel != null)
                _undoLabel.text = LocalizationManager.Get("tool.undo");
            if (_optionsTool != null) ShowToolOptions(_optionsTool);
            if (_hint != null) _hint.text = BuildHint();
        }

        private void Update()
        {
            if (_canvasRect == null) return;

            // Hover guard: while the right ray is over the palette, the tools'
            // trigger/A/B must stay quiet (clicking a button is a UI action).
            // Generous margin — the target is small and sits on a moving wrist.
            GetPointer(out Vector3 origin, out Vector3 dir);
            XrUiHover.Set(this, XrUiHover.RayOverRect(origin, dir,
                _canvasRect, margin: 0.10f));

            _refreshTimer += Time.deltaTime;
            if (_refreshTimer < 0.25f) return;
            _refreshTimer = 0f;

            foreach (var e in _entries)
            {
                if (ReferenceEquals(e, _surveyCellEntry))
                {
                    bool available = surveyPlan != null && surveyPlan.IsRunning;
                    if (e.root.activeSelf != available) e.root.SetActive(available);
                    if (!available && e.tool != null && e.tool.IsActive)
                        e.tool.SetActive(false);
                    if (!available) continue;
                }
                bool active = e.tool != null && e.tool.IsActive;
                e.image.color = !active ? UiBuilder.ButtonColor
                    : e.deleteStyle ? DeleteActiveColor
                    : UiBuilder.ActiveColor;
            }
            if (_undoImage != null)
                _undoImage.color = UndoStack.CanUndo
                    ? UiBuilder.ButtonColor
                    : new Color(0.12f, 0.13f, 0.17f, 1f); // dimmed: nothing to undo
            RefreshOptionColors();
            if (_hint != null) _hint.text = BuildHint();
        }

        // One grid cell. A null tool is the "✕ Aus" button (deactivate all).
        private Entry AddToolButton(Transform parent, string labelKey,
            IExclusiveTool tool, bool deleteStyle = false)
        {
            if (tool == null && labelKey != "tool.off") return null; // feature disabled
            // Unity fake-null check for destroyed/missing components.
            if (tool is Object obj && obj == null) return null;

            var go = new GameObject("ToolButton", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = UiBuilder.ButtonColor;
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            // Button supplies normal hover/pressed visuals. Gesture dispatch is
            // custom so a completed long press does not also click on release.
            var press = go.AddComponent<PalettePressHandler>();
            press.onShortPress = () =>
            {
                CloseToolOptions();
                if (tool != null) tool.SetActive(!tool.IsActive);
                else ExclusiveTools.DeactivateOthers(null);
                Haptics.TickRight();
            };
            if (HasOptions(tool))
                press.onLongPress = () =>
                {
                    if (!tool.IsActive) tool.SetActive(true);
                    ShowToolOptions(tool);
                    Haptics.PlaceRight();
                };

            var text = UiBuilder.AddText(go.transform,
                LocalizationManager.Get(labelKey), 18f,
                FontStyles.Normal, TextAlignmentOptions.Center);
            UiBuilder.Stretch(text.rectTransform);

            var entry = new Entry { tool = tool, image = img, label = text,
                labelKey = labelKey, deleteStyle = deleteStyle, root = go };
            _entries.Add(entry);
            return entry;
        }

        private bool HasOptions(IExclusiveTool tool) =>
            tool != null && (ReferenceEquals(tool, openingTool) ||
                             ReferenceEquals(tool, spatialNotes) ||
                             ReferenceEquals(tool, pingTester));

        private void ShowToolOptions(IExclusiveTool tool)
        {
            if (_optionsRoot == null) return;
            ClearOptionChildren();
            _optionsTool = tool;
            _optionImages = null;
            _optionsRoot.SetActive(true);
            _optionsLayout.preferredHeight = 162f;
            _canvasRect.sizeDelta = new Vector2(360f, 780f);

            if (ReferenceEquals(tool, openingTool))
            {
                UiBuilder.AddHeader(_optionsRoot.transform,
                    LocalizationManager.Get("tool.options.opening"));
                UiBuilder.AddButtonRow(_optionsRoot.transform, out _optionImages,
                    (LocalizationManager.Get("tool.options.door"), () => { openingTool.SetDoorMode(true); RefreshOptionColors(); }),
                    (LocalizationManager.Get("tool.options.window"), () => { openingTool.SetDoorMode(false); RefreshOptionColors(); }));
            }
            else if (ReferenceEquals(tool, spatialNotes))
            {
                UiBuilder.AddHeader(_optionsRoot.transform,
                    LocalizationManager.Get("tool.options.note"));
                UiBuilder.AddButtonRow(_optionsRoot.transform, out _optionImages,
                    ("AP", () => { spatialNotes.SetActiveType(0); RefreshOptionColors(); }),
                    (LocalizationManager.Get("tool.options.interference"), () => { spatialNotes.SetActiveType(1); RefreshOptionColors(); }),
                    (LocalizationManager.Get("tool.options.text"), () => { spatialNotes.SetActiveType(2); RefreshOptionColors(); }));
            }
            else if (ReferenceEquals(tool, pingTester))
            {
                UiBuilder.AddHeader(_optionsRoot.transform,
                    LocalizationManager.Get("tool.options.ping"));
                UiBuilder.AddTextField(_optionsRoot.transform,
                    LocalizationManager.Get("tool.options.host"),
                    pingTester.host, null, value => pingTester.SetHost(value));
            }

            UiBuilder.AddButtonRow(_optionsRoot.transform,
                (LocalizationManager.Get("tool.options.close"), CloseToolOptions));
            RefreshOptionColors();
        }

        private void RefreshOptionColors()
        {
            if (_optionImages == null || _optionsTool == null) return;
            for (int i = 0; i < _optionImages.Length; i++)
                _optionImages[i].color = UiBuilder.ButtonColor;
            if (ReferenceEquals(_optionsTool, openingTool) && _optionImages.Length >= 2)
                _optionImages[openingTool.IsDoorMode ? 0 : 1].color = UiBuilder.ActiveColor;
            else if (ReferenceEquals(_optionsTool, spatialNotes) && _optionImages.Length >= 3)
                _optionImages[Mathf.Clamp(spatialNotes.ActiveTypeIndex, 0, 2)].color =
                    UiBuilder.ActiveColor;
        }

        private void CloseToolOptions()
        {
            if (_optionsRoot == null) return;
            _optionsRoot.SetActive(false);
            _optionsTool = null;
            _optionImages = null;
            ClearOptionChildren();
            if (_canvasRect != null)
                _canvasRect.sizeDelta = new Vector2(360f, 610f);
        }

        private void ClearOptionChildren()
        {
            if (_optionsRoot == null) return;
            for (int i = _optionsRoot.transform.childCount - 1; i >= 0; i--)
                Destroy(_optionsRoot.transform.GetChild(i).gameObject);
        }

        // Pops the global undo stack; dimmed while there is nothing to undo.
        private void AddUndoButton(Transform parent)
        {
            var go = new GameObject("UndoButton", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            _undoImage = go.AddComponent<Image>();
            _undoImage.color = UiBuilder.ButtonColor;
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = _undoImage;
            btn.onClick.AddListener(() =>
            {
                if (UndoStack.UndoLast())
                    Haptics.TickRight();
            });
            _undoLabel = UiBuilder.AddText(go.transform,
                LocalizationManager.Get("tool.undo"), 18f,
                FontStyles.Normal, TextAlignmentOptions.Center);
            UiBuilder.Stretch(_undoLabel.rectTransform);
        }

        // Context help for whichever tool is active (moved here from the main
        // panel so the instructions are visible while working, without opening
        // the menu). Same wording/priority as the old panel traceHint.
        private string BuildHint()
        {
            string hint = roomTracer != null && roomTracer.IsActive
                ? LocalizationManager.Get("tool.hint.room")
                : "";
            if (drillPlanner != null && drillPlanner.IsActive)
                hint = LocalizationManager.Get("tool.hint.drill");
            else if (deleteController != null && deleteController.IsActive)
                hint = LocalizationManager.Get("tool.hint.delete");
            else if (distanceMeasurer != null && distanceMeasurer.IsActive)
                hint = LocalizationManager.Get("tool.hint.distance");
            else if (spatialNotes != null && spatialNotes.IsActive)
                hint = LocalizationManager.Get("tool.hint.note");
            else if (pingTester != null && pingTester.IsActive)
                hint = LocalizationManager.Get("tool.hint.ping");
            else if (openingTool != null && openingTool.IsActive)
                hint = openingTool.IsDoorMode
                    ? LocalizationManager.Get("tool.hint.door")
                    : LocalizationManager.Get("tool.hint.window");
            else if (surveyCellTool != null && surveyCellTool.IsActive)
                hint = LocalizationManager.Get("tool.hint.survey_cells");
            if (scanAligner != null && scanAligner.IsActive)
                hint = scanAligner.CurrentPhase == ScanAligner.Phase.SelectWall
                    ? LocalizationManager.Get("tool.hint.align_select")
                    : LocalizationManager.Get("tool.hint.align_place");

            if (hint.Length > 0) return hint;
            string undo = UndoStack.CanUndo
                ? LocalizationManager.Format("tool.hint.undo", UndoStack.TopLabel)
                : "";
            return LocalizationManager.Format("tool.hint.idle", undo);
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

        /// <summary>
        /// The controller pose from the TrackedPoseDriver is expressed in the XR
        /// tracking space (the "Camera Offset" that also holds the head camera).
        /// Parenting the anchor there places the palette correctly in the world.
        /// </summary>
        private Transform ResolveTrackingSpace()
        {
            var cam = Camera.main;
            if (cam != null && cam.transform.parent != null)
                return cam.transform.parent; // Camera Offset
            Debug.LogWarning("[ToolPalette] Camera Offset not found — palette " +
                             "may be positionally offset. Is the XR camera tagged " +
                             "MainCamera under a Camera Offset?");
            return transform;
        }
    }
}
