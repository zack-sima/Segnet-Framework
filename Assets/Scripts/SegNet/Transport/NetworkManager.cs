using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SegNet {

    /// <summary>
    /// Persistent parent controller for the SegNet framework.
    /// Keep this on the root GameObject that also contains the transport/managers hierarchy.
    ///
    /// Keys:
    ///   H = Load game scene (optional) and Start Host
    ///   C = Load game scene (optional) and Start Client
    ///   J = Load game scene (optional) and Start Server
    ///   X = Stop session, optionally load menu scene, then destroy the persistent root
    ///   T = Spawn test prefab (server)    D = Despawn last spawned (server)
    ///   M = Move last spawned object (server)
    ///   S = Broadcast test message        P = Print player/object list
    /// </summary>
    public class NetworkManager : MonoBehaviour {
        public static NetworkManager Instance { get; private set; }
        internal static bool IsReplacingPersistentRoot { get; private set; }

        [Header("Scenes")]
        [Tooltip("Optional scene to load before starting host/server/client.")]
        [SerializeField] private string gameScene;
        [Tooltip("Optional scene to load after exiting the current network session.")]
        [SerializeField] private string menuScene;

        [Header("Test")]
        [SerializeField] private string testMessage = "hello, network";

        [Tooltip("Assign a prefab from the PrefabRegistry to test runtime spawning.")]
        [SerializeField] private GameObject testSpawnPrefab;

        private const ushort TestMessageType = (ushort)NetworkMessageType.UserStart;

        private NetworkBehaviour _lastSpawned;
        private bool _isTransitioning;

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

        private void Start() {
            if (Instance == this)
                IsReplacingPersistentRoot = false;

            var sm = ServerManager.Instance;
            if (sm == null) {
                Debug.LogError("[NetworkManager] ServerManager not found in scene.");
                return;
            }

            sm.Messages.RegisterHandler(TestMessageType, OnTestMessageReceived);

            sm.OnPlayerJoined += p =>
                Debug.Log($"[NetworkManager] === PLAYER JOINED: {p}  isLocal={p.IsLocal}  isHost={p.IsHost} ===");
            sm.OnPlayerLeft += p =>
                Debug.Log($"[NetworkManager] === PLAYER LEFT: {p} ===");
            sm.OnObjectSpawned += o =>
                Debug.Log($"[NetworkManager] === SPAWNED: {o} ===");
            sm.OnObjectDespawned += o =>
                Debug.Log($"[NetworkManager] === DESPAWNED: {o} ===");
            sm.OnStarted += () =>
                Debug.Log($"[NetworkManager] === NETWORK STARTED ({sm.State}) ===");
            sm.OnStopped += () =>
                Debug.Log("[NetworkManager] === NETWORK STOPPED ===");
        }

        private void OnDestroy() {
            if (Instance == this)
                Instance = null;

            if (ServerManager.Instance != null && ServerManager.Instance.Messages != null)
                ServerManager.Instance.Messages.UnregisterHandler(TestMessageType);
        }

        private void Update() {
            var sm = ServerManager.Instance;
            if (sm == null) return;

            if (!sm.IsOnline) {
                if (_isTransitioning) return;

                if (Input.GetKeyDown(KeyCode.H)) StartHost();
                if (Input.GetKeyDown(KeyCode.C)) StartClient();
                if (Input.GetKeyDown(KeyCode.J)) StartServer();
                return;
            }

            if (Input.GetKeyDown(KeyCode.X)) {
                StopGame();
                return;
            }

            if (sm.IsServer) {
                if (Input.GetKeyDown(KeyCode.T) && testSpawnPrefab != null) {
                    Vector3 pos = new Vector3(
                        Random.Range(-3f, 3f), 0f, Random.Range(-3f, 3f));
                    _lastSpawned = sm.ServerSpawn(testSpawnPrefab, pos, Quaternion.identity);
                    Debug.Log($"[NetworkManager] Spawned: {_lastSpawned}");
                }

                if (Input.GetKeyDown(KeyCode.D) && _lastSpawned != null) {
                    sm.ServerDespawn(_lastSpawned);
                    _lastSpawned = null;
                }

                if (Input.GetKeyDown(KeyCode.M) && _lastSpawned != null) {
                    _lastSpawned.transform.position += new Vector3(1f, 0f, 0f);
                    Debug.Log($"[NetworkManager] Moved {_lastSpawned.name} to {_lastSpawned.transform.position}");
                }
            }

            if (Input.GetKeyDown(KeyCode.S)) {
                var writer = new NetworkWriter();
                writer.WriteString(testMessage);
                sm.Messages.Broadcast(TestMessageType, writer);
                Debug.Log($"[NetworkManager] Sent test message: \"{testMessage}\"");
            }

            if (Input.GetKeyDown(KeyCode.P)) {
                Debug.Log($"--- Players ({sm.Players.Count}) ---");
                foreach (var kvp in sm.Players)
                    Debug.Log($"  {kvp.Value}  local={kvp.Value.IsLocal}  host={kvp.Value.IsHost}");
                Debug.Log($"  LocalPlayer: {sm.LocalPlayer}");

                Debug.Log($"--- Objects ({sm.NetworkedObjects.Count}) ---");
                foreach (var kvp in sm.NetworkedObjects) {
                    var r = kvp.Value;
                    int childCount = r.AllBehaviours != null ? r.AllBehaviours.Length : 1;
                    Debug.Log($"  {r}  behaviours={childCount}  pos={r.transform.position}");
                }
            }
        }

        private void OnTestMessageReceived(ConnectionId from, NetworkReader reader) {
            string msg = reader.ReadString();
            Debug.Log($"[NetworkManager] Test message from {from}: \"{msg}\"");
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
    }
}
