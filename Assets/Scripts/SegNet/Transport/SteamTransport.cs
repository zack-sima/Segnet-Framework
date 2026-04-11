// NOTE: requires SteamManager prefab in scene and Steamworks.NET installed.
// AppID should be 480 (Spacewar) for testing.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using Steamworks;

namespace SegNet {

    public class SteamTransport : MonoBehaviour, ITransport {
        // Hard-coded test room for simple host/join
        private const string ROOM_NAME = "TEST_ROOM_42069";

        // Lobby / connection callbacks
        private Callback<LobbyCreated_t> _lobbyCreated;
        private Callback<LobbyMatchList_t> _lobbyMatchList;
        private Callback<LobbyEnter_t> _lobbyEnter;
        private Callback<GameLobbyJoinRequested_t> _lobbyJoinRequested;
        private Callback<SteamNetConnectionStatusChangedCallback_t> _connStatusChanged;
        private bool _initialized;

        private CSteamID _lobbyId;
        private HSteamListenSocket _listenSocket = HSteamListenSocket.Invalid;

        private readonly Dictionary<HSteamNetConnection, ConnectionId> _connToId =
            new Dictionary<HSteamNetConnection, ConnectionId>();
        private readonly Dictionary<int, HSteamNetConnection> _idToConn =
            new Dictionary<int, HSteamNetConnection>();
        private int _nextConnectionId = 1;

        public NetRole Role { get; private set; } = NetRole.None;
        public bool IsRunning { get; private set; }
        public int MaxPacketSize { get; private set; } = 1200;

        public event Action<ConnectionId> OnConnected;
        public event Action<ConnectionId, DisconnectReason> OnDisconnected;
        public event Action<ConnectionId, ArraySegment<byte>, ChannelType> OnData;

        public void Initialize() {
            if (_initialized)
                return;

            if (!SteamManager.Initialized) {
                Debug.LogWarning("[SteamTransport] SteamManager not initialized.");
                return;
            }

            _lobbyCreated = Callback<LobbyCreated_t>.Create(OnLobbyCreated);
            _lobbyMatchList = Callback<LobbyMatchList_t>.Create(OnLobbyMatchList);
            _lobbyEnter = Callback<LobbyEnter_t>.Create(OnLobbyEnter);
            _lobbyJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnLobbyJoinRequested);
            _connStatusChanged = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);

