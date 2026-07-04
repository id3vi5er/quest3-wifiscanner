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

        private UdpClient _client;
        private IPEndPoint _endPoint;

        private void Awake()
        {
            RebuildEndpoint();
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
            if (!enableStreaming || _client == null) return;
            try
            {
                string json = JsonUtility.ToJson(sample);
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
