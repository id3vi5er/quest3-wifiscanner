using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace WifiScanner
{
    /// <summary>One placed latency probe: world position + measured round-trip.</summary>
    [Serializable]
    public class PingSample
    {
        public float posX, posY, posZ;
        public int latencyMs; // -2 = pending, -1 = failed, >=0 = ms
    }

    /// <summary>
    /// Active latency test ("Ping-Test" on the panel). While active, the trigger
    /// drops a permanent 3D node at the controller and fires a latency probe: a
    /// TCP connect to host:port (default google.com:443) timed on a background
    /// thread — a full ICMP ping needs root on Android, a TCP handshake does not
    /// and is close enough. A label above the node shows "… → 24 ms" (or a red
    /// "Fehler"); the node colour reflects the latency band.
    ///
    /// Nodes are streamed to the PC viewer ("ping" packet) and exported to
    /// {stem}_ping.json. Created and wired by SceneBootstrap.
    /// </summary>
    public class PingTester : MonoBehaviour, IExclusiveTool
    {
        [Header("Wired by SceneBootstrap")]
        [Tooltip("Material ASSET (URP/Unlit) cloned + tinted for nodes.")]
        public Material visualMaterial;
        public NetworkStreamer streamer;

        [Header("Probe")]
        [Tooltip("Host to connect to for the latency measurement.")]
        public string host = "google.com";
        public int port = 443;
        [Tooltip("Connect timeout (ms) — reported as a failure beyond this.")]
        public int timeoutMs = 2000;
        [Tooltip("Distance of the placement cursor in front of the controller (m).")]
        public float cursorDistance = 0.3f;
        public float nodeDiameter = 0.05f;

        public bool IsActive { get; private set; }
        public int NodeCount => _nodes.Count;
        public IReadOnlyList<PingSample> Samples
        {
            get
            {
                _export.Clear();
                foreach (var n in _nodes)
                    _export.Add(new PingSample
                    {
                        posX = n.pos.x, posY = n.pos.y, posZ = n.pos.z,
                        latencyMs = n.latencyMs,
                    });
                return _export;
            }
        }

        private class Node
        {
            public Vector3 pos;
            public Transform marker;
            public Renderer renderer;
            public WorldLabel label;
            public volatile int latencyMs = -2; // -2 pending, -1 failed, >=0 ms
            public volatile bool done;
            public bool applied;
        }

        private readonly List<Node> _nodes = new List<Node>();
        private readonly List<PingSample> _export = new List<PingSample>();

        private InputAction _posAction;
        private InputAction _rotAction;
        private InputAction _placeAction;

        private GameObject _root;
        private Transform _cursor;
        private Renderer _cursorRenderer;
        private Material _matCursor, _matPending, _matGood, _matOk, _matBad;
        private Mesh _sphereMesh;
        private float _resendTimer;

        [Serializable]
        private class PingPacket
        {
            public string type = "ping";
            public List<PingSample> nodes;
        }

        private void Start()
        {
            if (PlayerPrefs.HasKey("wifi_ping_host"))
                host = PlayerPrefs.GetString("wifi_ping_host", host);

            var temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _sphereMesh = temp.GetComponent<MeshFilter>().sharedMesh;
            Destroy(temp);

            if (visualMaterial != null)
            {
                _matCursor = MakeTinted(new Color(0f, 0.86f, 1f, 1f));
                _matPending = MakeTinted(new Color(0.7f, 0.7f, 0.7f, 1f));
                _matGood = MakeTinted(new Color(0.2f, 1f, 0.3f, 1f));   // < 50 ms
                _matOk = MakeTinted(new Color(1f, 0.85f, 0.1f, 1f));    // < 120 ms
                _matBad = MakeTinted(new Color(1f, 0.25f, 0.2f, 1f));   // slow / failed
            }
            else
            {
                Debug.LogError("[PingTester] visualMaterial not assigned — assign the " +
                               "point material in SceneBootstrap.");
            }

            _root = new GameObject("PingTesterVisuals");
            _root.transform.SetParent(transform, false);

            _cursor = CreateSphere(_root.transform, nodeDiameter, _matCursor);
            _cursorRenderer = _cursor.GetComponent<Renderer>();
            _cursor.gameObject.SetActive(false);
        }

        private Material MakeTinted(Color c)
        {
            var m = new Material(visualMaterial); // asset clone keeps its variant
            m.SetColor("_BaseColor", c);
            return m;
        }

        private void OnEnable()
        {
            _posAction = new InputAction("PingPos", InputActionType.Value,
                "<XRController>{RightHand}/pointerPosition", expectedControlType: "Vector3");
            _posAction.AddBinding("<XRController>{RightHand}/devicePosition"); // fallback
            _rotAction = new InputAction("PingRot", InputActionType.Value,
                "<XRController>{RightHand}/pointerRotation", expectedControlType: "Quaternion");
            _rotAction.AddBinding("<XRController>{RightHand}/deviceRotation"); // fallback
            _placeAction = new InputAction("PingPlace", InputActionType.Button,
                "<XRController>{RightHand}/triggerPressed");
            _posAction.Enable();
            _rotAction.Enable();
            _placeAction.Enable();
            ExclusiveTools.Register(this);
        }

        private void OnDisable()
        {
            _posAction?.Disable();
            _rotAction?.Disable();
            _placeAction?.Disable();
            ExclusiveTools.Unregister(this);
        }

        // ---- Mode control ----

        public void ToggleActive() => SetActive(!IsActive);

        /// <summary>Updates and persists the host edited in the wrist palette.</summary>
        public bool SetHost(string value)
        {
            value = (value ?? "").Trim();
            if (value.Length == 0) return false;
            host = value;
            PlayerPrefs.SetString("wifi_ping_host", host);
            PlayerPrefs.Save();
            return true;
        }

        public void SetActive(bool active)
        {
            if (IsActive == active) return;
            IsActive = active;
            if (active) ExclusiveTools.DeactivateOthers(this);
            if (_cursor != null) _cursor.gameObject.SetActive(active);
            Debug.Log($"[PingTester] Ping-Test {(IsActive ? "AKTIV" : "beendet")} " +
                      $"({_nodes.Count} Knoten).");
        }

        // ---- Main loop ----

        private void Update()
        {
            // Apply finished background measurements + periodic re-broadcast.
            bool anyApplied = false;
            foreach (var n in _nodes)
            {
                if (n.done && !n.applied)
                {
                    n.applied = true;
                    anyApplied = true;
                    ApplyResult(n);
                }
                if (n.label != null)
                    n.label.SetWorldPosition(n.pos + Vector3.up * (nodeDiameter + 0.03f));
            }
            if (anyApplied) SendPingUpdate();

            if (streamer != null && _nodes.Count > 0)
            {
                _resendTimer += Time.deltaTime;
                if (_resendTimer >= 3f) SendPingUpdate();
            }

            if (!IsActive) return;

            GetPointer(out Vector3 handPos, out Vector3 handFwd);
            Vector3 cursor = handPos + handFwd * cursorDistance;
            if (_cursor != null) _cursor.position = cursor;

            // Pointer on the panel = trigger belongs to the UI, don't place.
            if (!ControlPanel.PointerOverPanel && _placeAction.WasPressedThisFrame())
                PlaceNode(cursor);
        }

        private void PlaceNode(Vector3 pos)
        {
            var node = new Node { pos = pos };
            node.marker = CreateSphere(_root.transform, nodeDiameter, _matPending);
            node.marker.position = pos;
            node.renderer = node.marker.GetComponent<Renderer>();
            node.label = WorldLabel.Create(_root.transform);
            node.label.SetText("…");
            _nodes.Add(node);
            Haptics.PlaceRight();
            Measure(node);
            Debug.Log($"[PingTester] Knoten {_nodes.Count} gesetzt — messe {host}:{port} …");
        }

        // Times a TCP connect on the thread pool; never touches Unity APIs off
        // the main thread — only the volatile result fields.
        private void Measure(Node node)
        {
            string h = host;
            int p = port;
            int to = timeoutMs;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int result;
                try
                {
                    using (var client = new TcpClient())
                    {
                        var ar = client.BeginConnect(h, p, null, null);
                        bool ok = ar.AsyncWaitHandle.WaitOne(to);
                        sw.Stop();
                        if (ok && client.Connected)
                        {
                            client.EndConnect(ar);
                            result = (int)sw.ElapsedMilliseconds;
                        }
                        else result = -1; // timeout / refused
                    }
                }
                catch (Exception)
                {
                    result = -1;
                }
                node.latencyMs = result;
                node.done = true;
            });
        }

        private void ApplyResult(Node node)
        {
            int ms = node.latencyMs;
            Material m = ms < 0 ? _matBad
                : ms < 50 ? _matGood
                : ms < 120 ? _matOk
                : _matBad;
            if (node.renderer != null && m != null) node.renderer.sharedMaterial = m;
            if (node.label != null)
                node.label.SetText(ms < 0 ? "Fehler" : $"{ms} ms");
            Debug.Log($"[PingTester] {host}: {(ms < 0 ? "Fehler" : ms + " ms")}.");
        }

        public void ClearNodes()
        {
            foreach (var n in _nodes)
            {
                if (n.marker != null) Destroy(n.marker.gameObject);
                if (n.label != null) Destroy(n.label.gameObject);
            }
            _nodes.Clear();
            SendPingUpdate();
        }

        /// <summary>Replaces all nodes (session load / re-alignment). Loaded
        /// nodes keep their stored latency — they are NOT re-probed. Null or
        /// empty just clears.</summary>
        public void LoadNodes(IReadOnlyList<PingSample> loaded)
        {
            // Snapshot first: the Samples property (a likely caller source)
            // reuses the shared _export list, which SendPingUpdate refills.
            var copy = loaded != null
                ? new List<PingSample>(loaded) : new List<PingSample>();
            foreach (var n in _nodes)
            {
                if (n.marker != null) Destroy(n.marker.gameObject);
                if (n.label != null) Destroy(n.label.gameObject);
            }
            _nodes.Clear();
            foreach (var s in copy)
            {
                var node = new Node
                {
                    pos = new Vector3(s.posX, s.posY, s.posZ),
                    latencyMs = s.latencyMs,
                    done = true,
                    applied = true,
                };
                node.marker = CreateSphere(_root.transform, nodeDiameter, _matPending);
                node.marker.position = node.pos;
                node.renderer = node.marker.GetComponent<Renderer>();
                node.label = WorldLabel.Create(_root.transform);
                ApplyResult(node); // colour band + "24 ms" / "Fehler" label
                _nodes.Add(node);
            }
            SendPingUpdate();
            Debug.Log($"[PingTester] {_nodes.Count} Ping-Knoten geladen.");
        }

        // ---- Delete-mode support ----

        public bool TryGetNodePos(int i, out Vector3 p)
        {
            p = Vector3.zero;
            if (i < 0 || i >= _nodes.Count) return false;
            p = _nodes[i].pos;
            return true;
        }

        public void SetHighlight(int i, bool on)
        {
            if (i < 0 || i >= _nodes.Count) return;
            var n = _nodes[i];
            if (n.renderer == null) return;
            n.renderer.sharedMaterial = on ? _matBad : ColorMat(n.latencyMs);
        }

        public void RemoveNode(int i)
        {
            if (i < 0 || i >= _nodes.Count) return;
            // Samples builds fresh PingSample objects, so the list copy is a
            // safe snapshot.
            var snapshot = new List<PingSample>(Samples);
            UndoStack.Push("Ping-Knoten gelöscht", () => LoadNodes(snapshot));
            var n = _nodes[i];
            if (n.marker != null) Destroy(n.marker.gameObject);
            if (n.label != null) Destroy(n.label.gameObject);
            _nodes.RemoveAt(i);
            Haptics.HeavyRight();
            SendPingUpdate();
        }

        private Material ColorMat(int ms) =>
            ms < 0 ? _matBad : ms < 50 ? _matGood : ms < 120 ? _matOk : _matBad;

        // ---- Streaming ----

        private void SendPingUpdate()
        {
            if (streamer == null) return;
            _resendTimer = 0f;
            var packet = new PingPacket { nodes = new List<PingSample>(Samples) };
            streamer.SendRaw(JsonUtility.ToJson(packet));
        }

        // ---- Helpers ----

        private Transform CreateSphere(Transform parent, float diameter, Material mat)
        {
            var go = new GameObject("PingNode");
            go.transform.SetParent(parent, false);
            go.transform.localScale = Vector3.one * diameter;
            go.AddComponent<MeshFilter>().sharedMesh = _sphereMesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return go.transform;
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
