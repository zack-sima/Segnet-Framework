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
        public NetworkBehaviour[] AllBehaviours { get; internal set; }

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

        public bool IsLocalPlayer =>
            OwnerPlayer != null && OwnerPlayer.IsLocal;

        /// <summary>True if the local player owns this object.</summary>
        public bool IsOwner =>
            OwnerPlayer != null && OwnerPlayer.IsLocal;

        // ---- Unreliable state sequencing ----

        internal ushort NextUnreliableSequence { get; set; } = 1;
        internal ushort LastReceivedUnreliableSequence { get; set; }
        internal bool HasReceivedUnreliableState { get; set; }

        // ---- Networked object operations ----

        /// <summary>
        /// Spawn a registered network prefab through the active NetworkManager.
        /// Server/host only.
        /// </summary>
        public static NetworkBehaviour InstantiateNetworked(GameObject prefab, Vector3 position, Quaternion rotation,
            NetworkPlayer owner = null) {
            var manager = BaseNetworkManager.Instance;
            if (manager == null) {
                Debug.LogError("[NetworkBehaviour] InstantiateNetworked failed: NetworkManager not found.");
                return null;
            }

            if (!manager.IsServer) {
                Debug.LogError("[NetworkBehaviour] InstantiateNetworked can only be called on server or host.");
                return null;
            }

            return manager.ServerSpawn(prefab, position, rotation, owner);
        }

        /// <summary>
        /// Despawn a runtime network object through the active NetworkManager.
        /// Server/host only.
        /// </summary>
        public static void DestroyNetworked(NetworkBehaviour obj) {
            var manager = BaseNetworkManager.Instance;
            if (manager == null) {
                Debug.LogError("[NetworkBehaviour] DestroyNetworked failed: NetworkManager not found.");
                return;
            }

            if (!manager.IsServer) {
                Debug.LogError("[NetworkBehaviour] DestroyNetworked can only be called on server or host.");
                return;
            }

            manager.ServerDespawn(obj);
        }

        // ---- Dirty tracking ----

        private bool _dirty;

        /// <summary>Mark this behaviour as needing a state update sent to clients.</summary>
        public void SetDirty() {
            if (IsServer && IsSpawned) {
                _dirty = true;
                ServerManager.Instance?.MarkBehaviourDirty(this);
            }
        }

        internal bool ConsumeDirty() {
            bool d = _dirty;
            _dirty = false;
            return d;
        }

        internal void ResetUnreliableStateTracking() {
            NextUnreliableSequence = 1;
            LastReceivedUnreliableSequence = 0;
            HasReceivedUnreliableState = false;
        }

        // ---- SyncVar wiring ----
        //
        // Each NetworkBehaviour subclass with [SyncVar] fields gets a private ulong
        // dirty-mask field generated by the weaver (per class, not shared). Each
        // [SyncVar] field is assigned a bit index 0..63 within its declaring class.
        //
        // The weaver replaces every `stfld syncVarField` outside of constructors with
        // a call to a generated setter that:
        //   1. Compares old vs new (via ceq for primitives/refs, op_Equality for
        //      Unity structs) and bails out if equal — no spurious dirty marks.
        //   2. Stores the new value to the backing field directly.
        //   3. ORs its bit into the per-class dirty mask.
        //   4. Calls SetDirty() so the ServerManager's LateUpdate flush picks it up.
        //   5. Invokes the [SyncVar(hook = "...")] callback if any. The setter path is
        //      what makes the host (and dedicated server) see its own changes — on host,
        //      the receive-side OnDeserialize is short-circuited by the IsHost guard.
        //
        // The weaver also overrides OnSerialize / OnDeserialize per class:
        //   - OnSerialize calls base.OnSerialize first (chains the inheritance), then
        //     writes either every field (initial state) or the per-class mask plus
        //     each dirty field (delta), and finally zeroes the per-class mask.
        //   - OnDeserialize calls base.OnDeserialize first, then reads the same
        //     payload and stores values directly to the backing fields (bypassing the
        //     setter, so receiving clients don't accidentally re-mark dirty), invoking
        //     the same hook on remote clients with (oldValue, newValue) on change.
        //     Hooks accept either `void Hook(T old, T new)` or `void Hook()`.

        /// <summary>
        /// Returns true when this behaviour has an unreliable delta update ready to send.
        /// Generated by the weaver for behaviours with [UnreliableSyncVar] fields.
        /// </summary>
        public virtual bool ShouldSerializeUnreliable(float nowSeconds) {
            return false;
        }

        /// <summary>
        /// Writes the current unreliable delta payload for this behaviour.
        /// Generated by the weaver for behaviours with [UnreliableSyncVar] fields.
        /// </summary>
        public virtual void OnSerializeUnreliable(NetworkWriter writer, float nowSeconds) { }

        /// <summary>
        /// Applies an unreliable delta payload on the receiving peer.
        /// Generated by the weaver for behaviours with [UnreliableSyncVar] fields.
        /// </summary>
        public virtual void OnDeserializeUnreliable(NetworkReader reader) { }

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
        //   [uint networkId][ushort componentIndex][uint rpcId][ushort argLen][argBytes...]

        protected void SendRpcInternal(uint rpcId, RpcDirection direction,
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
                                $"[NetworkBehaviour] ClientToServer RPC 0x{rpcId:X8} called " +
                                "but not running as client.");
                            NetworkWriter.Return(payload);
                            return;
                        }
                        var serverConn = sm.ServerConnection;
                        if (serverConn == ConnectionId.Invalid) {
                            Debug.LogWarning(
                                $"[NetworkBehaviour] ClientToServer RPC 0x{rpcId:X8}: " +
                                "no server connection.");
                            NetworkWriter.Return(payload);
                            return;
                        }
                        sm.Messages.Send(serverConn, NetworkMessageType.RPC, payload, channel);
                        break;
                    }

                case RpcDirection.ServerToClients: {
                        if (!sm.IsServer) {
                            Debug.LogWarning(
                                $"[NetworkBehaviour] ServerToClients RPC 0x{rpcId:X8} called " +
                                "but not running as server.");
                            NetworkWriter.Return(payload);
                            return;
                        }
                        sm.Messages.Broadcast(NetworkMessageType.RPC, payload, channel);
                        break;
                    }

                case RpcDirection.ServerToClient:
                    Debug.LogError(
                        $"[NetworkBehaviour] ServerToClient RPC 0x{rpcId:X8}: " +
                        "weaver should have routed this through SendRpcInternalTo.");
                    NetworkWriter.Return(payload);
                    return;
            }

            NetworkWriter.Return(payload);
        }

        /// <summary>
        /// Single-target server send. Used by woven [Rpc(ServerToClient)] wrappers after
        /// resolving the target NetworkPlayer (typically the object's OwnerPlayer).
        ///
        /// The host shortcut (target == local host player) is handled in the wrapper, so
        /// by the time we get here we expect a remote target with a valid ConnectionId.
        /// </summary>
        protected void SendRpcInternalTo(uint rpcId, ChannelType channel,
            NetworkWriter args, NetworkPlayer target) {

            var sm = ServerManager.Instance;
            if (!CanSendRpc(sm, rpcId)) return;

            if (!sm.IsServer) {
                Debug.LogWarning(
                    $"[NetworkBehaviour] ServerToClient RPC 0x{rpcId:X8} called " +
                    "but not running as server.");
                return;
            }
            if (target == null) {
                Debug.LogWarning(
                    $"[NetworkBehaviour] ServerToClient RPC 0x{rpcId:X8}: target is null.");
                return;
            }
            if (target.ConnectionId == ConnectionId.Invalid) {
                Debug.LogWarning(
                    $"[NetworkBehaviour] ServerToClient RPC 0x{rpcId:X8}: target player " +
                    $"P{target.PlayerId} has no connection (host self-target should have been " +
                    "handled by the wrapper).");
                return;
            }

            var payload = BuildRpcPayload(rpcId, args);
            if (payload == null) return;
            sm.Messages.Send(target.ConnectionId, NetworkMessageType.RPC, payload, channel);
            NetworkWriter.Return(payload);
        }

        /// <summary>Common preconditions for any RPC send. Logs and returns false on failure.</summary>
        private bool CanSendRpc(ServerManager sm, uint rpcId) {
            if (sm == null || !sm.IsOnline) {
                Debug.LogWarning(
                    $"[NetworkBehaviour] Cannot send RPC 0x{rpcId:X8}: ServerManager offline.");
                return false;
            }
            if (!IsSpawned) {
                Debug.LogWarning(
                    $"[NetworkBehaviour] Cannot send RPC 0x{rpcId:X8} on '{name}': not spawned.");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Build the framed RPC payload (the NetworkMessageType.RPC header is added later
        /// by MessageDispatcher). Returns null on overflow.
        /// </summary>
        private NetworkWriter BuildRpcPayload(uint rpcId, NetworkWriter args) {
            var payload = NetworkWriter.Get();
            payload.WriteUInt(NetworkId);
            payload.WriteUShort((ushort)ComponentIndex);
            payload.WriteUInt(rpcId);

            ArraySegment<byte> argSeg = args != null
                ? args.ToArraySegment()
                : new ArraySegment<byte>(Array.Empty<byte>());

            if (argSeg.Count > ushort.MaxValue) {
                Debug.LogError(
                    $"[NetworkBehaviour] RPC 0x{rpcId:X8} arg payload {argSeg.Count} bytes " +
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
