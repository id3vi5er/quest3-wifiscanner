using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace WifiScanner
{
    [Serializable]
    public class ThroughputSample
    {
        public long timestampMs;
        public float posX, posY, posZ;
        public int floor;
        public float questToBrowserMbps;
        public float browserToQuestMbps;
        public float latencyMs;
        public string client = "";
        public string ssid = "";
        public string bssid = "";
        public int frequencyMhz;
    }

    /// <summary>Stores and renders local browser-assisted throughput results.
    /// The traffic itself is served by WebDashboardServer; this component only
    /// touches Unity objects on the main thread.</summary>
    public class ThroughputTester : MonoBehaviour
    {
        public NetworkStreamer streamer;
        public FloorManager floorManager;
        public SampleCollector collector;
        public Material visualMaterial;
        public float markerDiameter = 0.075f;

        public IReadOnlyList<ThroughputSample> Samples => _samples;
        public int NodeCount => _samples.Count;
        public bool IsVisible => _visible;

        private readonly List<ThroughputSample> _samples = new List<ThroughputSample>();
        private readonly List<GameObject> _markers = new List<GameObject>();
        private readonly List<WorldLabel> _labels = new List<WorldLabel>();
        private GameObject _root;
        private Mesh _sphereMesh;
        private Material _good, _ok, _bad, _highlight;
        private bool _visible = true;

        [Serializable]
        private class ThroughputPacket
        {
            public string type = "throughput";
            public List<ThroughputSample> nodes;
        }

        private void Start()
        {
            var temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _sphereMesh = temp.GetComponent<MeshFilter>().sharedMesh;
            Destroy(temp);
            if (visualMaterial != null)
            {
                _good = Make(new Color(0.2f, 1f, 0.3f));
                _ok = Make(new Color(1f, 0.8f, 0.1f));
                _bad = Make(new Color(1f, 0.25f, 0.2f));
                _highlight = Make(new Color(1f, 1f, 1f));
            }
            _root = new GameObject("ThroughputNodes");
            _root.transform.SetParent(transform, false);
            _root.SetActive(_visible);
        }

        private Material Make(Color color)
        {
            var m = new Material(visualMaterial);
            m.SetColor("_BaseColor", color);
            return m;
        }

        public void AddResult(Vector3 pos, float down, float up, float latency,
            string client)
        {
            int floor = floorManager != null
                ? floorManager.RecordingFloor(pos.y) : 0;
            var s = new ThroughputSample
            {
                timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                posX = pos.x, posY = pos.y, posZ = pos.z,
                floor = floor,
                questToBrowserMbps = Mathf.Max(0f, down),
                browserToQuestMbps = Mathf.Max(0f, up),
                latencyMs = Mathf.Max(0f, latency),
                client = client ?? "",
                ssid = collector != null ? collector.LastSsid : "",
                bssid = collector != null ? collector.LastBssid : "",
                frequencyMhz = collector != null ? collector.LastFrequency : 0,
            };
            _samples.Add(s);
            CreateVisual(s);
            SendUpdate();
            Haptics.PlaceRight();
            Debug.Log($"[Throughput] Quest→Browser {down:F1}, Browser→Quest " +
                      $"{up:F1} Mbit/s bei ({pos.x:F1}, {pos.z:F1}).");
        }

        private void CreateVisual(ThroughputSample s)
        {
            if (_root == null) return;
            var go = new GameObject("ThroughputNode");
            go.transform.SetParent(_root.transform, false);
            go.transform.position = new Vector3(s.posX, s.posY, s.posZ);
            go.transform.localScale = Vector3.one * markerDiameter;
            go.AddComponent<MeshFilter>().sharedMesh = _sphereMesh;
            var mr = go.AddComponent<MeshRenderer>();
            float weakest = Mathf.Min(s.questToBrowserMbps, s.browserToQuestMbps);
            mr.sharedMaterial = weakest >= 150f ? _good : weakest >= 50f ? _ok : _bad;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            _markers.Add(go);

            var label = WorldLabel.Create(_root.transform);
            label.SetWorldPosition(go.transform.position + Vector3.up * 0.12f);
            // Keep this label ASCII-only: the compact Quest TMP font does not
            // contain the former directional arrow glyphs and rendered boxes.
            label.SetText($"Q>PC  {s.questToBrowserMbps:F0} Mbit/s\n" +
                          $"PC>Q  {s.browserToQuestMbps:F0} Mbit/s");
            label.SetColor(weakest >= 150f ? new Color(0.2f, 1f, 0.3f)
                : weakest >= 50f ? new Color(1f, 0.8f, 0.1f)
                : new Color(1f, 0.3f, 0.25f));
            _labels.Add(label);
        }

        public void ClearNodes()
        {
            foreach (var go in _markers) if (go != null) Destroy(go);
            foreach (var label in _labels) if (label != null) Destroy(label.gameObject);
            _markers.Clear(); _labels.Clear(); _samples.Clear();
            SendUpdate();
        }

        public void ToggleVisible() => SetVisible(!_visible);

        public void SetVisible(bool visible)
        {
            _visible = visible;
            if (_root != null) _root.SetActive(visible);
        }

        public void OnFloorInserted(int atIndex)
        {
            foreach (var s in _samples)
                if (s.floor >= atIndex) s.floor++;
            SendUpdate();
        }

        public void LoadNodes(IReadOnlyList<ThroughputSample> loaded)
        {
            ClearNodes();
            if (loaded != null)
                foreach (var source in loaded)
                {
                    var s = Copy(source);
                    _samples.Add(s);
                    CreateVisual(s);
                }
            SendUpdate();
        }

        private static ThroughputSample Copy(ThroughputSample s) => new ThroughputSample
        {
            timestampMs = s.timestampMs,
            posX = s.posX, posY = s.posY, posZ = s.posZ, floor = s.floor,
            questToBrowserMbps = s.questToBrowserMbps,
            browserToQuestMbps = s.browserToQuestMbps,
            latencyMs = s.latencyMs, client = s.client,
            ssid = s.ssid, bssid = s.bssid, frequencyMhz = s.frequencyMhz,
        };

        // ---- Delete-mode support ----

        public bool TryGetNodePos(int i, out Vector3 pos)
        {
            pos = Vector3.zero;
            if (!_visible || i < 0 || i >= _samples.Count) return false;
            var s = _samples[i];
            pos = new Vector3(s.posX, s.posY, s.posZ);
            return true;
        }

        public void SetHighlight(int i, bool on)
        {
            if (i < 0 || i >= _samples.Count || i >= _markers.Count) return;
            var renderer = _markers[i] != null
                ? _markers[i].GetComponent<MeshRenderer>() : null;
            if (renderer == null) return;
            renderer.sharedMaterial = on ? _highlight : MaterialFor(_samples[i]);
        }

        public void RemoveNode(int i)
        {
            if (i < 0 || i >= _samples.Count) return;
            var snapshot = new List<ThroughputSample>(_samples.Count);
            foreach (var s in _samples) snapshot.Add(Copy(s));
            UndoStack.Push("Durchsatzpunkt gelöscht", () => LoadNodes(snapshot));

            if (i < _markers.Count && _markers[i] != null) Destroy(_markers[i]);
            if (i < _labels.Count && _labels[i] != null) Destroy(_labels[i].gameObject);
            _samples.RemoveAt(i);
            if (i < _markers.Count) _markers.RemoveAt(i);
            if (i < _labels.Count) _labels.RemoveAt(i);
            Haptics.HeavyRight();
            SendUpdate();
        }

        private Material MaterialFor(ThroughputSample s)
        {
            float weakest = Mathf.Min(s.questToBrowserMbps, s.browserToQuestMbps);
            return weakest >= 150f ? _good : weakest >= 50f ? _ok : _bad;
        }

        private void SendUpdate()
        {
            if (streamer == null) return;
            streamer.SendRaw(JsonUtility.ToJson(new ThroughputPacket
                { nodes = new List<ThroughputSample>(_samples) }));
        }
    }
}
