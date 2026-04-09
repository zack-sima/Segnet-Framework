using UnityEngine;

namespace SegNet {

    /// <summary>
    /// Test controller for the SegNet framework.
    ///
    /// Keys:
    ///   H = Start Host     C = Start Client     X = Stop
    ///   T = Spawn test prefab (server)    D = Despawn last spawned (server)
    ///   M = Move last spawned object (server)
    ///   S = Broadcast test message         P = Print player/object list
    /// </summary>
    public class NetworkManager : MonoBehaviour {

        [Header("Test")]
        [SerializeField] private string testMessage = "hello, network";

        [Tooltip("Assign a prefab from the PrefabRegistry to test runtime spawning.")]
        [SerializeField] private GameObject testSpawnPrefab;

        private const ushort TestMessageType = (ushort)NetworkMessageType.UserStart;

        // Track last spawned object so we can despawn/move it
        private NetworkBehaviour _lastSpawned;

        private void Start() {
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
            if (ServerManager.Instance != null && ServerManager.Instance.Messages != null)
                ServerManager.Instance.Messages.UnregisterHandler(TestMessageType);
        }

        private void Update() {
            var sm = ServerManager.Instance;
            if (sm == null) return;

            if (!sm.IsOnline) {
                if (Input.GetKeyDown(KeyCode.H)) sm.StartHost();
                if (Input.GetKeyDown(KeyCode.C)) sm.StartClient();
                return;
            }

            if (Input.GetKeyDown(KeyCode.X)) {
                sm.Stop();
                return;
            }

            // ---- Server-only actions ----

            if (sm.IsServer) {
                // Spawn test prefab
                if (Input.GetKeyDown(KeyCode.T) && testSpawnPrefab != null) {
                    Vector3 pos = new Vector3(
                        Random.Range(-3f, 3f), 0f, Random.Range(-3f, 3f));
                    _lastSpawned = sm.ServerSpawn(testSpawnPrefab, pos, Quaternion.identity);
                    Debug.Log($"[NetworkManager] Spawned: {_lastSpawned}");
                }

                // Despawn last
                if (Input.GetKeyDown(KeyCode.D) && _lastSpawned != null) {
                    sm.ServerDespawn(_lastSpawned);
                    _lastSpawned = null;
                }

                // Move last spawned (tests NetworkTransform sync)
                if (Input.GetKeyDown(KeyCode.M) && _lastSpawned != null) {
                    _lastSpawned.transform.position += new Vector3(1f, 0f, 0f);
                    Debug.Log($"[NetworkManager] Moved {_lastSpawned.name} to {_lastSpawned.transform.position}");
                }
            }

            // ---- Any peer ----

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
    }
}
