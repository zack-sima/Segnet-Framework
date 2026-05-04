using System;
using System.Collections.Generic;
using UnityEngine;

namespace SegNet {

    /// <summary>
    /// Transparent fragmentation/reassembly layer.
    ///
    /// Sits between MessageDispatcher and NetworkConnectionManager.
    /// Accepts arbitrary byte[] payloads — if a payload exceeds the transport's max packet size
    /// it is automatically chunked with a small header and reassembled on the remote side.
    ///
    /// Small messages (fit in one packet) are sent with a single-byte prefix (0x00).
    /// Fragmented messages use prefix 0x01 followed by stream metadata.
    /// </summary>
    public class NetworkStreamManager : MonoBehaviour {
        private NetworkConnectionManager connectionManager;

        private const byte PrefixWhole = 0x00;
        private const byte PrefixFragment = 0x01;

        // Fragment header: 1 prefix + 2 streamId + 4 totalLen + 4 offset + 1 isLast = 12 bytes
        private const int FragmentHeaderSize = 1 + 2 + 4 + 4 + 1;

        /// <summary>
        /// Maximum reassembled message size accepted from the wire (16 MB).
        /// Fragments claiming a larger totalLength are silently dropped to prevent
        /// a malicious or buggy peer from OOM-crashing the receiver.
        /// </summary>
        private const int MaxReassembledMessageSize = 16 * 1024 * 1024;

        private ushort _nextStreamId = 1;

        /// <summary>
        /// Fires when a complete message has been reassembled (or arrived whole).
        /// Parameters: (sender ConnectionId, complete payload bytes).
        /// </summary>
        public event Action<ConnectionId, byte[]> OnMessageReceived;

        // Per-connection, per-stream reassembly state
        private readonly Dictionary<ConnectionId, Dictionary<ushort, IncomingStream>> _incoming =
            new Dictionary<ConnectionId, Dictionary<ushort, IncomingStream>>();

        /// <summary>
        /// Partial fragment streams older than this (in seconds) are discarded.
        /// Protects against memory leaks when a sender drops mid-message.
        /// </summary>
        private const float FragmentTimeoutSeconds = 10f;

        private float _nextStalenessSweepAt;

        private class IncomingStream {
            public int TotalLength;
            public int Received;
            public byte[] Buffer;
            public float LastActivityTime;
        }

        // ---- Unity lifecycle ----

        public void Configure(NetworkConnectionManager manager) {
            if (connectionManager == manager)
                return;

            Unsubscribe();
            connectionManager = manager;
            if (isActiveAndEnabled)
                Subscribe();
        }

        private void Awake() {
            if (connectionManager == null) {
                Debug.LogError("[NetworkStreamManager] No NetworkConnectionManager assigned.");
                enabled = false;
                return;
            }
            Subscribe();
        }

        private void OnDestroy() {
            Unsubscribe();
        }

        private void Update() {
            SweepStaleFragments();
        }

        private void Subscribe() {
            if (connectionManager == null)
                return;

            connectionManager.OnData -= HandleTransportData;
            connectionManager.OnClientDisconnected -= HandleDisconnect;
            connectionManager.OnData += HandleTransportData;
            connectionManager.OnClientDisconnected += HandleDisconnect;
        }

        private void Unsubscribe() {
            if (connectionManager != null) {
                connectionManager.OnData -= HandleTransportData;
                connectionManager.OnClientDisconnected -= HandleDisconnect;
            }
        }

        // ---- Public send API ----

        /// <summary>Send a complete message to one connection. Fragments automatically if needed.</summary>
        public void Send(ConnectionId target, byte[] data, ChannelType channel) {
            if (data == null || data.Length == 0) return;

            int maxPacket = connectionManager.MaxPacketSize;

            // +1 for the PrefixWhole byte
            if (data.Length + 1 <= maxPacket) {
                SendWhole(target, data, channel);
            } else if (channel == ChannelType.Unreliable) {
                Debug.LogWarning(
                    $"[NetworkStreamManager] Dropping oversized unreliable payload ({data.Length} bytes). " +
                    $"Max whole-packet payload is {maxPacket - 1} bytes.");
            } else {
                SendFragmented(target, data, channel);
            }
        }

        /// <summary>Broadcast a complete message to all connections.</summary>
        public void Broadcast(byte[] data, ChannelType channel) {
            if (data == null || data.Length == 0) return;
            BroadcastInternal(data, channel, ConnectionId.Invalid);
        }

