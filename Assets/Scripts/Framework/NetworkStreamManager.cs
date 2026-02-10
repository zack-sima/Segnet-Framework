using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;
using UnityEngine.Events;
using NetCore;

public class NetworkStreamManager : MonoBehaviour {
    [SerializeField] private NetworkConnectionManager connectionManager;

    // Message type tags inside your own protocol
    private enum MessageType : byte {
        StreamFragment = 1,
        RpcFragment = 2
    }

    // Shell for user-defined stream payloads
    [Serializable]
    public class NetStreamPayload {

        [Serializable]
        public struct BuildingDiff {
            public uint c;   // coordinates
            public ulong d;  // building data
        }

        // High-throughput data (chunk diffs, etc.)
        public BuildingDiff[] diffs;

        // Optional string for additional info
        public string data;
    }

    // RPC payload
    [Serializable]
    public class RPCPayload {
        public int messageType; //convert to enum -- message decrypted using Newtonsoft Json
        public string message;
    }

    // UnityEvents so you can wire handlers in the inspector
    [Serializable]
    public class NetStreamPayloadEvent : UnityEvent<NetStreamPayload> { }

    [Serializable]
    public class RPCPayloadEvent : UnityEvent<RPCPayload> { }

    [Header("Callbacks")]
    [SerializeField] private NetStreamPayloadEvent onStreamReceivedObject;
    [SerializeField] private RPCPayloadEvent onRpcReceivedObject;

    private class IncomingStream {
        public int TotalLength;
        public int Received;
        public byte[] Buffer;
    }

    // Per-connection, per-stream state
    private readonly Dictionary<ConnectionId, Dictionary<ushort, IncomingStream>> _incoming =
        new Dictionary<ConnectionId, Dictionary<ushort, IncomingStream>>();

    private ushort _nextStreamId = 1;

    // Optional: raw-bytes event if you still want it
    public event Action<ConnectionId, byte[]> OnStreamReceivedBytes;

    // ---------- Unity lifecycle ----------

    private void Awake() {
        if (connectionManager == null) {
            Debug.LogError("[NetworkStreamManager] No NetworkConnectionManager assigned.");
            enabled = false;
            return;
        }

        connectionManager.OnData += HandleTransportData;
    }

    private void OnDestroy() {
        if (connectionManager != null) {
            connectionManager.OnData -= HandleTransportData;
        }
    }

    // ---------- Public API: Streams (server -> client) ----------

    // Generic raw bytes streaming (server -> client)
    public void ServerSendStreamBytes(ConnectionId client, byte[] data) {
        if (!connectionManager.IsServer || data == null || data.Length == 0)
            return;

        ushort streamId = _nextStreamId++;
        if (_nextStreamId == 0) _nextStreamId = 1;

        // For now, assume SteamTransport is on same GameObject and use its MaxPacketSize
        int maxPacketSize = connectionManager.MaxPacketSize;

        // Header: 1 byte type + 2 bytes streamId + 4 bytes total + 4 bytes offset + 1 byte isLast = 12 bytes
        int headerSize = 1 + 2 + 4 + 4 + 1;
        int maxChunkSize = Mathf.Max(1, maxPacketSize - headerSize);

        int total = data.Length;
        int offset = 0;

        while (offset < total) {
            int chunkSize = Mathf.Min(maxChunkSize, total - offset);
            bool isLast = (offset + chunkSize) >= total;

            int packetSize = headerSize + chunkSize;
            byte[] packet = new byte[packetSize];

            int p = 0;
            packet[p++] = (byte)MessageType.StreamFragment;

            // streamId (ushort)
            packet[p++] = (byte)(streamId & 0xFF);
            packet[p++] = (byte)((streamId >> 8) & 0xFF);

            // total length (int, little endian)
            WriteInt(packet, ref p, total);

            // offset (int, little endian)
            WriteInt(packet, ref p, offset);

            // isLast (byte)
            packet[p++] = isLast ? (byte)1 : (byte)0;

            // payload
            Buffer.BlockCopy(data, offset, packet, p, chunkSize);

            connectionManager.SendTo(client, new ArraySegment<byte>(packet), ChannelType.Reliable);

            offset += chunkSize;
        }
    }

    // Compresses a NetStreamPayload and streams it
    public void ServerSendStreamObject(ConnectionId client, NetStreamPayload payload) {
        if (!connectionManager.IsServer || payload == null)
            return;

        // Custom binary serialization for NetStreamPayload
        byte[] raw = SerializeStreamPayload(payload);
        byte[] compressed = Compress(raw);

        ServerSendStreamBytes(client, compressed);
    }

