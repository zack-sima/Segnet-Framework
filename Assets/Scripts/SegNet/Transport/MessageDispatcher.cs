using System;
using System.Collections.Generic;
using UnityEngine;

namespace SegNet {

    /// <summary>
    /// Framework message types. User-defined messages should start at UserStart (1000+).
    /// Types below UserStart are reserved for the framework.
    /// </summary>
    public enum NetworkMessageType : ushort {
        None = 0,

        // Object lifecycle
        Spawn = 1,
        Despawn = 2,

        // State replication
        StateUpdate = 3,
        StateDelta = 4,
        FullSnapshot = 5,

        // RPC
        RPC = 10,

        // Player/session
        PlayerJoined = 20,
        PlayerLeft = 21,

        // Authority
        OwnershipChange = 30,

        // User-defined messages start here
        UserStart = 1000,
    }

    /// <summary>
    /// Routes network messages by type. Sits between ServerManager and NetworkStreamManager.
    ///
    /// Wire format: [ushort messageType][payload bytes...]
    ///
    /// The dispatcher adds the 2-byte header on send and strips it on receive,
    /// passing the remaining bytes to the registered handler as a NetworkReader.
    /// </summary>
    public class MessageDispatcher {
        private readonly NetworkStreamManager _streamManager;

        private readonly Dictionary<ushort, Action<ConnectionId, NetworkReader>> _handlers =
            new Dictionary<ushort, Action<ConnectionId, NetworkReader>>();

        public MessageDispatcher(NetworkStreamManager streamManager) {
            _streamManager = streamManager ?? throw new ArgumentNullException(nameof(streamManager));
            _streamManager.OnMessageReceived += HandleIncoming;
        }

        public void Dispose() {
            if (_streamManager != null)
                _streamManager.OnMessageReceived -= HandleIncoming;
        }

        // ---- Handler registration ----

        public void RegisterHandler(NetworkMessageType type, Action<ConnectionId, NetworkReader> handler) {
            RegisterHandler((ushort)type, handler);
        }

        public void RegisterHandler(ushort type, Action<ConnectionId, NetworkReader> handler) {
            _handlers[type] = handler;
        }

        public void UnregisterHandler(NetworkMessageType type) {
            UnregisterHandler((ushort)type);
        }

        public void UnregisterHandler(ushort type) {
            _handlers.Remove(type);
        }

        // ---- Send ----

        /// <summary>
        /// Sends a message to a single connection. The writer should contain only the payload
        /// (the message type header is prepended automatically).
        /// </summary>
        public void Send(ConnectionId target, NetworkMessageType type, NetworkWriter writer,
            ChannelType channel = ChannelType.Reliable) {
            Send(target, (ushort)type, writer, channel);
        }

        public void Send(ConnectionId target, ushort type, NetworkWriter writer,
            ChannelType channel = ChannelType.Reliable) {
            byte[] framed = Frame(type, writer);
            _streamManager.Send(target, framed, channel);
        }

        /// <summary>Broadcasts a message to all connections.</summary>
        public void Broadcast(NetworkMessageType type, NetworkWriter writer,
            ChannelType channel = ChannelType.Reliable) {
            Broadcast((ushort)type, writer, channel);
        }

        public void Broadcast(ushort type, NetworkWriter writer,
            ChannelType channel = ChannelType.Reliable) {
            byte[] framed = Frame(type, writer);
            _streamManager.Broadcast(framed, channel);
        }

        /// <summary>Sends a message to all connections except one (e.g. skip the sender).</summary>
        public void BroadcastExcept(ConnectionId exclude, NetworkMessageType type, NetworkWriter writer,
            ChannelType channel = ChannelType.Reliable) {
            BroadcastExcept(exclude, (ushort)type, writer, channel);
        }

        public void BroadcastExcept(ConnectionId exclude, ushort type, NetworkWriter writer,
            ChannelType channel = ChannelType.Reliable) {
            byte[] framed = Frame(type, writer);
            _streamManager.BroadcastExcept(exclude, framed, channel);
        }

        // ---- Internal ----

        private byte[] Frame(ushort type, NetworkWriter writer) {
            var payload = writer.ToArraySegment();
            byte[] framed = new byte[2 + payload.Count];
            framed[0] = (byte)type;
            framed[1] = (byte)(type >> 8);
            Buffer.BlockCopy(payload.Array, payload.Offset, framed, 2, payload.Count);
            return framed;
        }

        private void HandleIncoming(ConnectionId from, byte[] data) {
            if (data == null || data.Length < 2) {
                Debug.LogWarning($"[MessageDispatcher] Received too-short message from {from}");
                return;
            }

            ushort type = (ushort)(data[0] | (data[1] << 8));

            if (!_handlers.TryGetValue(type, out var handler)) {
                Debug.LogWarning($"[MessageDispatcher] No handler for message type {type} from {from}");
                return;
            }

            var reader = new NetworkReader(data, 2, data.Length - 2);

            try {
                handler(from, reader);
            } catch (Exception ex) {
                Debug.LogError($"[MessageDispatcher] Error handling message type {type} from {from}: {ex}");
            }
        }
    }
}
