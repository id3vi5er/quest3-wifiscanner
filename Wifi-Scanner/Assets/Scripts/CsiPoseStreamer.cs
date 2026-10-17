using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

namespace WifiScanner
{
    /// <summary>
    /// Labels for WLAN-CSI tracking : streams the
    /// headset pose at a fixed rate with the Quest's own clock, so the PC
    /// recorder can line it up with the ESPectre CSI packets.
    ///
    /// Packets (UDP JSON to the NetworkStreamer target, typed so the existing
    /// receivers ignore them):
    ///   {"type":"csipose","seq","t","px","py","pz","qx","qy","qz","qw","posture"}
    ///   {"type":"csimark","id","t","mx","my","mz"}   (sent 3x, UDP may drop)
    ///
    /// Controls (left controller, buttons unused by the other tools):
    ///   X              = calibration mark at the left controller position;
    ///                    marks are numbered in the order of config/anchors.json.
    ///   X held 1.5 s   = restart marks at #0.
    ///   Thumbstick press = cycle posture label auto/stand/sit/lie/empty.
    /// A small label at the left hand shows the next mark and the posture.
    /// Created and wired by SceneBootstrap.
    /// </summary>
    public class CsiPoseStreamer : MonoBehaviour
    {
        [Header("Wired by SceneBootstrap")]
        public NetworkStreamer streamer;

        [Tooltip("Pose packets per second.")]
        public float rate = 30f;
        [Tooltip("Hold X this long to restart the calibration marks.")]
        public float resetHoldSeconds = 1.5f;

        // "auto" goes out as "unknown": the PC derives the posture from head height.
        private static readonly string[] Postures = { "unknown", "stand", "sit", "lie", "empty" };
        private static readonly string[] PostureLabels = { "auto", "stehen", "sitzen", "liegen", "leer" };

        [Serializable]
        private class PosePacket
        {
            public string type = "csipose";
            public long seq;
            public double t;
            public float px, py, pz, qx, qy, qz, qw;
            public string posture;
        }

        [Serializable]
        private class MarkPacket
        {
            public string type = "csimark";
            public int id;
            public double t;
            public float mx, my, mz;
        }

        private readonly PosePacket _pose = new PosePacket();
        private readonly MarkPacket _mark = new MarkPacket();
        private UdpClient _client;
        private IPEndPoint _endPoint;
        private string _endPointKey;
        private double _nextSend;
        private int _nextMark;
        private int _posture;
        private float _xDownSince = -1f;
        private bool _resetDone;

        private InputAction _markAction;
        private InputAction _postureAction;
        private InputAction _leftPos;
        private TextMesh _label;

        private void OnEnable()
        {
            _markAction = new InputAction("CsiMark", InputActionType.Button,
                "<XRController>{LeftHand}/primaryButton");
            _postureAction = new InputAction("CsiPosture", InputActionType.Button,
                "<XRController>{LeftHand}/thumbstickClicked");
            _leftPos = new InputAction("CsiLeftPos", InputActionType.Value,
                "<XRController>{LeftHand}/devicePosition", expectedControlType: "Vector3");
            _markAction.Enable();
            _postureAction.Enable();
            _leftPos.Enable();
        }

        private void OnDisable()
        {
            _markAction?.Disable();
            _postureAction?.Disable();
            _leftPos?.Disable();
        }

        private void Start()
        {
            var go = new GameObject("CsiPoseLabel");
            go.transform.SetParent(transform, false);
            _label = go.AddComponent<TextMesh>();
            _label.characterSize = 0.004f;
            _label.fontSize = 48;
            _label.anchor = TextAnchor.LowerCenter;
            _label.color = new Color(0.6f, 1f, 0.7f);
            UpdateLabel();
        }

        private void Update()
        {
            var cam = Camera.main;
            if (cam == null || streamer == null) return;
            double now = Time.realtimeSinceStartupAsDouble;
            Vector3 leftWorld = LeftControllerWorld(cam);

            HandleButtons(now, leftWorld);
            PlaceLabel(cam, leftWorld);

            if (!streamer.enableStreaming || now < _nextSend) return;
            // Catch up without bursting after a hitch.
            _nextSend = Math.Max(_nextSend + 1.0 / rate, now);

            Transform head = cam.transform;
            _pose.seq++;
            _pose.t = now;
            _pose.px = head.position.x;
            _pose.py = head.position.y;
            _pose.pz = head.position.z;
            _pose.qx = head.rotation.x;
            _pose.qy = head.rotation.y;
            _pose.qz = head.rotation.z;
            _pose.qw = head.rotation.w;
            _pose.posture = Postures[_posture];
            Send(JsonUtility.ToJson(_pose));
        }

        private void HandleButtons(double now, Vector3 leftWorld)
        {
            if (_postureAction.WasPressedThisFrame())
            {
                _posture = (_posture + 1) % Postures.Length;
                Haptics.TickLeft();
                UpdateLabel();
            }

            if (_markAction.WasPressedThisFrame())
            {
                _xDownSince = Time.unscaledTime;
                _resetDone = false;
            }
            if (_markAction.IsPressed() && !_resetDone && _xDownSince >= 0f &&
                Time.unscaledTime - _xDownSince >= resetHoldSeconds)
            {
                _nextMark = 0;
                _resetDone = true;
                Haptics.Pulse(false, 0.85f, 0.25f);
                UpdateLabel();
            }
            if (_markAction.WasReleasedThisFrame())
            {
                if (!_resetDone)
                {
                    _mark.id = _nextMark++;
                    _mark.t = now;
                    _mark.mx = leftWorld.x;
                    _mark.my = leftWorld.y;
                    _mark.mz = leftWorld.z;
                    string json = JsonUtility.ToJson(_mark);
                    for (int i = 0; i < 3; i++) Send(json);
                    Haptics.Pulse(false, 0.6f, 0.05f);
                    UpdateLabel();
                }
                _xDownSince = -1f;
            }
        }

        // Controller device poses are relative to the XR Origin's camera offset,
        // the camera's parent; the head comes from the camera in world space.
        private Vector3 LeftControllerWorld(Camera cam)
        {
            Vector3 local = _leftPos.ReadValue<Vector3>();
            Transform offset = cam.transform.parent;
            return offset != null ? offset.TransformPoint(local) : local;
        }

        private void PlaceLabel(Camera cam, Vector3 leftWorld)
        {
            if (_label == null) return;
            _label.transform.position = leftWorld + Vector3.up * 0.08f;
            _label.transform.rotation = Quaternion.LookRotation(leftWorld - cam.transform.position);
        }

        private void UpdateLabel()
        {
            if (_label != null)
                _label.text = $"CSI  Marke #{_nextMark}  ·  {PostureLabels[_posture]}";
        }

        private void Send(string json)
        {
            string key = $"{streamer.targetIp}:{streamer.targetPort}";
            if (key != _endPointKey)
            {
                _endPointKey = key;
                _client?.Close();
                _client = null;
                try
                {
                    _endPoint = new IPEndPoint(IPAddress.Parse(streamer.targetIp), streamer.targetPort);
                    _client = new UdpClient();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[CsiPoseStreamer] Bad target {key} — {e.Message}");
                }
            }
            if (_client == null) return;
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                _client.Send(bytes, bytes.Length, _endPoint);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CsiPoseStreamer] Send failed: {e.Message}");
            }
        }

        private void OnDestroy()
        {
            _client?.Close();
        }
    }
}
