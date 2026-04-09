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

        [Header("Sync Toggles")]
        [SerializeField] private bool syncPosition = true;
        [SerializeField] private bool syncRotation = true;
        [SerializeField] private bool syncScale = false;

        // Cached last-sent values (server) / last-received values (client)
        private Vector3 _lastPosition;
        private Quaternion _lastRotation;
        private Vector3 _lastScale;

        public override void OnNetworkSpawn() {
            SnapshotCurrent();
        }

        private void LateUpdate() {
            if (!IsSpawned) return;

            if (IsServer) {
                DetectChanges();
            }
        }

        // ---- Server: change detection ----

        private void DetectChanges() {
            bool dirty = false;

            if (syncPosition && transform.position != _lastPosition) {
                _lastPosition = transform.position;
                dirty = true;
            }
            if (syncRotation && transform.rotation != _lastRotation) {
                _lastRotation = transform.rotation;
                dirty = true;
            }
            if (syncScale && transform.localScale != _lastScale) {
                _lastScale = transform.localScale;
                dirty = true;
            }

            if (dirty)
                SetDirty();
        }

        // ---- Serialization ----

        public override void OnSerialize(NetworkWriter writer, bool initialState) {
            // Write which channels are active so the reader knows what to expect
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

        public override void OnDeserialize(NetworkReader reader, bool initialState) {
            byte mask = reader.ReadByte();

            if ((mask & 0x01) != 0) {
                Vector3 pos = reader.ReadVector3();
                transform.position = pos;
                _lastPosition = pos;
            }
            if ((mask & 0x02) != 0) {
                Quaternion rot = reader.ReadQuaternion();
                transform.rotation = rot;
                _lastRotation = rot;
            }
            if ((mask & 0x04) != 0) {
                Vector3 scale = reader.ReadVector3();
                transform.localScale = scale;
                _lastScale = scale;
            }
        }

        // ---- Helpers ----

        private void SnapshotCurrent() {
            _lastPosition = transform.position;
            _lastRotation = transform.rotation;
            _lastScale = transform.localScale;
        }
    }
}
