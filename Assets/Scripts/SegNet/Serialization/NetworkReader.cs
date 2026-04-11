using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace SegNet {

    public class NetworkReader {
        private readonly byte[] _buffer;
        private readonly int _offset;
        private readonly int _length;
        private int _position;

        public int Position => _position - _offset;
        public int Remaining => (_offset + _length) - _position;

        public NetworkReader(byte[] data) {
            _buffer = data ?? throw new ArgumentNullException(nameof(data));
            _offset = 0;
            _length = data.Length;
            _position = 0;
        }

        public NetworkReader(byte[] data, int offset, int length) {
            _buffer = data ?? throw new ArgumentNullException(nameof(data));
            _offset = offset;
            _length = length;
            _position = offset;
        }

        public NetworkReader(ArraySegment<byte> segment) {
            _buffer = segment.Array ?? throw new ArgumentNullException(nameof(segment));
            _offset = segment.Offset;
            _length = segment.Count;
            _position = segment.Offset;
        }

        private void CheckRemaining(int bytes) {
            if (_position + bytes > _offset + _length)
                throw new IndexOutOfRangeException(
                    $"NetworkReader: read overrun (need {bytes}, have {Remaining})");
        }

        // ---- Primitives ----

        public byte ReadByte() {
            CheckRemaining(1);
            return _buffer[_position++];
        }

        public sbyte ReadSByte() => (sbyte)ReadByte();

        public bool ReadBool() => ReadByte() != 0;

        public ushort ReadUShort() {
            CheckRemaining(2);
            ushort value = (ushort)(_buffer[_position] | (_buffer[_position + 1] << 8));
            _position += 2;
            return value;
        }

        public short ReadShort() => (short)ReadUShort();

        public uint ReadUInt() {
            CheckRemaining(4);
            uint value = (uint)(
                _buffer[_position]
                | (_buffer[_position + 1] << 8)
                | (_buffer[_position + 2] << 16)
                | (_buffer[_position + 3] << 24));
            _position += 4;
            return value;
        }

        public int ReadInt() => (int)ReadUInt();

        public ulong ReadULong() {
            CheckRemaining(8);
            ulong value = 0;
            for (int i = 0; i < 8; i++)
                value |= (ulong)_buffer[_position + i] << (i * 8);
            _position += 8;
            return value;
        }

        public long ReadLong() => (long)ReadULong();

        public float ReadFloat() {
            FloatUnion u = default;
            u.intValue = ReadUInt();
            return u.floatValue;
        }

        public double ReadDouble() {
            DoubleUnion u = default;
            u.longValue = ReadULong();
            return u.doubleValue;
        }

        // ---- Strings ----

        /// <summary>
        /// Reads a length-prefixed UTF-8 string.
        /// 0 = null, otherwise reads (stored - 1) bytes of UTF-8.
        /// </summary>
        public string ReadString() {
            ushort encoded = ReadUShort();
            if (encoded == 0) return null;

            int byteCount = encoded - 1;
            if (byteCount == 0) return string.Empty;

            CheckRemaining(byteCount);
            string value = Encoding.UTF8.GetString(_buffer, _position, byteCount);
            _position += byteCount;
            return value;
        }

        // ---- Unity types ----

        public Vector2 ReadVector2() => new Vector2(ReadFloat(), ReadFloat());

        public Vector3 ReadVector3() => new Vector3(ReadFloat(), ReadFloat(), ReadFloat());

        public Vector4 ReadVector4() => new Vector4(ReadFloat(), ReadFloat(), ReadFloat(), ReadFloat());

        public Quaternion ReadQuaternion() => new Quaternion(ReadFloat(), ReadFloat(), ReadFloat(), ReadFloat());

        public Color ReadColor() => new Color(ReadFloat(), ReadFloat(), ReadFloat(), ReadFloat());

        public Color32 ReadColor32() => new Color32(ReadByte(), ReadByte(), ReadByte(), ReadByte());

        public Vector2Int ReadVector2Int() => new Vector2Int(ReadInt(), ReadInt());

        public Vector3Int ReadVector3Int() => new Vector3Int(ReadInt(), ReadInt(), ReadInt());

        // ---- NetworkBehaviour / NetworkPlayer references ----
        //
        // Reads a stable ID off the wire and resolves it to a live instance via
        // NetworkManager/ServerManager. Returns null if the id is the sentinel (0 / -1) or if
        // the target object isn't currently known on this peer (e.g. RPC arrived
        // before the spawn message). Callers may need to handle null.

        /// <summary>
        /// Reads a NetworkBehaviour reference (uint NetworkId).
        /// Returns null for id 0 or if the object isn't currently spawned on this peer.
        /// To get a typed reference, the IL weaver emits a castclass after this call.
        /// </summary>
        public NetworkBehaviour ReadNetworkBehaviour() {
            uint nid = ReadUInt();
            if (nid == 0u) return null;
            var nm = NetworkManager.Instance;
            if (nm != null) return nm.GetNetworkObject(nid);
            var sm = ServerManager.Instance;
            if (sm == null) return null;
            return sm.GetNetworkObject(nid);
        }

        /// <summary>
        /// Reads a NetworkPlayer reference (int PlayerId).
        /// Returns null for id -1 or if the player isn't currently known on this peer.
        /// </summary>
        public NetworkPlayer ReadNetworkPlayer() {
            int pid = ReadInt();
            if (pid < 0) return null;
            var sm = ServerManager.Instance;
            if (sm == null) return null;
            return sm.GetPlayer(pid);
        }

        // ---- Raw bytes ----

        /// <summary>Reads a length-prefixed byte array. -1 length = null.</summary>
        public byte[] ReadByteArray() {
            int length = ReadInt();
            if (length < 0) return null;
            if (length == 0) return Array.Empty<byte>();

            CheckRemaining(length);
            byte[] result = new byte[length];
            Buffer.BlockCopy(_buffer, _position, result, 0, length);
            _position += length;
            return result;
        }

        /// <summary>Reads raw bytes with no length prefix into the destination.</summary>
        public void ReadRawBytes(byte[] dest, int destOffset, int count) {
            CheckRemaining(count);
            Buffer.BlockCopy(_buffer, _position, dest, destOffset, count);
            _position += count;
        }

        /// <summary>Reads raw bytes with no length prefix, returning a new array.</summary>
        public byte[] ReadRawBytes(int count) {
            byte[] result = new byte[count];
            ReadRawBytes(result, 0, count);
            return result;
        }

        // ---- Enums ----

        /// <summary>Reads an enum stored as int.</summary>
        public T ReadEnum<T>() where T : Enum {
            return (T)(object)ReadInt();
        }

        // ---- Collections (delegate-based; IL weaver will generate direct calls) ----

        public T[] ReadArray<T>(Func<NetworkReader, T> readElement) {
            int count = ReadInt();
            if (count < 0) return null;

            T[] array = new T[count];
            for (int i = 0; i < count; i++)
                array[i] = readElement(this);
            return array;
        }

        public List<T> ReadList<T>(Func<NetworkReader, T> readElement) {
            int count = ReadInt();
            if (count < 0) return null;

            var list = new List<T>(count);
            for (int i = 0; i < count; i++)
                list.Add(readElement(this));
            return list;
        }

        public HashSet<T> ReadHashSet<T>(Func<NetworkReader, T> readElement) {
            int count = ReadInt();
            if (count < 0) return null;

            var set = new HashSet<T>();
            for (int i = 0; i < count; i++)
                set.Add(readElement(this));
            return set;
        }

        public Dictionary<TKey, TValue> ReadDictionary<TKey, TValue>(
            Func<NetworkReader, TKey> readKey,
            Func<NetworkReader, TValue> readValue
        ) {
            int count = ReadInt();
            if (count < 0) return null;

            var dict = new Dictionary<TKey, TValue>(count);
            for (int i = 0; i < count; i++) {
                TKey key = readKey(this);
                TValue val = readValue(this);
                dict[key] = val;
            }
            return dict;
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
