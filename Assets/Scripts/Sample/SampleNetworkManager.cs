using SegNet;
using UnityEngine;

public class SampleNetworkManager : NetworkManager {
    [Header("Sample Player")]
    [SerializeField] private GameObject samplePlayerPrefab;
    [SerializeField] private Vector3 spawnOrigin = Vector3.zero;
    [SerializeField] private float spawnSpacing = 2f;

    private void Start() {
        if (ServerManager.Instance == null) {
            Debug.LogError("[SampleNetworkManager] ServerManager not found in scene.");
            return;
        }

        ServerManager.Instance.OnPlayerJoined += HandlePlayerJoined;
    }

    private void OnDestroy() {
        if (ServerManager.Instance != null)
            ServerManager.Instance.OnPlayerJoined -= HandlePlayerJoined;
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
        NetworkBehaviour spawned = serverManager.ServerSpawn(
            samplePlayerPrefab,
            spawnPosition,
            Quaternion.identity,
            player);

        if (spawned != null)
            player.PrimaryBehaviour = spawned;
    }
}
