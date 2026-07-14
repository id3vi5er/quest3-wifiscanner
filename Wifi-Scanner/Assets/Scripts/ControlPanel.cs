using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace WifiScanner
{
    /// <summary>
    /// Interactive world-space control panel, operated with the XR ray
    /// interactors of the XRI rig (point at it and pull the trigger).
    ///
    /// Runtime-built — no scene wiring needed. Compact TABBED layout: a fixed
    /// header (title + live stats) and a tab bar (Aufnahme / Ansicht / Räume /
    /// System) switch four pages in place. The exclusive tools (room tracer,
    /// drill planner, …) are NOT here any more — they live on the left-wrist
    /// ToolPalette; the panel keeps configuration and data actions.
    ///
    /// The MENU button on the left controller toggles the panel and re-places
    /// it in front of the user. The controller shortcuts (RecordingControls,
    /// OpacityControls) keep working in parallel.
    /// </summary>
    public class ControlPanel : MonoBehaviour
    {
        [Header("Wired by SceneBootstrap")]
        public SceneBootstrap bootstrap;
        public SampleCollector collector;
        public PointCloudRenderer cloud;
        public FloorHeatmapRenderer floor;
        public VisualizationModeController modeController;
        public DynamicOcclusionController occlusion;
        public MinimapController minimap;
        public GridCalibrator gridCalibrator;
        public FloorManager floorManager;
        public WebDashboardServer webServer;
        public RoomTracer roomTracer;
        public ApLocator apLocator;
        public PingTester pingTester;
        public SpatialNoteManager spatialNotes;
        public DistanceMeasurer distanceMeasurer;
        public GeigerCounter geiger;
        public SessionLoader sessionLoader;
        public WallLossEstimator wallLoss;
        public CoverageSimulator coverage;
        public OnboardingGuide onboarding;
        public RoamingTracker roaming;
        public MultiApScanner multiAp;
        public ThroughputTester throughputTester;
        public DynamicRoomMeshController dynamicRoomMesh;
        public NetworkStreamer streamer;
        public int streamPort = 9999;

        [Header("Placement")]
        [Tooltip("Distance in front of the user when (re)summoned (m).")]
        public float summonDistance = 0.75f;
        [Tooltip("Vertical offset relative to eye height when summoned (m).")]
        public float summonHeightOffset = -0.1f;
        [Tooltip("Point at the panel and hold the RIGHT grip to move it freely.")]
        public bool gripDragEnabled = true;
        [Tooltip("Keep the panel upright and continuously turn its front towards the user.")]
        public bool faceUserAutomatically = true;
        [Tooltip("Maximum speed of the automatic turn towards the user (degrees/s).")]
        public float faceUserDegreesPerSecond = 540f;
        public bool visibleAtStart = true;

        // dBm range covered by the filter sliders.
        private const int FilterRangeMin = -100;
        private const int FilterRangeMax = -30;
        private static readonly Color DestructiveButtonColor =
            new Color(0.42f, 0.16f, 0.18f, 1f);

        private GameObject _root;
        private TextMeshProUGUI _statsText;
        private TextMeshProUGUI _recLabel;
        private TextMeshProUGUI _modeLabel;
        private TextMeshProUGUI _occlusionLabel;
        private TextMeshProUGUI _minimapLabel;
        private TextMeshProUGUI _gridLabel;
        private TextMeshProUGUI _floorLabel;
        private TextMeshProUGUI _floorCreateLabel;
        private TextMeshProUGUI _webLabel;
        private TextMeshProUGUI _webUrlText;
        private TextMeshProUGUI _floorCalLabel;
        private TextMeshProUGUI _floorPlanLabel;
        private TextMeshProUGUI _mapRotateLabel;
        private TextMeshProUGUI _apLabel;
        private TextMeshProUGUI _audioLabel;
        private TextMeshProUGUI _wallLossLabel;
        private TextMeshProUGUI _coverageLabel;
        private TextMeshProUGUI _bssidLabel;
        private TextMeshProUGUI _roamLabel;
        private TextMeshProUGUI _multiApLabel;
        private TextMeshProUGUI _autosaveLabel;
        private TextMeshProUGUI _languageLabel;
        private TextMeshProUGUI _throughputLabel;
        private TextMeshProUGUI _throughputVisibilityLabel;
        private TextMeshProUGUI _roomScanLabel;
        private TextMeshProUGUI _roomMeshVisibilityLabel;
        private TextMeshProUGUI _roomMeshColorLabel;
        private UiCheckButton _occlusionCheck;
        private UiCheckButton _roomScanCheck;
        private UiCheckButton _roomMeshVisibilityCheck;
        private UiCheckButton _minimapCheck;
        private UiCheckButton _floorPlanCheck;
        private UiCheckButton _mapRotateCheck;
        private UiCheckButton _apCheck;
        private UiCheckButton _audioCheck;
        private UiCheckButton _coverageCheck;
        private UiCheckButton _roamingCheck;
        private UiCheckButton _multiApCheck;
        private UiCheckButton _autosaveCheck;
        private UiCheckButton _throughputVisibilityCheck;
        private UiCheckButton _wallLossCheck;
        private UiCheckButton _dashboardCheck;
        private UiCheckButton _diffCheck;
        private int _throughputPendingCount = -1;
        private float _throughputClaimUntil;
        private float _throughputRequestUntil;
        private float _throughputInfoUntil;
        private string _throughputInfoText = "";
        private string _bssidFilter = ""; // "" = alle APs (Parallel-Ansicht)
        private GameObject _apListGo;     // AP-Dropdown (aufklappbare Liste)
        private int _apListCount = -1;
        private GameObject _floorListGo; // Etagen-Dropdown (aufklappbare Liste)
        private int _floorListVersion = -1;
        private TextMeshProUGUI _roomNameLabel;
        private TextMeshProUGUI _wipeLabel;
        private float _wipeArmedUntil;
        private float _wipeInfoUntil;
        private string _wipeInfoText = "";
        private TextMeshProUGUI _scanStemLabel;
        private TextMeshProUGUI _scanLoadLabel;
        private TextMeshProUGUI _newScanLabel;
        private GameObject _newScanPromptGo;
        private bool _newScanWasRecording;
        private float _newScanInfoUntil;
        private string _newScanInfoText = "";
        private TextMeshProUGUI _diffStemLabel;
        private TextMeshProUGUI _diffToggleLabel;
        private readonly System.Collections.Generic.List<string> _diffStems =
            new System.Collections.Generic.List<string>();
        private int _diffIndex;
        private float _loadArmedUntil;
        private float _loadInfoUntil;
        private string _loadInfoText = "";
        private TextMeshProUGUI _roomsClearLabel;
        private float _roomsClearArmedUntil;
        private TMP_InputField _ipField;
        private TextMeshProUGUI _filterLabel;
        private int _filterMin = FilterRangeMin;
        private int _filterMax = FilterRangeMax;
        private InputAction _menuAction;
        private InputAction _posAction;
        private InputAction _rotAction;
        private InputAction _gripAction;
        private float _statsTimer;
        private bool _placed;
        private bool _pointerOverRoot;
        private bool _dragging;
        private Vector3 _dragLocalPosition;
        private Quaternion _dragLocalRotation;

        // Tab pages: Aufnahme / Ansicht / Räume / System.
        private readonly GameObject[] _pages = new GameObject[4];
        private Image[] _tabImages;
        private int _tabIndex;

        private static string T(string key) => LocalizationManager.Get(key);
        private static string F(string key, params object[] args) =>
            LocalizationManager.Format(key, args);

        /// <summary>True while the right controller's pointer ray currently
        /// lies over ANY visible XR UI surface (this panel or the wrist tool
        /// palette). The mode tools consult this to NOT also fire their
        /// trigger/A/B when you are clicking a UI button — otherwise
        /// deactivating a tool would place one last item.</summary>
        public static bool PointerOverPanel => XrUiHover.Any;

        private void Start()
        {
            XrUiHover.EnsureXrUiInputModule();
            BuildPanel();
            _root.SetActive(visibleAtStart);
        }

        private void OnEnable()
        {
            LocalizationManager.LanguageChanged += OnLanguageChanged;
            _menuAction = new InputAction("TogglePanel", InputActionType.Button,
                "<XRController>{LeftHand}/menuButton");
            _menuAction.Enable();
            _posAction = new InputAction("PanelPos", InputActionType.Value,
                "<XRController>{RightHand}/pointerPosition", expectedControlType: "Vector3");
            _posAction.AddBinding("<XRController>{RightHand}/devicePosition"); // fallback
            _rotAction = new InputAction("PanelRot", InputActionType.Value,
                "<XRController>{RightHand}/pointerRotation", expectedControlType: "Quaternion");
            _rotAction.AddBinding("<XRController>{RightHand}/deviceRotation"); // fallback
            _gripAction = new InputAction("MovePanel", InputActionType.Button,
                "<XRController>{RightHand}/gripPressed");
            _posAction.Enable();
            _rotAction.Enable();
            _gripAction.Enable();
        }

        private void OnDisable()
        {
            LocalizationManager.LanguageChanged -= OnLanguageChanged;
            _menuAction?.Disable();
            _posAction?.Disable();
            _rotAction?.Disable();
            _gripAction?.Disable();
            _dragging = false;
            XrUiHover.Clear(this);
        }

        private void OnLanguageChanged()
        {
            if (_root == null) return;
            bool wasVisible = _root.activeSelf;
            bool wasPlaced = _placed;
            int selectedTab = _tabIndex;
            Vector3 position = _root.transform.position;
            Quaternion rotation = _root.transform.rotation;
            _root.SetActive(false);
            Destroy(_root);
            BuildPanel();
            _root.transform.SetPositionAndRotation(position, rotation);
            _placed = wasPlaced;
            SelectTab(selectedTab);
            _root.SetActive(wasVisible);
        }

        private void Update()
        {
            if (_root == null) return;

            UpdatePointerOverPanel();
            UpdateGripDrag();

            if (_menuAction.WasPressedThisFrame())
            {
                bool show = !_root.activeSelf;
                _dragging = false;
                if (!show) CancelNewScanPrompt(resumeRecording: true);
                _root.SetActive(show);
                if (show)
                {
                    PlaceInFrontOfUser();
                    sessionLoader?.RefreshStems(); // new exports appear at once
                }
            }

            if (!_root.activeSelf) return;

            // First placement has to wait until the XR camera pose is live.
            if (!_placed) PlaceInFrontOfUser();

            _statsTimer += Time.deltaTime;
            if (_statsTimer >= 0.25f)
            {
                _statsTimer = 0f;
                RefreshStats();
            }
        }

        private void LateUpdate()
        {
            // Run after XR/controller pose updates and after grip dragging so
            // the panel remains readable while the user walks around it.
            if (faceUserAutomatically && _root != null && _root.activeSelf)
                FaceTowardsUser();
        }

        // ---- Tabs ----

        private void SelectTab(int index)
        {
            _tabIndex = index;
            for (int i = 0; i < _pages.Length; i++)
                if (_pages[i] != null)
                    _pages[i].SetActive(i == index);
            if (_tabImages != null)
                for (int i = 0; i < _tabImages.Length; i++)
                    _tabImages[i].color = i == index
                        ? UiBuilder.ActiveColor : UiBuilder.ButtonColor;
            // A pending destructive confirmation must not survive a page
            // switch — the armed button is no longer in view.
            _wipeArmedUntil = 0f;
            _loadArmedUntil = 0f;
            _roomsClearArmedUntil = 0f;
            CancelNewScanPrompt(resumeRecording: true);
        }

        // ---- Actions ----

        private void OnRecordClicked()
        {
            if (collector == null) return;
            if (collector.IsRecording) collector.StopRecording();
            else collector.StartRecording();
            RefreshStats();
        }

        private void OnExportClicked()
        {
            if (collector == null) return;
            string dir = collector.Export();
            Debug.Log($"[ControlPanel] Export -> {dir}");
        }

        private void OnThroughputClicked()
        {
            if (_throughputPendingCount >= 0 &&
                Time.unscaledTime < _throughputRequestUntil)
                return;

            if (webServer == null || !webServer.IsRunning)
            {
                ShowThroughputInfo(T("ui.throughput.web_off"), 4f);
                return;
            }
            if (!webServer.RequestBrowserThroughputTest())
            {
                ShowThroughputInfo(T("ui.throughput.open_web"), 5f);
                return;
            }

            _throughputPendingCount = throughputTester != null
                ? throughputTester.NodeCount : 0;
            _throughputClaimUntil = Time.unscaledTime + 8f;
            _throughputRequestUntil = Time.unscaledTime + 90f;
            _throughputInfoText = T("ui.throughput.requesting");
            _throughputInfoUntil = _throughputRequestUntil;
        }

        private void ShowThroughputInfo(string text, float seconds)
        {
            _throughputPendingCount = -1;
            _throughputInfoText = text;
            _throughputInfoUntil = Time.unscaledTime + seconds;
        }

        private void OnClearClicked()
        {
            if (collector == null) return;
            if (collector.SampleCount > 0)
            {
                // Sample is a struct — the list copy is a deep snapshot.
                var snapshot = new System.Collections.Generic.List<Sample>(collector.Samples);
                UndoStack.Push(F("ui.points.deleted_undo", snapshot.Count),
                    () => collector.LoadSamples(snapshot));
            }
            collector.ClearAll();
        }

        private void OnNewScanClicked()
        {
            if (collector == null || _newScanPromptGo == null) return;
            _newScanWasRecording = collector.IsRecording;
            collector.StopRecording(); // freeze the exact export/reset decision state
            _newScanPromptGo.SetActive(true);
            if (_newScanLabel != null)
                _newScanLabel.text = "<color=#FFB86B>" +
                    T("ui.new_scan.ask_export") + "</color>";
        }

        private void CompleteNewScan(bool exportFirst)
        {
            if (collector == null) return;
            if (exportFirst)
            {
                try
                {
                    collector.Export();
                }
                catch (System.Exception e)
                {
                    _newScanInfoText = T("ui.new_scan.export_failed");
                    _newScanInfoUntil = Time.unscaledTime + 4f;
                    Debug.LogError($"[ControlPanel] Neuer Scan abgebrochen: {e.Message}");
                    return; // never destroy data after a failed requested export
                }
            }

            collector.ResetSession();
            SetBssidFilter("");
            _diffStems.Clear();
            _diffIndex = 0;
            sessionLoader?.RefreshStems();
            _newScanPromptGo?.SetActive(false);
            _newScanWasRecording = false;
            _newScanInfoText = T(exportFirst
                ? "ui.new_scan.export_ready" : "ui.new_scan.ready");
            _newScanInfoUntil = Time.unscaledTime + 3f;
            RefreshStats();
        }

        private void CancelNewScanPrompt(bool resumeRecording)
        {
            if (_newScanPromptGo == null || !_newScanPromptGo.activeSelf) return;
            _newScanPromptGo.SetActive(false);
            if (resumeRecording && _newScanWasRecording && collector != null)
                collector.StartRecording();
            _newScanWasRecording = false;
        }

        // Deletes ALL export files on the device (frees storage). Destructive,
        // so it needs a second click within 4 s to confirm.
        private void OnWipeClicked()
        {
            if (Time.unscaledTime > _wipeArmedUntil)
            {
                _wipeArmedUntil = Time.unscaledTime + 4f;
                return;
            }
            _wipeArmedUntil = 0f;
            int count = DataExporter.DeleteAllExports();
            _wipeInfoText = F("ui.files.deleted", count);
            _wipeInfoUntil = Time.unscaledTime + 3f;
        }

        private void OnScanCycleClicked() => sessionLoader?.CycleStem();

        // Loading replaces the whole running session, so confirm like the
        // storage wipe (second click within 4 s).
        private void OnScanLoadClicked()
        {
            if (sessionLoader == null || sessionLoader.CurrentStem == null) return;
            if (Time.unscaledTime > _loadArmedUntil)
            {
                _loadArmedUntil = Time.unscaledTime + 4f;
                return;
            }
            _loadArmedUntil = 0f;
            bool ok = sessionLoader.LoadCurrent();
            _loadInfoText = ok
                ? F("ui.scan.loaded_points",
                    collector != null ? collector.SampleCount : 0)
                : T("ui.scan.load_failed");
            _loadInfoUntil = Time.unscaledTime + 3f;
        }

        private void OnDiffCycleClicked()
        {
            string selected = _diffIndex < _diffStems.Count ? _diffStems[_diffIndex] : null;
            _diffStems.Clear();
            _diffStems.AddRange(DataExporter.ListScanStems());
            if (_diffStems.Count == 0) { _diffIndex = 0; return; }
            _diffIndex = (_diffStems.IndexOf(selected) + 1) % _diffStems.Count;
        }

        // Diff view: compare the CURRENT session against a saved scan
        // (before/after an AP move). The reference should have been loaded
        // and aligned once so both share the tracking origin.
        private void OnDiffToggleClicked()
        {
            if (floor == null) return;
            if (floor.DiffEnabled)
            {
                floor.SetDiffReference(null, null);
                return;
            }
            if (_diffStems.Count == 0) OnDiffCycleClicked();
            if (_diffIndex >= _diffStems.Count) return;
            string stem = _diffStems[_diffIndex];
            if (DataExporter.TryLoadSamples(stem, out var samples, out _, out _))
                floor.SetDiffReference(stem, samples);
        }

        private void OnModeClicked() => modeController?.CycleMode();

        private void OnFloorCalibrateClicked() => roomTracer?.ToggleFloorCalibration();

        // Deletes all traced rooms; redrawing is real work, so confirm like
        // the storage wipe (second click within 4 s).
        private void OnRoomsClearClicked()
        {
            if (roomTracer == null) return;
            if (Time.unscaledTime > _roomsClearArmedUntil)
            {
                _roomsClearArmedUntil = Time.unscaledTime + 4f;
                return;
            }
            _roomsClearArmedUntil = 0f;
            if (roomTracer.Rooms.Count > 0)
            {
                var snapshot = UndoStack.CloneRooms(roomTracer.Rooms);
                UndoStack.Push(T("ui.rooms.deleted_undo"),
                    () => roomTracer.LoadRooms(snapshot));
            }
            roomTracer.ClearRooms();
        }

        // New streaming target: validate, apply live, persist across restarts.
        private void OnIpEndEdit(string value)
        {
            if (streamer == null || _ipField == null) return;
            value = value.Trim();
            if (System.Net.IPAddress.TryParse(value, out _))
            {
                streamer.SetTarget(value, streamPort);
                PlayerPrefs.SetString("wifi_pc_ip", value);
                PlayerPrefs.Save();
                Debug.Log($"[ControlPanel] Stream-Ziel -> {value}:{streamPort}");
            }
            else
            {
                _ipField.SetTextWithoutNotify(streamer.targetIp); // revert
            }
        }

        private void OnAudioClicked()
        {
            if (geiger == null) return;
            geiger.Toggle();
            _audioCheck?.SetChecked(geiger.IsOn);
        }

        private void OnOcclusionClicked()
        {
            if (occlusion == null) return;
            occlusion.ToggleOcclusion(); // dynamische Tiefensensor-Occlusion
            _occlusionCheck?.SetChecked(occlusion.OcclusionEnabled);
        }

        private void OnRoomScanClicked() => dynamicRoomMesh?.ToggleScan();

        private void OnRoomMeshVisibilityClicked() =>
            dynamicRoomMesh?.ToggleMeshVisible();

        private void OnRoomMeshColorClicked() => dynamicRoomMesh?.ToggleColorMode();

        private void OnMinimapClicked()
        {
            if (minimap == null) return;
            minimap.Toggle();
            _minimapCheck?.SetChecked(minimap.IsVisible);
        }

        private void OnGridCalibrateClicked() => gridCalibrator?.ToggleArmed();

        private void OnOpeningRemoveClicked() => roomTracer?.RemoveLastOpening();

        private void OnRoomNameClicked() => roomTracer?.ToggleRoomNaming();

        private void OnFloorCreateClicked() => floorManager?.CreateFloorAtHead();

        private void OnFloorAboveClicked() => floorManager?.CreateFloorAbove();

        private void OnFloorBelowClicked() => floorManager?.CreateFloorBelow();

        // Re-designates the storey labels (e.g. starting floor = OG2).
        private void OnLabelShiftUpClicked() => floorManager?.ShiftLabels(1);

        private void OnLabelShiftDownClicked() => floorManager?.ShiftLabels(-1);

        private void OnFloorDropdownClicked()
        {
            if (_floorListGo == null) return;
            bool show = !_floorListGo.activeSelf;
            if (show) RebuildFloorList();
            _floorListGo.SetActive(show);
        }

        // One button per storey (top first) + "Alle"; selecting collapses.
        private void RebuildFloorList()
        {
            if (_floorListGo == null || floorManager == null) return;
            _floorListVersion = floorManager.Version;
            foreach (Transform child in _floorListGo.transform)
                Destroy(child.gameObject);

            AddListEntry(_floorListGo.transform, T("ui.floor.all"),
                floorManager.ShowAll, () =>
                {
                    floorManager.ShowAllFloors();
                    _floorListGo.SetActive(false);
                });
            var floors = floorManager.Floors;
            for (int i = floors.Count - 1; i >= 0; i--)
            {
                int index = i;
                bool active = !floorManager.ShowAll &&
                              floorManager.ActiveIndex == index;
                AddListEntry(_floorListGo.transform,
                    $"{LocalizationManager.DisplayFloorLabel(floors[i].label)}   " +
                    $"({floors[i].baseY:F1} m)",
                    active, () =>
                    {
                        floorManager.SelectFloor(index);
                        _floorListGo.SetActive(false);
                    });
            }
        }

        // One 44-px dropdown row (shared by the floor and AP lists).
        private void AddListEntry(Transform parent, string text, bool active,
            UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject("ListEntry", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            go.AddComponent<LayoutElement>().preferredHeight = 44f;
            var img = go.AddComponent<Image>();
            img.color = active ? UiBuilder.ActiveColor : UiBuilder.ButtonColor;
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(onClick);
            var label = UiBuilder.AddText(go.transform, text, 20f,
                FontStyles.Normal, TextAlignmentOptions.Center);
            UiBuilder.Stretch(label.rectTransform);
        }

        private void OnWebDashboardClicked()
        {
            if (webServer != null) webServer.SetEnabled(!webServer.IsRunning);
        }

        private void OnFloorPlanClicked() => minimap?.ToggleFloorPlan();

        private void OnMapRotateClicked() => minimap?.ToggleRotationMode();

        private void OnApLocatorClicked() => apLocator?.Toggle();

        private void OnWallLossClicked() => wallLoss?.Toggle();

        private void OnCoverageClicked() => coverage?.Toggle();

        private void OnMultiApClicked() => multiAp?.Toggle();

        private void OnAutosaveClicked() => bootstrap?.ToggleAutosave();

        private void OnRoomMeshOpacityChanged(float value01) =>
            dynamicRoomMesh?.SetMeshOpacity(value01);

        private void OnThroughputVisibilityClicked() => throughputTester?.ToggleVisible();

        // AP dropdown: pick "Alle" (parallel view) or one BSSID — heatmap AND
        // AP-locator icons follow the same selection.
        private void OnApDropdownClicked()
        {
            if (_apListGo == null) return;
            bool show = !_apListGo.activeSelf;
            if (show) RebuildApList();
            _apListGo.SetActive(show);
        }

        private void SetBssidFilter(string bssid)
        {
            _bssidFilter = bssid ?? "";
            floor?.SetBssidFilter(_bssidFilter);
            apLocator?.SetBssidFilter(_bssidFilter);
            dynamicRoomMesh?.SetBssidFilter(_bssidFilter);
        }

        // One button per known AP (order of first appearance) + "Alle APs".
        private void RebuildApList()
        {
            if (_apListGo == null || collector == null) return;
            var bssids = collector.KnownBssids;
            _apListCount = bssids.Count;
            foreach (Transform child in _apListGo.transform)
                Destroy(child.gameObject);

            AddListEntry(_apListGo.transform, F("ui.ap.all_entry", bssids.Count),
                _bssidFilter.Length == 0, () =>
                {
                    SetBssidFilter("");
                    _apListGo.SetActive(false);
                });
            for (int i = 0; i < bssids.Count; i++)
            {
                var info = bssids[i];
                string ssid = string.IsNullOrEmpty(info.ssid) ? "?" : info.ssid;
                if (ssid.Length > 12) ssid = ssid.Substring(0, 11) + "…";
                string tail = info.bssid.Length > 5
                    ? info.bssid.Substring(info.bssid.Length - 5) : info.bssid;
                AddListEntry(_apListGo.transform,
                    $"{ssid} · {Sample.BandLabel(info.frequencyMhz)} · …{tail}",
                    _bssidFilter == info.bssid, () =>
                    {
                        SetBssidFilter(info.bssid);
                        _apListGo.SetActive(false);
                    });
            }
        }

        private void OnRoamingClicked() => roaming?.Toggle();

        // Data-clear actions for the palette tools (the tool TOGGLES live on
        // the left-wrist ToolPalette, only their data actions stay here).
        // Each pushes an undo snapshot; the Load APIs replay it.
        private void OnMeasureClearClicked()
        {
            if (distanceMeasurer == null) return;
            if (distanceMeasurer.Measurements.Count > 0)
            {
                var snapshot = UndoStack.CloneMeasurements(distanceMeasurer.Measurements);
                UndoStack.Push(T("ui.measurements.deleted_undo"),
                    () => distanceMeasurer.LoadMeasurements(snapshot));
            }
            distanceMeasurer.ClearMeasurements();
        }

        private void OnNotesClearClicked()
        {
            if (spatialNotes == null) return;
            if (spatialNotes.Notes.Count > 0)
            {
                var snapshot = UndoStack.CloneNotes(spatialNotes.Notes);
                UndoStack.Push(T("ui.notes.deleted_undo"),
                    () => spatialNotes.LoadNotes(snapshot));
            }
            spatialNotes.ClearNotes();
        }

        private void OnPingClearClicked()
        {
            if (pingTester == null) return;
            if (pingTester.NodeCount > 0)
            {
                var snapshot = new System.Collections.Generic.List<PingSample>(
                    pingTester.Samples);
                UndoStack.Push(T("ui.pings.deleted_undo"),
                    () => pingTester.LoadNodes(snapshot));
            }
            pingTester.ClearNodes();
        }

        private void OnOpacityChanged(float value01)
        {
            cloud?.SetOpacity(value01);
            // The slider fades whichever heatmap currently owns the floor.
            if (coverage != null && coverage.IsVisible)
                coverage.SetOpacity(value01);
            else
                floor?.SetOpacity(value01);
        }

        private void OnFilterMinChanged(float v)
        {
            _filterMin = Mathf.Min(Mathf.RoundToInt(v), _filterMax - 1);
            ApplyFilter();
        }

        private void OnFilterMaxChanged(float v)
        {
            _filterMax = Mathf.Max(Mathf.RoundToInt(v), _filterMin + 1);
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            // At the slider extremes, open the range fully so no edge value is cut.
            int min = _filterMin <= FilterRangeMin ? -127 : _filterMin;
            int max = _filterMax >= FilterRangeMax ? 0 : _filterMax;
            cloud?.SetRssiFilter(min, max);
            floor?.SetRssiFilter(min, max);
            _filterLabel.text = F("ui.filter.value", _filterMin, _filterMax);
        }

        private void RefreshStats()
        {
            if (_statsText == null || collector == null) return;

            string rec = T(collector.IsRecording
                ? "ui.stats.recording" : "ui.stats.paused");
            int secs = Mathf.RoundToInt(collector.RecordingSeconds);
            string stats = collector.TryGetRssiStats(out int min, out int max, out float avg)
                ? F("ui.stats.signal", min, max, avg)
                : T("ui.stats.signal_empty");

            _statsText.text = F("ui.stats.main", rec, secs / 60, secs % 60,
                modeController != null ? modeController.ModeLabel : "-",
                collector.SampleCount, collector.LastRssi, stats, collector.LastSsid);

            if (_modeLabel != null && modeController != null)
                _modeLabel.text = F("ui.view.mode", modeController.ModeLabel);
            if (_recLabel != null)
                _recLabel.text = T(collector.IsRecording
                    ? "ui.record.pause" : "ui.record.start");
            if (_floorCalLabel != null && roomTracer != null)
                _floorCalLabel.text = T(roomTracer.IsCalibratingFloor
                    ? "ui.floor.cursor" : "ui.floor.calibrate");
            if (_roomsClearLabel != null)
                _roomsClearLabel.text = Time.unscaledTime < _roomsClearArmedUntil
                    ? "<color=#FF6B6B>" + T("common.confirm_again") + "</color>"
                    : T("ui.rooms.delete");
            if (_roomNameLabel != null && roomTracer != null)
                _roomNameLabel.text = T(roomTracer.IsNamingRoom
                    ? "ui.room.aim_name" : "ui.room.name");
            if (_occlusionLabel != null && occlusion != null)
            {
                _occlusionLabel.text = T("ui.occlusion.label");
                _occlusionCheck?.SetChecked(occlusion.OcclusionEnabled);
            }
            if (_roomScanLabel != null && dynamicRoomMesh != null)
            {
                _roomScanLabel.text = T("ui.room_scan.label");
                _roomScanCheck?.SetChecked(dynamicRoomMesh.ScanEnabled);
            }
            if (_roomMeshVisibilityLabel != null && dynamicRoomMesh != null)
            {
                _roomMeshVisibilityLabel.text = T("ui.room_mesh.visibility_label");
                _roomMeshVisibilityCheck?.SetChecked(dynamicRoomMesh.MeshVisible);
            }
            if (_roomMeshColorLabel != null && dynamicRoomMesh != null)
                _roomMeshColorLabel.text = T(dynamicRoomMesh.ColorMode ==
                    RoomMeshColorMode.WifiSignal
                        ? "ui.room_mesh.color_wifi"
                        : "ui.room_mesh.color_normals");
            if (_minimapLabel != null && minimap != null)
            {
                _minimapLabel.text = T("ui.minimap.label");
                _minimapCheck?.SetChecked(minimap.IsVisible);
            }
            if (_gridLabel != null && gridCalibrator != null)
                _gridLabel.text = gridCalibrator.IsArmed
                    ? T("ui.grid.hold")
                    : (floor != null && Mathf.Abs(floor.gridYawDegrees) > 0.05f
                        ? F("ui.grid.align_value", floor.gridYawDegrees)
                        : T("ui.grid.align"));
            if (_floorLabel != null && floorManager != null)
                _floorLabel.text = F("ui.floor.value",
                    LocalizationManager.DisplayFloorLabel(floorManager.CurrentLabel));
            if (_floorCreateLabel != null && floorManager != null)
                _floorCreateLabel.text = floorManager.NewFloorSuggested
                    ? "<color=#7CFC7C>" + T("ui.floor.new") + "</color>"
                    : T("ui.floor.create");
            // Open dropdown follows floor changes (creation, auto-switch).
            if (_floorListGo != null && _floorListGo.activeSelf &&
                floorManager != null && floorManager.Version != _floorListVersion)
                RebuildFloorList();
            if (_webLabel != null && webServer != null)
            {
                _webLabel.text = T("ui.dashboard.label");
                _dashboardCheck?.SetChecked(webServer.IsRunning);
            }
            if (_webUrlText != null && webServer != null)
                _webUrlText.text = webServer.IsRunning
                    ? webServer.Url + "  " + T("ui.local_network")
                    : "(" + T("common.off") + ")";
            if (_floorPlanLabel != null && minimap != null)
            {
                _floorPlanLabel.text = T("ui.floor_plan.label");
                _floorPlanCheck?.SetChecked(minimap.showFloorPlan);
            }
            if (_mapRotateLabel != null && minimap != null)
            {
                _mapRotateLabel.text = T("ui.map.rotate_label");
                _mapRotateCheck?.SetChecked(minimap.rotateWithHead);
            }
            if (_apLabel != null && apLocator != null)
            {
                _apLabel.text = T("ui.ap_location.label");
                _apCheck?.SetChecked(apLocator.IsVisible);
            }
            if (_bssidLabel != null && collector != null)
            {
                var bssids = collector.KnownBssids;
                if (_bssidFilter.Length > 0)
                {
                    // Selected AP vanished (clear/load)? Drop the stale filter.
                    SampleCollector.BssidInfo selected = null;
                    foreach (var info in bssids)
                        if (info.bssid == _bssidFilter) { selected = info; break; }
                    if (selected == null)
                    {
                        SetBssidFilter("");
                        _bssidLabel.text = F("ui.ap.all", bssids.Count);
                    }
                    else
                    {
                        string ssid = string.IsNullOrEmpty(selected.ssid)
                            ? "?" : selected.ssid;
                        if (ssid.Length > 10) ssid = ssid.Substring(0, 9) + "…";
                        _bssidLabel.text = F("ui.ap.value", ssid,
                            Sample.BandLabel(selected.frequencyMhz));
                    }
                }
                else
                {
                    _bssidLabel.text = F("ui.ap.all", bssids.Count);
                }
                // Open dropdown follows new networks appearing.
                if (_apListGo != null && _apListGo.activeSelf &&
                    bssids.Count != _apListCount)
                    RebuildApList();
            }
            if (_roamLabel != null && roaming != null)
            {
                _roamLabel.text = F("ui.roaming.label", roaming.Events.Count);
                _roamingCheck?.SetChecked(roaming.IsVisible);
            }
            if (_multiApLabel != null && multiAp != null)
            {
                _multiApLabel.text = T("ui.multi_ap.label");
                _multiApCheck?.SetChecked(multiAp.IsEnabled);
            }
            if (_autosaveLabel != null && bootstrap != null)
            {
                _autosaveLabel.text = T("ui.autosave.label");
                _autosaveCheck?.SetChecked(bootstrap.AutosaveEnabled);
            }
            if (_throughputLabel != null)
            {
                if (_throughputPendingCount >= 0 && throughputTester != null &&
                    throughputTester.NodeCount > _throughputPendingCount)
                {
                    var samples = throughputTester.Samples;
                    var result = samples[samples.Count - 1];
                    ShowThroughputInfo(
                        $"Q>PC {result.questToBrowserMbps:F0} | PC>Q " +
                        $"{result.browserToQuestMbps:F0} Mbit/s", 8f);
                }
                else if (_throughputPendingCount >= 0 &&
                         Time.unscaledTime >= _throughputClaimUntil &&
                         (webServer == null ||
                          !webServer.BrowserThroughputTestActive))
                {
                    ShowThroughputInfo(T("ui.throughput.reload"), 7f);
                }
                else if (_throughputPendingCount >= 0 && webServer != null &&
                         webServer.BrowserThroughputTestActive)
                {
                    _throughputInfoText = T("ui.throughput.running");
                }
                else if (_throughputPendingCount >= 0 &&
                         Time.unscaledTime >= _throughputRequestUntil)
                {
                    ShowThroughputInfo(T("ui.throughput.no_result"), 5f);
                }

                _throughputLabel.text = Time.unscaledTime < _throughputInfoUntil
                    ? _throughputInfoText
                    : F("ui.throughput.measure",
                        throughputTester != null ? throughputTester.NodeCount : 0);
            }
            if (_throughputVisibilityLabel != null && throughputTester != null)
            {
                _throughputVisibilityLabel.text = F("ui.throughput.points_label",
                    throughputTester.NodeCount);
                _throughputVisibilityCheck?.SetChecked(throughputTester.IsVisible);
            }
            if (_audioLabel != null && geiger != null)
            {
                _audioLabel.text = T("ui.audio.label");
                _audioCheck?.SetChecked(geiger.IsOn);
            }
            if (_coverageLabel != null && coverage != null)
            {
                _coverageLabel.text = T("ui.coverage.label");
                _coverageCheck?.SetChecked(coverage.IsVisible);
            }
            if (_wallLossLabel != null && wallLoss != null)
            {
                _wallLossLabel.text = T("ui.wall_loss.label");
                _wallLossCheck?.SetChecked(wallLoss.IsVisible);
            }
            if (_wipeLabel != null)
            {
                if (Time.unscaledTime < _wipeInfoUntil)
                    _wipeLabel.text = _wipeInfoText;
                else if (Time.unscaledTime < _wipeArmedUntil)
                    _wipeLabel.text = "<color=#FF6B6B>" +
                        T("common.confirm_again") + "</color>";
                else
                    _wipeLabel.text = T("ui.scans.delete");
            }
            if (_scanStemLabel != null && sessionLoader != null)
                _scanStemLabel.text = F("ui.scan.value",
                    ShortStem(sessionLoader.CurrentStem));
            if (_diffStemLabel != null)
                _diffStemLabel.text = F("ui.compare.value", ShortStem(
                    _diffIndex < _diffStems.Count ? _diffStems[_diffIndex] : null));
            if (_diffToggleLabel != null && floor != null)
            {
                _diffToggleLabel.text = floor.DiffEnabled
                    ? F("ui.diff.value", ShortStem(floor.DiffStem))
                    : T("ui.diff.label");
                _diffCheck?.SetChecked(floor.DiffEnabled);
            }
            if (_scanLoadLabel != null)
            {
                if (Time.unscaledTime < _loadInfoUntil)
                    _scanLoadLabel.text = _loadInfoText;
                else if (Time.unscaledTime < _loadArmedUntil)
                    _scanLoadLabel.text = "<color=#FF6B6B>" +
                        T("ui.scan.load_replace") + "</color>";
                else
                    _scanLoadLabel.text = T("common.load");
            }
            if (_newScanLabel != null)
            {
                if (Time.unscaledTime < _newScanInfoUntil)
                    _newScanLabel.text = _newScanInfoText;
                else if (_newScanPromptGo != null && _newScanPromptGo.activeSelf)
                    _newScanLabel.text = "<color=#FFB86B>" +
                        T("ui.new_scan.ask_export") + "</color>";
                else
                    _newScanLabel.text = T("ui.new_scan");
            }
        }

        // "wifi_20250704_213012" -> "20250704_213012", middle-ellipsed so it
        // fits a half-width button.
        private static string ShortStem(string stem)
        {
            if (string.IsNullOrEmpty(stem)) return "–";
            if (stem.StartsWith("wifi_", System.StringComparison.Ordinal))
                stem = stem.Substring(5);
            if (stem.Length > 16)
                stem = stem.Substring(0, 7) + "…" + stem.Substring(stem.Length - 8);
            return stem;
        }

        // Casts the right controller's pointer ray against the panel quad and
        // reports whether it currently lies inside to the shared hover registry
        // (aggregated with the tool palette via XrUiHover.Any).
        private void UpdatePointerOverPanel()
        {
            if (_root == null || !_root.activeSelf)
            {
                _pointerOverRoot = false;
                XrUiHover.Set(this, false);
                return;
            }
            GetPointer(out Vector3 origin, out Vector3 dir);
            _pointerOverRoot = XrUiHover.RayOverRect(origin, dir,
                (RectTransform)_root.transform, margin: 0.04f);
            // Keep the UI guard asserted while dragging even if the pointer
            // slips outside the moving rectangle for a frame.
            XrUiHover.Set(this, _pointerOverRoot || _dragging);
        }

        private void UpdateGripDrag()
        {
            if (!gripDragEnabled || _gripAction == null || _root == null)
                return;

            GetPointerPose(out Vector3 handPos, out Quaternion handRot);
            if (_dragging)
            {
                if (!_gripAction.IsPressed() || !_root.activeSelf)
                {
                    _dragging = false;
                    Haptics.PlaceRight();
                    return;
                }
                _root.transform.position = handPos + handRot * _dragLocalPosition;
                if (!faceUserAutomatically)
                    _root.transform.rotation = handRot * _dragLocalRotation;
                _placed = true;
                return;
            }

            if (_root.activeSelf && _pointerOverRoot &&
                _gripAction.WasPressedThisFrame())
            {
                _dragLocalPosition = Quaternion.Inverse(handRot) *
                    (_root.transform.position - handPos);
                _dragLocalRotation = Quaternion.Inverse(handRot) *
                    _root.transform.rotation;
                _dragging = true;
                XrUiHover.Set(this, true);
                Haptics.TickRight();
            }
        }

        private void FaceTowardsUser()
        {
            var cam = Camera.main;
            if (cam == null) return;

            // World-space canvases in this project show their front on the
            // -forward side. Therefore forward points from the user towards
            // the panel, matching PlaceInFrontOfUser's established orientation.
            Vector3 awayFromUser = _root.transform.position - cam.transform.position;
            awayFromUser.y = 0f; // stay upright; only yaw follows the user
            if (awayFromUser.sqrMagnitude < 1e-4f) return;

            Quaternion target = Quaternion.LookRotation(awayFromUser.normalized,
                Vector3.up);
            float step = Mathf.Max(0f, faceUserDegreesPerSecond) *
                Time.unscaledDeltaTime;
            _root.transform.rotation = step > 0f
                ? Quaternion.RotateTowards(_root.transform.rotation, target, step)
                : target;
        }

        private void GetPointer(out Vector3 worldPos, out Vector3 worldFwd)
        {
            GetPointerPose(out worldPos, out Quaternion worldRot);
            worldFwd = worldRot * Vector3.forward;
        }

        private void GetPointerPose(out Vector3 worldPos, out Quaternion worldRot)
        {
            Vector3 pos = _posAction.ReadValue<Vector3>();
            Quaternion rot = _rotAction.ReadValue<Quaternion>();
            var cam = Camera.main;
            Transform space = cam != null ? cam.transform.parent : null;
            if (space != null)
            {
                worldPos = space.TransformPoint(pos);
                worldRot = space.rotation * rot;
            }
            else
            {
                worldPos = pos;
                worldRot = rot;
            }
        }

        private void PlaceInFrontOfUser()
        {
            var cam = Camera.main;
            if (cam == null) return;
            Vector3 fwd = cam.transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) return;
            fwd.Normalize();
            _root.transform.position = cam.transform.position
                + fwd * summonDistance + Vector3.up * summonHeightOffset;
            _root.transform.rotation = Quaternion.LookRotation(fwd);
            _placed = true;
        }

        // ---- UI construction ----

        private void BuildPanel()
        {
            _root = new GameObject("ControlPanel", typeof(RectTransform), typeof(Canvas));
            _root.transform.SetParent(transform, false);
            var canvas = _root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.overrideSorting = true;
            canvas.sortingOrder = 200;
            var rootRt = (RectTransform)_root.transform;
            // 480 px main surface plus a narrow navigation rail attached to
            // its left. The main surface keeps its former physical width.
            rootRt.sizeDelta = new Vector2(650f, 1000f);
            _root.transform.localScale = Vector3.one * 0.0008f; // 0.52 x 0.80 m
            _root.AddComponent<GraphicRaycaster>();            // editor mouse
            _root.AddComponent<TrackedDeviceGraphicRaycaster>(); // XR rays

            var shell = new GameObject("PanelShell", typeof(RectTransform));
            shell.transform.SetParent(_root.transform, false);
            UiBuilder.Stretch((RectTransform)shell.transform);
            var shellLayout = shell.AddComponent<HorizontalLayoutGroup>();
            shellLayout.spacing = 10f;
            shellLayout.childControlWidth = true;
            shellLayout.childControlHeight = true;
            shellLayout.childForceExpandWidth = false;
            shellLayout.childForceExpandHeight = true;

            // ── Attached navigation rail ─────────────────────────────
            var navigation = new GameObject("Navigation", typeof(RectTransform));
            navigation.transform.SetParent(shell.transform, false);
            navigation.AddComponent<LayoutElement>().preferredWidth = 150f;
            var navigationBackground = navigation.AddComponent<Image>();
            navigationBackground.color = new Color(0.07f, 0.08f, 0.12f, 0.94f);
            var navigationLayout = navigation.AddComponent<VerticalLayoutGroup>();
            navigationLayout.padding = new RectOffset(10, 10, 18, 18);
            navigationLayout.spacing = 9f;
            navigationLayout.childControlWidth = true;
            navigationLayout.childControlHeight = true;
            navigationLayout.childForceExpandWidth = true;
            navigationLayout.childForceExpandHeight = false;

            var navigationTitle = UiBuilder.AddText(navigation.transform,
                T("ui.title"), 24f, FontStyles.Bold,
                TextAlignmentOptions.Center);
            navigationTitle.gameObject.AddComponent<LayoutElement>()
                .preferredHeight = 56f;

            var recordingTab = UiBuilder.AddButtonRow(navigation.transform,
                out Image[] recordingTabBackground,
                (T("ui.tab.recording"), () => SelectTab(0)))[0];
            var viewTab = UiBuilder.AddButtonRow(navigation.transform,
                out Image[] viewTabBackground,
                (T("ui.tab.view"), () => SelectTab(1)))[0];
            var roomsTab = UiBuilder.AddButtonRow(navigation.transform,
                out Image[] roomsTabBackground,
                (T("ui.tab.rooms"), () => SelectTab(2)))[0];
            var systemTab = UiBuilder.AddButtonRow(navigation.transform,
                out Image[] systemTabBackground,
                (T("ui.tab.system"), () => SelectTab(3)))[0];
            recordingTab.fontSize = viewTab.fontSize = roomsTab.fontSize =
                systemTab.fontSize = 20f;
            _tabImages = new[]
            {
                recordingTabBackground[0], viewTabBackground[0],
                roomsTabBackground[0], systemTabBackground[0]
            };

            var navigationSpacer = new GameObject("Spacer", typeof(RectTransform));
            navigationSpacer.transform.SetParent(navigation.transform, false);
            navigationSpacer.AddComponent<LayoutElement>().flexibleHeight = 1f;

            _recLabel = UiBuilder.AddButtonRow(navigation.transform,
                (T("ui.record.pause"), OnRecordClicked))[0];
            _recLabel.fontSize = 20f;
            var exportLabel = UiBuilder.AddButtonRow(navigation.transform,
                (T("ui.export"), OnExportClicked))[0];
            exportLabel.fontSize = 20f;

            // ── Main surface: live status + full-height tab content ───
            var content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(shell.transform, false);
            var contentElement = content.AddComponent<LayoutElement>();
            contentElement.preferredWidth = 480f;
            contentElement.flexibleWidth = 1f;
            var contentBackground = content.AddComponent<Image>();
            contentBackground.color = new Color(0.05f, 0.05f, 0.08f, 0.96f);
            var layout = content.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(20, 20, 18, 18);
            layout.spacing = 8f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            // Navigation no longer consumes rows above the active page.
            _statsText = UiBuilder.AddText(content.transform, "…", 22f,
                FontStyles.Normal, TextAlignmentOptions.TopLeft);

            _pages[0] = AddPage(content.transform, "PageAufnahme",
                out Transform recordingPage);
            _pages[1] = AddPage(content.transform, "PageAnsicht",
                out Transform viewPage);
            _pages[2] = AddPage(content.transform, "PageRaeume",
                out Transform roomsPage);
            _pages[3] = AddPage(content.transform, "PageSystem",
                out Transform systemPage);
            BuildPageAufnahme(recordingPage);
            BuildPageAnsicht(viewPage);
            BuildPageRaeume(roomsPage);
            BuildPageSystem(systemPage);
            SelectTab(0);
        }

        // One tab page: its own vertical stack, toggled as a whole by SelectTab.
        private static GameObject AddPage(Transform parent, string name,
            out Transform content)
        {
            var viewport = new GameObject(name, typeof(RectTransform));
            viewport.transform.SetParent(parent, false);
            var viewportLayout = viewport.AddComponent<LayoutElement>();
            viewportLayout.flexibleHeight = 1f;
            viewportLayout.minHeight = 120f;
            viewport.AddComponent<RectMask2D>();

            var page = new GameObject(name + "Content", typeof(RectTransform));
            page.transform.SetParent(viewport.transform, false);
            var pageRect = (RectTransform)page.transform;
            pageRect.anchorMin = new Vector2(0f, 1f);
            pageRect.anchorMax = new Vector2(1f, 1f);
            pageRect.pivot = new Vector2(0.5f, 1f);
            pageRect.anchoredPosition = Vector2.zero;
            pageRect.sizeDelta = Vector2.zero;
            var layout = page.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 8f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            page.AddComponent<ContentSizeFitter>().verticalFit =
                ContentSizeFitter.FitMode.PreferredSize;

            var scroll = viewport.AddComponent<ScrollRect>();
            scroll.viewport = (RectTransform)viewport.transform;
            scroll.content = pageRect;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 45f;
            scroll.verticalNormalizedPosition = 1f;
            content = page.transform;
            return viewport;
        }

        private void BuildPageAufnahme(Transform page)
        {
            UiBuilder.AddHeader(page, T("ui.section.recording"));
            UiBuilder.AddTextField(page, T("ui.export_name"),
                collector != null ? collector.exportName : "",
                value => { if (collector != null) collector.exportName = value; },
                null);
            bool hasAutosave = bootstrap != null && bootstrap.autosaveInterval > 0f;
            if (hasAutosave && multiAp != null)
            {
                var automationLabels = UiBuilder.AddButtonRow(page,
                    out Image[] automationBackgrounds,
                    (T("ui.autosave.label"), OnAutosaveClicked),
                    (T("ui.multi_ap.label"), OnMultiApClicked));
                _autosaveLabel = automationLabels[0];
                _multiApLabel = automationLabels[1];
                _autosaveCheck = UiBuilder.MakeCheckButton(_autosaveLabel,
                    automationBackgrounds[0], bootstrap.AutosaveEnabled);
                _multiApCheck = UiBuilder.MakeCheckButton(_multiApLabel,
                    automationBackgrounds[1], multiAp.IsEnabled);
            }
            else if (hasAutosave)
            {
                _autosaveLabel = UiBuilder.AddButtonRow(page,
                    out Image[] autosaveBackground,
                    (T("ui.autosave.label"), OnAutosaveClicked))[0];
                _autosaveCheck = UiBuilder.MakeCheckButton(_autosaveLabel,
                    autosaveBackground[0], bootstrap.AutosaveEnabled);
            }
            else if (multiAp != null)
            {
                _multiApLabel = UiBuilder.AddButtonRow(page,
                    out Image[] multiApBackground,
                    (T("ui.multi_ap.label"), OnMultiApClicked))[0];
                _multiApCheck = UiBuilder.MakeCheckButton(_multiApLabel,
                    multiApBackground[0], multiAp.IsEnabled);
            }

            UiBuilder.AddHeader(page, T("ui.section.sessions"));
            if (sessionLoader != null)
            {
                var loadLabels = UiBuilder.AddButtonRow(page,
                    (T("ui.scan.none"), OnScanCycleClicked),
                    (T("common.load"), OnScanLoadClicked));
                _scanStemLabel = loadLabels[0];
                _scanLoadLabel = loadLabels[1];
            }
            var resetLabels = UiBuilder.AddButtonRow(page, out Image[] resetBackgrounds,
                (T("ui.points.delete"), OnClearClicked),
                (T("ui.new_scan"), OnNewScanClicked));
            resetBackgrounds[0].color = DestructiveButtonColor;
            _newScanLabel = resetLabels[1];

            _newScanPromptGo = new GameObject("NewScanPrompt", typeof(RectTransform));
            _newScanPromptGo.transform.SetParent(page, false);
            var promptLayout = _newScanPromptGo.AddComponent<VerticalLayoutGroup>();
            promptLayout.spacing = 6f;
            promptLayout.childControlWidth = true;
            promptLayout.childControlHeight = true;
            promptLayout.childForceExpandWidth = true;
            promptLayout.childForceExpandHeight = false;
            var promptText = UiBuilder.AddText(_newScanPromptGo.transform,
                T("ui.new_scan.question"), 18f,
                FontStyles.Bold, TextAlignmentOptions.Center);
            promptText.gameObject.AddComponent<LayoutElement>().preferredHeight = 30f;
            UiBuilder.AddButtonRow(_newScanPromptGo.transform,
                (T("ui.new_scan.export"), () => CompleteNewScan(exportFirst: true)),
                (T("ui.new_scan.without_export"), () => CompleteNewScan(exportFirst: false)),
                (T("common.cancel"), () => CancelNewScanPrompt(resumeRecording: true)));
            _newScanPromptGo.SetActive(false);
            var note = UiBuilder.AddText(page,
                T("ui.recording.note"),
                16f, FontStyles.Normal, TextAlignmentOptions.TopLeft);
            note.color = new Color(1f, 1f, 1f, 0.45f);
        }

        private void BuildPageAnsicht(Transform page)
        {
            UiBuilder.AddHeader(page, T("ui.section.measurement_view"));
            var vizLabels = UiBuilder.AddButtonRow(page, out Image[] vizBackgrounds,
                (F("ui.view.mode", T("mode.both")), OnModeClicked),
                (T("ui.occlusion.label"), OnOcclusionClicked));
            _modeLabel = vizLabels[0];
            _occlusionLabel = vizLabels[1];
            _occlusionCheck = UiBuilder.MakeCheckButton(_occlusionLabel,
                vizBackgrounds[1], occlusion != null && occlusion.OcclusionEnabled);
            float startOpacity = cloud != null ? cloud.opacity : 0.6f;
            UiBuilder.AddSliderRow(page, T("ui.opacity"), 0f, 1f, startOpacity,
                false, OnOpacityChanged);

            UiBuilder.AddHeader(page, T("ui.filter.header"));
            UiBuilder.AddLegend(page);
            _filterLabel = UiBuilder.AddText(page,
                F("ui.filter.value", _filterMin, _filterMax), 22f,
                FontStyles.Normal, TextAlignmentOptions.Left);
            UiBuilder.AddSliderRow(page, null, FilterRangeMin, FilterRangeMax,
                _filterMin, true, OnFilterMinChanged);
            UiBuilder.AddSliderRow(page, null, FilterRangeMin, FilterRangeMax,
                _filterMax, true, OnFilterMaxChanged);

            UiBuilder.AddHeader(page, T("ui.section.map_wifi"));
            var mapLabels = UiBuilder.AddButtonRow(page, out Image[] mapBackgrounds,
                (T("ui.minimap.label"), OnMinimapClicked),
                (T("ui.floor_plan.label"), OnFloorPlanClicked));
            _minimapLabel = mapLabels[0];
            _floorPlanLabel = mapLabels[1];
            _minimapCheck = UiBuilder.MakeCheckButton(_minimapLabel,
                mapBackgrounds[0], minimap != null && minimap.IsVisible);
            _floorPlanCheck = UiBuilder.MakeCheckButton(_floorPlanLabel,
                mapBackgrounds[1], minimap != null && minimap.showFloorPlan);
            var mapApLabels = UiBuilder.AddButtonRow(page, out Image[] mapApBackgrounds,
                (T("ui.map.rotate_label"), OnMapRotateClicked),
                (T("ui.ap_location.label"), OnApLocatorClicked));
            _mapRotateLabel = mapApLabels[0];
            _apLabel = mapApLabels[1];
            _mapRotateCheck = UiBuilder.MakeCheckButton(_mapRotateLabel,
                mapApBackgrounds[0], minimap != null && minimap.rotateWithHead);
            _apCheck = UiBuilder.MakeCheckButton(_apLabel,
                mapApBackgrounds[1], apLocator != null && apLocator.IsVisible);
            var bssidLabels = UiBuilder.AddButtonRow(page, out Image[] bssidBackgrounds,
                (F("ui.ap.all", 0), OnApDropdownClicked),
                (F("ui.roaming.label", 0), OnRoamingClicked));
            _bssidLabel = bssidLabels[0];
            _roamLabel = bssidLabels[1];
            _roamingCheck = UiBuilder.MakeCheckButton(_roamLabel,
                bssidBackgrounds[1], roaming != null && roaming.IsVisible);
            if (throughputTester != null)
            {
                _throughputVisibilityLabel = UiBuilder.AddButtonRow(page,
                    out Image[] throughputVisibilityBackground,
                    (F("ui.throughput.points_label", 0), OnThroughputVisibilityClicked))[0];
                _throughputVisibilityCheck = UiBuilder.MakeCheckButton(
                    _throughputVisibilityLabel, throughputVisibilityBackground[0],
                    throughputTester.IsVisible);
            }

            // Collapsible AP list ("dropdown"): all known BSSIDs, filled on
            // open — with multi-AP scanning this grows beyond a cycle button.
            _apListGo = new GameObject("ApList", typeof(RectTransform));
            _apListGo.transform.SetParent(page, false);
            var apListLayout = _apListGo.AddComponent<VerticalLayoutGroup>();
            apListLayout.spacing = 6f;
            apListLayout.childControlWidth = true;
            apListLayout.childControlHeight = true;
            apListLayout.childForceExpandWidth = true;
            apListLayout.childForceExpandHeight = false;
            _apListGo.SetActive(false);
            if (geiger != null && coverage != null)
            {
                var extraRow = UiBuilder.AddButtonRow(page,
                    out Image[] extraBackgrounds,
                    (T("ui.audio.label"), OnAudioClicked),
                    (T("ui.coverage.label"), OnCoverageClicked));
                _audioLabel = extraRow[0];
                _coverageLabel = extraRow[1];
                _audioCheck = UiBuilder.MakeCheckButton(_audioLabel,
                    extraBackgrounds[0], geiger.IsOn);
                _coverageCheck = UiBuilder.MakeCheckButton(_coverageLabel,
                    extraBackgrounds[1], coverage.IsVisible);
            }
            else if (geiger != null)
            {
                _audioLabel = UiBuilder.AddButtonRow(page,
                    out Image[] audioBackground,
                    (T("ui.audio.label"), OnAudioClicked))[0];
                _audioCheck = UiBuilder.MakeCheckButton(_audioLabel,
                    audioBackground[0], geiger.IsOn);
            }
            else if (coverage != null)
            {
                _coverageLabel = UiBuilder.AddButtonRow(page,
                    out Image[] coverageBackground,
                    (T("ui.coverage.label"), OnCoverageClicked))[0];
                _coverageCheck = UiBuilder.MakeCheckButton(_coverageLabel,
                    coverageBackground[0], coverage.IsVisible);
            }

            if (floor != null)
            {
                UiBuilder.AddHeader(page, T("ui.section.comparison"));
                var diffLabels = UiBuilder.AddButtonRow(page, out Image[] diffBackgrounds,
                    (T("ui.compare.none"), OnDiffCycleClicked),
                    (T("ui.diff.label"), OnDiffToggleClicked));
                _diffStemLabel = diffLabels[0];
                _diffToggleLabel = diffLabels[1];
                _diffCheck = UiBuilder.MakeCheckButton(_diffToggleLabel,
                    diffBackgrounds[1], floor.DiffEnabled);
            }
        }

        private void BuildPageRaeume(Transform page)
        {
            if (dynamicRoomMesh != null)
            {
                UiBuilder.AddHeader(page, T("ui.section.room_mesh"));
                var roomMeshLabels = UiBuilder.AddButtonRow(page,
                    out Image[] roomMeshBackgrounds,
                    (T("ui.room_scan.label"), OnRoomScanClicked),
                    (T("ui.room_mesh.visibility_label"), OnRoomMeshVisibilityClicked),
                    (T("ui.room_mesh.color_normals"), OnRoomMeshColorClicked));
                _roomScanLabel = roomMeshLabels[0];
                _roomMeshVisibilityLabel = roomMeshLabels[1];
                _roomMeshColorLabel = roomMeshLabels[2];
                _roomScanCheck = UiBuilder.MakeCheckButton(_roomScanLabel,
                    roomMeshBackgrounds[0], dynamicRoomMesh.ScanEnabled);
                _roomMeshVisibilityCheck = UiBuilder.MakeCheckButton(
                    _roomMeshVisibilityLabel, roomMeshBackgrounds[1],
                    dynamicRoomMesh.MeshVisible);
                _roomMeshColorLabel.fontSize = 18f;
                UiBuilder.AddSliderRow(page, T("ui.room_mesh.opacity"), 0f, 1f,
                    dynamicRoomMesh.MeshOpacity, false,
                    OnRoomMeshOpacityChanged);
            }

            UiBuilder.AddHeader(page, T("ui.section.room_layout"));
            var traceLabels = UiBuilder.AddButtonRow(page,
                (T("ui.grid.align"), OnGridCalibrateClicked),
                (T("ui.floor.calibrate"), OnFloorCalibrateClicked));
            _gridLabel = traceLabels[0];
            _floorCalLabel = traceLabels[1];
            _roomNameLabel = UiBuilder.AddButtonRow(page,
                (T("ui.room.name"), OnRoomNameClicked))[0];

            UiBuilder.AddHeader(page, T("ui.section.room_data"));
            var clearLabels = UiBuilder.AddButtonRow(page, out Image[] roomBackgrounds,
                (T("ui.rooms.delete"), OnRoomsClearClicked),
                (T("ui.opening.remove"), OnOpeningRemoveClicked));
            roomBackgrounds[0].color = DestructiveButtonColor;
            _roomsClearLabel = clearLabels[0];
            UiBuilder.AddButtonRow(page, out Image[] dataBackgrounds,
                (T("ui.measurements.delete"), OnMeasureClearClicked),
                (T("ui.notes.delete"), OnNotesClearClicked));
            foreach (var background in dataBackgrounds)
                background.color = DestructiveButtonColor;
            if (wallLoss != null)
            {
                var row = UiBuilder.AddButtonRow(page, out Image[] pingBackgrounds,
                    (T("ui.pings.delete"), OnPingClearClicked),
                    (T("ui.wall_loss.label"), OnWallLossClicked));
                pingBackgrounds[0].color = DestructiveButtonColor;
                _wallLossLabel = row[1];
                _wallLossCheck = UiBuilder.MakeCheckButton(_wallLossLabel,
                    pingBackgrounds[1], wallLoss.IsVisible);
            }
            else
            {
                UiBuilder.AddButtonRow(page, out Image[] pingBackgrounds,
                    (T("ui.pings.delete"), OnPingClearClicked));
                pingBackgrounds[0].color = DestructiveButtonColor;
            }

            if (floorManager != null)
            {
                UiBuilder.AddHeader(page, T("ui.floors.header"));
                var floorLabels = UiBuilder.AddButtonRow(page,
                    (F("ui.floor.value", LocalizationManager.DisplayFloorLabel(
                        floorManager.CurrentLabel)), OnFloorDropdownClicked),
                    (T("ui.floor.create"), OnFloorCreateClicked));
                _floorLabel = floorLabels[0];
                _floorCreateLabel = floorLabels[1];

                // Collapsible storey list ("dropdown"): filled on open, one
                // button per storey (top first) plus "Alle".
                _floorListGo = new GameObject("FloorList", typeof(RectTransform));
                _floorListGo.transform.SetParent(page, false);
                var listLayout = _floorListGo.AddComponent<VerticalLayoutGroup>();
                listLayout.spacing = 6f;
                listLayout.childControlWidth = true;
                listLayout.childControlHeight = true;
                listLayout.childForceExpandWidth = true;
                listLayout.childForceExpandHeight = false;
                _floorListGo.SetActive(false);

                UiBuilder.AddButtonRow(page,
                    (T("ui.floor.above"), OnFloorAboveClicked),
                    (T("ui.floor.below"), OnFloorBelowClicked));
                UiBuilder.AddButtonRow(page,
                    (T("ui.floor.label_up"), OnLabelShiftUpClicked),
                    (T("ui.floor.label_down"), OnLabelShiftDownClicked));
            }
        }

        private void BuildPageSystem(Transform page)
        {
            UiBuilder.AddHeader(page, T("ui.section.connections"));
            _ipField = UiBuilder.AddTextField(page, T("ui.pc_ip"),
                streamer != null ? streamer.targetIp : "",
                null, OnIpEndEdit);
            if (webServer != null)
            {
                _webLabel = UiBuilder.AddButtonRow(page, out Image[] dashboardBackground,
                    (T("ui.dashboard.label"), OnWebDashboardClicked))[0];
                _dashboardCheck = UiBuilder.MakeCheckButton(_webLabel,
                    dashboardBackground[0], webServer.IsRunning);
                _webUrlText = UiBuilder.AddText(page, "", 18f,
                    FontStyles.Normal, TextAlignmentOptions.Left);
                _webUrlText.color = new Color(0.42f, 0.75f, 1f);
            }
            if (webServer != null && throughputTester != null)
                _throughputLabel = UiBuilder.AddButtonRow(page,
                    (F("ui.throughput.measure", 0), OnThroughputClicked))[0];

            UiBuilder.AddHeader(page, T("ui.section.settings"));
            _languageLabel = UiBuilder.AddButtonRow(page,
                (T("language.switch"), () => LocalizationManager.ToggleLanguage()))[0];
            if (onboarding != null)
            {
                UiBuilder.AddButtonRow(page,
                    (T("ui.tutorial.start"), () => onboarding.StartTutorial()));
            }

            UiBuilder.AddHeader(page, T("ui.controls.header"));
            var help = UiBuilder.AddText(page,
                T("ui.controls.help"),
                18f, FontStyles.Normal, TextAlignmentOptions.TopLeft);
            help.color = new Color(1f, 1f, 1f, 0.75f);

            UiBuilder.AddHeader(page, T("ui.section.storage"));
            _wipeLabel = UiBuilder.AddButtonRow(page, out Image[] wipeBackgrounds,
                (T("ui.scans.delete"), OnWipeClicked))[0];
            wipeBackgrounds[0].color = DestructiveButtonColor;
        }
    }
}