        /// <summary>Broadcast to all connections except one.</summary>
        public void BroadcastExcept(ConnectionId exclude, byte[] data, ChannelType channel) {
            if (data == null || data.Length == 0) return;
            BroadcastInternal(data, channel, exclude);
        }

        /// <summary>
        /// Shared broadcast implementation. Computes whole-message prefix or fragment
        /// packets once and reuses them across all connections.
        /// </summary>
        private void BroadcastInternal(byte[] data, ChannelType channel, ConnectionId exclude) {
            int maxPacket = connectionManager.MaxPacketSize;

            if (data.Length + 1 <= maxPacket) {
                // Small message — build prefixed packet once.
                byte[] packet = new byte[1 + data.Length];
                packet[0] = PrefixWhole;
                Buffer.BlockCopy(data, 0, packet, 1, data.Length);
                var segment = new ArraySegment<byte>(packet);

                foreach (var conn in connectionManager.Connections) {
                    if (conn != exclude)
                        connectionManager.SendTo(conn, segment, channel);
                }
            } else if (channel == ChannelType.Unreliable) {
                Debug.LogWarning(
                    $"[NetworkStreamManager] Dropping oversized unreliable broadcast ({data.Length} bytes). " +
                    $"Max whole-packet payload is {maxPacket - 1} bytes.");
            } else {
                // Large message — compute fragments once, send to all.
                var fragments = BuildFragments(data);
                foreach (var conn in connectionManager.Connections) {
                    if (conn != exclude) {
                        for (int i = 0; i < fragments.Count; i++)
                            connectionManager.SendTo(conn, new ArraySegment<byte>(fragments[i]), channel);
                    }
                }
            }
        }

        // ---- Send internals ----

        private void SendWhole(ConnectionId target, byte[] data, ChannelType channel) {
            byte[] packet = new byte[1 + data.Length];
            packet[0] = PrefixWhole;
            Buffer.BlockCopy(data, 0, packet, 1, data.Length);
            connectionManager.SendTo(target, new ArraySegment<byte>(packet), channel);
        }

        private void SendFragmented(ConnectionId target, byte[] data, ChannelType channel) {
            var fragments = BuildFragments(data);
            for (int i = 0; i < fragments.Count; i++)
                connectionManager.SendTo(target, new ArraySegment<byte>(fragments[i]), channel);
        }

        /// <summary>
        /// Splits data into fragment packets. Each packet includes the fragment header.
        /// Reusable across multiple targets for broadcast without re-computing.
        /// </summary>
        private List<byte[]> BuildFragments(byte[] data) {
            ushort streamId = _nextStreamId++;
            if (_nextStreamId == 0) _nextStreamId = 1;

            int maxPacket = connectionManager.MaxPacketSize;
            int maxChunk = Mathf.Max(1, maxPacket - FragmentHeaderSize);
            int total = data.Length;
            int offset = 0;

            var fragments = new List<byte[]>();

            while (offset < total) {
                int chunkSize = Mathf.Min(maxChunk, total - offset);
                bool isLast = (offset + chunkSize) >= total;

                byte[] packet = new byte[FragmentHeaderSize + chunkSize];
                int p = 0;

                packet[p++] = PrefixFragment;

                // streamId (ushort LE)
                packet[p++] = (byte)streamId;
                packet[p++] = (byte)(streamId >> 8);

                // total length (int LE)
                WriteInt(packet, ref p, total);

                // offset (int LE)
                WriteInt(packet, ref p, offset);

                // isLast
                packet[p++] = isLast ? (byte)1 : (byte)0;

                // chunk data
                Buffer.BlockCopy(data, offset, packet, p, chunkSize);

                fragments.Add(packet);
                offset += chunkSize;
            }

            return fragments;
        }

        // ---- Receive internals ----

        private void HandleTransportData(ConnectionId from, ArraySegment<byte> payload, ChannelType channel) {
            if (payload.Count <= 0) return;

            byte prefix = payload.Array[payload.Offset];

            switch (prefix) {
                case PrefixWhole:
                    HandleWhole(from, payload);
                    break;
                case PrefixFragment:
                    HandleFragment(from, payload);
                    break;
                default:
                    Debug.LogWarning($"[NetworkStreamManager] Unknown prefix 0x{prefix:X2} from {from}");
                    break;
            }
        }