            Role = NetRole.None;
            IsRunning = false;
            _initialized = true;
        }

        public void Shutdown() {
            try {
                Stop();
            } catch (Exception ex) {
                if (!Application.isEditor) {
                    Debug.LogError("[SteamTransport] Error during Shutdown: " + ex);
                } else {
                    Debug.Log("Editor terminated -- skipping SteamTransport Shutdown error.");
                }
            }
        }

        public void StartServer() {
            if (IsRunning)
                return;
            if (!SteamManager.Initialized) {
                Debug.LogWarning("[SteamTransport] Cannot start server: SteamManager not initialized.");
                FailStartup(DisconnectReason.TransportError);
                return;
            }

            Debug.Log("[SteamTransport] StartServer -> creating lobby");
            Role = NetRole.Server;
            IsRunning = true;

            SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, 8);
        }

        public void StartClient() {
            if (IsRunning)
                return;
            if (!SteamManager.Initialized) {
                Debug.LogWarning("[SteamTransport] Cannot start client: SteamManager not initialized.");
                FailStartup(DisconnectReason.TransportError);
                return;
            }

            Debug.Log("[SteamTransport] StartClient -> requesting lobby list");
            Role = NetRole.Client;
            IsRunning = true;

            SteamMatchmaking.AddRequestLobbyListStringFilter(
                "room", ROOM_NAME, ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.RequestLobbyList();
        }

        public void Stop() {
            foreach (var kvp in _idToConn) {
                SteamNetworkingSockets.CloseConnection(
                    kvp.Value, 0, "Shutdown", false);
            }

            _connToId.Clear();
            _idToConn.Clear();

            if (_listenSocket != HSteamListenSocket.Invalid) {
                SteamNetworkingSockets.CloseListenSocket(_listenSocket);
                _listenSocket = HSteamListenSocket.Invalid;
            }

            if (SteamManager.Initialized && _lobbyId.IsValid()) {
                SteamMatchmaking.LeaveLobby(_lobbyId);
                _lobbyId = CSteamID.Nil;
            }

            Role = NetRole.None;
            IsRunning = false;
        }

        public void Poll() {
            if (!SteamManager.Initialized || !IsRunning)
                return;

            SteamAPI.RunCallbacks();
            ReceiveAllMessages();
        }

        public void Send(ConnectionId connection, ArraySegment<byte> payload, ChannelType channel) {
            if (!_idToConn.TryGetValue(connection.Value, out var hConn))
                return;

            SendInternal(hConn, payload, channel);
        }

        public void Broadcast(ArraySegment<byte> payload, ChannelType channel) {
            foreach (var kvp in _idToConn) {
                SendInternal(kvp.Value, payload, channel);
            }
        }

        public void Disconnect(ConnectionId connection) {
            if (!_idToConn.TryGetValue(connection.Value, out var hConn))
                return;

            SteamNetworkingSockets.CloseConnection(
                hConn, 0, "Disconnect", false);

            _idToConn.Remove(connection.Value);
            _connToId.Remove(hConn);

            OnDisconnected?.Invoke(connection, DisconnectReason.LocalShutdown);
        }

        // ---------- Lobby callbacks ----------

        private void OnLobbyJoinRequested(GameLobbyJoinRequested_t cb) {
            CSteamID lobbyId = cb.m_steamIDLobby;
            Debug.Log("[SteamTransport] Overlay join requested for lobby " + lobbyId);
            SteamMatchmaking.JoinLobby(lobbyId);
        }

        private void OnLobbyCreated(LobbyCreated_t cb) {
            if (cb.m_eResult != EResult.k_EResultOK) {
                Debug.LogError("[SteamTransport] Lobby creation failed: " + cb.m_eResult);
                FailStartup(DisconnectReason.TransportError);
                return;
            }

            _lobbyId = new CSteamID(cb.m_ulSteamIDLobby);
            Debug.Log("[SteamTransport] Lobby created: " + _lobbyId);

            SteamMatchmaking.SetLobbyData(_lobbyId, "room", ROOM_NAME);
            SteamMatchmaking.SetLobbyJoinable(_lobbyId, true);

            _listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(0, 0, null);
            Debug.Log("[SteamTransport] Listen socket: " + _listenSocket.m_HSteamListenSocket);
        }

        private void OnLobbyMatchList(LobbyMatchList_t cb) {
            Debug.Log("[SteamTransport] Lobby match list count = " + cb.m_nLobbiesMatching);

            if (cb.m_nLobbiesMatching <= 0) {
                Debug.LogWarning("[SteamTransport] No lobbies found.");
                FailStartup(DisconnectReason.TransportError);
                return;
            }

            CSteamID lobbyId = SteamMatchmaking.GetLobbyByIndex(0);
            Debug.Log("[SteamTransport] Joining lobby " + lobbyId);
            SteamMatchmaking.JoinLobby(lobbyId);
        }

        private void OnLobbyEnter(LobbyEnter_t cb) {
            _lobbyId = new CSteamID(cb.m_ulSteamIDLobby);
            Debug.Log("[SteamTransport] Entered lobby " + _lobbyId);

            if (Role == NetRole.Client) {
                CSteamID hostSteamId = SteamMatchmaking.GetLobbyOwner(_lobbyId);
                Debug.Log("[SteamTransport] Lobby owner SteamID = " + hostSteamId);

                SteamNetworkingIdentity identity = new SteamNetworkingIdentity();
                identity.SetSteamID(hostSteamId);

                var conn = SteamNetworkingSockets.ConnectP2P(ref identity, 0, 0, null);
                Debug.Log("[SteamTransport] ConnectP2P handle = " + conn.m_HSteamNetConnection);
            }
        }

        // ---------- Connection status ----------

        private void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t cb) {
            var info = cb.m_info;
            var state = info.m_eState;

            Debug.Log("[SteamTransport] Connection status: " + state +
                      " conn=" + cb.m_hConn.m_HSteamNetConnection);

            switch (state) {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                    if (Role == NetRole.Server) {
                        SteamNetworkingSockets.AcceptConnection(cb.m_hConn);
                    }
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    RegisterConnection(cb.m_hConn);
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                    HandleDisconnect(cb.m_hConn, DisconnectReason.RemoteShutdown);
                    break;
            }
        }

        private void RegisterConnection(HSteamNetConnection hConn) {
            if (_connToId.TryGetValue(hConn, out _))
                return;

            var id = new ConnectionId(_nextConnectionId++);
            _connToId[hConn] = id;
            _idToConn[id.Value] = hConn;

            OnConnected?.Invoke(id);
        }

        private void HandleDisconnect(HSteamNetConnection hConn, DisconnectReason reason) {
            if (!_connToId.TryGetValue(hConn, out var id)) {
                if (Role == NetRole.Client)
                    FailStartup(reason);
                return;
            }

            _connToId.Remove(hConn);
            _idToConn.Remove(id.Value);

            OnDisconnected?.Invoke(id, reason);
        }

        private void FailStartup(DisconnectReason reason) {
            Stop();
            OnDisconnected?.Invoke(ConnectionId.Invalid, reason);
        }

        // ---------- Send / Receive internals ----------

        private void SendInternal(HSteamNetConnection hConn, ArraySegment<byte> payload, ChannelType channel) {
            if (payload.Count <= 0)
                return;

            int flags = channel == ChannelType.Reliable
                ? Constants.k_nSteamNetworkingSend_Reliable
                : Constants.k_nSteamNetworkingSend_Unreliable;

            GCHandle handle = GCHandle.Alloc(payload.Array, GCHandleType.Pinned);
            try {
                IntPtr ptr = handle.AddrOfPinnedObject() + payload.Offset;
                SteamNetworkingSockets.SendMessageToConnection(
                    hConn,
                    ptr,
                    (uint)payload.Count,
                    flags,
                    out long _);
            } finally {
                handle.Free();
            }
        }

        private void ReceiveAllMessages() {
            var msgPtrs = new IntPtr[256];

            foreach (var kvp in _connToId) {
                HSteamNetConnection hConn = kvp.Key;
                ConnectionId id = kvp.Value;

                while (true) {
                    int msgCount = SteamNetworkingSockets.ReceiveMessagesOnConnection(
                        hConn,
                        msgPtrs,
                        msgPtrs.Length);

                    if (msgCount <= 0)
                        break;

                    for (int i = 0; i < msgCount; i++) {
                        IntPtr ptr = msgPtrs[i];
                        if (ptr == IntPtr.Zero)
                            continue;

                        SteamNetworkingMessage_t msg =
                            (SteamNetworkingMessage_t)Marshal.PtrToStructure(
                                ptr,
                                typeof(SteamNetworkingMessage_t));

                        if (msg.m_cbSize > 0 && msg.m_pData != IntPtr.Zero) {
                            byte[] buffer = new byte[msg.m_cbSize];
                            Marshal.Copy(msg.m_pData, buffer, 0, (int)msg.m_cbSize);
                            var segment = new ArraySegment<byte>(buffer, 0, buffer.Length);

                            OnData?.Invoke(id, segment, ChannelType.Unreliable);
                        }

                        SteamNetworkingMessage_t.Release(ptr);
                    }
                }
            }
        }

        // ---------- Unity lifecycle ----------

        private void Awake() {
            Initialize();
        }

        private void OnDestroy() {
            Shutdown();
            _initialized = false;
        }
    }
}
