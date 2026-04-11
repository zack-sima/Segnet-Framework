using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SegNet {

    /// <summary>
    /// Persistent parent controller for the SegNet framework.
    /// Keep this on the root GameObject that also contains the transport/managers hierarchy.
    /// </summary>
    public class NetworkManager : MonoBehaviour {
        public static NetworkManager Instance { get; private set; }
        internal static bool IsReplacingPersistentRoot { get; private set; }

        [Header("Scenes")]
        [Tooltip("Optional scene to load before starting host/server/client.")]
        [SerializeField] private string gameScene;
        [Tooltip("Optional scene to load after exiting the current network session.")]
        [SerializeField] private string menuScene;

        [Header("Transport")]
        [SerializeField] private NetworkConnectionManager connectionManager;

        [Header("Traffic")]
        [SerializeField] private float kbIn;
        [SerializeField] private float kbOut;

        private bool _isTransitioning;
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
            ResolveConnectionManager();
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
            if (!ResolveConnectionManager())
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
            if (!ResolveConnectionManager())
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

        private bool ResolveConnectionManager() {
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
