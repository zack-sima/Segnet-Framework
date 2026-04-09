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
        [SerializeField] private NetworkConnectionManager connectionManager;

        private const byte PrefixWhole = 0x00;
        private const byte PrefixFragment = 0x01;

        // Fragment header: 1 prefix + 2 streamId + 4 totalLen + 4 offset + 1 isLast = 12 bytes
        private const int FragmentHeaderSize = 1 + 2 + 4 + 4 + 1;

        private ushort _nextStreamId = 1;

        /// <summary>
        /// Fires when a complete message has been reassembled (or arrived whole).
        /// Parameters: (sender ConnectionId, complete payload bytes).
        /// </summary>
        public event Action<ConnectionId, byte[]> OnMessageReceived;

        // Per-connection, per-stream reassembly state
        private readonly Dictionary<ConnectionId, Dictionary<ushort, IncomingStream>> _incoming =
            new Dictionary<ConnectionId, Dictionary<ushort, IncomingStream>>();

        private class IncomingStream {
            public int TotalLength;
            public int Received;
            public byte[] Buffer;
        }

        // ---- Unity lifecycle ----

        private void Awake() {
            if (connectionManager == null) {
                Debug.LogError("[NetworkStreamManager] No NetworkConnectionManager assigned.");
                enabled = false;
                return;
            }
            connectionManager.OnData += HandleTransportData;
            connectionManager.OnClientDisconnected += HandleDisconnect;
        }

        private void OnDestroy() {
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
            } else {
                SendFragmented(target, data, channel);
            }
        }

        /// <summary>Broadcast a complete message to all connections.</summary>
        public void Broadcast(byte[] data, ChannelType channel) {
            foreach (var conn in connectionManager.Connections)
                Send(conn, data, channel);
        }

        /// <summary>Broadcast to all connections except one.</summary>
        public void BroadcastExcept(ConnectionId exclude, byte[] data, ChannelType channel) {
            foreach (var conn in connectionManager.Connections) {
                if (conn != exclude)
                    Send(conn, data, channel);
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
            ushort streamId = _nextStreamId++;
            if (_nextStreamId == 0) _nextStreamId = 1;

            int maxPacket = connectionManager.MaxPacketSize;
            int maxChunk = Mathf.Max(1, maxPacket - FragmentHeaderSize);
            int total = data.Length;
            int offset = 0;

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

                connectionManager.SendTo(target, new ArraySegment<byte>(packet), channel);
                offset += chunkSize;
            }
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

            if (!_incoming.TryGetValue(from, out var streams)) {
                streams = new Dictionary<ushort, IncomingStream>();
                _incoming[from] = streams;
            }

            if (!streams.TryGetValue(streamId, out var stream)) {
                stream = new IncomingStream {
                    TotalLength = totalLength,
                    Received = 0,
                    Buffer = new byte[totalLength]
                };
                streams[streamId] = stream;
            }

            // Bounds check
            if (chunkOffset < 0 || chunkOffset + chunkLen > stream.TotalLength) return;

            Buffer.BlockCopy(arr, p, stream.Buffer, chunkOffset, chunkLen);
            stream.Received += chunkLen;

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
