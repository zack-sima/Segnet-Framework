using System.Collections.Generic;
using UnityEngine;

namespace SegNet {

    /// <summary>
    /// Base class for all replicated objects. Subclass this for game-specific networked logic.
    ///
    /// Runtime-spawned prefabs:
    ///   - One root NetworkBehaviour on the root GameObject (required).
    ///   - Additional NetworkBehaviours allowed on root or one level of children.
    ///   - All share a single NetworkId (the root's). Each has a ComponentIndex.
    ///   - Only root GameObject destruction is allowed (destroys all children).
    ///
    /// Scene-placed objects:
    ///   - Mark isSceneObject = true in the inspector.
    ///   - Each isSceneObject behaviour gets its own NetworkId (independent, no root/child linkage).
    ///   - Never destroyed by the framework. Scene reload handles cleanup.
    ///   - Self-registers with NetworkSceneManager on Awake.
    /// </summary>
    public class NetworkBehaviour : MonoBehaviour {

        // ---- Serialized (set in inspector) ----

        [Header("Scene Object")]
        [Tooltip("Check this for objects placed in the scene that should be networked. " +
                 "Leave unchecked for runtime-spawned prefab instances.")]
        [SerializeField] private bool isSceneObject;

        // ---- Framework-managed state ----

        /// <summary>Runtime network ID. Shared by all behaviours on a runtime-spawned root.</summary>
        public uint NetworkId { get; internal set; }

        /// <summary>Stable prefab type ID from PrefabRegistry. 0 for scene objects.</summary>
        public ushort PrefabId { get; internal set; }

        /// <summary>Index among all NetworkBehaviours on the root hierarchy (0 = root itself).</summary>
        public int ComponentIndex { get; internal set; }

        /// <summary>Owning player, or null for server-owned world objects.</summary>
        public NetworkPlayer OwnerPlayer { get; internal set; }

        /// <summary>True once the framework has activated this object for networking.</summary>
        public bool IsSpawned { get; internal set; }

        /// <summary>Deterministic ID derived from scene hierarchy path. 0 for runtime objects.</summary>
        public uint SceneObjectId { get; internal set; }

        // ---- Root / child model (runtime-spawned objects) ----

        /// <summary>The root NetworkBehaviour of this object's hierarchy. Self for scene objects.</summary>
        public NetworkBehaviour Root { get; internal set; }

        /// <summary>All NetworkBehaviours under the root (populated on root only). Includes root at index 0.</summary>
        internal NetworkBehaviour[] AllBehaviours;

        // ---- Convenience properties ----

        public bool IsSceneObject => isSceneObject;
        public bool IsRoot => Root == this;
        public bool HasOwner => OwnerPlayer != null;

        public bool IsServer =>
            ServerManager.Instance != null && ServerManager.Instance.IsServer;

        public bool IsClient =>
            ServerManager.Instance != null && ServerManager.Instance.IsClient;

        /// <summary>True if the local player owns this object.</summary>
        public bool IsOwner =>
            OwnerPlayer != null && OwnerPlayer.IsLocal;

        // ---- Dirty tracking ----

        private bool _dirty;

        /// <summary>Mark this behaviour as needing a state update sent to clients.</summary>
        public void SetDirty() {
            if (IsServer && IsSpawned)
                _dirty = true;
        }

        internal bool ConsumeDirty() {
            bool d = _dirty;
            _dirty = false;
            return d;
        }

        // ---- Virtual lifecycle callbacks ----

        /// <summary>Called on all peers after this behaviour is activated for networking.</summary>
        public virtual void OnNetworkSpawn() { }

        /// <summary>Called on all peers just before this behaviour is deactivated.</summary>
        public virtual void OnNetworkDespawn() { }

        /// <summary>
        /// Write this behaviour's replicated state.
        /// Called on the server. initialState = true for spawn/late-join, false for delta updates.
        /// </summary>
        public virtual void OnSerialize(NetworkWriter writer, bool initialState) { }

        /// <summary>
        /// Read this behaviour's replicated state.
        /// Called on the client. initialState = true for spawn/late-join, false for delta updates.
        /// </summary>
        public virtual void OnDeserialize(NetworkReader reader, bool initialState) { }

        // ---- Scene object self-registration ----

        private static readonly List<NetworkBehaviour> PendingSceneRegistrations =
            new List<NetworkBehaviour>();

        protected virtual void Awake() {
            if (isSceneObject) {
                var sceneManager = NetworkSceneManager.Instance;
                if (sceneManager != null)
                    sceneManager.RegisterSceneObject(this);
                else
                    PendingSceneRegistrations.Add(this);
            }
        }

        /// <summary>
        /// Called by NetworkSceneManager to drain behaviours that registered before it was ready.
        /// </summary>
        internal static List<NetworkBehaviour> DrainPendingSceneRegistrations() {
            var list = new List<NetworkBehaviour>(PendingSceneRegistrations);
            PendingSceneRegistrations.Clear();
            return list;
        }

        // ---- Debug ----

        public override string ToString() {
            string owner = OwnerPlayer != null ? $"owner=P{OwnerPlayer.PlayerId}" : "world";
            return $"NetObj({name}, nid={NetworkId}, {owner}, scene={isSceneObject})";
        }
    }
}
