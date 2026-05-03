using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace SegNet {

    /// <summary>
    /// Direct transport for testing without Steam.
    ///
    /// Reliable traffic goes over TCP. Unreliable traffic goes over UDP on the same
    /// host/port pair, with a tiny transport-level handshake that associates each
    /// client's UDP endpoint with its existing TCP connection.
    ///
    /// TCP frame format: [4 bytes LE length][1 byte frameType][payload bytes]
    /// UDP server->client: [1 byte packetType][payload bytes]
    /// UDP client->server: [1 byte packetType][4 bytes connId][4 bytes token][payload bytes]
    /// </summary>
    public class LocalTransport : MonoBehaviour, ITransport {
        private const byte TcpFrameReliableData = 0x00;
        private const byte TcpFrameControl = 0x7F;

        private const byte TcpControlAssignUdp = 0x01;

        private const byte UdpPacketHello = 0x01;
        private const byte UdpPacketData = 0x02;

        private const float UdpHelloIntervalSeconds = 1f;

        private int port = 8000;
        private string host = "127.0.0.1";

        public NetRole Role { get; private set; } = NetRole.None;
        public bool IsRunning { get; private set; }

        // StreamManager adds its own whole-message prefix byte before handing packets to
        // the transport. Keep one extra byte free for our UDP packet header too.
        public int MaxPacketSize => 1191;

        public event Action<ConnectionId> OnConnected;
        public event Action<ConnectionId, DisconnectReason> OnDisconnected;
        public event Action<ConnectionId, ArraySegment<byte>, ChannelType> OnData;

        // ---- Server TCP state ----
        private TcpListener _listener;
        private readonly Dictionary<int, TcpClient> _idToClient = new Dictionary<int, TcpClient>();
        private readonly Dictionary<TcpClient, ConnectionId> _clientToId =
            new Dictionary<TcpClient, ConnectionId>();

        // ---- Server UDP state ----
        private UdpClient _serverUdpSocket;
        private readonly Dictionary<int, IPEndPoint> _idToUdpEndpoint =
            new Dictionary<int, IPEndPoint>();
        private readonly Dictionary<string, ConnectionId> _udpEndpointToId =
            new Dictionary<string, ConnectionId>();
        private readonly Dictionary<int, int> _idToUdpToken = new Dictionary<int, int>();

        // ---- Client TCP state ----
        private TcpClient _clientSocket;
        private ConnectionId _serverConnId;
        private bool _clientConnected;

        // ---- Client UDP state ----
        private UdpClient _clientUdpSocket;
        private int _serverUdpRemoteConnectionId = -1;
        private int _clientUdpToken;
        private float _nextUdpHelloAt;

        // ---- Shared ----
        private readonly Dictionary<TcpClient, ReceiveBuffer> _receiveBuffers =
            new Dictionary<TcpClient, ReceiveBuffer>();
        private int _nextId = 1;
        private readonly byte[] _readScratch = new byte[8192];
        private bool _clientConnectCompleted;
        private bool _clientConnectSucceeded;
        private string _clientConnectError;

        // ---- ITransport ----

        public void Configure(string hostAddress, int portNumber) {
            host = string.IsNullOrWhiteSpace(hostAddress) ? "127.0.0.1" : hostAddress;
            port = portNumber > 0 ? portNumber : 8000;
        }

        public void Initialize() {
            Role = NetRole.None;
            IsRunning = false;
        }

        public void Shutdown() {
            Stop();
        }

        public void StartServer() {
            if (IsRunning) return;

            try {
                Role = NetRole.Server;
                IsRunning = true;

                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Server.NoDelay = true;
                _listener.Start();

                _serverUdpSocket = new UdpClient(port);

                Debug.Log($"[LocalTransport] Server listening on TCP/UDP port {port}");
            } catch (Exception ex) {
                Debug.LogError($"[LocalTransport] Server start failed: {ex.Message}");
                Stop();
            }
        }

        public void StartClient() {
            if (IsRunning) return;

            try {
                Role = NetRole.Client;
                IsRunning = true;
                _clientConnected = false;

                _clientUdpSocket = new UdpClient(0);
                _clientUdpSocket.Connect(host, port);

                _clientSocket = new TcpClient();
                _clientSocket.NoDelay = true;
                _clientSocket.BeginConnect(host, port, OnClientConnectComplete, null);
                Debug.Log($"[LocalTransport] Client connecting to {host}:{port} (TCP + UDP)");
            } catch (Exception ex) {
                Debug.LogError($"[LocalTransport] Client connect failed: {ex.Message}");
                Stop();
            }
        }

        public void Stop() {
            // Close server TCP listener
            if (_listener != null) {
                _listener.Stop();
                _listener = null;
            }

            // Close server UDP socket
            if (_serverUdpSocket != null) {
                CloseUdpSocket(_serverUdpSocket);
                _serverUdpSocket = null;
            }

            // Close all server-side client sockets
            foreach (var kvp in _idToClient)
                CloseSocket(kvp.Value);

            _idToClient.Clear();
            _clientToId.Clear();
            _idToUdpEndpoint.Clear();
            _udpEndpointToId.Clear();
            _idToUdpToken.Clear();

            // Close client TCP socket
            if (_clientSocket != null) {
                CloseSocket(_clientSocket);
                _clientSocket = null;
            }
            _clientConnected = false;

            // Close client UDP socket
            if (_clientUdpSocket != null) {
                CloseUdpSocket(_clientUdpSocket);
                _clientUdpSocket = null;
            }

            _serverUdpRemoteConnectionId = -1;
            _clientUdpToken = 0;
            _nextUdpHelloAt = 0f;

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
            if (channel == ChannelType.Unreliable) {
                SendUnreliable(connection, payload);
                return;
            }

            TcpClient socket = null;

            if (Role == NetRole.Server)
                _idToClient.TryGetValue(connection.Value, out socket);
            else if (Role == NetRole.Client && connection == _serverConnId)
                socket = _clientSocket;

            if (socket == null || !socket.Connected) return;
            SendTcpFrame(socket, TcpFrameReliableData, payload);
        }

        public void Broadcast(ArraySegment<byte> payload, ChannelType channel) {
            if (Role != NetRole.Server)
                return;

            if (channel == ChannelType.Unreliable) {
                foreach (var kvp in _idToUdpEndpoint)
                    SendUdpDataToClient(kvp.Value, payload);
                return;
            }

            foreach (var kvp in _idToClient)
                SendTcpFrame(kvp.Value, TcpFrameReliableData, payload);
        }

        public void Disconnect(ConnectionId connection) {
            if (Role == NetRole.Server && _idToClient.TryGetValue(connection.Value, out var client)) {
                RemoveUdpRegistration(connection);
                CloseSocket(client);
                _clientToId.Remove(client);
                _idToClient.Remove(connection.Value);
                _receiveBuffers.Remove(client);
                OnDisconnected?.Invoke(connection, DisconnectReason.LocalShutdown);
            }
        }

        // ---- Server polling ----

        private void PollServer() {
            while (_listener != null && _listener.Pending()) {
                TcpClient newClient = _listener.AcceptTcpClient();
                newClient.NoDelay = true;

                var id = new ConnectionId(_nextId++);
                _idToClient[id.Value] = newClient;
                _clientToId[newClient] = id;
                _receiveBuffers[newClient] = new ReceiveBuffer();

                int udpToken = CreateUdpToken();
                _idToUdpToken[id.Value] = udpToken;
                SendUdpAssignmentControl(newClient, id, udpToken);

                Debug.Log($"[LocalTransport] Server accepted connection {id}");
                OnConnected?.Invoke(id);
            }

            var disconnected = new List<TcpClient>();
            foreach (var kvp in _clientToId) {
                TcpClient client = kvp.Key;
                ConnectionId id = kvp.Value;

                if (!ReadFromSocket(client, id))
                    disconnected.Add(client);
            }

            foreach (var client in disconnected)
                DropConnection(client, DisconnectReason.RemoteShutdown);

            PollServerUdp();
        }

        private void PollServerUdp() {
            try {
                while (_serverUdpSocket != null && _serverUdpSocket.Available > 0) {
                    IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] packet = _serverUdpSocket.Receive(ref remote);
                    if (packet == null || packet.Length == 0)
                        continue;

                    switch (packet[0]) {
                        case UdpPacketHello:
                            HandleServerUdpHello(remote, packet);
                            break;

                        case UdpPacketData:
                            HandleServerUdpData(remote, packet);
                            break;

                        default:
                            Debug.LogWarning($"[LocalTransport] Unknown UDP packet type 0x{packet[0]:X2} from {remote}");
                            break;
                    }
                }
            } catch (Exception ex) {
                Debug.LogWarning($"[LocalTransport] Server UDP poll error: {ex.Message}");
            }
        }

        // ---- Client polling ----

        private void PollClient() {
            ProcessPendingClientConnect();

            if (_clientSocket == null || !_clientConnected)
                return;

            if (!ReadFromSocket(_clientSocket, _serverConnId)) {
                DropConnection(_clientSocket, DisconnectReason.RemoteShutdown);
                return;
            }

            MaybeSendClientUdpHello(force: false);
            PollClientUdp();
        }

        private void PollClientUdp() {
            try {
                while (_clientUdpSocket != null && _clientUdpSocket.Available > 0) {
                    IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] packet = _clientUdpSocket.Receive(ref remote);
                    if (packet == null || packet.Length <= 1)
                        continue;

                    if (packet[0] != UdpPacketData) {
                        Debug.LogWarning($"[LocalTransport] Unexpected UDP packet type 0x{packet[0]:X2} from server.");
                        continue;
                    }

                    OnData?.Invoke(_serverConnId,
                        new ArraySegment<byte>(packet, 1, packet.Length - 1),
                        ChannelType.Unreliable);
                }
            } catch (Exception ex) {
                Debug.LogWarning($"[LocalTransport] Client UDP poll error: {ex.Message}");
            }
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

                while (true) {
                    if (!buf.TryExtractFrame(out byte frameType, out byte[] msg))
                        break;

                    switch (frameType) {
                        case TcpFrameReliableData:
                            OnData?.Invoke(id, new ArraySegment<byte>(msg), ChannelType.Reliable);
                            break;

                        case TcpFrameControl:
                            HandleTcpControlFrame(id, msg);
                            break;

                        default:
                            Debug.LogWarning($"[LocalTransport] Unknown TCP frame type 0x{frameType:X2} from {id}");
                            break;
                    }
                }

                return true;
            } catch (Exception ex) {
                Debug.Log($"[LocalTransport] Read error from {id}: {ex.Message}");
                return false;
            }
        }

        private void SendTcpFrame(TcpClient client, byte frameType, ArraySegment<byte> payload) {
            try {
                if (client == null || !client.Connected) return;

                NetworkStream stream = client.GetStream();
                int len = payload.Count + 1;

                byte[] header = new byte[4];
                header[0] = (byte)len;
                header[1] = (byte)(len >> 8);
                header[2] = (byte)(len >> 16);
                header[3] = (byte)(len >> 24);

                stream.Write(header, 0, 4);
                stream.WriteByte(frameType);
                if (payload.Count > 0)
                    stream.Write(payload.Array, payload.Offset, payload.Count);
            } catch (Exception ex) {
                Debug.LogWarning($"[LocalTransport] TCP send error: {ex.Message}");
            }
        }

        private void SendUdpAssignmentControl(TcpClient client, ConnectionId id, int udpToken) {
            byte[] payload = new byte[1 + 4 + 4];
            payload[0] = TcpControlAssignUdp;
            WriteInt(payload, 1, id.Value);
            WriteInt(payload, 5, udpToken);
            SendTcpFrame(client, TcpFrameControl, new ArraySegment<byte>(payload));
        }

        private void SendUnreliable(ConnectionId connection, ArraySegment<byte> payload) {
            if (payload.Count <= 0)
                return;

            if (Role == NetRole.Server) {
                if (_serverUdpSocket == null)
                    return;

                if (_idToUdpEndpoint.TryGetValue(connection.Value, out var endpoint))
                    SendUdpDataToClient(endpoint, payload);
                return;
            }

            if (Role == NetRole.Client && connection == _serverConnId)
                SendUdpDataToServer(payload);
        }

        private void SendUdpDataToClient(IPEndPoint endpoint, ArraySegment<byte> payload) {
            try {
                if (_serverUdpSocket == null || endpoint == null || payload.Count <= 0)
                    return;

                byte[] packet = new byte[1 + payload.Count];
                packet[0] = UdpPacketData;
                Buffer.BlockCopy(payload.Array, payload.Offset, packet, 1, payload.Count);
                _serverUdpSocket.Send(packet, packet.Length, endpoint);
            } catch (Exception ex) {
                Debug.LogWarning($"[LocalTransport] Server UDP send error: {ex.Message}");
            }
        }

        private void SendUdpDataToServer(ArraySegment<byte> payload) {
            try {
                if (_clientUdpSocket == null || payload.Count <= 0)
                    return;

                if (_serverUdpRemoteConnectionId < 0 || _clientUdpToken == 0)
                    return;

                MaybeSendClientUdpHello(force: false);

                byte[] packet = new byte[1 + 4 + 4 + payload.Count];
                packet[0] = UdpPacketData;
                WriteInt(packet, 1, _serverUdpRemoteConnectionId);
                WriteInt(packet, 5, _clientUdpToken);
                Buffer.BlockCopy(payload.Array, payload.Offset, packet, 9, payload.Count);
                _clientUdpSocket.Send(packet, packet.Length);
            } catch (Exception ex) {
                Debug.LogWarning($"[LocalTransport] Client UDP send error: {ex.Message}");
            }
        }

        private void MaybeSendClientUdpHello(bool force) {
            if (Role != NetRole.Client || !_clientConnected || _clientUdpSocket == null)
                return;

            if (_serverUdpRemoteConnectionId < 0 || _clientUdpToken == 0)
                return;

            float now = Time.realtimeSinceStartup;
            if (!force && now < _nextUdpHelloAt)
                return;

            try {
                byte[] packet = new byte[1 + 4 + 4];
                packet[0] = UdpPacketHello;
                WriteInt(packet, 1, _serverUdpRemoteConnectionId);
                WriteInt(packet, 5, _clientUdpToken);
                _clientUdpSocket.Send(packet, packet.Length);
                _nextUdpHelloAt = now + UdpHelloIntervalSeconds;
            } catch (Exception ex) {
                Debug.LogWarning($"[LocalTransport] Client UDP hello send error: {ex.Message}");
            }
        }

        private void HandleTcpControlFrame(ConnectionId id, byte[] payload) {
            if (payload == null || payload.Length < 1)
                return;

            byte controlType = payload[0];
            if (controlType != TcpControlAssignUdp) {
                Debug.LogWarning($"[LocalTransport] Unknown TCP control message 0x{controlType:X2} from {id}");
                return;
            }

            if (Role != NetRole.Client || payload.Length < 9)
                return;

            _serverUdpRemoteConnectionId = ReadInt(payload, 1);
            _clientUdpToken = ReadInt(payload, 5);
            _nextUdpHelloAt = 0f;
            MaybeSendClientUdpHello(force: true);
        }

        private void HandleServerUdpHello(IPEndPoint remote, byte[] packet) {
            if (packet == null || packet.Length < 9)
                return;

            int connValue = ReadInt(packet, 1);
            int token = ReadInt(packet, 5);
            if (!ValidateUdpClientIdentity(connValue, token))
                return;

            RegisterUdpEndpoint(connValue, remote);
        }

        private void HandleServerUdpData(IPEndPoint remote, byte[] packet) {
            if (packet == null || packet.Length < 9)
                return;

            int connValue = ReadInt(packet, 1);
            int token = ReadInt(packet, 5);
            if (!ValidateUdpClientIdentity(connValue, token))
                return;

            RegisterUdpEndpoint(connValue, remote);

            int payloadCount = packet.Length - 9;
            if (payloadCount <= 0)
                return;

            var connection = new ConnectionId(connValue);
            OnData?.Invoke(connection,
                new ArraySegment<byte>(packet, 9, payloadCount),
                ChannelType.Unreliable);
        }

        private bool ValidateUdpClientIdentity(int connValue, int token) {
            if (!_idToClient.ContainsKey(connValue))
                return false;

            if (!_idToUdpToken.TryGetValue(connValue, out int expectedToken))
                return false;

            return token == expectedToken;
        }

        private void RegisterUdpEndpoint(int connValue, IPEndPoint endpoint) {
            if (endpoint == null)
                return;

            string key = EndpointKey(endpoint);

            if (_idToUdpEndpoint.TryGetValue(connValue, out var existing)) {
                string existingKey = EndpointKey(existing);
                if (existingKey == key)
                    return;

                _udpEndpointToId.Remove(existingKey);
            }

            _idToUdpEndpoint[connValue] = endpoint;
            _udpEndpointToId[key] = new ConnectionId(connValue);
        }

        private void RemoveUdpRegistration(ConnectionId id) {
            _idToUdpToken.Remove(id.Value);

            if (_idToUdpEndpoint.TryGetValue(id.Value, out var endpoint)) {
                _udpEndpointToId.Remove(EndpointKey(endpoint));
                _idToUdpEndpoint.Remove(id.Value);
            }
        }

        private void DropConnection(TcpClient client, DisconnectReason reason) {
            if (_clientToId.TryGetValue(client, out var id)) {
                RemoveUdpRegistration(id);
                _clientToId.Remove(client);
                _idToClient.Remove(id.Value);
                _receiveBuffers.Remove(client);
                CloseSocket(client);
                Debug.Log($"[LocalTransport] Connection {id} dropped ({reason})");
                OnDisconnected?.Invoke(id, reason);
            } else if (client == _clientSocket) {
                _receiveBuffers.Remove(client);
                CloseSocket(client);
                _clientSocket = null;
                _clientConnected = false;
                if (_clientUdpSocket != null) {
                    CloseUdpSocket(_clientUdpSocket);
                    _clientUdpSocket = null;
                }
                _serverUdpRemoteConnectionId = -1;
                _clientUdpToken = 0;
                _nextUdpHelloAt = 0f;
                Debug.Log($"[LocalTransport] Lost server connection ({reason})");
                OnDisconnected?.Invoke(_serverConnId, reason);
            }
        }

        private static void CloseSocket(TcpClient client) {
            try { client?.Close(); } catch { }
        }

        private static void CloseUdpSocket(UdpClient socket) {
            try { socket?.Close(); } catch { }
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

        private static string EndpointKey(IPEndPoint endpoint) {
            return endpoint.Address + ":" + endpoint.Port;
        }

        private static int CreateUdpToken() {
            int token = Guid.NewGuid().GetHashCode();
            return token == 0 ? 1 : token;
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
                    _nextUdpHelloAt = 0f;

                    Debug.Log($"[LocalTransport] Client connected to server as {_serverConnId}");
                    OnConnected?.Invoke(_serverConnId);
                    return;
                }
            }

            Debug.LogError($"[LocalTransport] Client connection failed: {_clientConnectError}");
            CloseSocket(_clientSocket);
            _clientSocket = null;
            _clientConnected = false;
            _serverUdpRemoteConnectionId = -1;
            _clientUdpToken = 0;
            _nextUdpHelloAt = 0f;
            if (_clientUdpSocket != null) {
                CloseUdpSocket(_clientUdpSocket);
                _clientUdpSocket = null;
            }
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

        // ---- Helpers ----

        private static void WriteInt(byte[] buffer, int offset, int value) {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private static int ReadInt(byte[] buffer, int offset) {
            return buffer[offset]
                   | (buffer[offset + 1] << 8)
                   | (buffer[offset + 2] << 16)
                   | (buffer[offset + 3] << 24);
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
            /// Tries to extract one complete frame from the buffer.
            /// Returns false if not enough data is buffered yet.
            /// </summary>
            public bool TryExtractFrame(out byte frameType, out byte[] payload) {
                frameType = 0;
                payload = null;

                if (Count < 4) return false;

                int len = Data[0]
                          | (Data[1] << 8)
                          | (Data[2] << 16)
                          | (Data[3] << 24);

                if (len <= 0 || len > 1024 * 1024) {
                    Count = 0;
                    return false;
                }

                if (Count < 4 + len) return false;

                frameType = Data[4];

                int payloadLength = len - 1;
                payload = new byte[payloadLength];
                if (payloadLength > 0)
                    Buffer.BlockCopy(Data, 5, payload, 0, payloadLength);

                int remaining = Count - 4 - len;
                if (remaining > 0)
                    Buffer.BlockCopy(Data, 4 + len, Data, 0, remaining);
                Count = remaining;

                return true;
            }

            private void EnsureCapacity(int needed) {
                if (needed <= Data.Length) return;
                int newSize = Math.Max(Data.Length * 2, needed);
                Array.Resize(ref Data, newSize);
            }
        }
    }
}
