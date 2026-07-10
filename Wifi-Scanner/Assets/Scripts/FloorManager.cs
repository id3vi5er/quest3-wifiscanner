using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;

namespace WifiScanner
{
    /// <summary>One storey: the world Y of its floor plane (y=0 reference) + a label.</summary>
    [System.Serializable]
    public class FloorLevel
    {
        public float baseY;
        public string label;
    }

    /// <summary>
    /// Multi-storey manager. Keeps an ordered list of floor levels (each with a
    /// base Y). A measurement point belongs to the storey directly below the
    /// headset at capture time (RecordingFloor). The displayed/active storey
    /// auto-switches when the head crosses a floor boundary (e.g. stairs), and
    /// when the head climbs more than <see cref="newFloorThreshold"/> above the
    /// topmost known floor a new storey is suggested (panel button "Etage
    /// anlegen"). The panel also lets you view a specific storey or all at once.
    ///
    /// Consumers poll <see cref="Version"/> (bumped on any change) to refresh.
    /// Created and wired by SceneBootstrap.
    /// </summary>
    public class FloorManager : MonoBehaviour
    {
        [Tooltip("Head height above the top floor's base that suggests a new storey (m).")]
        public float newFloorThreshold = 2f;
        [Tooltip("Assumed eye-to-floor height when creating a storey from head height (m). " +
                 "The base can be corrected later with 'Boden kalibrieren'.")]
        public float assumedEyeHeight = 1.5f;

        private readonly List<FloorLevel> _floors = new List<FloorLevel>();
        public IReadOnlyList<FloorLevel> Floors => _floors;
        public int ActiveIndex { get; private set; }
        public bool ShowAll { get; private set; }
        public bool NewFloorSuggested { get; private set; }
        /// <summary>Bumped on every change so renderers/panel can refresh.</summary>
        public int Version { get; private set; }

        public float ActiveBaseY =>
            _floors.Count > 0 ? _floors[Mathf.Clamp(ActiveIndex, 0, _floors.Count - 1)].baseY : 0f;
        public string CurrentLabel =>
            ShowAll ? "Alle" : (_floors.Count > 0 ? _floors[ActiveIndex].label : "-");

        private XROrigin _origin;
        private int _lastHeadFloor;

        private void Start()
        {
            _origin = FindFirstObjectByType<XROrigin>();
            float baseY = 0f;
            if (FloorHeatmapRenderer.TryGetTrackedFloorY(_origin, out float y))
                baseY = y;
            _floors.Add(new FloorLevel { baseY = baseY, label = "EG" });
            ActiveIndex = 0;
            Version++;
        }

        /// <summary>Storey directly below world height <paramref name="y"/>
        /// (largest base ≤ y), i.e. the floor a point/head sits on.</summary>
        public int FloorIndexForY(float y)
        {
            int idx = 0;
            for (int i = 0; i < _floors.Count; i++)
                if (y >= _floors[i].baseY - 0.001f) idx = i;
            return idx;
        }

        /// <summary>Storey a point recorded at head height <paramref name="headY"/>
        /// belongs to — always the floor directly beneath the Quest.</summary>
        public int RecordingFloor(float headY) => FloorIndexForY(headY);

        private void Update()
        {
            var cam = Camera.main;
            if (cam == null || _floors.Count == 0) return;
            float hy = cam.transform.position.y;

            // Auto-switch the active storey when the head crosses a boundary
            // (edge-triggered, so a manual panel selection persists in between).
            int headFloor = FloorIndexForY(hy);
            if (headFloor != _lastHeadFloor)
            {
                _lastHeadFloor = headFloor;
                ShowAll = false;
                if (headFloor != ActiveIndex) { ActiveIndex = headFloor; Version++; }
            }

            float topBase = _floors[_floors.Count - 1].baseY;
            bool suggest = hy > topBase + newFloorThreshold;
            if (suggest != NewFloorSuggested) { NewFloorSuggested = suggest; Version++; }
        }

        /// <summary>Creates a new storey from the current head height (base =
        /// head − assumed eye height) and switches to it. Correctable later via
        /// "Boden kalibrieren".</summary>
        public int CreateFloorAtHead()
        {
            var cam = Camera.main;
            float topBase = _floors.Count > 0 ? _floors[_floors.Count - 1].baseY : 0f;
            float hy = cam != null ? cam.transform.position.y : topBase + assumedEyeHeight + 2.6f;
            float baseY = hy - assumedEyeHeight;

            var level = new FloorLevel { baseY = baseY };
            _floors.Add(level);
            _floors.Sort((a, b) => a.baseY.CompareTo(b.baseY));
            RelabelFloors();

            ShowAll = false;
            ActiveIndex = _floors.IndexOf(level);
            _lastHeadFloor = ActiveIndex;
            NewFloorSuggested = false;
            Version++;
            Debug.Log($"[FloorManager] Etage angelegt: {level.label} (baseY {baseY:F2} m). " +
                      $"{_floors.Count} Etage(n).");
            return ActiveIndex;
        }

        /// <summary>Sets the active storey's base height (from "Boden kalibrieren").</summary>
        public void SetActiveBaseY(float y)
        {
            if (_floors.Count == 0) return;
            _floors[Mathf.Clamp(ActiveIndex, 0, _floors.Count - 1)].baseY = y;
            _floors.Sort((a, b) => a.baseY.CompareTo(b.baseY));
            RelabelFloors();
            Version++;
        }

        /// <summary>
        /// Replaces all storeys (session load / re-alignment). Never leaves the
        /// list empty: null/empty falls back to a single "EG" at the tracked
        /// floor (old files without floor data).
        /// </summary>
        public void LoadFloors(IReadOnlyList<FloorLevel> floors)
        {
            _floors.Clear();
            if (floors != null)
                foreach (var f in floors)
                    _floors.Add(new FloorLevel { baseY = f.baseY, label = f.label });
            if (_floors.Count == 0)
            {
                float baseY = 0f;
                if (FloorHeatmapRenderer.TryGetTrackedFloorY(_origin, out float y))
                    baseY = y;
                _floors.Add(new FloorLevel { baseY = baseY, label = "EG" });
            }
            _floors.Sort((a, b) => a.baseY.CompareTo(b.baseY));
            RelabelFloors();
            ShowAll = false;
            ActiveIndex = 0;
            _lastHeadFloor = 0; // Update() re-syncs to the head next frame
            NewFloorSuggested = false;
            Version++;
            Debug.Log($"[FloorManager] {_floors.Count} Etage(n) geladen.");
        }

        /// <summary>Panel cycle: EG → OG1 → … → Alle → EG.</summary>
        public void CycleView()
        {
            if (_floors.Count == 0) return;
            if (ShowAll) { ShowAll = false; ActiveIndex = 0; }
            else if (ActiveIndex >= _floors.Count - 1) { ShowAll = true; }
            else ActiveIndex++;
            Version++;
        }

        private void RelabelFloors()
        {
            for (int i = 0; i < _floors.Count; i++)
                _floors[i].label = i == 0 ? "EG" : $"OG{i}";
        }
    }
}
