using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace WifiScanner
{
    /// <summary>
    /// Streams each sample to a PC on the same network as a UDP JSON packet.
    /// UDP is fire-and-forget (low latency, no blocking) which suits a live
    /// feed — occasional dropped packets don't matter for a heatmap.
    ///
    /// Configure the target PC IP/port in the Inspector, or push a config at
    /// runtime via SetTarget(). The companion receiver is tools/receiver.py.
    /// </summary>
    public class NetworkStreamer : MonoBehaviour
    {
        [Tooltip("IP address of the PC running tools/receiver.py")]
        public string targetIp = "192.168.1.100";

        [Tooltip("UDP port the receiver listens on")]
        public int targetPort = 9999;

        public bool enableStreaming = true;

        [Tooltip("Optional: also push every packet to the embedded web dashboard.")]
        public WebDashboardServer webServer;

        [Tooltip("Optional: source of the grid-alignment yaw (sent with the pose " +
                 "so the web dashboard can align the map like the minimap).")]
        public FloorHeatmapRenderer floor;

        [Tooltip("Seconds between live headset-pose packets (0 = disabled).")]
        public float poseInterval = 0.25f;

        [Serializable]
        private class PosePacket
        {
            public string type = "pose";
            public float posX, posY, posZ;
            public float yawDeg;
            public float gridYawDegrees; // grid alignment, for the web dashboard
        }

        private UdpClient _client;
        private IPEndPoint _endPoint;
        private readonly PosePacket _pose = new PosePacket();
        private float _poseTimer;

        private void Awake()
        {
            RebuildEndpoint();
        }

        // Streams the headset pose continuously (independent of recording) so
        // the PC viewer can show the live position like the in-app minimap.
        private void Update()
        {
            // Pose is produced when EITHER sink is live (UDP to the PC or the
            // embedded web dashboard) — SendRaw fans out to both.
            bool web = webServer != null && webServer.IsRunning;
            if (poseInterval <= 0f || (!enableStreaming && !web)) return;
            _poseTimer += Time.deltaTime;
            if (_poseTimer < poseInterval) return;
            _poseTimer = 0f;

            var cam = Camera.main;
            if (cam == null) return;
            Vector3 p = cam.transform.position;
            Vector3 fwd = cam.transform.forward;
            _pose.posX = p.x;
            _pose.posY = p.y;
            _pose.posZ = p.z;
            _pose.yawDeg = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
            _pose.gridYawDegrees = floor != null ? floor.gridYawDegrees : 0f;
            SendRaw(JsonUtility.ToJson(_pose));
        }

        public void SetTarget(string ip, int port)
        {
            targetIp = ip;
            targetPort = port;
            RebuildEndpoint();
        }

        private void RebuildEndpoint()
        {
            try
            {
                _client?.Close();
                _client = new UdpClient();
                _endPoint = new IPEndPoint(IPAddress.Parse(targetIp), targetPort);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[NetworkStreamer] Bad target {targetIp}:{targetPort} — {e.Message}");
                _client = null;
            }
        }

        public void Send(Sample sample)
        {
            SendRaw(JsonUtility.ToJson(sample));
        }

        /// <summary>Fans an arbitrary JSON packet out to both sinks: the embedded
        /// web dashboard (always, if running) and the UDP PC stream (if enabled
        /// and configured). Fire-and-forget.</summary>
        public void SendRaw(string json)
        {
            webServer?.Broadcast(json);

            if (!enableStreaming || _client == null) return;
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                _client.Send(bytes, bytes.Length, _endPoint);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[NetworkStreamer] Send failed: {e.Message}");
            }
        }

        private void OnDestroy()
        {
            _client?.Close();
        }
    }
}
