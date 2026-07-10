using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;

namespace WifiScanner
{
    /// <summary>
    /// Embedded web dashboard served straight from the Quest. A lightweight
    /// TcpListener (no HttpListener — its WebSocket support is unreliable on
    /// IL2CPP/Android) serves a self-contained HTML/JS page and a WebSocket that
    /// pushes the SAME live JSON packets the NetworkStreamer already produces
    /// (pose, rooms, drill, apfix, ping, notes, samples). Any phone/tablet/PC on
    /// the WLAN opens http://&lt;quest-ip&gt;:8080 — no install.
    ///
    /// It also lets you download the recorded scan files (persistentDataPath):
    ///   GET /scans        -> JSON list of export files
    ///   GET /download?f=  -> one file (attachment)
    ///   GET /download-all -> all export files zipped
    ///   GET /export       -> trigger a fresh export (runs on the main thread)
    ///
    /// Fed by NetworkStreamer.Broadcast(); created and wired by SceneBootstrap.
    /// </summary>
    public class WebDashboardServer : MonoBehaviour
    {
        [Tooltip("TCP port for the dashboard (http://<ip>:<port>).")]
        public int port = 8080;
        [Tooltip("Start the server automatically on launch.")]
        public bool runAtStart = true;
        [Tooltip("Optional: lets the dashboard trigger a fresh export.")]
        public SampleCollector collector;
        [Tooltip("Newest samples kept for late-joining clients.")]
        public int maxSnapshotSamples = 8000;

        public bool IsRunning { get; private set; }

        private const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private static readonly string[] ExportPatterns =
            { "*.csv", "*.json", "*.png", "*.svg", "*.obj", "*.html" };

        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;

        private class Client
        {
            public TcpClient tcp;
            public NetworkStream stream;
            public readonly object sendLock = new object();
        }

        private readonly List<Client> _clients = new List<Client>();
        // Latest packet per "type" + accumulated samples, replayed to new clients.
        private readonly Dictionary<string, string> _lastByType =
            new Dictionary<string, string>();
        private readonly List<string> _samples = new List<string>();
        private readonly object _stateLock = new object();
        private readonly Queue<Action> _mainThread = new Queue<Action>();

        /// <summary>Best-guess dashboard URL on the local network.</summary>
        public string Url => $"http://{LocalIPv4()}:{port}";

        private void Start()
        {
            if (runAtStart) SetEnabled(true);
        }

        public void SetEnabled(bool on)
        {
            if (on) StartServer();
            else StopServer();
        }

        private void StartServer()
        {
            if (_running) return;
            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                _running = true;
                IsRunning = true;
                _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
                _acceptThread.Start();
                Debug.Log($"[WebDashboard] Läuft auf {Url}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[WebDashboard] Konnte Port {port} nicht öffnen: {e.Message}");
                _running = false;
                IsRunning = false;
            }
        }

        private void StopServer()
        {
            _running = false;
            IsRunning = false;
            try { _listener?.Stop(); } catch { }
            lock (_clients)
            {
                foreach (var c in _clients) { try { c.tcp.Close(); } catch { } }
                _clients.Clear();
            }
            _listener = null;
            Debug.Log("[WebDashboard] Gestoppt.");
        }

        private void Update()
        {
            // Run queued work (e.g. an export triggered from the browser) on the
            // Unity main thread.
            while (true)
            {
                Action a = null;
                lock (_mainThread)
                    if (_mainThread.Count > 0) a = _mainThread.Dequeue();
                if (a == null) break;
                try { a(); } catch (Exception e) { Debug.LogWarning($"[WebDashboard] {e.Message}"); }
            }
        }

        private void OnDestroy() => StopServer();
        private void OnApplicationQuit() => StopServer();

        // ---- Live broadcast (called from NetworkStreamer, main thread) ----

        /// <summary>Pushes one JSON packet to all WS clients and records it for
        /// the snapshot sent to future clients.</summary>
        public void Broadcast(string json)
        {
            if (!_running || string.IsNullOrEmpty(json)) return;

            // Remember state so a late-joining browser gets the full picture.
            lock (_stateLock)
            {
                string type = ExtractType(json);
                if (type == null)
                {
                    _samples.Add(json);
                    if (_samples.Count > maxSnapshotSamples)
                        _samples.RemoveRange(0, _samples.Count - maxSnapshotSamples);
                }
                else
                {
                    _lastByType[type] = json;
                    if (type == "clear") _samples.Clear();
                }
            }

            byte[] frame = BuildTextFrame(json);
            SendFrameToAll(frame);
        }

