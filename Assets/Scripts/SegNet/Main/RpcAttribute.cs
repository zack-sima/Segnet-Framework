using System;

namespace SegNet {

    /// <summary>
    /// Direction of an RPC call.
    /// </summary>
    public enum RpcDirection {
        /// <summary>
        /// Any client sends to server. The server still validates ownership before
        /// dispatching, so a non-owner that calls this is rejected server-side.
        /// Use when you trust clients to gate themselves (or when ownership doesn't apply).
        /// </summary>
        ClientToServer = 0,

        /// <summary>Server broadcasts to all observer clients.</summary>
        ServerToClients = 1,

        /// <summary>
        /// Server sends to a single target client — specifically, the object's
        /// <c>OwnerPlayer</c>. Drops with a warning if the object has no owner.
        /// Host shortcut: if the host owns the object, the impl runs directly with no wire send.
        /// </summary>
        ServerToClient = 2,

        /// <summary>
        /// Owning client sends to server. The wrapper itself drops the call client-side
        /// unless <c>OwnerPlayer.IsLocal</c>, so non-owners can't even put bytes on the wire.
        /// Server still re-validates ownership on receive (defense in depth).
        /// Prefer this over <see cref="ClientToServer"/> for player-controlled objects to
        /// prevent one player from impersonating another.
        /// </summary>
        LocalClientToServer = 3,
    }

    /// <summary>
    /// Marks a method on a NetworkBehaviour as a Remote Procedure Call.
    ///
    /// The IL weaver will eventually:
    ///   1. Replace the method body with serialization + send logic.
    ///   2. Generate a __RPC_Handler_MethodName dispatch method.
    ///   3. Register the handler in a static dispatch table.
    ///
    /// Supported parameter types (current and planned):
    ///   - All [SyncVar]-supported primitives and Unity types
    ///   - NetworkBehaviour references (serialized by NetworkId)
    ///   - NetworkPlayer references (serialized by PlayerId)
    ///   - Arrays/Lists of the above
    ///
    /// Usage:
    ///   [Rpc(RpcDirection.ClientToServer)]
    ///   void CmdFire(Vector3 direction) { /* server-side logic */ }
    ///
    ///   [Rpc(RpcDirection.ServerToClients)]
    ///   void RpcExplosion(Vector3 position, float radius) { /* client-side logic */ }
    ///
    ///   [Rpc(RpcDirection.ServerToClient)]
    ///   void TargetShowMessage(string message) { /* runs on one client */ }
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class RpcAttribute : Attribute {

        public RpcDirection Direction { get; }

        /// <summary>Transport channel. Reliable (default) or Unreliable.</summary>
        public ChannelType Channel { get; set; } = ChannelType.Reliable;

        public RpcAttribute(RpcDirection direction) {
            Direction = direction;
        }
    }
}
