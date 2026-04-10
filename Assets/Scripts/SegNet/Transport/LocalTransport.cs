using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace SegNet {

    /// <summary>
    /// Localhost TCP transport for testing without Steam.
    /// Both reliable and unreliable channels go over TCP (this is a test transport).
    ///
    /// Wire format per message: [4 bytes LE length][payload bytes].
    /// </summary>
    public class LocalTransport : MonoBehaviour, ITransport {
        [SerializeField] private int port = 8000;
        [SerializeField] private string host = "127.0.0.1";

        public NetRole Role { get; private set; } = NetRole.None;
        public bool IsRunning { get; private set; }
        public int MaxPacketSize => 1200;

        public event Action<ConnectionId> OnConnected;
        public event Action<ConnectionId, DisconnectReason> OnDisconnected;
        public event Action<ConnectionId, ArraySegment<byte>, ChannelType> OnData;

        // ---- Server state ----
        private TcpListener _listener;
        private readonly Dictionary<int, TcpClient> _idToClient = new Dictionary<int, TcpClient>();
        private readonly Dictionary<TcpClient, ConnectionId> _clientToId = new Dictionary<TcpClient, ConnectionId>();

        // ---- Client state ----
        private TcpClient _clientSocket;
        private ConnectionId _serverConnId;
        private bool _clientConnected;

        // ---- Shared ----
        private readonly Dictionary<TcpClient, ReceiveBuffer> _receiveBuffers =
            new Dictionary<TcpClient, ReceiveBuffer>();
        private int _nextId = 1;
        private readonly byte[] _readScratch = new byte[8192];
        private bool _clientConnectCompleted;
        private bool _clientConnectSucceeded;
        private string _clientConnectError;

        // ---- ITransport ----

        public void Initialize() {
            Role = NetRole.None;
            IsRunning = false;
        }

        public void Shutdown() {
            Stop();
        }

        public void StartServer() {
            if (IsRunning) return;

            Role = NetRole.Server;
            IsRunning = true;

            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Server.NoDelay = true;
            _listener.Start();

            Debug.Log($"[LocalTransport] Server listening on port {port}");
        }

        public void StartClient() {
            if (IsRunning) return;

            Role = NetRole.Client;
            IsRunning = true;
            _clientConnected = false;

            try {
                _clientSocket = new TcpClient();
                _clientSocket.NoDelay = true;
                _clientSocket.BeginConnect(host, port, OnClientConnectComplete, null);
                Debug.Log($"[LocalTransport] Client connecting to {host}:{port}");
            } catch (Exception ex) {
                Debug.LogError($"[LocalTransport] Client connect failed: {ex.Message}");
                Stop();
            }
        }

        public void Stop() {
            // Close server
            if (_listener != null) {
                _listener.Stop();
                _listener = null;
            }

            // Close all server-side client sockets
            foreach (var kvp in _idToClient) {
                CloseSocket(kvp.Value);
            }
            _idToClient.Clear();
            _clientToId.Clear();

            // Close client socket
            if (_clientSocket != null) {
                CloseSocket(_clientSocket);
                _clientSocket = null;
            }
            _clientConnected = false;

            _receiveBuffers.Clear();
            _clientConnectCompleted = false;
            _clientConnectSucceeded = false;
            _clientConnectError = null;

            Role = NetRole.None;
            IsRunning = false;
        }

        public void Poll() {
            if (!IsRunning) return;

            if (Role == NetRole.Server)
                PollServer();
            else if (Role == NetRole.Client)
                PollClient();
        }

        public void Send(ConnectionId connection, ArraySegment<byte> payload, ChannelType channel) {
            TcpClient socket = null;

            if (Role == NetRole.Server)
                _idToClient.TryGetValue(connection.Value, out socket);
            else if (Role == NetRole.Client && connection == _serverConnId)
                socket = _clientSocket;

            if (socket == null || !socket.Connected) return;

            SendFramed(socket, payload);
        }

        public void Broadcast(ArraySegment<byte> payload, ChannelType channel) {
            if (Role == NetRole.Server) {
                foreach (var kvp in _idToClient)
                    SendFramed(kvp.Value, payload);
            }
        }

        public void Disconnect(ConnectionId connection) {
            if (Role == NetRole.Server && _idToClient.TryGetValue(connection.Value, out var client)) {
                CloseSocket(client);
                _clientToId.Remove(client);
                _idToClient.Remove(connection.Value);
                _receiveBuffers.Remove(client);
                OnDisconnected?.Invoke(connection, DisconnectReason.LocalShutdown);
            }
        }

        // ---- Server polling ----

        private void PollServer() {
            // Accept pending connections
            while (_listener != null && _listener.Pending()) {
                TcpClient newClient = _listener.AcceptTcpClient();
                newClient.NoDelay = true;

                var id = new ConnectionId(_nextId++);
                _idToClient[id.Value] = newClient;
                _clientToId[newClient] = id;
                _receiveBuffers[newClient] = new ReceiveBuffer();

                Debug.Log($"[LocalTransport] Server accepted connection {id}");
                OnConnected?.Invoke(id);
            }

            // Read from all connected clients
            var disconnected = new List<TcpClient>();
            foreach (var kvp in _clientToId) {
                TcpClient client = kvp.Key;
                ConnectionId id = kvp.Value;

                if (!ReadFromSocket(client, id))
                    disconnected.Add(client);
            }

            foreach (var client in disconnected)
                DropConnection(client, DisconnectReason.RemoteShutdown);
        }

        // ---- Client polling ----

        private void PollClient() {
            ProcessPendingClientConnect();

            if (_clientSocket == null || !_clientConnected) return;

            if (!ReadFromSocket(_clientSocket, _serverConnId))
                DropConnection(_clientSocket, DisconnectReason.RemoteShutdown);
        }

        private void OnClientConnectComplete(IAsyncResult ar) {
            try {
                _clientSocket.EndConnect(ar);
                _clientConnectSucceeded = true;
                _clientConnectError = null;
            } catch (Exception ex) {
                _clientConnectSucceeded = false;
                _clientConnectError = ex.Message;
            } finally {
                _clientConnectCompleted = true;
            }
        }

        // ---- Read / Write ----

        /// <summary>Returns false if the socket is dead and should be dropped.</summary>
        private bool ReadFromSocket(TcpClient client, ConnectionId id) {
            try {
                if (client == null || !client.Connected) return false;

                NetworkStream stream = client.GetStream();
                if (!stream.DataAvailable) {
                    if (IsRemoteClosed(client))
                        return false;

                    return true;
                }

                if (!_receiveBuffers.TryGetValue(client, out var buf)) return false;

                int bytesRead = stream.Read(_readScratch, 0, _readScratch.Length);
                if (bytesRead <= 0) return false;

                buf.Append(_readScratch, 0, bytesRead);

                // Extract complete messages
                while (true) {
                    byte[] msg = buf.TryExtractMessage();
                    if (msg == null) break;

                    OnData?.Invoke(id, new ArraySegment<byte>(msg), ChannelType.Reliable);
                }

                return true;
            } catch (Exception ex) {
                Debug.Log($"[LocalTransport] Read error from {id}: {ex.Message}");
                return false;
            }
        }

        private void SendFramed(TcpClient client, ArraySegment<byte> payload) {
            try {
                if (client == null || !client.Connected) return;

                NetworkStream stream = client.GetStream();
                int len = payload.Count;

                // 4-byte LE length header
                byte[] header = new byte[4];
                header[0] = (byte)len;
                header[1] = (byte)(len >> 8);
                header[2] = (byte)(len >> 16);
                header[3] = (byte)(len >> 24);

                stream.Write(header, 0, 4);
                stream.Write(payload.Array, payload.Offset, payload.Count);
            } catch (Exception ex) {
                Debug.LogWarning($"[LocalTransport] Send error: {ex.Message}");
            }
        }

        private void DropConnection(TcpClient client, DisconnectReason reason) {
            if (_clientToId.TryGetValue(client, out var id)) {
                // Server-side drop
                _clientToId.Remove(client);
                _idToClient.Remove(id.Value);
                _receiveBuffers.Remove(client);
                CloseSocket(client);
                Debug.Log($"[LocalTransport] Connection {id} dropped ({reason})");
                OnDisconnected?.Invoke(id, reason);
            } else if (client == _clientSocket) {
                // Client-side: lost connection to server
                _receiveBuffers.Remove(client);
                CloseSocket(client);
                _clientSocket = null;
                _clientConnected = false;
                Debug.Log($"[LocalTransport] Lost server connection ({reason})");
                OnDisconnected?.Invoke(_serverConnId, reason);
            }
        }

        private static void CloseSocket(TcpClient client) {
            try { client?.Close(); } catch { }
        }

        private static bool IsRemoteClosed(TcpClient client) {
            try {
                Socket socket = client.Client;
                return socket == null
                       || (socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
            } catch {
                return true;
            }
        }

        private void ProcessPendingClientConnect() {
            if (!_clientConnectCompleted)
                return;

            _clientConnectCompleted = false;

            if (_clientConnectSucceeded) {
                if (_clientSocket == null || !_clientSocket.Connected) {
                    _clientConnectSucceeded = false;
                    _clientConnectError = "Socket was not connected when processed on main thread.";
                } else {
                    _serverConnId = new ConnectionId(_nextId++);
                    _receiveBuffers[_clientSocket] = new ReceiveBuffer();
                    _clientConnected = true;

                    Debug.Log($"[LocalTransport] Client connected to server as {_serverConnId}");
                    OnConnected?.Invoke(_serverConnId);
                    return;
                }
            }

            Debug.LogError($"[LocalTransport] Client connection failed: {_clientConnectError}");
            CloseSocket(_clientSocket);
            _clientSocket = null;
            _clientConnected = false;
            IsRunning = false;
            Role = NetRole.None;
        }

        // ---- Unity lifecycle ----

        private void Awake() {
            Initialize();
        }

        private void OnDestroy() {
            Shutdown();
        }

        private void OnApplicationQuit() {
            Shutdown();
        }

        // ---- TCP receive buffer (handles stream reassembly) ----

        private class ReceiveBuffer {
            public byte[] Data = new byte[8192];
            public int Count;

            public void Append(byte[] src, int offset, int count) {
                EnsureCapacity(Count + count);
                Buffer.BlockCopy(src, offset, Data, Count, count);
                Count += count;
            }

            /// <summary>
            /// Tries to extract one complete message from the buffer.
            /// Returns null if not enough data yet.
            /// </summary>
            public byte[] TryExtractMessage() {
                if (Count < 4) return null;

                int len = Data[0]
                          | (Data[1] << 8)
                          | (Data[2] << 16)
                          | (Data[3] << 24);

                if (len < 0 || len > 1024 * 1024) {
                    // Corrupt frame — dump buffer
                    Count = 0;
                    return null;
                }

                if (Count < 4 + len) return null;

                byte[] msg = new byte[len];
                Buffer.BlockCopy(Data, 4, msg, 0, len);

                // Shift remaining data forward
                int remaining = Count - 4 - len;
                if (remaining > 0)
                    Buffer.BlockCopy(Data, 4 + len, Data, 0, remaining);
                Count = remaining;

                return msg;
            }

            private void EnsureCapacity(int needed) {
                if (needed <= Data.Length) return;
                int newSize = Math.Max(Data.Length * 2, needed);
                Array.Resize(ref Data, newSize);
            }
        }
    }
}