        private void SendFrameToAll(byte[] frame)
        {
            List<Client> dead = null;
            lock (_clients)
            {
                foreach (var c in _clients)
                {
                    try
                    {
                        lock (c.sendLock) c.stream.Write(frame, 0, frame.Length);
                    }
                    catch
                    {
                        (dead ??= new List<Client>()).Add(c);
                    }
                }
                if (dead != null) foreach (var c in dead) _clients.Remove(c);
            }
        }

        // ---- Connection handling ----

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient tcp;
                try { tcp = _listener.AcceptTcpClient(); }
                catch { break; } // listener stopped
                var t = new Thread(() => HandleClient(tcp)) { IsBackground = true };
                t.Start();
            }
        }

        private void HandleClient(TcpClient tcp)
        {
            try
            {
                tcp.NoDelay = true;
                var stream = tcp.GetStream();
                if (!ReadRequest(stream, out string method, out string path,
                        out string query, out var headers))
                { tcp.Close(); return; }

                bool wantsWs = headers.TryGetValue("upgrade", out string up) &&
                               up.ToLowerInvariant().Contains("websocket");

                if (wantsWs && path == "/ws")
                {
                    Handshake(stream, headers);
                    var client = new Client { tcp = tcp, stream = stream };
                    lock (_clients) _clients.Add(client);
                    SendSnapshot(client);
                    ReadLoop(client); // blocks until the socket closes
                    lock (_clients) _clients.Remove(client);
                    tcp.Close();
                    return;
                }

                RouteHttp(stream, method, path, query);
                tcp.Close();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[WebDashboard] Client-Fehler: {e.Message}");
                try { tcp.Close(); } catch { }
            }
        }

        private void RouteHttp(NetworkStream stream, string method, string path,
            string query)
        {
            if (path == "/" || path == "/index.html")
                WriteHttp(stream, "200 OK", "text/html; charset=utf-8",
                    Encoding.UTF8.GetBytes(WebDashboardPage.Html));
            else if (path == "/scans")
                WriteHttp(stream, "200 OK", "application/json",
                    Encoding.UTF8.GetBytes(ScanListJson()));
            else if (path == "/download")
                ServeDownload(stream, query);
            else if (path == "/download-all")
                ServeZip(stream);
            else if (path == "/export")
                ServeExport(stream);
            else
                WriteHttp(stream, "404 Not Found", "text/plain",
                    Encoding.UTF8.GetBytes("not found"));
        }

        // ---- Scan downloads ----

        private static string ScanListJson()
        {
            var sb = new StringBuilder("{\"files\":[");
            bool first = true;
            foreach (var p in ExportFiles())
            {
                var fi = new FileInfo(p);
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"name\":\"").Append(Escape(fi.Name))
                  .Append("\",\"size\":").Append(fi.Length).Append('}');
            }
            return sb.Append("]}").ToString();
        }

        private void ServeDownload(NetworkStream stream, string query)
        {
            string name = QueryValue(query, "f");
            // Path-traversal guard: basename only, must be an actual export file.
            if (string.IsNullOrEmpty(name) || name.Contains("/") ||
                name.Contains("\\") || name.Contains(".."))
            { WriteHttp(stream, "400 Bad Request", "text/plain", Bytes("bad name")); return; }

            string full = Path.Combine(Application.persistentDataPath, name);
            if (!File.Exists(full) || !IsExport(name))
            { WriteHttp(stream, "404 Not Found", "text/plain", Bytes("no file")); return; }

            byte[] data = File.ReadAllBytes(full);
            WriteHttp(stream, "200 OK", ContentType(name), data,
                $"Content-Disposition: attachment; filename=\"{name}\"\r\n");
        }

        private void ServeZip(NetworkStream stream)
        {
            byte[] zip;
            using (var ms = new MemoryStream())
            {
                using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
                    foreach (var p in ExportFiles())
                    {
                        var entry = archive.CreateEntry(Path.GetFileName(p),
                            System.IO.Compression.CompressionLevel.Fastest);
                        using var es = entry.Open();
                        using var fs = File.OpenRead(p);
                        fs.CopyTo(es);
                    }
                zip = ms.ToArray();
            }
            WriteHttp(stream, "200 OK", "application/zip", zip,
                "Content-Disposition: attachment; filename=\"scans.zip\"\r\n");
        }

        private void ServeExport(NetworkStream stream)
        {
            if (collector != null)
                lock (_mainThread) _mainThread.Enqueue(() => collector.Export());
            WriteHttp(stream, "200 OK", "application/json",
                Bytes("{\"ok\":true}"));
        }

        private static IEnumerable<string> ExportFiles()
        {
            string dir = Application.persistentDataPath;
            foreach (var pattern in ExportPatterns)
                foreach (var f in Directory.GetFiles(dir, pattern))
                    yield return f;
        }

        private static bool IsExport(string name)
        {
            string ext = Path.GetExtension(name).ToLowerInvariant();
            return ext == ".csv" || ext == ".json" || ext == ".png" ||
                   ext == ".svg" || ext == ".obj";
        }

        // ---- WebSocket plumbing ----

        private void Handshake(NetworkStream stream, Dictionary<string, string> headers)
        {
            headers.TryGetValue("sec-websocket-key", out string key);
            string accept;
            using (var sha1 = SHA1.Create())
                accept = Convert.ToBase64String(
                    sha1.ComputeHash(Encoding.ASCII.GetBytes((key ?? "") + WsGuid)));
            string resp = "HTTP/1.1 101 Switching Protocols\r\n" +
                          "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
                          $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
            byte[] b = Encoding.ASCII.GetBytes(resp);
            stream.Write(b, 0, b.Length);
        }

        private void SendSnapshot(Client client)
        {
            List<string> packets = new List<string>();
            lock (_stateLock)
            {
                foreach (var kv in _lastByType) packets.Add(kv.Value);
                if (_samples.Count > 0)
                {
                    var sb = new StringBuilder("{\"type\":\"samplebatch\",\"items\":[");
                    for (int i = 0; i < _samples.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append(_samples[i]);
                    }
                    sb.Append("]}");
                    packets.Add(sb.ToString());
                }
            }
            foreach (var p in packets)
            {
                byte[] frame = BuildTextFrame(p);
                try { lock (client.sendLock) client.stream.Write(frame, 0, frame.Length); }
                catch { return; }
            }
        }

        // Reads client frames only to notice close/ping (data frames ignored).
        private void ReadLoop(Client client)
        {
            var s = client.stream;
            while (_running)
            {
                if (!ReadFully(s, 2, out byte[] h)) return;
                int opcode = h[0] & 0x0F;
                bool masked = (h[1] & 0x80) != 0;
                long len = h[1] & 0x7F;
                if (len == 126)
                {
                    if (!ReadFully(s, 2, out byte[] e)) return;
                    len = (e[0] << 8) | e[1];
                }
                else if (len == 127)
                {
                    if (!ReadFully(s, 8, out byte[] e)) return;
                    len = 0;
                    for (int i = 0; i < 8; i++) len = (len << 8) | e[i];
                }
                byte[] mask = null;
                if (masked && !ReadFully(s, 4, out mask)) return;
                byte[] payload = null;
                if (len > 0 && !ReadFully(s, (int)len, out payload)) return;

                if (opcode == 0x8) return; // close
                if (opcode == 0x9) // ping -> pong
                {
                    if (masked && payload != null)
                        for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];
                    byte[] pong = BuildFrame(0xA, payload ?? Array.Empty<byte>());
                    try { lock (client.sendLock) s.Write(pong, 0, pong.Length); } catch { return; }
                }
            }
        }

        private static bool ReadFully(NetworkStream s, int count, out byte[] buf)
        {
            buf = new byte[count];
            int off = 0;
            while (off < count)
            {
                int n;
                try { n = s.Read(buf, off, count - off); } catch { return false; }
                if (n <= 0) return false;
                off += n;
            }
            return true;
        }

        private static byte[] BuildTextFrame(string text) =>
            BuildFrame(0x1, Encoding.UTF8.GetBytes(text));

        // Server frames are never masked.
        private static byte[] BuildFrame(int opcode, byte[] payload)
        {
            int len = payload.Length;
            using var ms = new MemoryStream();
            ms.WriteByte((byte)(0x80 | opcode)); // FIN + opcode
            if (len < 126) ms.WriteByte((byte)len);
            else if (len < 65536)
            {
                ms.WriteByte(126);
                ms.WriteByte((byte)(len >> 8));
                ms.WriteByte((byte)len);
            }
            else
            {
                ms.WriteByte(127);
                for (int i = 7; i >= 0; i--) ms.WriteByte((byte)((long)len >> (8 * i)));
            }
            ms.Write(payload, 0, len);
            return ms.ToArray();
        }

        // ---- HTTP helpers ----

        private static bool ReadRequest(NetworkStream stream, out string method,
            out string path, out string query, out Dictionary<string, string> headers)
        {
            method = path = query = "";
            headers = new Dictionary<string, string>();
            string raw = ReadHeaderText(stream);
            if (string.IsNullOrEmpty(raw)) return false;

            string[] lines = raw.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] req = lines[0].Split(' ');
            if (req.Length < 2) return false;
            method = req[0];
            string target = req[1];
            int q = target.IndexOf('?');
            if (q >= 0) { path = target.Substring(0, q); query = target.Substring(q + 1); }
            else path = target;

            for (int i = 1; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf(':');
                if (c <= 0) continue;
                headers[lines[i].Substring(0, c).Trim().ToLowerInvariant()] =
                    lines[i].Substring(c + 1).Trim();
            }
            return true;
        }

        private static string ReadHeaderText(NetworkStream stream)
        {
            var sb = new StringBuilder();
            var buf = new byte[1];
            int tail = 0; // matches \r\n\r\n
            while (sb.Length < 16384)
            {
                int n;
                try { n = stream.Read(buf, 0, 1); } catch { return null; }
                if (n <= 0) return sb.Length > 0 ? sb.ToString() : null;
                char ch = (char)buf[0];
                sb.Append(ch);
                if ((tail == 0 || tail == 2) && ch == '\r') tail++;
                else if ((tail == 1 || tail == 3) && ch == '\n') { if (++tail == 4) break; }
                else tail = 0;
            }
            return sb.ToString();
        }

        private static void WriteHttp(NetworkStream stream, string status,
            string contentType, byte[] body, string extraHeaders = "")
        {
            string head = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\n" +
                          $"Content-Length: {body.Length}\r\n" +
                          "Access-Control-Allow-Origin: *\r\n" +
                          extraHeaders + "Connection: close\r\n\r\n";
            byte[] h = Encoding.ASCII.GetBytes(head);
            stream.Write(h, 0, h.Length);
            if (body.Length > 0) stream.Write(body, 0, body.Length);
        }

        private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

        private static string ContentType(string name)
        {
            switch (Path.GetExtension(name).ToLowerInvariant())
            {
                case ".csv": return "text/csv";
                case ".json": return "application/json";
                case ".png": return "image/png";
                case ".svg": return "image/svg+xml";
                case ".obj": return "text/plain";
                case ".zip": return "application/zip";
                default: return "application/octet-stream";
            }
        }

        private static string QueryValue(string query, string key)
        {
            if (string.IsNullOrEmpty(query)) return null;
            foreach (var part in query.Split('&'))
            {
                int e = part.IndexOf('=');
                if (e > 0 && part.Substring(0, e) == key)
                    return Uri.UnescapeDataString(part.Substring(e + 1));
            }
            return null;
        }

        // Cheap "type" extraction without a JSON parser: finds "type":"<value>".
        private static string ExtractType(string json)
        {
            const string tag = "\"type\":\"";
            int i = json.IndexOf(tag, StringComparison.Ordinal);
            if (i < 0) return null;
            i += tag.Length;
            int e = json.IndexOf('"', i);
            return e > i ? json.Substring(i, e - i) : null;
        }

        private static string Escape(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static string LocalIPv4()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !IPAddress.IsLoopback(ua.Address))
                            return ua.Address.ToString();
                }
            }
            catch { }
            return "127.0.0.1";
        }
    }
}
