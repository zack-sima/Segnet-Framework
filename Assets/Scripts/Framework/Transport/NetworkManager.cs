using UnityEngine;

namespace SegNet {

    /// <summary>
    /// Temporary test controller that exercises the ServerManager / MessageDispatcher pipeline.
    /// Replace with your actual game networking logic once the framework is wired up.
    ///
    /// Keys:
    ///   H = Start Host
    ///   C = Start Client
    ///   S = Server broadcasts a test message to all clients
    ///   Q = Send a test user-message to all connections
    ///   X = Stop
    /// </summary>
    public class NetworkManager : MonoBehaviour {

        [SerializeField] private string testMessage = "hello, network";

        // Use a user-range message type for the test payload
        private const ushort TestMessageType = (ushort)NetworkMessageType.UserStart;

        private void Start() {
            if (ServerManager.Instance == null) {
                Debug.LogError("[NetworkManager] ServerManager not found in scene.");
                return;
            }

            // Register a handler for our test message type
            ServerManager.Instance.Messages.RegisterHandler(TestMessageType, OnTestMessageReceived);
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

            // Broadcast a test message (works from server/host or client)
            if (Input.GetKeyDown(KeyCode.S) || Input.GetKeyDown(KeyCode.Q)) {
                var writer = new NetworkWriter();
                writer.WriteString(testMessage);
                sm.Messages.Broadcast(TestMessageType, writer);
                Debug.Log($"[NetworkManager] Sent test message: \"{testMessage}\"");
            }
        }

        private void OnTestMessageReceived(ConnectionId from, NetworkReader reader) {
            string msg = reader.ReadString();
            Debug.Log($"[NetworkManager] Test message from {from}: \"{msg}\"");
        }
    }
}
