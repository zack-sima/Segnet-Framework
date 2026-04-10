using System;
using System.Collections.Generic;

namespace SegNet {

    /// <summary>
    /// Process-wide table of generated RPC dispatch handlers, keyed by stable rpcId.
    ///
    /// Populated at assembly load by code emitted by the IL post-processor (one
    /// Register call per [Rpc] method discovered in user assemblies).
    ///
    /// The receive path (see ServerManager.OnMsg_Rpc) reads the rpcId off the wire,
    /// looks up the handler, and invokes it with the target NetworkBehaviour and a
    /// reader positioned at the start of the serialized argument blob.
    /// </summary>
    public static class RpcRegistry {

        private static readonly Dictionary<ushort, Action<NetworkBehaviour, NetworkReader>> _handlers =
            new Dictionary<ushort, Action<NetworkBehaviour, NetworkReader>>();

        /// <summary>
        /// Register a generated dispatch handler. Throws on a true collision (same id,
        /// different handler) but silently no-ops if the same handler is re-registered,
        /// which happens naturally when Enter Play Mode runs without a domain reload.
        /// </summary>
        public static void Register(ushort rpcId, Action<NetworkBehaviour, NetworkReader> handler) {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            if (_handlers.TryGetValue(rpcId, out var existing)) {
                if (existing == handler) return; // benign re-registration
                throw new InvalidOperationException(
                    $"[RpcRegistry] Duplicate rpcId 0x{rpcId:X4} with a different handler. " +
                    "Hash collision between two [Rpc] methods.");
            }

            _handlers[rpcId] = handler;
        }

        public static bool TryGetHandler(ushort rpcId, out Action<NetworkBehaviour, NetworkReader> handler) {
            return _handlers.TryGetValue(rpcId, out handler);
        }

        /// <summary>Total number of registered RPC handlers (debug / sanity).</summary>
        public static int Count => _handlers.Count;
    }
}
