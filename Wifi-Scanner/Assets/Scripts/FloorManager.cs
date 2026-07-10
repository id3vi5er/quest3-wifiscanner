using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;

namespace WifiScanner
{
    /// <summary>One storey: the world Y of its floor plane plus its logical
    /// level (0 = EG, 1 = OG1, −1 = KG …) from which the label derives. The
    /// level survives export/load; old files without it get levels
    /// reconstructed from their sort order.</summary>
    [System.Serializable]
    public class FloorLevel
    {
        public float baseY;
        public string label;
        public int level;
    }

    /// <summary>
    /// Multi-storey manager. Keeps an ordered list of floor levels (each with
    /// a base Y and a logical level: … KG, EG, OG1 …). A measurement point
    /// belongs to the storey directly below the headset at capture time
    /// (RecordingFloor). The displayed/active storey auto-switches when the
    /// head crosses a floor boundary (e.g. stairs); a new storey is suggested
    /// when the head climbs above the top floor OR descends below the bottom
    /// one (basement). Floors can also be created explicitly above/below and
    /// selected via the panel dropdown; ShiftLabels re-designates which floor
    /// is "EG" (the first scan may well start on the 2nd floor).
    ///
    /// Creating a floor BELOW shifts the indices of existing floors —
    /// <see cref="FloorInserted"/> tells the SampleCollector to remap the
    /// floor tags of already-recorded samples.
    ///
    /// Consumers poll <see cref="Version"/> (bumped on any change) to refresh.
    /// Created and wired by SceneBootstrap.
    /// </summary>
    public class FloorManager : MonoBehaviour
    {
        [Tooltip("Head height above the top floor's base that suggests a new storey (m).")]
        public float newFloorThreshold = 2f;
        [Tooltip("Head depth below the bottom floor's base that suggests a new " +
                 "storey underneath (m).")]
        public float newFloorBelowThreshold = 0.5f;
        [Tooltip("Assumed eye-to-floor height when creating a storey from head height (m). " +
                 "The base can be corrected later with 'Boden kalibrieren'.")]
        public float assumedEyeHeight = 1.5f;
        [Tooltip("Storey height used when creating a floor explicitly while the " +
                 "head is not there yet (m).")]
        public float defaultStoreyHeight = 2.8f;

        /// <summary>Raised AFTER a floor was inserted at this index — indices
        /// of floors at/above it shifted by +1, so sample floor tags must be
        /// remapped (SampleCollector subscribes).</summary>
        public event System.Action<int> FloorInserted;

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
            _floors.Add(new FloorLevel { baseY = baseY, level = 0 });
            RefreshLabels();
            ActiveIndex = 0;
            Version++;
        }

        /// <summary>Label for a logical level: 0 = EG, positive = OGn,
        /// negative = KG / KGn.</summary>
        public static string LabelFor(int level) =>
            level == 0 ? "EG"
            : level > 0 ? $"OG{level}"
            : level == -1 ? "KG"
            : $"KG{-level}";

        private void RefreshLabels()
        {
            foreach (var f in _floors)
                f.label = LabelFor(f.level);
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

            // Suggest a new storey above the top floor (climbed the stairs) or
            // BELOW the bottom one (went down into an unknown basement).
            float topBase = _floors[_floors.Count - 1].baseY;
            float bottomBase = _floors[0].baseY;
            bool suggest = hy > topBase + newFloorThreshold
                || hy < bottomBase - newFloorBelowThreshold;
            if (suggest != NewFloorSuggested) { NewFloorSuggested = suggest; Version++; }
        }

        /// <summary>Creates a new storey from the current head height (base =
        /// head − assumed eye height) and switches to it — works above AND
        /// below the known floors (basement). Correctable later via
        /// "Boden kalibrieren".</summary>
        public int CreateFloorAtHead()
        {
            var cam = Camera.main;
            float topBase = _floors.Count > 0 ? _floors[_floors.Count - 1].baseY : 0f;
            float hy = cam != null ? cam.transform.position.y : topBase + assumedEyeHeight + 2.6f;
            return InsertFloor(hy - assumedEyeHeight);
        }

        /// <summary>Explicitly creates the storey ABOVE the top floor (uses the
        /// head height when the user already stands up there).</summary>
        public int CreateFloorAbove()
        {
            float topBase = _floors.Count > 0 ? _floors[_floors.Count - 1].baseY : 0f;
            var cam = Camera.main;
            float baseY = cam != null &&
                          cam.transform.position.y - assumedEyeHeight > topBase + 1f
                ? cam.transform.position.y - assumedEyeHeight
                : topBase + defaultStoreyHeight;
            return InsertFloor(baseY);
        }

