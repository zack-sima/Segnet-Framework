using System;
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

        /// <summary>True when this peer is running as both server and client (host mode).</summary>
        public bool IsHost =>
            ServerManager.Instance != null && ServerManager.Instance.IsHost;

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

        // ---- SyncVar dirty mask ----
        //
        // The IL weaver assigns each [SyncVar] field a sequential bit index within its
        // declaring type (max 64 SyncVars per behaviour). Generated setter wrappers call
        // SetSyncVarDirty(bit) on assignment, which both flips the bit and marks the whole
        // behaviour dirty so the existing LateUpdate flush in ServerManager picks it up.
        //
        // Generated OnSerialize writes the mask + only the dirty fields for delta updates
        // and clears it when done. Generated OnDeserialize reads the mask back.

        internal ulong _syncVarDirtyMask;

        /// <summary>Called by weaver-generated SyncVar setters. Marks the bit and the behaviour dirty.</summary>
        protected void SetSyncVarDirty(int bitIndex) {
            _syncVarDirtyMask |= 1UL << bitIndex;
            SetDirty();
        }

        /// <summary>Called by weaver-generated OnSerialize after writing dirty deltas.</summary>
        internal void ClearSyncVarDirtyMask() {
            _syncVarDirtyMask = 0;
        }

        // ---- RPC send entry point ----
        //
        // Single funnel called by woven [Rpc] method bodies. The original method body is
        // moved to __SegNetRpcImpl_<Name> and replaced with code that:
        //   1. Builds a NetworkWriter containing the serialized arguments
        //   2. Calls SendRpcInternal with the stable rpcId, direction, and channel
        //   3. (Host shortcut) calls the impl method directly so the host's local side
        //      sees ServerToClients RPCs and ClientToServer RPCs without round-trip
        //
        // Wire format of the payload built here (the NetworkMessageType.RPC header is
        // prepended by MessageDispatcher.Send/Broadcast):
        //   [uint networkId][ushort componentIndex][ushort rpcId][ushort argLen][argBytes...]

        protected void SendRpcInternal(ushort rpcId, RpcDirection direction,
            ChannelType channel, NetworkWriter args) {

            var sm = ServerManager.Instance;
            if (!CanSendRpc(sm, rpcId)) return;

            var payload = BuildRpcPayload(rpcId, args);
            if (payload == null) return;

            switch (direction) {
                case RpcDirection.ClientToServer:
                case RpcDirection.LocalClientToServer: {
                    if (!sm.IsClient) {
                        Debug.LogWarning(
                            $"[NetworkBehaviour] ClientToServer RPC 0x{rpcId:X4} called " +
                            "but not running as client.");
                        return;
                    }
                    var serverConn = sm.ServerConnection;
                    if (serverConn == ConnectionId.Invalid) {
                        Debug.LogWarning(
                            $"[NetworkBehaviour] ClientToServer RPC 0x{rpcId:X4}: " +
                            "no server connection.");
                        return;
                    }
                    sm.Messages.Send(serverConn, NetworkMessageType.RPC, payload, channel);
                    break;
                }

                case RpcDirection.ServerToClients: {
                    if (!sm.IsServer) {
                        Debug.LogWarning(
                            $"[NetworkBehaviour] ServerToClients RPC 0x{rpcId:X4} called " +
                            "but not running as server.");
                        return;
                    }
                    sm.Messages.Broadcast(NetworkMessageType.RPC, payload, channel);
                    break;
                }

                case RpcDirection.ServerToClient:
                    Debug.LogError(
                        $"[NetworkBehaviour] ServerToClient RPC 0x{rpcId:X4}: " +
                        "weaver should have routed this through SendRpcInternalTo.");
                    break;
            }
        }

        /// <summary>
        /// Single-target server send. Used by woven [Rpc(ServerToClient)] wrappers after
        /// resolving the target NetworkPlayer (typically the object's OwnerPlayer).
        ///
        /// The host shortcut (target == local host player) is handled in the wrapper, so
        /// by the time we get here we expect a remote target with a valid ConnectionId.
        /// </summary>
        protected void SendRpcInternalTo(ushort rpcId, ChannelType channel,
            NetworkWriter args, NetworkPlayer target) {

            var sm = ServerManager.Instance;
            if (!CanSendRpc(sm, rpcId)) return;

            if (!sm.IsServer) {
                Debug.LogWarning(
                    $"[NetworkBehaviour] ServerToClient RPC 0x{rpcId:X4} called " +
                    "but not running as server.");
                return;
            }
            if (target == null) {
                Debug.LogWarning(
                    $"[NetworkBehaviour] ServerToClient RPC 0x{rpcId:X4}: target is null.");
                return;
            }
            if (target.ConnectionId == ConnectionId.Invalid) {
                Debug.LogWarning(
                    $"[NetworkBehaviour] ServerToClient RPC 0x{rpcId:X4}: target player " +
                    $"P{target.PlayerId} has no connection (host self-target should have been " +
                    "handled by the wrapper).");
                return;
            }

            var payload = BuildRpcPayload(rpcId, args);
            if (payload == null) return;
            sm.Messages.Send(target.ConnectionId, NetworkMessageType.RPC, payload, channel);
        }

        /// <summary>Common preconditions for any RPC send. Logs and returns false on failure.</summary>
        private bool CanSendRpc(ServerManager sm, ushort rpcId) {
            if (sm == null || !sm.IsOnline) {
                Debug.LogWarning(
                    $"[NetworkBehaviour] Cannot send RPC 0x{rpcId:X4}: ServerManager offline.");
                return false;
            }
            if (!IsSpawned) {
                Debug.LogWarning(
                    $"[NetworkBehaviour] Cannot send RPC 0x{rpcId:X4} on '{name}': not spawned.");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Build the framed RPC payload (the NetworkMessageType.RPC header is added later
        /// by MessageDispatcher). Returns null on overflow.
        /// </summary>
        private NetworkWriter BuildRpcPayload(ushort rpcId, NetworkWriter args) {
            var payload = new NetworkWriter(64);
            payload.WriteUInt(NetworkId);
            payload.WriteUShort((ushort)ComponentIndex);
            payload.WriteUShort(rpcId);

            ArraySegment<byte> argSeg = args != null
                ? args.ToArraySegment()
                : new ArraySegment<byte>(Array.Empty<byte>());

            if (argSeg.Count > ushort.MaxValue) {
                Debug.LogError(
                    $"[NetworkBehaviour] RPC 0x{rpcId:X4} arg payload {argSeg.Count} bytes " +
                    $"exceeds ushort max ({ushort.MaxValue}).");
                return null;
            }

            payload.WriteUShort((ushort)argSeg.Count);
            if (argSeg.Count > 0)
                payload.WriteRawBytes(argSeg);
            return payload;
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
