using System;
using System.Collections.Generic;
using UnityEngine;

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
    ///
    /// Domain-reload-free play mode: Register is idempotent — it always overwrites
    /// the handler for a given rpcId. This means stale delegates from a previous
    /// play-mode session are naturally replaced when the
    /// [RuntimeInitializeOnLoadMethod] registration methods re-run, without needing
    /// a separate Reset step (which had ordering issues with other SubsystemRegistration
    /// callbacks).
    /// </summary>
    public static class RpcRegistry {

        private static readonly Dictionary<uint, Action<NetworkBehaviour, NetworkReader>> _handlers =
            new Dictionary<uint, Action<NetworkBehaviour, NetworkReader>>();

        /// <summary>
        /// Register a generated dispatch handler. Always overwrites the previous handler
        /// for the same rpcId, which makes it safe for domain-reload-free play mode
        /// (stale delegates are replaced on re-entry). In debug builds, logs a warning
        /// if a *different* handler was already registered — this could indicate a hash
        /// collision between two [Rpc] methods.
        /// </summary>
        public static void Register(uint rpcId, Action<NetworkBehaviour, NetworkReader> handler) {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

#if DEBUG || UNITY_EDITOR
            if (_handlers.TryGetValue(rpcId, out var existing) && existing != handler) {
                Debug.LogWarning(
                    $"[RpcRegistry] Overwriting rpcId 0x{rpcId:X8} with a different handler. " +
                    "If this happens every play-mode entry it is harmless (stale delegate replacement). " +
                    "If two distinct [Rpc] methods share this id, it is a hash collision — rename one " +
                    "of the colliding methods or change its parameter list to resolve.");
            }
#endif

            _handlers[rpcId] = handler;
        }

        public static bool TryGetHandler(uint rpcId, out Action<NetworkBehaviour, NetworkReader> handler) {
            return _handlers.TryGetValue(rpcId, out handler);
        }

        /// <summary>Total number of registered RPC handlers (debug / sanity).</summary>
        public static int Count => _handlers.Count;
    }
}