        /// <summary>Explicitly creates the storey BELOW the bottom floor
        /// (Keller) — head-based when the user already stands down there.</summary>
        public int CreateFloorBelow()
        {
            float bottomBase = _floors.Count > 0 ? _floors[0].baseY : 0f;
            var cam = Camera.main;
            float baseY = cam != null &&
                          cam.transform.position.y - assumedEyeHeight < bottomBase - 1f
                ? cam.transform.position.y - assumedEyeHeight
                : bottomBase - defaultStoreyHeight;
            return InsertFloor(baseY);
        }

        // Core insertion: the logical level derives from the vertical position;
        // inserting anywhere but on top shifts existing indices, so subscribers
        // (SampleCollector) remap their floor tags via FloorInserted.
        private int InsertFloor(float baseY)
        {
            var level = new FloorLevel { baseY = baseY };
            if (_floors.Count == 0)
                level.level = 0;
            else if (baseY > _floors[_floors.Count - 1].baseY)
                level.level = _floors[_floors.Count - 1].level + 1;
            else if (baseY < _floors[0].baseY)
                level.level = _floors[0].level - 1;
            else
            {
                // Between two known floors (unusual): take the slot above the
                // lower neighbour and push everything higher up one level.
                int below = FloorIndexForY(baseY);
                level.level = _floors[below].level + 1;
                for (int i = below + 1; i < _floors.Count; i++)
                    _floors[i].level++;
            }
            _floors.Add(level);
            _floors.Sort((a, b) => a.baseY.CompareTo(b.baseY));
            RefreshLabels();

            int index = _floors.IndexOf(level);
            if (index < _floors.Count - 1)
                FloorInserted?.Invoke(index); // indices above shifted by +1

            ShowAll = false;
            ActiveIndex = index;
            _lastHeadFloor = ActiveIndex;
            NewFloorSuggested = false;
            Version++;
            Debug.Log($"[FloorManager] Etage angelegt: {level.label} " +
                      $"(baseY {baseY:F2} m). {_floors.Count} Etage(n).");
            return ActiveIndex;
        }

        /// <summary>Sets the active storey's base height (from "Boden
        /// kalibrieren"). Deliberately does NOT re-sort: a corrected base must
        /// not swap floor indices (recorded samples reference them).</summary>
        public void SetActiveBaseY(float y)
        {
            if (_floors.Count == 0) return;
            _floors[Mathf.Clamp(ActiveIndex, 0, _floors.Count - 1)].baseY = y;
            Version++;
        }

        /// <summary>Panel dropdown: activates one storey (manual selection
        /// persists until the head crosses the next floor boundary).</summary>
        public void SelectFloor(int index)
        {
            if (index < 0 || index >= _floors.Count) return;
            ShowAll = false;
            ActiveIndex = index;
            var cam = Camera.main;
            if (cam != null)
                _lastHeadFloor = FloorIndexForY(cam.transform.position.y);
            Version++;
        }

        /// <summary>Panel dropdown: show every storey at once.</summary>
        public void ShowAllFloors()
        {
            ShowAll = true;
            Version++;
        }

        /// <summary>Shifts ALL logical levels by ±1 — declares e.g. the
        /// starting floor to really be "OG2" (first scan on the 2nd floor)
        /// without moving any geometry.</summary>
        public void ShiftLabels(int delta)
        {
            foreach (var f in _floors) f.level += delta;
            RefreshLabels();
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
                    _floors.Add(new FloorLevel
                    { baseY = f.baseY, label = f.label, level = f.level });
            if (_floors.Count == 0)
            {
                float baseY = 0f;
                if (FloorHeatmapRenderer.TryGetTrackedFloorY(_origin, out float y))
                    baseY = y;
                _floors.Add(new FloorLevel { baseY = baseY, level = 0 });
            }
            _floors.Sort((a, b) => a.baseY.CompareTo(b.baseY));

            // Files from before the level field carry level 0 everywhere —
            // reconstruct from the sort order (bottom floor was "EG").
            bool allZero = true;
            foreach (var f in _floors)
                if (f.level != 0) { allZero = false; break; }
            if (allZero && _floors.Count > 1)
                for (int i = 0; i < _floors.Count; i++)
                    _floors[i].level = i;

            RefreshLabels();
            ShowAll = false;
            ActiveIndex = 0;
            _lastHeadFloor = 0; // Update() re-syncs to the head next frame
            NewFloorSuggested = false;
            Version++;
            Debug.Log($"[FloorManager] {_floors.Count} Etage(n) geladen.");
        }

        /// <summary>Panel cycle: unterste → … → oberste → Alle → unterste.</summary>
        public void CycleView()
        {
            if (_floors.Count == 0) return;
            if (ShowAll) { ShowAll = false; ActiveIndex = 0; }
            else if (ActiveIndex >= _floors.Count - 1) { ShowAll = true; }
            else ActiveIndex++;
            Version++;
        }
    }
}
