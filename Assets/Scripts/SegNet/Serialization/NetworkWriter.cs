using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace SegNet {

    public class NetworkWriter {
        private byte[] _buffer;
        private int _position;

        private const int DefaultCapacity = 256;
        private const int MaxPoolSize = 32;
        private static readonly Stack<NetworkWriter> _pool = new Stack<NetworkWriter>();

        public int Position => _position;
        public int Length => _position;

        public NetworkWriter() {
            _buffer = new byte[DefaultCapacity];
        }

        public NetworkWriter(int capacity) {
            _buffer = new byte[capacity];
        }

        // ---- Pooling ----

        /// <summary>Get a writer from the pool (or create one). Call Return() when done.</summary>
        public static NetworkWriter Get() {
            if (_pool.Count > 0) {
                var w = _pool.Pop();
                w._position = 0;
                return w;
            }
            return new NetworkWriter();
        }

        /// <summary>Return a writer to the pool for reuse.</summary>
        public static void Return(NetworkWriter writer) {
            if (writer == null) return;
            writer._position = 0;
            if (_pool.Count < MaxPoolSize)
                _pool.Push(writer);
        }

        // ---- Output ----

        public ArraySegment<byte> ToArraySegment() => new ArraySegment<byte>(_buffer, 0, _position);

        public byte[] ToArray() {
            byte[] result = new byte[_position];
            Buffer.BlockCopy(_buffer, 0, result, 0, _position);
            return result;
        }

        public void Reset() {
            _position = 0;
        }

        // ---- Capacity ----

        private void EnsureCapacity(int additional) {
            int required = _position + additional;
            if (required <= _buffer.Length) return;
            int newSize = Math.Max(_buffer.Length * 2, required);
            Array.Resize(ref _buffer, newSize);
        }

        // ---- Primitives ----

        public void WriteByte(byte value) {
            EnsureCapacity(1);
            _buffer[_position++] = value;
        }

        public void WriteSByte(sbyte value) => WriteByte((byte)value);

        public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

        public void WriteUShort(ushort value) {
            EnsureCapacity(2);
            _buffer[_position++] = (byte)value;
            _buffer[_position++] = (byte)(value >> 8);
        }

        public void WriteShort(short value) => WriteUShort((ushort)value);

        public void WriteUInt(uint value) {
            EnsureCapacity(4);
            _buffer[_position++] = (byte)value;
            _buffer[_position++] = (byte)(value >> 8);
            _buffer[_position++] = (byte)(value >> 16);
            _buffer[_position++] = (byte)(value >> 24);
        }

        public void WriteInt(int value) => WriteUInt((uint)value);

        public void WriteULong(ulong value) {
            EnsureCapacity(8);
            _buffer[_position++] = (byte)value;
            _buffer[_position++] = (byte)(value >> 8);
            _buffer[_position++] = (byte)(value >> 16);
            _buffer[_position++] = (byte)(value >> 24);
            _buffer[_position++] = (byte)(value >> 32);
            _buffer[_position++] = (byte)(value >> 40);
            _buffer[_position++] = (byte)(value >> 48);
            _buffer[_position++] = (byte)(value >> 56);
        }

        public void WriteLong(long value) => WriteULong((ulong)value);

        public void WriteFloat(float value) {
            FloatUnion u = default;
            u.floatValue = value;
            WriteUInt(u.intValue);
        }

        public void WriteDouble(double value) {
            DoubleUnion u = default;
            u.doubleValue = value;
            WriteULong(u.longValue);
        }

        // ---- Strings ----

        /// <summary>
        /// Writes a string as length-prefixed UTF-8.
        /// Encoding: 0 = null, otherwise (byteCount + 1) followed by UTF-8 bytes.
        /// Max string byte length: 65534.
        /// </summary>
        public void WriteString(string value) {
            if (value == null) {
                WriteUShort(0);
                return;
            }

            int byteCount = Encoding.UTF8.GetByteCount(value);
            if (byteCount > ushort.MaxValue - 1)
                throw new ArgumentException($"String too long for network serialization ({byteCount} bytes)");

            WriteUShort((ushort)(byteCount + 1));
            EnsureCapacity(byteCount);
            Encoding.UTF8.GetBytes(value, 0, value.Length, _buffer, _position);
            _position += byteCount;
        }

        // ---- Unity types ----

        public void WriteVector2(Vector2 v) {
            WriteFloat(v.x);
            WriteFloat(v.y);
        }

        public void WriteVector3(Vector3 v) {
            WriteFloat(v.x);
            WriteFloat(v.y);
            WriteFloat(v.z);
        }

        public void WriteVector4(Vector4 v) {
            WriteFloat(v.x);
            WriteFloat(v.y);
            WriteFloat(v.z);
            WriteFloat(v.w);
        }

        public void WriteQuaternion(Quaternion q) {
            WriteFloat(q.x);
            WriteFloat(q.y);
            WriteFloat(q.z);
            WriteFloat(q.w);
        }

        public void WriteColor(Color c) {
            WriteFloat(c.r);
            WriteFloat(c.g);
            WriteFloat(c.b);
            WriteFloat(c.a);
        }

        public void WriteColor32(Color32 c) {
            WriteByte(c.r);
            WriteByte(c.g);
            WriteByte(c.b);
            WriteByte(c.a);
        }

        public void WriteVector2Int(Vector2Int v) {
            WriteInt(v.x);
            WriteInt(v.y);
        }

        public void WriteVector3Int(Vector3Int v) {
            WriteInt(v.x);
            WriteInt(v.y);
            WriteInt(v.z);
        }

        // ---- NetworkBehaviour / NetworkPlayer references ----
        //
        // Reference types are serialized by stable ID, not value. The reader resolves
        // the ID back to an instance via NetworkManager/ServerManager. Null and unspawned objects
        // (or unknown players) round-trip as null.

        /// <summary>
        /// Writes a NetworkBehaviour reference as its NetworkId.
        /// Null or not-yet-spawned behaviours serialize as 0.
        /// </summary>
        public void WriteNetworkBehaviour(NetworkBehaviour behaviour) {
            if (behaviour == null || !behaviour.IsSpawned)
                WriteUInt(0u);
            else
                WriteUInt(behaviour.NetworkId);
        }

        /// <summary>
        /// Writes a NetworkPlayer reference as its PlayerId.
        /// Null serializes as -1.
        /// </summary>
        public void WriteNetworkPlayer(NetworkPlayer player) {
            if (player == null)
                WriteInt(-1);
            else
                WriteInt(player.PlayerId);
        }

        // ---- Raw bytes ----

        /// <summary>Writes length-prefixed byte array. Null is written as length -1.</summary>
        public void WriteByteArray(byte[] data) {
            if (data == null) {
                WriteInt(-1);
                return;
            }
            WriteInt(data.Length);
            if (data.Length > 0) {
                EnsureCapacity(data.Length);
                Buffer.BlockCopy(data, 0, _buffer, _position, data.Length);
                _position += data.Length;
            }
        }

        /// <summary>Writes raw bytes with no length prefix.</summary>
        public void WriteRawBytes(byte[] data, int offset, int count) {
            if (count <= 0) return;
            EnsureCapacity(count);
            Buffer.BlockCopy(data, offset, _buffer, _position, count);
            _position += count;
        }

        /// <summary>Writes raw bytes from an ArraySegment with no length prefix.</summary>
        public void WriteRawBytes(ArraySegment<byte> segment) {
            WriteRawBytes(segment.Array, segment.Offset, segment.Count);
        }

        // ---- Enums ----

        /// <summary>Writes an enum as its underlying int value.</summary>
        public void WriteEnum<T>(T value) where T : Enum {
            WriteInt(Convert.ToInt32(value));
        }

        // ---- Collections (delegate-based for flexibility; IL weaver will generate direct calls) ----

        public void WriteArray<T>(T[] array, Action<NetworkWriter, T> writeElement) {
            if (array == null) {
                WriteInt(-1);
                return;
            }
            WriteInt(array.Length);
            for (int i = 0; i < array.Length; i++)
                writeElement(this, array[i]);
        }

        public void WriteList<T>(List<T> list, Action<NetworkWriter, T> writeElement) {
            if (list == null) {
                WriteInt(-1);
                return;
            }
            WriteInt(list.Count);
            for (int i = 0; i < list.Count; i++)
                writeElement(this, list[i]);
        }

        public void WriteHashSet<T>(HashSet<T> set, Action<NetworkWriter, T> writeElement) {
            if (set == null) {
                WriteInt(-1);
                return;
            }
            WriteInt(set.Count);
            foreach (var item in set)
                writeElement(this, item);
        }

        public void WriteDictionary<TKey, TValue>(
            Dictionary<TKey, TValue> dict,
            Action<NetworkWriter, TKey> writeKey,
            Action<NetworkWriter, TValue> writeValue
        ) {
            if (dict == null) {
                WriteInt(-1);
                return;
            }
            WriteInt(dict.Count);
            foreach (var kvp in dict) {
                writeKey(this, kvp.Key);
                writeValue(this, kvp.Value);
            }
        }

        // ---- Float/double union helpers (avoids unsafe) ----

        [StructLayout(LayoutKind.Explicit)]
        private struct FloatUnion {
            [FieldOffset(0)] public float floatValue;
            [FieldOffset(0)] public uint intValue;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct DoubleUnion {
            [FieldOffset(0)] public double doubleValue;
            [FieldOffset(0)] public ulong longValue;
        }
    }
}
