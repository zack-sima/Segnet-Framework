using System.Collections.Generic;
using UnityEngine;

namespace SegNet {

    /// <summary>
    /// Represents a connected player/session. Managed exclusively by the framework —
    /// game code should never create or destroy these directly.
    ///
    /// On the server: one per remote client + one for the host's local player (if hosting).
    /// On clients: one per known player (mirrored from server via PlayerJoined/Left messages).
    ///
    /// GameObject lives under ServerManager in the hierarchy and contains only this component.
    /// </summary>
    public sealed class NetworkPlayer : MonoBehaviour {

        /// <summary>Unique player/session ID assigned by the server. Stable for the session lifetime.</summary>
        public int PlayerId { get; internal set; }

        /// <summary>
        /// Transport connection associated with this player.
        /// Invalid for the host's local player (no network connection to self).
        /// On clients this is only meaningful for the local player (maps to the server connection).
        /// </summary>
        public ConnectionId ConnectionId { get; internal set; } = ConnectionId.Invalid;

        /// <summary>True if this is the local machine's own player.</summary>
        public bool IsLocal { get; internal set; }

        /// <summary>True if this player is the host (server + local client).</summary>
        public bool IsHost { get; internal set; }

        /// <summary>Optional primary NetworkBehaviour owned by this player (e.g. a player character).</summary>
        public NetworkBehaviour PrimaryBehaviour { get; internal set; }

        // ---- Owned objects ----

        internal readonly List<NetworkBehaviour> OwnedObjects = new List<NetworkBehaviour>();

        /// <summary>All network objects currently owned by this player (root behaviours only).</summary>
        public IReadOnlyList<NetworkBehaviour> GetOwnedObjects() => OwnedObjects;

        internal void AddOwnedObject(NetworkBehaviour root) {
            if (!OwnedObjects.Contains(root))
                OwnedObjects.Add(root);
        }

        internal void RemoveOwnedObject(NetworkBehaviour root) {
            OwnedObjects.Remove(root);
        }

        public override string ToString() => $"Player({PlayerId}, conn={ConnectionId})";
    }
}
