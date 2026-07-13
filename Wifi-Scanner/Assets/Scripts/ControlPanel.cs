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
        private TextMeshProUGUI _throughputLabel;
        private TextMeshProUGUI _throughputVisibilityLabel;
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
            _menuAction?.Disable();
            _posAction?.Disable();
            _rotAction?.Disable();
            _gripAction?.Disable();
            _dragging = false;
            XrUiHover.Clear(this);
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
                ShowThroughputInfo("Web-Dashboard ist aus", 4f);
                return;
            }
            if (!webServer.RequestBrowserThroughputTest())
            {
                ShowThroughputInfo("Erst Webinterface öffnen", 5f);
                return;
            }

            _throughputPendingCount = throughputTester != null
                ? throughputTester.NodeCount : 0;
            _throughputClaimUntil = Time.unscaledTime + 8f;
            _throughputRequestUntil = Time.unscaledTime + 90f;
            _throughputInfoText = "Browser wird angefragt ...";
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
                UndoStack.Push($"{snapshot.Count} Punkte gelöscht",
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
                _newScanLabel.text = "<color=#FFB86B>Export vorher?</color>";
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
                    _newScanInfoText = "Export fehlgeschlagen – Daten behalten";
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
            _newScanInfoText = exportFirst
                ? "Exportiert · neuer Scan bereit (Pause)"
                : "Neuer Scan bereit (Pause)";
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
            _wipeInfoText = $"{count} Datei(en) gelöscht";
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
                ? $"{(collector != null ? collector.SampleCount : 0)} Punkte geladen"
                : "Laden fehlgeschlagen";
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
                UndoStack.Push("Räume gelöscht", () => roomTracer.LoadRooms(snapshot));
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
            _audioLabel.text = geiger.IsOn ? "Audio: an" : "Audio: aus";
        }

        private void OnOcclusionClicked()
        {
            if (occlusion == null) return;
            occlusion.ToggleOcclusion(); // dynamische Tiefensensor-Occlusion
            _occlusionLabel.text = occlusion.OcclusionEnabled
                ? "Occlusion: an" : "Occlusion: aus";
        }

        private void OnMinimapClicked()
        {
            if (minimap == null) return;
            minimap.Toggle();
            _minimapLabel.text = minimap.IsVisible ? "Minimap: an" : "Minimap: aus";
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

            AddListEntry(_floorListGo.transform, "Alle Etagen",
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
                    $"{floors[i].label}   ({floors[i].baseY:F1} m)",
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
        }

        // One button per known AP (order of first appearance) + "Alle APs".
        private void RebuildApList()
        {
            if (_apListGo == null || collector == null) return;
            var bssids = collector.KnownBssids;
            _apListCount = bssids.Count;
            foreach (Transform child in _apListGo.transform)
                Destroy(child.gameObject);

            AddListEntry(_apListGo.transform, $"Alle APs ({bssids.Count})",
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
                UndoStack.Push("Messungen gelöscht",
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
                UndoStack.Push("Notizen gelöscht", () => spatialNotes.LoadNotes(snapshot));
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
                UndoStack.Push("Ping-Knoten gelöscht", () => pingTester.LoadNodes(snapshot));
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
            _filterLabel.text = $"Filter: {_filterMin} … {_filterMax} dBm";
        }

        private void RefreshStats()
        {
            if (_statsText == null || collector == null) return;

            string rec = collector.IsRecording
                ? "<color=#7CFC7C>● REC</color>"
                : "<color=#FF6B6B>■ PAUSE</color>";
            int secs = Mathf.RoundToInt(collector.RecordingSeconds);
            string stats = collector.TryGetRssiStats(out int min, out int max, out float avg)
                ? $"Min {min} / Max {max} / Ø {avg:F0} dBm"
                : "Min – / Max – / Ø – dBm";

            _statsText.text =
                $"{rec}   {secs / 60:D2}:{secs % 60:D2}   " +
                $"Ansicht: {(modeController != null ? modeController.ModeLabel : "-")}\n" +
                $"Punkte: {collector.SampleCount}    " +
                $"RSSI: {collector.LastRssi} dBm\n" +
                $"{stats}\n" +
                $"SSID: {collector.LastSsid}";

            if (_modeLabel != null && modeController != null)
                _modeLabel.text = $"Ansicht: {modeController.ModeLabel}";
            if (_recLabel != null)
                _recLabel.text = collector.IsRecording ? "❚❚ Pause" : "● Aufnahme";
            if (_floorCalLabel != null && roomTracer != null)
                _floorCalLabel.text = roomTracer.IsCalibratingFloor
                    ? "Cursor an Boden,\nTrigger drücken"
                    : "Boden kalibrieren";
            if (_roomsClearLabel != null)
                _roomsClearLabel.text = Time.unscaledTime < _roomsClearArmedUntil
                    ? "<color=#FF6B6B>Sicher? Nochmal</color>"
                    : "Räume löschen";
            if (_roomNameLabel != null && roomTracer != null)
                _roomNameLabel.text = roomTracer.IsNamingRoom
                    ? "Raum anvisieren,\nTrigger = Tastatur"
                    : "Raum benennen";
            if (_occlusionLabel != null && occlusion != null)
                _occlusionLabel.text = occlusion.OcclusionEnabled
                    ? "Occlusion: an" : "Occlusion: aus";
            if (_minimapLabel != null && minimap != null)
                _minimapLabel.text = minimap.IsVisible ? "Minimap: an" : "Minimap: aus";
            if (_gridLabel != null && gridCalibrator != null)
                _gridLabel.text = gridCalibrator.IsArmed
                    ? "Grips halten,\nloslassen = fertig"
                    : (floor != null && Mathf.Abs(floor.gridYawDegrees) > 0.05f
                        ? $"Grid ausrichten ({floor.gridYawDegrees:F0}°)"
                        : "Grid ausrichten");
            if (_floorLabel != null && floorManager != null)
                _floorLabel.text = $"Etage: {floorManager.CurrentLabel} ▾";
            if (_floorCreateLabel != null && floorManager != null)
                _floorCreateLabel.text = floorManager.NewFloorSuggested
                    ? "<color=#7CFC7C>Neue Etage!</color>"
                    : "Etage anlegen";
            // Open dropdown follows floor changes (creation, auto-switch).
            if (_floorListGo != null && _floorListGo.activeSelf &&
                floorManager != null && floorManager.Version != _floorListVersion)
                RebuildFloorList();
            if (_webLabel != null && webServer != null)
                _webLabel.text = webServer.IsRunning
                    ? "Web-Dashboard: an" : "Web-Dashboard: aus";
            if (_webUrlText != null && webServer != null)
                _webUrlText.text = webServer.IsRunning
                    ? webServer.Url + "  (nur lokales Netz)" : "(aus)";
            if (_floorPlanLabel != null && minimap != null)
                _floorPlanLabel.text = minimap.showFloorPlan
                    ? "Grundriss: an" : "Grundriss: aus";
            if (_mapRotateLabel != null && minimap != null)
                _mapRotateLabel.text = minimap.rotateWithHead
                    ? "Karte: dreht mit Blick" : "Karte: Norden fest";
            if (_apLabel != null && apLocator != null)
                _apLabel.text = apLocator.IsVisible
                    ? "AP-Standort: an" : "AP-Standort: aus";
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
                        _bssidLabel.text = $"AP: Alle ({bssids.Count}) ▾";
                    }
                    else
                    {
                        string ssid = string.IsNullOrEmpty(selected.ssid)
                            ? "?" : selected.ssid;
                        if (ssid.Length > 10) ssid = ssid.Substring(0, 9) + "…";
                        _bssidLabel.text = $"AP: {ssid} · " +
                            $"{Sample.BandLabel(selected.frequencyMhz)} ▾";
                    }
                }
                else
                {
                    _bssidLabel.text = $"AP: Alle ({bssids.Count}) ▾";
                }
                // Open dropdown follows new networks appearing.
                if (_apListGo != null && _apListGo.activeSelf &&
                    bssids.Count != _apListCount)
                    RebuildApList();
            }
            if (_roamLabel != null && roaming != null)
                _roamLabel.text = roaming.IsVisible
                    ? $"Roaming: an ({roaming.Events.Count})"
                    : "Roaming: aus";
            if (_multiApLabel != null && multiAp != null)
                _multiApLabel.text = multiAp.StatusLabel;
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
                    ShowThroughputInfo("Browser reagiert nicht - neu laden", 7f);
                }
                else if (_throughputPendingCount >= 0 && webServer != null &&
                         webServer.BrowserThroughputTestActive)
                {
                    _throughputInfoText = "Durchsatz: Test läuft ...";
                }
                else if (_throughputPendingCount >= 0 &&
                         Time.unscaledTime >= _throughputRequestUntil)
                {
                    ShowThroughputInfo("Durchsatz: kein Ergebnis", 5f);
                }

                _throughputLabel.text = Time.unscaledTime < _throughputInfoUntil
                    ? _throughputInfoText
                    : $"Durchsatz messen ({(throughputTester != null ? throughputTester.NodeCount : 0)})";
            }
            if (_throughputVisibilityLabel != null && throughputTester != null)
                _throughputVisibilityLabel.text = throughputTester.IsVisible
                    ? $"Durchsatzpunkte: an ({throughputTester.NodeCount})"
                    : $"Durchsatzpunkte: aus ({throughputTester.NodeCount})";
            if (_coverageLabel != null && coverage != null)
                _coverageLabel.text = !coverage.IsVisible
                    ? "Prognose: aus"
                    : (coverage.StatusHint.Length > 0
                        ? "<color=#FFB86B>Prognose: fehlt Basis</color>"
                        : "Prognose: an");
            if (_wallLossLabel != null && wallLoss != null)
                _wallLossLabel.text = !wallLoss.IsVisible
                    ? "Wand-Dämpfung: aus"
                    : (apLocator != null && apLocator.HasFit
                        ? $"Wand-Dämpfung ({wallLoss.Losses.Count})"
                        : "Wand-Dämpfung (braucht\nAP-Fit + Messungen)");
            if (_wipeLabel != null)
            {
                if (Time.unscaledTime < _wipeInfoUntil)
                    _wipeLabel.text = _wipeInfoText;
                else if (Time.unscaledTime < _wipeArmedUntil)
                    _wipeLabel.text = "<color=#FF6B6B>Sicher? Nochmal klicken</color>";
                else
                    _wipeLabel.text = "Scans löschen (Gerät)";
            }
            if (_scanStemLabel != null && sessionLoader != null)
                _scanStemLabel.text = $"Scan: {ShortStem(sessionLoader.CurrentStem)}";
            if (_diffStemLabel != null)
                _diffStemLabel.text = "Vergleich: " + ShortStem(
                    _diffIndex < _diffStems.Count ? _diffStems[_diffIndex] : null);
            if (_diffToggleLabel != null && floor != null)
                _diffToggleLabel.text = floor.DiffEnabled
                    ? $"<color=#7CFC7C>Diff: {ShortStem(floor.DiffStem)}</color>"
                    : "Diff: aus";
            if (_scanLoadLabel != null)
            {
                if (Time.unscaledTime < _loadInfoUntil)
                    _scanLoadLabel.text = _loadInfoText;
                else if (Time.unscaledTime < _loadArmedUntil)
                    _scanLoadLabel.text = "<color=#FF6B6B>Sicher? Ersetzt!</color>";
                else
                    _scanLoadLabel.text = "Laden";
            }
            if (_newScanLabel != null)
            {
                if (Time.unscaledTime < _newScanInfoUntil)
                    _newScanLabel.text = _newScanInfoText;
                else if (_newScanPromptGo != null && _newScanPromptGo.activeSelf)
                    _newScanLabel.text = "<color=#FFB86B>Export vorher?</color>";
                else
                    _newScanLabel.text = "Neuer Scan";
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
            var rootRt = (RectTransform)_root.transform;
            rootRt.sizeDelta = new Vector2(480f, 920f); // tallest page: Ansicht
            _root.transform.localScale = Vector3.one * 0.0008f; // 0.38 x 0.69 m
            _root.AddComponent<GraphicRaycaster>();            // editor mouse
            _root.AddComponent<TrackedDeviceGraphicRaycaster>(); // XR rays

            var bg = new GameObject("Background", typeof(RectTransform)).AddComponent<Image>();
            bg.transform.SetParent(_root.transform, false);
            bg.color = new Color(0.05f, 0.05f, 0.08f, 0.85f);
            UiBuilder.Stretch(bg.rectTransform);

            var content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(_root.transform, false);
            UiBuilder.Stretch((RectTransform)content.transform);
            var layout = content.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(20, 20, 18, 18);
            layout.spacing = 8f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            // ── Fixed header: title + live stats + tab bar ────────────
            UiBuilder.AddText(content.transform, "WiFi-Scanner", 30f,
                FontStyles.Bold, TextAlignmentOptions.Center);
            _statsText = UiBuilder.AddText(content.transform, "…", 22f,
                FontStyles.Normal, TextAlignmentOptions.TopLeft);

            var tabLabels = UiBuilder.AddButtonRow(content.transform, out _tabImages,
                ("Aufnahme", () => SelectTab(0)),
                ("Ansicht", () => SelectTab(1)),
                ("Räume", () => SelectTab(2)),
                ("System", () => SelectTab(3)));
            foreach (var label in tabLabels)
                label.fontSize = 19f; // four tabs share one row

            _pages[0] = AddPage(content.transform, "PageAufnahme");
            _pages[1] = AddPage(content.transform, "PageAnsicht");
            _pages[2] = AddPage(content.transform, "PageRaeume");
            _pages[3] = AddPage(content.transform, "PageSystem");
            BuildPageAufnahme(_pages[0].transform);
            BuildPageAnsicht(_pages[1].transform);
            BuildPageRaeume(_pages[2].transform);
            BuildPageSystem(_pages[3].transform);
            SelectTab(0);
        }

        // One tab page: its own vertical stack, toggled as a whole by SelectTab.
        private static GameObject AddPage(Transform parent, string name)
        {
            var page = new GameObject(name, typeof(RectTransform));
            page.transform.SetParent(parent, false);
            var layout = page.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 8f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            return page;
        }

        private void BuildPageAufnahme(Transform page)
        {
            UiBuilder.AddTextField(page, "Export-Name (optional)",
                collector != null ? collector.exportName : "",
                value => { if (collector != null) collector.exportName = value; },
                null);
            _recLabel = UiBuilder.AddButtonRow(page,
                ("❚❚ Pause", OnRecordClicked),
                ("Export", OnExportClicked))[0];
            if (sessionLoader != null)
            {
                var loadLabels = UiBuilder.AddButtonRow(page,
                    ("Scan: –", OnScanCycleClicked),
                    ("Laden", OnScanLoadClicked));
                _scanStemLabel = loadLabels[0];
                _scanLoadLabel = loadLabels[1];
            }
            var resetLabels = UiBuilder.AddButtonRow(page,
                ("Punkte löschen", OnClearClicked),
                ("Neuer Scan", OnNewScanClicked));
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
                "Aktuelle Session vorher exportieren?", 18f,
                FontStyles.Bold, TextAlignmentOptions.Center);
            promptText.gameObject.AddComponent<LayoutElement>().preferredHeight = 30f;
            UiBuilder.AddButtonRow(_newScanPromptGo.transform,
                ("Export + Neu", () => CompleteNewScan(exportFirst: true)),
                ("Ohne Export", () => CompleteNewScan(exportFirst: false)),
                ("Abbrechen", () => CancelNewScanPrompt(resumeRecording: true)));
            _newScanPromptGo.SetActive(false);
            if (multiAp != null)
                _multiApLabel = UiBuilder.AddButtonRow(page,
                    ("Multi-AP-Scan: an", OnMultiApClicked))[0];
            if (webServer != null && throughputTester != null)
                _throughputLabel = UiBuilder.AddButtonRow(page,
                    ("Durchsatz messen (0)", OnThroughputClicked))[0];
            if (floor != null)
            {
                var diffLabels = UiBuilder.AddButtonRow(page,
                    ("Vergleich: –", OnDiffCycleClicked),
                    ("Diff: aus", OnDiffToggleClicked));
                _diffStemLabel = diffLabels[0];
                _diffToggleLabel = diffLabels[1];
            }
            var note = UiBuilder.AddText(page,
                "Autosave läuft mit — Export schreibt CSV/JSON/PNG/SVG/OBJ.\n" +
                "Diff färbt die Heatmap: grün = besser als der Vergleichs-Scan, " +
                "rot = schlechter (Scan vorher ausrichten).",
                16f, FontStyles.Normal, TextAlignmentOptions.TopLeft);
            note.color = new Color(1f, 1f, 1f, 0.45f);
        }

        private void BuildPageAnsicht(Transform page)
        {
            var vizLabels = UiBuilder.AddButtonRow(page,
                ("Ansicht: Beides", OnModeClicked),
                ("Occlusion: aus", OnOcclusionClicked));
            _modeLabel = vizLabels[0];
            _occlusionLabel = vizLabels[1];
            var mapLabels = UiBuilder.AddButtonRow(page,
                ("Minimap: an", OnMinimapClicked),
                ("Grundriss: an", OnFloorPlanClicked));
            _minimapLabel = mapLabels[0];
            _floorPlanLabel = mapLabels[1];
            var mapApLabels = UiBuilder.AddButtonRow(page,
                ("Karte: Norden fest", OnMapRotateClicked),
                ("AP-Standort: aus", OnApLocatorClicked));
            _mapRotateLabel = mapApLabels[0];
            _apLabel = mapApLabels[1];
            var bssidLabels = UiBuilder.AddButtonRow(page,
                ("AP: Alle ▾", OnApDropdownClicked),
                ("Roaming: an", OnRoamingClicked));
            _bssidLabel = bssidLabels[0];
            _roamLabel = bssidLabels[1];
            if (throughputTester != null)
                _throughputVisibilityLabel = UiBuilder.AddButtonRow(page,
                    ("Durchsatzpunkte: an (0)", OnThroughputVisibilityClicked))[0];

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
                    ("Audio: aus", OnAudioClicked),
                    ("Prognose: aus", OnCoverageClicked));
                _audioLabel = extraRow[0];
                _coverageLabel = extraRow[1];
            }
            else if (geiger != null)
            {
                _audioLabel = UiBuilder.AddButtonRow(page,
                    ("Audio: aus", OnAudioClicked))[0];
            }
            else if (coverage != null)
            {
                _coverageLabel = UiBuilder.AddButtonRow(page,
                    ("Prognose: aus", OnCoverageClicked))[0];
            }
            float startOpacity = cloud != null ? cloud.opacity : 0.6f;
            UiBuilder.AddSliderRow(page, "Deckkraft", 0f, 1f, startOpacity,
                false, OnOpacityChanged);

            UiBuilder.AddHeader(page, "Filter (dBm)");
            UiBuilder.AddLegend(page);
            _filterLabel = UiBuilder.AddText(page,
                $"Filter: {_filterMin} … {_filterMax} dBm", 22f,
                FontStyles.Normal, TextAlignmentOptions.Left);
            UiBuilder.AddSliderRow(page, null, FilterRangeMin, FilterRangeMax,
                _filterMin, true, OnFilterMinChanged);
            UiBuilder.AddSliderRow(page, null, FilterRangeMin, FilterRangeMax,
                _filterMax, true, OnFilterMaxChanged);
        }

        private void BuildPageRaeume(Transform page)
        {
            var traceLabels = UiBuilder.AddButtonRow(page,
                ("Grid ausrichten", OnGridCalibrateClicked),
                ("Boden kalibrieren", OnFloorCalibrateClicked));
            _gridLabel = traceLabels[0];
            _floorCalLabel = traceLabels[1];
            var clearLabels = UiBuilder.AddButtonRow(page,
                ("Räume löschen", OnRoomsClearClicked),
                ("Öffnung entfernen", OnOpeningRemoveClicked));
            _roomsClearLabel = clearLabels[0];
            _roomNameLabel = UiBuilder.AddButtonRow(page,
                ("Raum benennen", OnRoomNameClicked))[0];
            UiBuilder.AddButtonRow(page,
                ("Messungen löschen", OnMeasureClearClicked),
                ("Notizen löschen", OnNotesClearClicked));
            if (wallLoss != null)
            {
                var row = UiBuilder.AddButtonRow(page,
                    ("Ping löschen", OnPingClearClicked),
                    ("Wand-Dämpfung: aus", OnWallLossClicked));
                _wallLossLabel = row[1];
            }
            else
            {
                UiBuilder.AddButtonRow(page, ("Ping löschen", OnPingClearClicked));
            }

            if (floorManager != null)
            {
                UiBuilder.AddHeader(page, "Etagen");
                var floorLabels = UiBuilder.AddButtonRow(page,
                    ("Etage: EG ▾", OnFloorDropdownClicked),
                    ("Etage anlegen", OnFloorCreateClicked));
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
                    ("▲ Etage darüber", OnFloorAboveClicked),
                    ("▼ Etage darunter", OnFloorBelowClicked));
                UiBuilder.AddButtonRow(page,
                    ("Bezeichnung ▲", OnLabelShiftUpClicked),
                    ("Bezeichnung ▼", OnLabelShiftDownClicked));
            }
        }

        private void BuildPageSystem(Transform page)
        {
            _ipField = UiBuilder.AddTextField(page, "PC-IP für Live-Stream",
                streamer != null ? streamer.targetIp : "",
                null, OnIpEndEdit);
            if (webServer != null)
            {
                _webLabel = UiBuilder.AddButtonRow(page,
                    ("Web-Dashboard: an", OnWebDashboardClicked))[0];
                _webUrlText = UiBuilder.AddText(page, "", 18f,
                    FontStyles.Normal, TextAlignmentOptions.Left);
                _webUrlText.color = new Color(0.42f, 0.75f, 1f);
            }
            if (onboarding != null)
            {
                var sysRow = UiBuilder.AddButtonRow(page,
                    ("Scans löschen (Gerät)", OnWipeClicked),
                    ("Tutorial starten", () => onboarding.StartTutorial()));
                _wipeLabel = sysRow[0];
            }
            else
            {
                _wipeLabel = UiBuilder.AddButtonRow(page,
                    ("Scans löschen (Gerät)", OnWipeClicked))[0];
            }

            UiBuilder.AddHeader(page, "Steuerung");
            var help = UiBuilder.AddText(page,
                "A   Aufnahme Pause/Weiter\n" +
                "B   Export\n" +
                "L-Stick ↑/↓   Deckkraft\n" +
                "R-Stick ↑/↓   Minimap-Zoom / Bohrtiefe\n" +
                "R-Stick-Klick   Ansicht wechseln\n" +
                "Y (links)   Minimap an/aus\n" +
                "Menü (links)   dieses Menü\n" +
                "R-Grip über Menü   Menü greifen/verschieben\n" +
                "Werkzeuge: Palette am linken Handgelenk —\n" +
                "Öffnung/Notiz/Ping halten = Optionen;\n" +
                "aktives Werkzeug belegt Trigger/A/B.",
                18f, FontStyles.Normal, TextAlignmentOptions.TopLeft);
            help.color = new Color(1f, 1f, 1f, 0.75f);
        }
    }
}
