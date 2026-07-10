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
            public bool deleteStyle; // delete mode highlights reddish
        }

        private static readonly Color DeleteActiveColor = new Color(0.85f, 0.25f, 0.2f, 1f);

        private readonly List<Entry> _entries = new List<Entry>();
        private RectTransform _canvasRect;
        private TextMeshProUGUI _hint;
        private Image _undoImage;
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
            _canvasRect = (RectTransform)canvasTr;
            _canvasRect.sizeDelta = new Vector2(360f, 610f); // 4 grid rows + hint
            canvasGo.AddComponent<GraphicRaycaster>();            // editor mouse
            canvasGo.AddComponent<TrackedDeviceGraphicRaycaster>(); // XR rays

            var bg = new GameObject("Background", typeof(RectTransform))
                .AddComponent<Image>();
            bg.transform.SetParent(canvasTr, false);
            bg.color = new Color(0.05f, 0.05f, 0.08f, 0.85f);
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

            UiBuilder.AddHeader(content.transform, "Werkzeuge");

            var gridGo = new GameObject("ToolGrid", typeof(RectTransform));
            gridGo.transform.SetParent(content.transform, false);
            var grid = gridGo.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(104f, 88f);
            grid.spacing = new Vector2(8f, 8f);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = 3;
            // The grid computes its own preferred height from the rows.

            AddToolButton(gridGo.transform, "Raum", roomTracer);
            AddToolButton(gridGo.transform, "Öffnung", openingTool);
            AddToolButton(gridGo.transform, "Bohren", drillPlanner);
            AddToolButton(gridGo.transform, "Distanz", distanceMeasurer);
            AddToolButton(gridGo.transform, "Notiz", spatialNotes);
            AddToolButton(gridGo.transform, "Ping", pingTester);
            AddToolButton(gridGo.transform, "Löschen", deleteController, deleteStyle: true);
            AddToolButton(gridGo.transform, "Ausrichten", scanAligner);
            AddToolButton(gridGo.transform, "✕ Aus", null);
            AddUndoButton(gridGo.transform);

            _hint = UiBuilder.AddText(content.transform, "", 16f,
                FontStyles.Normal, TextAlignmentOptions.TopLeft);
            _hint.textWrappingMode = TextWrappingModes.Normal;
        }

        private void OnEnable()
        {
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
            _posAction?.Disable();
            _rotAction?.Disable();
            XrUiHover.Clear(this);
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
                bool active = e.tool != null && e.tool.IsActive;
                e.image.color = !active ? UiBuilder.ButtonColor
                    : e.deleteStyle ? DeleteActiveColor
                    : UiBuilder.ActiveColor;
            }
            if (_undoImage != null)
                _undoImage.color = UndoStack.CanUndo
                    ? UiBuilder.ButtonColor
                    : new Color(0.12f, 0.13f, 0.17f, 1f); // dimmed: nothing to undo
            if (_hint != null) _hint.text = BuildHint();
        }

        // One grid cell. A null tool is the "✕ Aus" button (deactivate all).
        private void AddToolButton(Transform parent, string label,
            IExclusiveTool tool, bool deleteStyle = false)
        {
            if (tool == null && label != "✕ Aus") return; // feature disabled
            // Unity fake-null check for destroyed/missing components.
            if (tool is Object obj && obj == null) return;

            var go = new GameObject("ToolButton", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = UiBuilder.ButtonColor;
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            if (tool != null)
                btn.onClick.AddListener(() =>
                {
                    tool.SetActive(!tool.IsActive);
                    Haptics.TickRight();
                });
            else
                btn.onClick.AddListener(() =>
                {
                    ExclusiveTools.DeactivateOthers(null);
                    Haptics.TickRight();
                });

            var text = UiBuilder.AddText(go.transform, label, 18f,
                FontStyles.Normal, TextAlignmentOptions.Center);
            UiBuilder.Stretch(text.rectTransform);

            if (tool != null)
                _entries.Add(new Entry { tool = tool, image = img, deleteStyle = deleteStyle });
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
            var text = UiBuilder.AddText(go.transform, "↺ Undo", 18f,
                FontStyles.Normal, TextAlignmentOptions.Center);
            UiBuilder.Stretch(text.rectTransform);
        }

        // Context help for whichever tool is active (moved here from the main
        // panel so the instructions are visible while working, without opening
        // the menu). Same wording/priority as the old panel traceHint.
        private string BuildHint()
        {
            string hint = roomTracer != null && roomTracer.IsActive
                ? "<color=#00dcff>Raum: Trigger=Ecke, B=fertig, A=zurück. " +
                  "Fertige Räume: Ecke greifen = verschieben (gelb), " +
                  "Boden anvisieren + Trigger halten = Höhe ziehen (orange)</color>"
                : "";
            if (drillPlanner != null && drillPlanner.IsActive)
                hint = "<color=#00dcff>Bohren: Wand/Boden/Decke anvisieren, " +
                       "Trigger=Linie setzen, Stick hoch/runter=Bohrtiefe, " +
                       "A=letzte Linie entfernen</color>";
            else if (deleteController != null && deleteController.IsActive)
                hint = "<color=#FF6B6B>Löschen: Raum/Bohrung/Messung/Notiz/" +
                       "Ping anvisieren (leuchtet auf), Trigger 2× = löschen</color>";
            else if (distanceMeasurer != null && distanceMeasurer.IsActive)
                hint = "<color=#00dcff>Distanz: Trigger=Startpunkt, " +
                       "wegbewegen, Trigger=Endpunkt. Mehrere Messungen " +
                       "bleiben stehen</color>";
            else if (spatialNotes != null && spatialNotes.IsActive)
                hint = "<color=#00dcff>Notiz: Trigger=setzen am Controller, " +
                       "A=Typ wechseln (Virtueller AP/Störquelle/Text)</color>";
            else if (pingTester != null && pingTester.IsActive)
                hint = "<color=#00dcff>Ping: Trigger=Knoten setzen & Latenz " +
                       "messen</color>";
            else if (openingTool != null && openingTool.IsActive)
                hint = openingTool.IsDoorMode
                    ? "<color=#00dcff>Tür: Wand anvisieren, 1. Trigger = " +
                      "Anschlagseite (obere Ecke, dort ist das Scharnier), " +
                      "2. Trigger = Breite (Höhe bis Boden). A=Fenster, B=zurück</color>"
                    : "<color=#00dcff>Fenster: Wand anvisieren, 2× Trigger = " +
                      "gegenüberliegende Ecken. A=Tür, B=zurück</color>";
            if (scanAligner != null && scanAligner.IsActive)
                hint = scanAligner.CurrentPhase == ScanAligner.Phase.SelectWall
                    ? "<color=#00dcff>Ausrichten: Wand des geladenen Scans " +
                      "anvisieren, Trigger = wählen. Grüne Kugel = Anker-Ecke. " +
                      "Keine Wand wählbar? Erst Raumhöhe ziehen. B = abbrechen</color>"
                    : "<color=#00dcff>Ausrichten: LINKEN Controller an die " +
                      "Stelle der grünen Ecke, RECHTEN entlang der Wand, beide " +
                      "Grips halten, loslassen = übernehmen. B = zurück</color>";

            if (hint.Length > 0) return hint;
            string undo = UndoStack.CanUndo
                ? $"\n↺ macht rückgängig: {UndoStack.TopLabel}"
                : "";
            return "<color=#777777>Werkzeug antippen (rechter Strahl). " +
                   $"A/B = Aufnahme/Export, solange kein Werkzeug aktiv.{undo}</color>";
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
