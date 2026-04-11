using UnityEngine;

namespace SegNet {

    /// <summary>
    /// Syncs transform position, rotation, and/or scale from server to clients.
    ///
    /// Server-authoritative: the server detects changes each frame and marks dirty.
    /// Dirty state is flushed by ServerManager.LateUpdate via the standard replication path.
    /// Clients apply the received values directly.
    ///
    /// Toggle which axes to sync via the inspector bools.
    /// </summary>
    public class NetworkTransform : NetworkBehaviour {
        private enum SyncDirection {
            LocalClientToServer = 0,
            ServerToClients = 1,
        }

        private const ushort SyncTransformRpcId = 0x4E54; // "NT"

        [Header("Direction")]
        [SerializeField] private SyncDirection syncDirection = SyncDirection.ServerToClients;

        [Header("Sync Toggles")]
        [SerializeField] private bool syncPosition = true;
        [SerializeField] private bool syncRotation = true;
        [SerializeField] private bool syncScale = false;

        // Cached last-sent values (server) / last-received values (client)
        private Vector3 _lastPosition;
        private Quaternion _lastRotation;
        private Vector3 _lastScale;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void RegisterRpcHandler() {
            RpcRegistry.Register(SyncTransformRpcId, DispatchSyncTransformRpc);
        }

        public override void OnNetworkSpawn() {
            SnapshotCurrent();
        }

        private void LateUpdate() {
            if (!IsSpawned)
                return;

            if (syncDirection == SyncDirection.ServerToClients) {
                if (IsServer)
                    DetectChanges(sendToServer: false);
            } else if (IsOwner) {
                DetectChanges(sendToServer: true);
            }
        }

        // ---- Change detection ----

        private void DetectChanges(bool sendToServer) {
            bool changed = false;

            if (syncPosition && transform.position != _lastPosition) {
                _lastPosition = transform.position;
                changed = true;
            }
            if (syncRotation && transform.rotation != _lastRotation) {
                _lastRotation = transform.rotation;
                changed = true;
            }
            if (syncScale && transform.localScale != _lastScale) {
                _lastScale = transform.localScale;
                changed = true;
            }

            if (!changed)
                return;

            if (sendToServer)
                SendTransformToServer();
            else
                SetDirty();
        }

        // ---- Client -> server path ----

        private void SendTransformToServer() {
            var writer = new NetworkWriter();
            WriteTransformPayload(writer);

            if (IsHost) {
                ApplyTransformPayload(new NetworkReader(writer.ToArraySegment()), markDirty: true);
                return;
            }

            SendRpcInternal(
                SyncTransformRpcId,
                RpcDirection.LocalClientToServer,
                ChannelType.Reliable,
                writer);
        }

        private static void DispatchSyncTransformRpc(NetworkBehaviour target, NetworkReader reader) {
            var networkTransform = target as NetworkTransform;
            if (networkTransform == null) {
                Debug.LogWarning("[NetworkTransform] RPC target is not a NetworkTransform.");
                return;
            }

            networkTransform.ApplyTransformPayload(reader, markDirty: true);
        }

        // ---- Serialization ----

        public override void OnSerialize(NetworkWriter writer, bool initialState) {
            WriteTransformPayload(writer);
        }

        public override void OnDeserialize(NetworkReader reader, bool initialState) {
            ApplyTransformPayload(reader, markDirty: false);
        }

        private void WriteTransformPayload(NetworkWriter writer) {
            byte mask = 0;
            if (syncPosition) mask |= 0x01;
            if (syncRotation) mask |= 0x02;
            if (syncScale) mask |= 0x04;
            writer.WriteByte(mask);

            if (syncPosition)
                writer.WriteVector3(transform.position);
            if (syncRotation)
                writer.WriteQuaternion(transform.rotation);
            if (syncScale)
                writer.WriteVector3(transform.localScale);
        }

        private void ApplyTransformPayload(NetworkReader reader, bool markDirty) {
            byte mask = reader.ReadByte();

            if ((mask & 0x01) != 0) {
                Vector3 position = reader.ReadVector3();
                transform.position = position;
                _lastPosition = position;
            }
            if ((mask & 0x02) != 0) {
                Quaternion rotation = reader.ReadQuaternion();
                transform.rotation = rotation;
                _lastRotation = rotation;
            }
            if ((mask & 0x04) != 0) {
                Vector3 scale = reader.ReadVector3();
                transform.localScale = scale;
                _lastScale = scale;
            }

            if (markDirty)
                SetDirty();
        }

        // ---- Helpers ----

        private void SnapshotCurrent() {
            _lastPosition = transform.position;
            _lastRotation = transform.rotation;
            _lastScale = transform.localScale;
        }
    }
}
