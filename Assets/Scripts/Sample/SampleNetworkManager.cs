using SegNet;
using UnityEngine;

public class SampleNetworkManager : NetworkManager {
    [Header("Sample Player")]
    [SerializeField] private GameObject samplePlayerPrefab;
    [SerializeField] private Vector3 spawnOrigin = Vector3.zero;
    [SerializeField] private float spawnSpacing = 2f;

    [Header("Test")]
    [SerializeField] private string testMessage = "hello, network";

    [Tooltip("Assign a prefab from the PrefabRegistry to test runtime spawning.")]
    [SerializeField] private GameObject testSpawnPrefab;

    private const ushort TestMessageType = (ushort)NetworkMessageType.UserStart;

    private NetworkBehaviour _lastSpawned;

    protected override void Start() {
        base.Start();

        if (ServerManager.Instance == null) {
            Debug.LogError("[SampleNetworkManager] ServerManager not found in scene.");
            return;
        }

        ServerManager.Instance.Messages.RegisterHandler(TestMessageType, OnTestMessageReceived);
        ServerManager.Instance.OnPlayerJoined += HandlePlayerJoined;
        ServerManager.Instance.OnClientDisconnected += HandleClientDisconnected;
    }

    protected override void OnDestroy() {
        if (ServerManager.Instance != null) {
            if (ServerManager.Instance.Messages != null)
                ServerManager.Instance.Messages.UnregisterHandler(TestMessageType);

            ServerManager.Instance.OnPlayerJoined -= HandlePlayerJoined;
            ServerManager.Instance.OnClientDisconnected -= HandleClientDisconnected;
        }

        base.OnDestroy();
    }

    private void Update() {
        var sm = ServerManager.Instance;
        if (sm == null) return;

        if (!sm.IsOnline) {
            if (IsTransitioning) return;

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
                _lastSpawned = ServerSpawn(testSpawnPrefab, pos, Quaternion.identity);
                Debug.Log($"[SampleNetworkManager] Spawned: {_lastSpawned}");
            }

            if (Input.GetKeyDown(KeyCode.D) && _lastSpawned != null) {
                ServerDespawn(_lastSpawned);
                _lastSpawned = null;
            }

            if (Input.GetKeyDown(KeyCode.M) && _lastSpawned != null) {
                _lastSpawned.transform.position += new Vector3(1f, 0f, 0f);
                Debug.Log(
                    $"[SampleNetworkManager] Moved {_lastSpawned.name} to {_lastSpawned.transform.position}");
            }
        }

        if (Input.GetKeyDown(KeyCode.S)) {
            var writer = new NetworkWriter();
            writer.WriteString(testMessage);
            sm.Messages.Broadcast(TestMessageType, writer);
            Debug.Log($"[SampleNetworkManager] Sent test message: \"{testMessage}\"");
        }

        if (Input.GetKeyDown(KeyCode.P)) {
            Debug.Log($"--- Players ({sm.Players.Count}) ---");
            foreach (var kvp in sm.Players)
                Debug.Log($"  {kvp.Value}  local={kvp.Value.IsLocal}  host={kvp.Value.IsHost}");
            Debug.Log($"  LocalPlayer: {sm.LocalPlayer}");

            var objects = NetworkedObjects;
            Debug.Log($"--- Objects ({(objects != null ? objects.Count : 0)}) ---");
            if (objects == null) return;

            foreach (var kvp in objects) {
                var r = kvp.Value;
                int childCount = r.AllBehaviours != null ? r.AllBehaviours.Length : 1;
                Debug.Log($"  {r}  behaviours={childCount}  pos={r.transform.position}");
            }
        }
    }

    private void HandlePlayerJoined(NetworkPlayer player) {
        var serverManager = ServerManager.Instance;
        if (serverManager == null || !serverManager.IsServer)
            return;

        if (samplePlayerPrefab == null) {
            Debug.LogWarning("[SampleNetworkManager] Sample player prefab is not assigned.");
            return;
        }

        if (player == null || player.PrimaryBehaviour != null)
            return;

        Vector3 spawnPosition = spawnOrigin + new Vector3((player.PlayerId - 1) * spawnSpacing, 0f, 0f);
        NetworkBehaviour spawned = ServerSpawn(
            samplePlayerPrefab,
            spawnPosition,
            Quaternion.identity,
            player);

        if (spawned != null)
            player.PrimaryBehaviour = spawned;
    }

    private void HandleClientDisconnected(ConnectionId connectionId, DisconnectReason reason) {
        var serverManager = ServerManager.Instance;
        if (serverManager == null || serverManager.State != NetworkState.Client)
            return;

        Debug.LogWarning(
            $"[SampleNetworkManager] Host disconnected ({reason}). Returning to menu.");
        StopGame();
    }

    private void OnTestMessageReceived(ConnectionId from, NetworkReader reader) {
        string msg = reader.ReadString();
        Debug.Log($"[SampleNetworkManager] Test message from {from}: \"{msg}\"");
    }
}