    // ---------- Public API: RPC (server <-> client) ----------

    public void SendRpcTo(ConnectionId target, RPCPayload payload) {
        if (payload == null || connectionManager == null)
            return;

        // JSON is fine for small control messages
        byte[] raw = SerializeRpcPayload(payload);
        int len = raw.Length;

        // [MessageType.RpcFragment][int length][bytes...]
        int headerSize = 1 + 4;
        byte[] packet = new byte[headerSize + len];

        int p = 0;
        packet[p++] = (byte)MessageType.RpcFragment;
        WriteInt(packet, ref p, len);
        Buffer.BlockCopy(raw, 0, packet, p, len);

        connectionManager.SendTo(target, new ArraySegment<byte>(packet), ChannelType.Reliable);
    }

    public void BroadcastRpc(RPCPayload payload) {
        if (payload == null || connectionManager == null)
            return;

        byte[] raw = SerializeRpcPayload(payload);
        int len = raw.Length;

        int headerSize = 1 + 4;
        byte[] packet = new byte[headerSize + len];

        int p = 0;
        packet[p++] = (byte)MessageType.RpcFragment;
        WriteInt(packet, ref p, len);
        Buffer.BlockCopy(raw, 0, packet, p, len);

        foreach (var conn in connectionManager.Connections) {
            connectionManager.SendTo(conn, new ArraySegment<byte>(packet), ChannelType.Reliable);
        }
    }

    // ---------- Receive side ----------

    private void HandleTransportData(ConnectionId from, ArraySegment<byte> payload, ChannelType channel) {
        if (payload.Count <= 0)
            return;

        var array = payload.Array;
        int offset = payload.Offset;
        int count = payload.Count;

        byte type = array[offset];

        switch ((MessageType)type) {
            case MessageType.StreamFragment:
                HandleStreamFragment(from, array, offset + 1, count - 1);
                break;

            case MessageType.RpcFragment:
                HandleRpcFragment(from, array, offset + 1, count - 1);
                break;
        }
    }

    private void HandleStreamFragment(ConnectionId from, byte[] data, int offset, int length) {
        if (length < (2 + 4 + 4 + 1)) // streamId + total + offset + isLast
            return;

        int p = offset;

        // streamId
        ushort streamId = (ushort)(data[p] | (data[p + 1] << 8));
        p += 2;

        int totalLength = ReadInt(data, ref p);
        int chunkOffset = ReadInt(data, ref p);
        bool isLast = data[p++] != 0;

        int payloadLen = length - (p - offset);
        if (payloadLen <= 0)
            return;

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
        if (chunkOffset < 0 || chunkOffset + payloadLen > stream.TotalLength)
            return;

        Buffer.BlockCopy(data, p, stream.Buffer, chunkOffset, payloadLen);
        stream.Received += payloadLen;

        if (isLast || stream.Received >= stream.TotalLength) {
            byte[] complete = stream.Buffer;
            streams.Remove(streamId);

            try {
                // Decompress and deserialize into NetStreamPayload
                byte[] decompressed = Decompress(complete);
                var obj = DeserializeStreamPayload(decompressed);

                onStreamReceivedObject?.Invoke(obj);
                OnStreamReceivedBytes?.Invoke(from, complete);
            } catch (Exception ex) {
                Debug.LogError($"[NetworkStreamManager] Error deserializing stream from {from.Value}: {ex}");
            }
        }
    }

    private void HandleRpcFragment(ConnectionId from, byte[] data, int offset, int length) {
        if (length < 4) // need at least length int
            return;

        int p = offset;
        int msgLen = ReadInt(data, ref p);

        int remaining = length - (p - offset);
        if (msgLen < 0 || msgLen > remaining)
            return;

        // Extract just the RPC payload bytes
        byte[] payloadBytes = new byte[msgLen];
        Buffer.BlockCopy(data, p, payloadBytes, 0, msgLen);

        try {
            var rpc = DeserializeRpcPayload(payloadBytes);
            onRpcReceivedObject?.Invoke(rpc);
        } catch (Exception ex) {
            Debug.LogError($"[NetworkStreamManager] Error deserializing RPC from {from.Value}: {ex}");
        }
    }

    // ---------- Stream payload binary (de)serialization ----------

