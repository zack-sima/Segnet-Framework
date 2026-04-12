using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SegNet {

    /// <summary>
    /// Persistent parent controller for the SegNet framework.
    /// Add this one component to a GameObject; it creates and configures the SegNet
    /// runtime managers/transports underneath itself.
    /// </summary>
    [DefaultExecutionOrder(-900)]
    public class NetworkManager : MonoBehaviour {
        public static NetworkManager Instance { get; private set; }
        internal static bool IsReplacingPersistentRoot { get; private set; }

        [Header("Scenes")]
        [Tooltip("Optional scene to load before starting host/server/client.")]
        [SerializeField] private string gameScene;
        [Tooltip("Optional scene to load after exiting the current network session.")]
        [SerializeField] private string menuScene;

        [Header("Transport")]
        [Tooltip("Use Steam transport instead of direct TCP local/network address transport.")]
        [SerializeField] private bool useSteamTransport;
        [SerializeField] private int clientTimeoutMs = 10000;

        [Header("Local Transport")]
        [Tooltip("Address clients connect to when using local/direct TCP transport.")]
        [SerializeField] private string localAddress = "127.0.0.1";
        [Tooltip("TCP port used for hosting and joining when using local/direct TCP transport.")]
        [SerializeField] private int localPort = 8000;

        [Header("Spawning")]
        [SerializeField] private PrefabRegistry prefabRegistry;

        [Header("Traffic")]
        [SerializeField] private float kbIn;
        [SerializeField] private float kbOut;

        private SteamManager steamManager;
        private LocalTransport localTransport;
        private SteamTransport steamTransport;
        private NetworkSceneManager sceneManager;
        private NetworkConnectionManager connectionManager;
        private NetworkStreamManager streamManager;
        private ServerManager serverManager;

        private NetworkTransportMode transportMode = NetworkTransportMode.Local;
        private bool _isTransitioning;
        private bool _runtimeReady;
        private long _lastStatBytesIn;
        private long _lastStatBytesOut;
        private float _lastStatTime;

        public event Action<NetworkBehaviour> OnObjectSpawned;
        public event Action<NetworkBehaviour> OnObjectDespawned;

        protected bool IsTransitioning => _isTransitioning;

        public NetworkState State =>
            ServerManager.Instance != null ? ServerManager.Instance.State : NetworkState.Offline;

        public bool IsServer =>
            ServerManager.Instance != null && ServerManager.Instance.IsServer;

        public bool IsClient =>
            ServerManager.Instance != null && ServerManager.Instance.IsClient;

        public bool IsHost =>
            ServerManager.Instance != null && ServerManager.Instance.IsHost;

        public bool IsOnline =>
            ServerManager.Instance != null && ServerManager.Instance.IsOnline;

        public bool IsOffline => State == NetworkState.Offline;
        public bool IsStarting => State == NetworkState.Starting;
        public bool UseSteamTransport => useSteamTransport;
        public string LocalAddress => localAddress;
        public int LocalPort => localPort;
        public float KbIn => kbIn;
        public float KbOut => kbOut;

        public bool ConnectionActive {
            get {
                var sm = ServerManager.Instance;
                if (sm == null) return false;
                if (sm.IsHost) return true;
                if (sm.State == NetworkState.Server) return sm.Connections.Count > 0;
                return sm.State == NetworkState.Client && sm.ServerConnection != ConnectionId.Invalid;
            }
        }

        public IReadOnlyDictionary<uint, NetworkBehaviour> NetworkedObjects =>
            ServerManager.Instance != null
                ? ServerManager.Instance.NetworkedObjects
                : null;

        private void Awake() {
            if (Instance != null && Instance != this) {
                if (IsReplacingPersistentRoot) {
                    Debug.Log("[NetworkManager] Replacing previous persistent root.");
                    Destroy(Instance.transform.root.gameObject);
                } else {
                    Debug.LogWarning("[NetworkManager] Duplicate persistent root destroyed.");
                    Destroy(transform.root.gameObject);
                    return;
                }
            }

            Instance = this;
            DontDestroyOnLoad(transform.root.gameObject);
            EnsureRuntimeComponents();
            ResetBandwidthStats();
        }

        protected virtual void Start() {
            if (Instance == this)
                IsReplacingPersistentRoot = false;

            var sm = ServerManager.Instance;
            if (sm == null) {
                Debug.LogError("[NetworkManager] ServerManager not found in scene.");
                return;
            }

            sm.OnPlayerJoined += p =>
                Debug.Log($"[NetworkManager] === PLAYER JOINED: {p}  isLocal={p.IsLocal}  isHost={p.IsHost} ===");
            sm.OnPlayerLeft += p =>
                Debug.Log($"[NetworkManager] === PLAYER LEFT: {p} ===");
            sm.OnObjectSpawned += HandleObjectSpawned;
            sm.OnObjectDespawned += HandleObjectDespawned;
            sm.OnClientDisconnected += HandleClientDisconnected;
            sm.OnStarted += () =>
                Debug.Log($"[NetworkManager] === NETWORK STARTED ({sm.State}) ===");
            sm.OnStopped += () =>
                Debug.Log("[NetworkManager] === NETWORK STOPPED ===");
        }

        protected virtual void OnDestroy() {
            if (Instance == this)
                Instance = null;

            if (ServerManager.Instance != null) {
                ServerManager.Instance.OnObjectSpawned -= HandleObjectSpawned;
                ServerManager.Instance.OnObjectDespawned -= HandleObjectDespawned;
                ServerManager.Instance.OnClientDisconnected -= HandleClientDisconnected;
            }
        }

        protected virtual void LateUpdate() {
            UpdateBandwidthStats();
        }

        private void OnValidate() {
            transportMode = useSteamTransport ? NetworkTransportMode.Steam : NetworkTransportMode.Local;
            if (string.IsNullOrWhiteSpace(localAddress))
                localAddress = "127.0.0.1";
            if (localPort <= 0)
                localPort = 8000;
        }

        private void HandleClientDisconnected(ConnectionId connectionId, DisconnectReason reason) {
            var sm = ServerManager.Instance;
            if (sm == null)
                return;

            bool transportStartupFailed = connectionId == ConnectionId.Invalid &&
                (sm.State == NetworkState.Server || sm.State == NetworkState.Host);
            if (sm.State != NetworkState.Client && !transportStartupFailed)
                return;

            Debug.LogWarning($"[NetworkManager] Transport disconnected ({reason}). Returning to menu.");
            StopGame();
        }

        private void HandleObjectSpawned(NetworkBehaviour obj) {
            Debug.Log($"[NetworkManager] === SPAWNED: {obj} ===");
            OnObjectSpawned?.Invoke(obj);
        }

        private void HandleObjectDespawned(NetworkBehaviour obj) {
            Debug.Log($"[NetworkManager] === DESPAWNED: {obj} ===");
            OnObjectDespawned?.Invoke(obj);
        }

        private IEnumerator BeginSession(NetworkState targetState) {
            if (_isTransitioning)
                yield break;

            _isTransitioning = true;

            NetworkSceneManager.Instance?.ClearSceneObjects();

            if (!string.IsNullOrWhiteSpace(gameScene) &&
                SceneManager.GetActiveScene().name != gameScene) {
                yield return SceneManager.LoadSceneAsync(gameScene);
            }

            var sm = ServerManager.Instance;
            if (sm == null) {
                Debug.LogError("[NetworkManager] Cannot start session: ServerManager not found.");
                _isTransitioning = false;
                yield break;
            }

            switch (targetState) {
                case NetworkState.Server:
                    ResetBandwidthStats();
                    sm.StartServer();
                    break;
                case NetworkState.Host:
                    ResetBandwidthStats();
                    sm.StartHost();
                    break;
                case NetworkState.Client:
                    ResetBandwidthStats();
                    sm.StartClient();
                    break;
            }

            _isTransitioning = false;
        }

        private IEnumerator ExitSession() {
            if (_isTransitioning)
                yield break;

            _isTransitioning = true;

            var sm = ServerManager.Instance;
            if (sm != null)
                sm.Stop();

            NetworkSceneManager.Instance?.ClearSceneObjects();

            GameObject persistentRoot = transform.root.gameObject;

            if (!string.IsNullOrWhiteSpace(menuScene) &&
                SceneManager.GetActiveScene().name != menuScene) {
                IsReplacingPersistentRoot = true;
                yield return SceneManager.LoadSceneAsync(menuScene);
            }

            Destroy(persistentRoot);
        }

        // Public API, call from scripts!
        public void StartHost() => StartCoroutine(BeginSession(NetworkState.Host));
        public void StartClient() => StartCoroutine(BeginSession(NetworkState.Client));
        public void StartServer() => StartCoroutine(BeginSession(NetworkState.Server));
        public void StopGame() => StartCoroutine(ExitSession());

        public bool SetTransport(MonoBehaviour transportBehaviour) {
            EnsureRuntimeComponents();
            if (connectionManager == null)
                return false;
            if (!IsOffline) {
                Debug.LogWarning("[NetworkManager] Cannot swap transport while network session is active.");
                return false;
            }

            bool swapped = connectionManager.SetTransport(transportBehaviour);
            if (swapped)
                ResetBandwidthStats();
            return swapped;
        }

        public bool SetTransport(ITransport transport) {
            EnsureRuntimeComponents();
            if (connectionManager == null)
                return false;
            if (!IsOffline) {
                Debug.LogWarning("[NetworkManager] Cannot swap transport while network session is active.");
                return false;
            }

            bool swapped = connectionManager.SetTransport(transport);
            if (swapped)
                ResetBandwidthStats();
            return swapped;
        }

        public bool SetTransportMode(NetworkTransportMode mode) {
            EnsureRuntimeComponents();

            if (!IsOffline) {
                Debug.LogWarning("[NetworkManager] Cannot swap transport while network session is active.");
                return false;
            }

            transportMode = mode;
            useSteamTransport = mode == NetworkTransportMode.Steam;

            switch (transportMode) {
                case NetworkTransportMode.Local:
                    return SetTransport((ITransport)localTransport);
                case NetworkTransportMode.Steam:
                    return SetTransport((ITransport)steamTransport);
                default:
                    Debug.LogError($"[NetworkManager] Unknown transport mode {transportMode}.");
                    return false;
            }
        }

        public bool SetUseSteamTransport(bool useSteam) {
            return SetTransportMode(useSteam ? NetworkTransportMode.Steam : NetworkTransportMode.Local);
        }

        public void SetLocalTransportEndpoint(string address, int port) {
            if (!IsOffline) {
                Debug.LogWarning("[NetworkManager] Cannot change local transport endpoint while network session is active.");
                return;
            }

            localAddress = string.IsNullOrWhiteSpace(address) ? "127.0.0.1" : address;
            localPort = port > 0 ? port : 8000;

            EnsureRuntimeComponents();
            localTransport.Configure(localAddress, localPort);
        }

        public NetworkBehaviour ServerSpawn(GameObject prefab, Vector3 position, Quaternion rotation,
            NetworkPlayer owner = null) {
            var sm = ServerManager.Instance;
            if (sm == null) {
                Debug.LogError("[NetworkManager] ServerSpawn failed: ServerManager not found.");
                return null;
            }

            return sm.SpawnNetworkObject(prefab, position, rotation, owner);
        }

        public void ServerDespawn(NetworkBehaviour obj) {
            var sm = ServerManager.Instance;
            if (sm == null) {
                Debug.LogError("[NetworkManager] ServerDespawn failed: ServerManager not found.");
                return;
            }

            sm.DespawnNetworkObject(obj);
        }

        public NetworkBehaviour GetNetworkObject(uint networkId) {
            var sm = ServerManager.Instance;
            return sm != null ? sm.GetNetworkObject(networkId) : null;
        }

        private void EnsureRuntimeComponents() {
            if (_runtimeReady)
                return;

            transportMode = useSteamTransport ? NetworkTransportMode.Steam : NetworkTransportMode.Local;

            steamManager = GetOrCreateRuntimeComponent<SteamManager>("SteamManager");
            ActivateRuntimeComponent(steamManager);

            localTransport = GetOrCreateRuntimeComponent<LocalTransport>("LocalTransport");
            localTransport.Configure(localAddress, localPort);
            ActivateRuntimeComponent(localTransport);

            steamTransport = GetOrCreateRuntimeComponent<SteamTransport>("SteamTransport");
            ActivateRuntimeComponent(steamTransport);

            connectionManager = GetOrCreateRuntimeComponent<NetworkConnectionManager>(
                "NetworkConnectionManager");
            connectionManager.Configure(clientTimeoutMs);
            connectionManager.SetTransport(transportMode == NetworkTransportMode.Steam
                ? (ITransport)steamTransport
                : localTransport);
            ActivateRuntimeComponent(connectionManager);

            streamManager = GetOrCreateRuntimeComponent<NetworkStreamManager>(
                "NetworkStreamManager");
            streamManager.Configure(connectionManager);
            ActivateRuntimeComponent(streamManager);

            sceneManager = GetOrCreateRuntimeComponent<NetworkSceneManager>(
                "NetworkSceneManager");
            ActivateRuntimeComponent(sceneManager);

            serverManager = GetOrCreateRuntimeComponent<ServerManager>("ServerManager");
            serverManager.Configure(connectionManager, streamManager, prefabRegistry);
            ActivateRuntimeComponent(serverManager);

            _runtimeReady = true;
        }

        private static void ActivateRuntimeComponent(Component component) {
            if (component != null && !component.gameObject.activeSelf)
                component.gameObject.SetActive(true);
        }

        private T GetOrCreateRuntimeComponent<T>(string objectName) where T : Component {
            T existing = GetComponentInChildren<T>(true);
            if (existing != null)
                return existing;

            var go = new GameObject(objectName);
            go.SetActive(false);
            go.transform.SetParent(transform, worldPositionStays: false);
            return go.AddComponent<T>();
        }

        private bool ResolveConnectionManager() {
            EnsureRuntimeComponents();
            if (connectionManager != null)
                return true;

            var sm = ServerManager.Instance;
            if (sm != null)
                connectionManager = sm.GetComponentInChildren<NetworkConnectionManager>(true);

            if (connectionManager == null)
                connectionManager = GetComponentInChildren<NetworkConnectionManager>(true);

            if (connectionManager == null)
                Debug.LogError("[NetworkManager] NetworkConnectionManager reference is missing.");

            return connectionManager != null;
        }

        private void ResetBandwidthStats() {
            if (ResolveConnectionManager())
                connectionManager.ResetByteCounters();

            kbIn = 0f;
            kbOut = 0f;
            _lastStatBytesIn = 0;
            _lastStatBytesOut = 0;
            _lastStatTime = Time.unscaledTime;
        }

        private void UpdateBandwidthStats() {
            if (!ResolveConnectionManager())
                return;

            float now = Time.unscaledTime;
            float elapsed = now - _lastStatTime;
            if (elapsed < 1f)
                return;

            long bytesIn = connectionManager.TotalBytesIn;
            long bytesOut = connectionManager.TotalBytesOut;

            kbIn = (bytesIn - _lastStatBytesIn) / 1024f / elapsed;
            kbOut = (bytesOut - _lastStatBytesOut) / 1024f / elapsed;

            _lastStatBytesIn = bytesIn;
            _lastStatBytesOut = bytesOut;
            _lastStatTime = now;
        }
    }
}
