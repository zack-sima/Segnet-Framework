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
    /// Domain-reload-free play mode: Register is idempotent — re-registering the
    /// same underlying method (even via a new delegate instance) is a silent no-op.
    /// This means stale delegates from a previous play-mode session are harmlessly
    /// skipped when the [RuntimeInitializeOnLoadMethod] registration methods re-run,
    /// without needing a separate Reset step (which had ordering issues with other
    /// SubsystemRegistration callbacks). True hash collisions (different method, same
    /// rpcId) throw immediately so they're caught during development.
    /// </summary>
    public static class RpcRegistry {

        private static readonly Dictionary<uint, Action<NetworkBehaviour, NetworkReader>> _handlers =
            new Dictionary<uint, Action<NetworkBehaviour, NetworkReader>>();

        /// <summary>
        /// Register a generated dispatch handler. Idempotent — safe to call again with a
        /// new delegate wrapping the same method (which is what happens on play-mode
        /// re-entry without domain reload). Throws on a true hash collision (same rpcId,
        /// different underlying method), which would silently break one of the two RPCs.
        /// </summary>
        public static void Register(uint rpcId, Action<NetworkBehaviour, NetworkReader> handler) {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            if (_handlers.TryGetValue(rpcId, out var existing)) {
                // Domain-reload-free play mode: the [RuntimeInitializeOnLoadMethod]
                // registration method creates a fresh delegate each entry, but it wraps
                // the same static dispatch method. Comparing Delegate.Method (the
                // underlying MethodInfo) lets us distinguish harmless re-registration
                // from a genuine hash collision between two different [Rpc] methods.
                if (existing.Method == handler.Method)
                    return;

                throw new InvalidOperationException(
                    $"[RpcRegistry] Hash collision on rpcId 0x{rpcId:X8}! " +
                    $"Existing handler: {existing.Method.DeclaringType?.Name}.{existing.Method.Name}, " +
                    $"new handler: {handler.Method.DeclaringType?.Name}.{handler.Method.Name}. " +
                    "Rename one of the colliding [Rpc] methods or change its parameter list to produce " +
                    "a different hash.");
            }

            _handlers[rpcId] = handler;
        }

        public static bool TryGetHandler(uint rpcId, out Action<NetworkBehaviour, NetworkReader> handler) {
            return _handlers.TryGetValue(rpcId, out handler);
        }

        /// <summary>Total number of registered RPC handlers (debug / sanity).</summary>
        public static int Count => _handlers.Count;
    }
}