        private void HandleWhole(ConnectionId from, ArraySegment<byte> payload) {
            // Strip the 1-byte prefix
            int dataLen = payload.Count - 1;
            if (dataLen <= 0) return;

            byte[] data = new byte[dataLen];
            Buffer.BlockCopy(payload.Array, payload.Offset + 1, data, 0, dataLen);
            OnMessageReceived?.Invoke(from, data);
        }

        private void HandleFragment(ConnectionId from, ArraySegment<byte> payload) {
            // Need at least the full fragment header
            if (payload.Count < FragmentHeaderSize) return;

            byte[] arr = payload.Array;
            int p = payload.Offset + 1; // skip prefix byte

            ushort streamId = (ushort)(arr[p] | (arr[p + 1] << 8));
            p += 2;

            int totalLength = ReadInt(arr, ref p);
            int chunkOffset = ReadInt(arr, ref p);
            bool isLast = arr[p++] != 0;

            int chunkLen = payload.Count - (p - payload.Offset);
            if (chunkLen <= 0) return;

            if (totalLength <= 0 || totalLength > MaxReassembledMessageSize) {
                Debug.LogWarning(
                    $"[NetworkStreamManager] Dropping fragment from {from}: " +
                    $"claimed totalLength {totalLength} exceeds limit {MaxReassembledMessageSize}.");
                return;
            }

            if (!_incoming.TryGetValue(from, out var streams)) {
                streams = new Dictionary<ushort, IncomingStream>();
                _incoming[from] = streams;
            }

            if (!streams.TryGetValue(streamId, out var stream)) {
                stream = new IncomingStream {
                    TotalLength = totalLength,
                    Received = 0,
                    Buffer = new byte[totalLength],
                    LastActivityTime = Time.realtimeSinceStartup,
                };
                streams[streamId] = stream;
            }

            // Bounds check
            if (chunkOffset < 0 || chunkOffset + chunkLen > stream.TotalLength) return;

            Buffer.BlockCopy(arr, p, stream.Buffer, chunkOffset, chunkLen);
            stream.Received += chunkLen;
            stream.LastActivityTime = Time.realtimeSinceStartup;

            if (isLast || stream.Received >= stream.TotalLength) {
                byte[] complete = stream.Buffer;
                streams.Remove(streamId);
                OnMessageReceived?.Invoke(from, complete);
            }
        }

        private void HandleDisconnect(ConnectionId id, DisconnectReason reason) {
            // Clean up any partial streams for this connection
            _incoming.Remove(id);
        }

        /// <summary>
        /// Periodically discard partial fragment streams that haven't received
        /// new data within <see cref="FragmentTimeoutSeconds"/>. Prevents memory
        /// leaks when a sender drops mid-message without fully disconnecting.
        /// </summary>
        private void SweepStaleFragments() {
            float now = Time.realtimeSinceStartup;
            if (now < _nextStalenessSweepAt) return;
            _nextStalenessSweepAt = now + FragmentTimeoutSeconds;

            if (_incoming.Count == 0) return;

            // Collect stale entries to avoid modifying dictionaries during iteration.
            List<(ConnectionId conn, ushort streamId)> stale = null;

            foreach (var connKvp in _incoming) {
                foreach (var streamKvp in connKvp.Value) {
                    if (now - streamKvp.Value.LastActivityTime > FragmentTimeoutSeconds) {
                        if (stale == null)
                            stale = new List<(ConnectionId, ushort)>();
                        stale.Add((connKvp.Key, streamKvp.Key));
                    }
                }
            }

            if (stale == null) return;

            for (int i = 0; i < stale.Count; i++) {
                var (conn, streamId) = stale[i];
                if (_incoming.TryGetValue(conn, out var streams)) {
                    streams.Remove(streamId);
                    if (streams.Count == 0)
                        _incoming.Remove(conn);
                }
            }

            Debug.Log($"[NetworkStreamManager] Swept {stale.Count} stale fragment stream(s).");
        }

        // ---- Helpers ----

        private static void WriteInt(byte[] buf, ref int offset, int value) {
            buf[offset++] = (byte)value;
            buf[offset++] = (byte)(value >> 8);
            buf[offset++] = (byte)(value >> 16);
            buf[offset++] = (byte)(value >> 24);
        }

        private static int ReadInt(byte[] buf, ref int offset) {
            int value = buf[offset]
                        | (buf[offset + 1] << 8)
                        | (buf[offset + 2] << 16)
                        | (buf[offset + 3] << 24);
            offset += 4;
            return value;
        }
    }
}