    // Versioning lets you evolve the payload format later.
    private const int StreamPayloadVersion = 1;

    private static byte[] SerializeStreamPayload(NetStreamPayload payload) {
        if (payload == null) return Array.Empty<byte>();

        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms)) {
            // Version
            bw.Write(StreamPayloadVersion);

            // diffs[]
            var diffs = payload.diffs;
            int diffCount = diffs != null ? diffs.Length : 0;
            bw.Write(diffCount);
            if (diffCount > 0) {
                for (int i = 0; i < diffCount; i++) {
                    bw.Write(diffs[i].c);
                    bw.Write(diffs[i].d);
                }
            }

            // Optional debug string 'data'
            bool hasData = !string.IsNullOrEmpty(payload.data);
            bw.Write(hasData);
            if (hasData) {
                byte[] strBytes = Encoding.UTF8.GetBytes(payload.data);
                bw.Write(strBytes.Length);
                bw.Write(strBytes);
            }

            bw.Flush();
            return ms.ToArray();
        }
    }

    private static NetStreamPayload DeserializeStreamPayload(byte[] raw) {
        var result = new NetStreamPayload();

        if (raw == null || raw.Length == 0)
            return result;

        using (var ms = new MemoryStream(raw))
        using (var br = new BinaryReader(ms)) {
            int version = br.ReadInt32();
            if (version != StreamPayloadVersion) {
                // In the future: handle older/newer versions here.
                // For now, assume same version.
            }

            // diffs[]
            int diffCount = br.ReadInt32();
            if (diffCount < 0) diffCount = 0;

            if (diffCount > 0) {
                result.diffs = new NetStreamPayload.BuildingDiff[diffCount];
                for (int i = 0; i < diffCount; i++) {
                    NetStreamPayload.BuildingDiff d;
                    d.c = br.ReadUInt32();
                    d.d = br.ReadUInt64();
                    result.diffs[i] = d;
                }
            } else {
                result.diffs = Array.Empty<NetStreamPayload.BuildingDiff>();
            }

            // Optional string 'data'
            bool hasData = br.ReadBoolean();
            if (hasData) {
                int len = br.ReadInt32();
                if (len > 0) {
                    byte[] strBytes = br.ReadBytes(len);
                    result.data = Encoding.UTF8.GetString(strBytes);
                } else {
                    result.data = string.Empty;
                }
            } else {
                result.data = null;
            }
        }

        return result;
    }

    // ---------- RPC (de)serialization (JSON, small control messages) ----------

    private static byte[] SerializeRpcPayload(RPCPayload payload) {
        if (payload == null) return Array.Empty<byte>();
        string json = JsonUtility.ToJson(payload);
        return Encoding.UTF8.GetBytes(json);
    }

    private static RPCPayload DeserializeRpcPayload(byte[] data) {
        if (data == null || data.Length == 0) return new RPCPayload();
        string json = Encoding.UTF8.GetString(data);
        return JsonUtility.FromJson<RPCPayload>(json);
    }

    // ---------- Compression helpers (zlib/Deflate) ----------

    private static byte[] Compress(byte[] raw) {
        if (raw == null || raw.Length == 0)
            return Array.Empty<byte>();

        using (var output = new MemoryStream()) {
            using (var deflate = new DeflateStream(output, System.IO.Compression.CompressionLevel.Fastest, true)) {
                deflate.Write(raw, 0, raw.Length);
            }
            return output.ToArray();
        }
    }

    private static byte[] Decompress(byte[] compressed) {
        if (compressed == null || compressed.Length == 0)
            return Array.Empty<byte>();

        using (var input = new MemoryStream(compressed))
        using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
        using (var output = new MemoryStream()) {
            deflate.CopyTo(output);
            return output.ToArray();
        }
    }

    // ---------- Helpers ----------

    private static void WriteInt(byte[] buffer, ref int offset, int value) {
        buffer[offset++] = (byte)(value & 0xFF);
        buffer[offset++] = (byte)((value >> 8) & 0xFF);
        buffer[offset++] = (byte)((value >> 16) & 0xFF);
        buffer[offset++] = (byte)((value >> 24) & 0xFF);
    }

    private static int ReadInt(byte[] buffer, ref int offset) {
        int value = buffer[offset]
                    | (buffer[offset + 1] << 8)
                    | (buffer[offset + 2] << 16)
                    | (buffer[offset + 3] << 24);
        offset += 4;
        return value;
    }
}
