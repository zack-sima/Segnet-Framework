//NOTE: not publishing to non-Steamworks compatible platforms (only OSX, Windows, Linux)
// https://github.com/rlabrecque/Steamworks.NET.git?path=/com.rlabrecque.steamworks.net#2025.162.1

// NOTE: standalone Steam stress tester; if used without NetworkManager,
// it still requires a SteamManager in scene and Steamworks.NET installed.
// AppID should be 480 (Spacewar) for testing.

using UnityEngine;
using Steamworks;
using System;
using System.Runtime.InteropServices;
using UnityEngine.UI;
using TMPro;
using SegNet;

public class StressTester : MonoBehaviour {

    //TESTING: slider for packet testing
    [SerializeField] private Slider packetSendRateSlider; // in bytes/sec
    [SerializeField] private TMP_Text packetSendRateLabel, packetReceiveRateLabel;

    // Size (in bytes) of each test packet we send
    [SerializeField] private int testPacketSize = 64;

    //TESTING: hard-coded room id
    private const string ROOM_NAME = "TEST_ROOM_42069";

    // Lobby callbacks -- NOTE: ignore C# warnings for unused (they are used!)
    private Callback<LobbyCreated_t> m_LobbyCreated;
    private Callback<LobbyMatchList_t> m_LobbyMatchList;
    private Callback<LobbyEnter_t> m_LobbyEnter;
    private Callback<GameLobbyJoinRequested_t> m_LobbyJoinRequested;

    // Connection status callback
    private Callback<SteamNetConnectionStatusChangedCallback_t> m_ConnectionStatusChanged;

    private CSteamID m_LobbyId;
    private bool m_IsHost;
    private bool m_IsClient;

    private HSteamListenSocket m_ListenSocket;
    private HSteamNetConnection m_Connection;

    // --- Stress test state ---
    private float sendBudget = 0f;                 // bytes allowed to send (accumulates over time)
    private int bytesSentThisSecond = 0;
    private int bytesReceivedThisSecond = 0;
    private int lastSecondBytesSent = 0;
    private int lastSecondBytesReceived = 0;
    private float statsTimer = 0f;

    private float TargetBytesPerSecond =>
        packetSendRateSlider != null ? packetSendRateSlider.value : 0f;

