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

        private bool _isTransitioning;

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

        private void HandleClientDisconnected(ConnectionId connectionId, DisconnectReason reason) {
            var sm = ServerManager.Instance;
            if (sm == null || sm.State != NetworkState.Client)
                return;

            Debug.LogWarning($"[NetworkManager] Lost host connection ({reason}). Returning to menu.");
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
                    sm.StartServer();
                    break;
                case NetworkState.Host:
                    sm.StartHost();
                    break;
                case NetworkState.Client:
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
    }
}
