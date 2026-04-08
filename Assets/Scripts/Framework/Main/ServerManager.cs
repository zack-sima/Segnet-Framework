using System;
using System.Collections.Generic;
using UnityEngine;

namespace SegNet {

    /// <summary>
    /// The current network lifecycle state.
    /// </summary>
    public enum NetworkState {
        Offline = 0,
        Starting,
        Server,
        Host,   // server + local client
        Client,
    }

    /// <summary>
    /// Singleton entry point for the SegNet framework.
    /// Manages network lifecycle (start/stop), bridges transport events to framework callbacks,
    /// and owns the global registries for players and spawned objects (added in later phases).
    ///
    /// Exists in both offline and online states so game code can always reference it.
    /// </summary>
    public class ServerManager : MonoBehaviour {
        public static ServerManager Instance { get; private set; }

        [Header("Transport")]
        [SerializeField] private NetworkConnectionManager connectionManager;
        [SerializeField] private NetworkStreamManager streamManager;

        // ---- State ----

        public NetworkState State { get; private set; } = NetworkState.Offline;
        public bool IsServer => State == NetworkState.Server || State == NetworkState.Host;
        public bool IsClient => State == NetworkState.Client || State == NetworkState.Host;
        public bool IsHost => State == NetworkState.Host;
        public bool IsOnline => State != NetworkState.Offline && State != NetworkState.Starting;

        // ---- Message dispatcher (created at Awake, always available) ----

        public MessageDispatcher Messages { get; private set; }

        // ---- Connection access ----

        public IReadOnlyCollection<ConnectionId> Connections => connectionManager.Connections;

        // ---- Framework callbacks ----

        /// <summary>Fires on the server when a remote client's transport connection is established.</summary>
        public event Action<ConnectionId> OnClientConnected;

        /// <summary>Fires on the server when a remote client disconnects.</summary>
        public event Action<ConnectionId, DisconnectReason> OnClientDisconnected;

        /// <summary>Fires after the local node has fully started (server, host, or client).</summary>
        public event Action OnStarted;

        /// <summary>Fires after the local node has fully stopped.</summary>
        public event Action OnStopped;

        // Future phases will add: OnPlayerJoined, OnPlayerLeft, OnSpawn, OnDespawn, etc.

        // ---- Unity lifecycle ----

        private void Awake() {
            if (Instance != null && Instance != this) {
                Debug.LogWarning("[ServerManager] Duplicate instance destroyed.");
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            ValidateReferences();

            Messages = new MessageDispatcher(streamManager);

            connectionManager.OnClientConnected += HandleClientConnected;
            connectionManager.OnClientDisconnected += HandleClientDisconnected;
        }

        private void OnDestroy() {
            if (Instance == this) {
                Instance = null;
            }

            Messages?.Dispose();

            if (connectionManager != null) {
                connectionManager.OnClientConnected -= HandleClientConnected;
                connectionManager.OnClientDisconnected -= HandleClientDisconnected;
            }
        }

        // ---- Public API: lifecycle ----

        /// <summary>Start as a dedicated server (no local client).</summary>
        public void StartServer() {
            if (State != NetworkState.Offline) {
                Debug.LogWarning("[ServerManager] Cannot start server: already in state " + State);
                return;
            }

            State = NetworkState.Starting;
            connectionManager.Host();
            State = NetworkState.Server;

            Debug.Log("[ServerManager] Started as Server.");
            OnStarted?.Invoke();
        }

        /// <summary>Start as host (server + local client).</summary>
        public void StartHost() {
            if (State != NetworkState.Offline) {
                Debug.LogWarning("[ServerManager] Cannot start host: already in state " + State);
                return;
            }

            State = NetworkState.Starting;
            connectionManager.Host();
            State = NetworkState.Host;

            Debug.Log("[ServerManager] Started as Host.");
            OnStarted?.Invoke();
        }

        /// <summary>Start as a client connecting to a remote server.</summary>
        public void StartClient() {
            if (State != NetworkState.Offline) {
                Debug.LogWarning("[ServerManager] Cannot start client: already in state " + State);
                return;
            }

            State = NetworkState.Starting;
            connectionManager.Join();
            State = NetworkState.Client;

            Debug.Log("[ServerManager] Started as Client.");
            OnStarted?.Invoke();
        }

        /// <summary>Stop the network session and return to offline state.</summary>
        public void Stop() {
            if (State == NetworkState.Offline) return;

            NetworkState previousState = State;
            connectionManager.StopAll();
            State = NetworkState.Offline;

            Debug.Log($"[ServerManager] Stopped (was {previousState}).");
            OnStopped?.Invoke();
        }

        // ---- Connection event handlers ----

        private void HandleClientConnected(ConnectionId id) {
            Debug.Log($"[ServerManager] Client connected: {id}");
            OnClientConnected?.Invoke(id);
        }

        private void HandleClientDisconnected(ConnectionId id, DisconnectReason reason) {
            Debug.Log($"[ServerManager] Client disconnected: {id} ({reason})");
            OnClientDisconnected?.Invoke(id, reason);
        }

        // ---- Validation ----

        private void ValidateReferences() {
            if (connectionManager == null)
                Debug.LogError("[ServerManager] NetworkConnectionManager reference is missing.");
            if (streamManager == null)
                Debug.LogError("[ServerManager] NetworkStreamManager reference is missing.");
        }
    }
}
