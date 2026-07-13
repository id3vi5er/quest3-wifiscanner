using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace WifiScanner
{
    /// <summary>One roaming event: where the headset switched access points.</summary>
    [Serializable]
    public class RoamEvent
    {
        public long timestampMs;
        public float posX, posY, posZ;
        public string fromBssid = "";
        public string toBssid = "";
        public int fromFrequencyMhz;
        public int toFrequencyMhz;
    }

    /// <summary>
    /// Watches the connected BSSID (polled every frame by SampleCollector) and
    /// drops a marker at the head position whenever it changes — the classic
    /// trouble spots of mesh networks and band steering become visible in the
    /// room. Marker: small orange sphere + label "…abc12 → …def34" with the
    /// band change. Events are streamed ("roam" packet) and exported to
    /// {stem}_roam.json; the panel (Ansicht tab) toggles the marker display.
    /// Created and wired by SceneBootstrap.
    /// </summary>
    public class RoamingTracker : MonoBehaviour
    {
        [Header("Wired by SceneBootstrap")]
        public SampleCollector collector;
        public NetworkStreamer streamer; // optional
        [Tooltip("Material ASSET (URP/Unlit) cloned + tinted for markers.")]
        public Material visualMaterial;

        [Header("Markers")]
        public float markerDiameter = 0.07f;
        public bool visibleAtStart = true;

        public bool IsVisible { get; private set; }
        public IReadOnlyList<RoamEvent> Events => _events;

        private readonly List<RoamEvent> _events = new List<RoamEvent>();
        private GameObject _root;
        private Material _mat;
        private Mesh _sphereMesh;
        private readonly List<WorldLabel> _labels = new List<WorldLabel>();
        private string _lastBssid = "";
        private int _lastFrequency;
        private float _resendTimer;

        [Serializable]
        private class RoamPacket
        {
            public string type = "roam";
            public List<RoamEvent> events;
        }

        private void Start()
        {
            var temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _sphereMesh = temp.GetComponent<MeshFilter>().sharedMesh;
            Destroy(temp);

            if (visualMaterial != null)
            {
                _mat = new Material(visualMaterial); // asset clone keeps its variant
                _mat.SetColor("_BaseColor", new Color(1f, 0.55f, 0.1f, 1f));
            }
            else
            {
                Debug.LogError("[RoamingTracker] visualMaterial not assigned — " +
                               "assign the point material in SceneBootstrap.");
            }

            _root = new GameObject("RoamingMarkers");
            _root.transform.SetParent(transform, false);
            IsVisible = visibleAtStart;
            _root.SetActive(IsVisible);
        }

        public void Toggle() => SetVisible(!IsVisible);

        public void SetVisible(bool visible)
        {
            IsVisible = visible;
            if (_root != null) _root.SetActive(visible);
        }

        public void ClearEvents()
        {
            _events.Clear();
            _lastBssid = "";
            _lastFrequency = 0;
            _resendTimer = 0f;
            _labels.Clear();
            if (_root != null)
                for (int i = _root.transform.childCount - 1; i >= 0; i--)
                    Destroy(_root.transform.GetChild(i).gameObject);
            SendRoamUpdate();
        }

        private void Update()
        {
            if (collector == null) return;

            string bssid = collector.LastBssid;
            if (!string.IsNullOrEmpty(bssid))
            {
                // First reading only arms the tracker; a CHANGE is a roam.
                if (_lastBssid.Length > 0 && bssid != _lastBssid)
                    AddEvent(_lastBssid, _lastFrequency, bssid,
                        collector.LastFrequency);
                _lastBssid = bssid;
                _lastFrequency = collector.LastFrequency;
            }

            // Periodic re-broadcast so a late viewer still gets the markers.
            if (streamer != null && _events.Count > 0)
            {
                _resendTimer += Time.deltaTime;
                if (_resendTimer >= 3f) SendRoamUpdate();
            }
        }

        private void AddEvent(string fromBssid, int fromFreq, string toBssid,
            int toFreq)
        {
            var cam = Camera.main;
            if (cam == null) return;
            Vector3 pos = cam.transform.position;

            _events.Add(new RoamEvent
            {
                timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                posX = pos.x, posY = pos.y, posZ = pos.z,
                fromBssid = fromBssid, toBssid = toBssid,
                fromFrequencyMhz = fromFreq, toFrequencyMhz = toFreq,
            });

            // Marker sphere + label at the switch position.
            var go = new GameObject("RoamMarker");
            go.transform.SetParent(_root.transform, false);
            go.transform.position = pos;
            go.transform.localScale = Vector3.one * markerDiameter;
            go.AddComponent<MeshFilter>().sharedMesh = _sphereMesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;

            var label = WorldLabel.Create(_root.transform);
            label.SetWorldPosition(pos + Vector3.up * (markerDiameter + 0.04f));
            string bandFrom = Sample.BandLabel(fromFreq);
            string bandTo = Sample.BandLabel(toFreq);
            string bands = bandFrom == bandTo ? bandTo : $"{bandFrom} → {bandTo}";
            label.SetText($"Roaming ({bands})\n{Short(fromBssid)} → {Short(toBssid)}");
            label.SetColor(new Color(1f, 0.65f, 0.25f));
            _labels.Add(label);

            Haptics.TickLeft(); // subtle heads-up on the idle hand
            SendRoamUpdate();
            Debug.Log($"[RoamingTracker] Roaming #{_events.Count}: " +
                      $"{fromBssid} ({bandFrom}) -> {toBssid} ({bandTo}).");
        }

        private static string Short(string bssid) =>
            string.IsNullOrEmpty(bssid) || bssid.Length <= 5
                ? bssid : "…" + bssid.Substring(bssid.Length - 5);

        private void SendRoamUpdate()
        {
            if (streamer == null) return;
            _resendTimer = 0f;
            var packet = new RoamPacket { events = _events };
            streamer.SendRaw(JsonUtility.ToJson(packet));
        }
    }
}
