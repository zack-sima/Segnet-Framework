using UnityEngine;
using static NetworkStreamManager;

public class NetworkManager : MonoBehaviour {
    public static NetworkManager instance;

    //for joining/hosting game, etc
    [SerializeField] private NetworkConnectionManager connectionManager;

    //for sending data
    [SerializeField] private NetworkStreamManager streamManager;

    // Test payload string (server sends this when you press 'S')
    [SerializeField] private string testMessage = "hello, client";

    // Test RPC message (sent when you press 'Q')
    [SerializeField] private string testRpcMessage = "hello, rpc";

    private void Awake() {
        if (instance != null && instance != this) {
            Destroy(gameObject);
            return;
        }
        instance = this;
    }

    private void Update() {
        if (connectionManager == null || streamManager == null)
            return;

        if (!connectionManager.IsConnected()) {
            if (Input.GetKeyDown(KeyCode.H)) {
                connectionManager.Host();
            }

            if (Input.GetKeyDown(KeyCode.C)) {
                connectionManager.Join();
            }
            return;
        }

        // is in an active game
        if (Input.GetKeyDown(KeyCode.S) && connectionManager.IsServer) {
            var payload = new NetStreamPayload { data = testMessage, diffs = new NetStreamPayload.BuildingDiff[10] };
            foreach (var conn in connectionManager.Connections) {
                streamManager.ServerSendStreamObject(conn, payload);
            }
        }

        // Send RPC to all other connections (server -> clients, or client -> server)
        if (Input.GetKeyDown(KeyCode.Q)) {
            var rpc = new RPCPayload { message = testRpcMessage };
            foreach (var conn in connectionManager.Connections) {
                streamManager.SendRpcTo(conn, rpc);
            }
        }
    }

    // ---------- Callbacks wired from NetworkStreamManager's UnityEvents ----------

    // Hook this to NetworkStreamManager.onStreamReceivedObject in the inspector
    public void OnStreamObjectReceived(NetStreamPayload payload) {
        if (payload == null) {
            Debug.Log("[NetworkManager] Stream object received: <null>");
            return;
        }

        Debug.Log($"[NetworkManager] Stream object received: \"{payload.data}\"");
    }

    // Hook this to NetworkStreamManager.onRpcReceivedObject in the inspector
    public void OnRpcObjectReceived(RPCPayload payload) {
        if (payload == null) {
            Debug.Log("[NetworkManager] RPC object received: <null>");
            return;
        }

        Debug.Log($"[NetworkManager] RPC object received: \"{payload.message}\"");
    }
}