    private void OnEnable() {
        if (!SteamManager.Initialized)
            return;

        m_LobbyCreated = Callback<LobbyCreated_t>.Create(OnLobbyCreated);
        m_LobbyMatchList = Callback<LobbyMatchList_t>.Create(OnLobbyMatchList);
        m_LobbyEnter = Callback<LobbyEnter_t>.Create(OnLobbyEnter);
        m_ConnectionStatusChanged = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);
        m_LobbyJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnLobbyJoinRequested);

        m_Connection = HSteamNetConnection.Invalid;
        m_ListenSocket = HSteamListenSocket.Invalid;
    }

    private void Start() {
        Application.targetFrameRate = 60;

        if (SteamManager.Initialized) {
            Debug.Log("Steam user: " + SteamFriends.GetPersonaName());

            // Example: 5 MB send buffer (default is 512 KB)
            {
                int sendBuffer = 5 * 1024 * 1024;
                GCHandle handle = GCHandle.Alloc(sendBuffer, GCHandleType.Pinned);
                SteamNetworkingUtils.SetConfigValue(
                    ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize,
                    ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global,
                    IntPtr.Zero,
                    ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                    handle.AddrOfPinnedObject()
                );
                handle.Free();
            }

            // Example: 5 MB/s send rate max (0 = "no limit", but some games clamp it)
            {
                int sendRateMax = 5 * 1024 * 1024;
                GCHandle handle = GCHandle.Alloc(sendRateMax, GCHandleType.Pinned);
                SteamNetworkingUtils.SetConfigValue(
                    ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax,
                    ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global,
                    IntPtr.Zero,
                    ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                    handle.AddrOfPinnedObject()
                );
                handle.Free();
            }
        } else {
            Debug.LogWarning("SteamManager not initialized.");
        }
    }


    private void Update() {
        if (packetSendRateLabel != null) {
            packetSendRateLabel.text =
                "Target Send Rate: " + TargetBytesPerSecond.ToString("F0") + " bytes/sec";
        }

        if (packetReceiveRateLabel != null) {
            packetReceiveRateLabel.text =
                $"Actual: Sent {lastSecondBytesSent} B/s | Recv {lastSecondBytesReceived} B/s";
        }

        if (!SteamManager.Initialized)
            return;

        SteamAPI.RunCallbacks();

        if (Input.GetKeyDown(KeyCode.H)) {
            StartHost();
        }

        if (Input.GetKeyDown(KeyCode.C)) {
            StartClient();
        }

        if (m_Connection != HSteamNetConnection.Invalid) {
            float dt = Time.deltaTime;

            // --- Stress send based on bytes/sec budget ---
            DoStressSend(dt);

            // Receive
            ReceiveMessages();

            // Stats update
            UpdateStats(dt);
        }
    }

    // ---------------- Host / Client entry ----------------

    private void StartHost() {
        if (!SteamManager.Initialized || m_IsHost)
            return;

        Debug.Log("Host: Creating lobby...");
        m_IsHost = true;
        m_IsClient = false;

        SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, 2);
    }

    private void StartClient() {
        if (!SteamManager.Initialized || m_IsClient)
            return;

        Debug.Log("Client: Requesting lobby list...");
        m_IsClient = true;
        m_IsHost = false;

        // Only search for our hard-coded room
        SteamMatchmaking.AddRequestLobbyListStringFilter(
            "room", ROOM_NAME, ELobbyComparison.k_ELobbyComparisonEqual);

        SteamMatchmaking.RequestLobbyList();
    }

    // ---------------- Lobby callbacks ----------------

    private void OnLobbyJoinRequested(GameLobbyJoinRequested_t cb) {
        // Fired when user accepts a lobby invite / join-game from overlay
        CSteamID lobbyId = cb.m_steamIDLobby;
        Debug.Log("Overlay: Join requested for lobby " + lobbyId);

        SteamMatchmaking.JoinLobby(lobbyId);
    }

    private void OnLobbyCreated(LobbyCreated_t cb) {
        if (cb.m_eResult != EResult.k_EResultOK) {
            Debug.LogError("Host: Lobby creation failed: " + cb.m_eResult);
            return;
        }

        m_LobbyId = new CSteamID(cb.m_ulSteamIDLobby);
        Debug.Log("Host: Lobby created: " + m_LobbyId);

        // Hard-coded room name for testing
        SteamMatchmaking.SetLobbyData(m_LobbyId, "room", ROOM_NAME);
        SteamMatchmaking.SetLobbyJoinable(m_LobbyId, true);

        m_ListenSocket = SteamNetworkingSockets.CreateListenSocketP2P(0, 0, null);
        Debug.Log("Host: Listen socket: " + m_ListenSocket.m_HSteamListenSocket);
    }

    private void OnLobbyMatchList(LobbyMatchList_t cb) {
        Debug.Log("Client: Lobby match list count = " + cb.m_nLobbiesMatching);

        if (cb.m_nLobbiesMatching <= 0) {
            Debug.LogWarning("Client: No lobbies found.");
            return;
        }

        CSteamID lobbyId = SteamMatchmaking.GetLobbyByIndex(0);
        Debug.Log("Client: Joining lobby " + lobbyId);
        SteamMatchmaking.JoinLobby(lobbyId);
    }

    private void OnLobbyEnter(LobbyEnter_t cb) {
        m_LobbyId = new CSteamID(cb.m_ulSteamIDLobby);
        Debug.Log((m_IsHost ? "Host" : "Client") + ": Entered lobby " + m_LobbyId);

        if (m_IsClient) {
            // client -> connect to lobby owner (host)
            CSteamID hostSteamId = SteamMatchmaking.GetLobbyOwner(m_LobbyId);
            Debug.Log("Client: Lobby owner SteamID = " + hostSteamId);

            SteamNetworkingIdentity identity = new SteamNetworkingIdentity();
            identity.SetSteamID(hostSteamId);

            m_Connection = SteamNetworkingSockets.ConnectP2P(ref identity, 0, 0, null);
            Debug.Log("Client: ConnectP2P handle = " + m_Connection.m_HSteamNetConnection);
        }
    }

    // ---------------- Connection status ----------------

    private void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t cb) {
        var info = cb.m_info;
        var state = info.m_eState; // ESteamNetworkingConnectionState

        Debug.Log("Connection status: " + state + " conn=" + cb.m_hConn.m_HSteamNetConnection);

        switch (state) {
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                if (m_IsHost) {
                    // host accepts incoming
                    SteamNetworkingSockets.AcceptConnection(cb.m_hConn);
                    m_Connection = cb.m_hConn;
                    Debug.Log("Host: Accepted P2P connection.");
                }
                break;

            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                Debug.Log((m_IsHost ? "Host" : "Client") + ": P2P connected.");
                break;

            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                Debug.LogWarning("Connection closed / problem detected.");
                m_Connection = HSteamNetConnection.Invalid;
                break;
        }
    }

    // ---------------- Stress send & stats ----------------

    private void DoStressSend(float dt) {
        if (m_Connection == HSteamNetConnection.Invalid)
            return;

        if (testPacketSize <= 0)
            testPacketSize = 1;

        // Accumulate how many bytes we're allowed to send based on target bytes/sec
        sendBudget += TargetBytesPerSecond * dt;

        // Send as many test packets as fit into the budget
        while (sendBudget >= testPacketSize) {
            byte marker = m_IsHost ? (byte)'h' : (byte)'c';

            if (SendTestPacket(marker, testPacketSize)) {
                bytesSentThisSecond += testPacketSize;
            }

            sendBudget -= testPacketSize;
        }
    }

    private void UpdateStats(float dt) {
        statsTimer += dt;
        if (statsTimer >= 1f) {
            statsTimer -= 1f;

            lastSecondBytesSent = bytesSentThisSecond;
            lastSecondBytesReceived = bytesReceivedThisSecond;

            bytesSentThisSecond = 0;
            bytesReceivedThisSecond = 0;
        }
    }

    // ---------------- Send / Receive ----------------

    // Sends a packet of 'size' bytes; first byte is marker, rest is filler
    private bool SendTestPacket(byte marker, int size) {
        if (m_Connection == HSteamNetConnection.Invalid)
            return false;

        byte[] data = new byte[size];
        for (int i = 0; i < size; i++) {
            data[i] = marker;
        }

        GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try {
            EResult result = SteamNetworkingSockets.SendMessageToConnection(
                m_Connection,
                handle.AddrOfPinnedObject(),
                (uint)data.Length,
                Constants.k_nSteamNetworkingSend_Unreliable,
                out long _
            );

            if (result != EResult.k_EResultOK) {
                // You will almost certainly see k_EResultLimitExceeded here
                Debug.LogWarning("Send failed: " + result);
                return false;
            }

            return true;
        } finally {
            handle.Free();
        }
    }


    private void ReceiveMessages() {
        if (m_Connection == HSteamNetConnection.Invalid)
            return;

        IntPtr[] msgPtrs = new IntPtr[256];

        while (true) {
            int msgCount = SteamNetworkingSockets.ReceiveMessagesOnConnection(
                m_Connection,
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

                if (msg.m_cbSize > 0) {
                    bytesReceivedThisSecond += (int)msg.m_cbSize;

                    byte[] buffer = new byte[msg.m_cbSize];
                    Marshal.Copy(msg.m_pData, buffer, 0, (int)msg.m_cbSize);

                    char c = (char)buffer[0];
                    // Debug.Log((m_IsHost ? "Host" : "Client") + " received packet, first byte: " + c +
                    //           " (size " + msg.m_cbSize + " bytes)");
                }

                SteamNetworkingMessage_t.Release(ptr);
            }
        }
    }
}
