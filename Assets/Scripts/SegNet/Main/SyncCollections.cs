using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace SegNet {

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class CapacityAttribute : Attribute {
        public int Value { get; }

        public CapacityAttribute(int value) {
            Value = value;
        }
    }

    /// <summary>
    /// Base type for weaver-managed sync collections. Public __SegNet* members are
    /// framework hooks emitted by the IL weaver; game code should use SyncArray,
    /// SyncList, SyncDict, and SyncHashSet directly.
    /// </summary>
    public abstract class SyncCollection {
        private NetworkBehaviour _owner;
        private Action _changed;

        public int Capacity { get; private set; }

        public void __SegNetInitialize(NetworkBehaviour owner, int capacity, Action changed) {
            _owner = owner;
            _changed = changed;
            Capacity = capacity;
            ResizeForCapacity(capacity);
        }

        public abstract bool __SegNetIsDirty { get; }
        public abstract void __SegNetMarkFullDirty();
        public abstract void __SegNetClearDirty();
        public abstract void __SegNetSerializeFull(NetworkWriter writer);
        public abstract void __SegNetSerializeDelta(NetworkWriter writer);
        public abstract void __SegNetDeserializeFull(NetworkReader reader);
        public abstract void __SegNetDeserializeDelta(NetworkReader reader);

        protected abstract void ResizeForCapacity(int capacity);

        protected void MarkLocalChanged() {
            _owner?.SetDirty();
            _changed?.Invoke();
        }

        protected void MarkRemoteChanged() {
            _changed?.Invoke();
        }

        protected bool HasCapacityLimit => Capacity > 0;

        protected bool CanAddWithinCapacity(int currentCount) =>
            !HasCapacityLimit || currentCount < Capacity;

        protected void LogCapacityExceeded(string collectionName) {
            Debug.LogError(
                $"[{collectionName}] Capacity {Capacity} exceeded. " +
                "Increase [Capacity(n)] on the SyncVar field.");
        }

        protected void WarnCapacityExceeded(string collectionName) {
            Debug.LogWarning(
                $"[{collectionName}] Capacity {Capacity} exceeded. " +
                "Increase [Capacity(n)] on the SyncVar field.");
        }
    }

    public sealed class SyncArray<T> : SyncCollection, IEnumerable<T> {
        private enum OpCode : byte {
            Set = 1,
            Clear = 2,
        }

        private struct Operation {
            public OpCode Op;
            public int Index;
            public T Value;
        }

        private T[] _items = Array.Empty<T>();
        private readonly List<Operation> _ops = new List<Operation>();
        private bool _fullDirty;

        public int Length => _items.Length;

        public T this[int index] {
            get => _items[index];
            set => Set(index, value);
        }

        public void Set(int index, T value) {
            if ((uint)index >= (uint)_items.Length)
                throw new IndexOutOfRangeException(
                    $"SyncArray index {index} outside Length {_items.Length}.");

            if (EqualityComparer<T>.Default.Equals(_items[index], value))
                return;

            _items[index] = value;
            _ops.Add(new Operation {
                Op = OpCode.Set,
                Index = index,
                Value = value,
            });
            MarkLocalChanged();
        }

        public void Clear() {
            if (_items.Length == 0)
                return;

            Array.Clear(_items, 0, _items.Length);
            _ops.Add(new Operation { Op = OpCode.Clear });
            MarkLocalChanged();
        }

        public IEnumerator<T> GetEnumerator() {
            for (int i = 0; i < _items.Length; i++)
                yield return _items[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override bool __SegNetIsDirty => _fullDirty || _ops.Count > 0;

        public override void __SegNetMarkFullDirty() {
            _fullDirty = true;
            _ops.Clear();
        }

        public override void __SegNetClearDirty() {
            _fullDirty = false;
            _ops.Clear();
        }

        public override void __SegNetSerializeFull(NetworkWriter writer) {
            writer.WriteInt(_items.Length);
            for (int i = 0; i < _items.Length; i++)
                NetworkSerializer<T>.Write(writer, _items[i]);
        }

        public override void __SegNetSerializeDelta(NetworkWriter writer) {
            writer.WriteBool(_fullDirty);
            if (_fullDirty) {
                __SegNetSerializeFull(writer);
                return;
            }

            writer.WriteInt(_ops.Count);
            for (int i = 0; i < _ops.Count; i++) {
                Operation op = _ops[i];
                writer.WriteByte((byte)op.Op);
                if (op.Op == OpCode.Set) {
                    writer.WriteInt(op.Index);
                    NetworkSerializer<T>.Write(writer, op.Value);
                }
            }
        }

        public override void __SegNetDeserializeFull(NetworkReader reader) {
            int count = reader.ReadInt();

            if (count != _items.Length) {
                Debug.LogWarning(
                    $"[SyncArray] Incoming length {count} differs from local capacity {_items.Length}. " +
                    "Extra values will be discarded and missing values become default.");
            }

            int usable = Math.Min(Math.Max(count, 0), _items.Length);
            for (int i = 0; i < count; i++) {
                T value = NetworkSerializer<T>.Read(reader);
                if (i >= usable)
                    continue;
                if (!EqualityComparer<T>.Default.Equals(_items[i], value)) {
                    _items[i] = value;
                }
            }

            for (int i = usable; i < _items.Length; i++) {
                if (!EqualityComparer<T>.Default.Equals(_items[i], default)) {
                    _items[i] = default;
                }
            }

            MarkRemoteChanged();
        }

        public override void __SegNetDeserializeDelta(NetworkReader reader) {
            bool full = reader.ReadBool();
            if (full) {
                __SegNetDeserializeFull(reader);
                return;
            }

            int opCount = reader.ReadInt();
            bool changed = false;

            for (int i = 0; i < opCount; i++) {
                var op = (OpCode)reader.ReadByte();
                switch (op) {
                    case OpCode.Set: {
                        int index = reader.ReadInt();
                        T value = NetworkSerializer<T>.Read(reader);
                        if ((uint)index >= (uint)_items.Length) {
                            Debug.LogWarning(
                                $"[SyncArray] Ignoring set for index {index}; Length is {_items.Length}.");
                            break;
                        }
                        if (!EqualityComparer<T>.Default.Equals(_items[index], value)) {
                            _items[index] = value;
                            changed = true;
                        }
                        break;
                    }
                    case OpCode.Clear:
                        Array.Clear(_items, 0, _items.Length);
                        changed = true;
                        break;
                    default:
                        Debug.LogWarning($"[SyncArray] Unknown delta op {op}.");
                        break;
                }
            }

            if (changed)
                MarkRemoteChanged();
        }

        protected override void ResizeForCapacity(int capacity) {
            if (capacity < 0)
                capacity = 0;
            if (_items.Length == capacity)
                return;

            Array.Resize(ref _items, capacity);
        }
    }

    public sealed class SyncList<T> : SyncCollection, IEnumerable<T> {
        private enum OpCode : byte {
            Set = 1,
            Add = 2,
            Insert = 3,
            RemoveAt = 4,
            Clear = 5,
        }

        private struct Operation {
            public OpCode Op;
            public int Index;
            public T Value;
        }

        private readonly List<T> _items = new List<T>();
        private readonly List<Operation> _ops = new List<Operation>();
        private bool _fullDirty;

        public int Count => _items.Count;

        public T this[int index] {
            get => _items[index];
            set => Set(index, value);
        }

        public void Set(int index, T value) {
            if ((uint)index >= (uint)_items.Count)
                throw new IndexOutOfRangeException(
                    $"SyncList index {index} outside Count {_items.Count}.");

            if (EqualityComparer<T>.Default.Equals(_items[index], value))
                return;

            _items[index] = value;
            _ops.Add(new Operation {
                Op = OpCode.Set,
                Index = index,
                Value = value,
            });
            MarkLocalChanged();
        }

        public bool Add(T value) {
            if (!CanAddWithinCapacity(_items.Count)) {
                WarnCapacityExceeded("SyncList");
                return false;
            }

            _items.Add(value);
            _ops.Add(new Operation {
                Op = OpCode.Add,
                Value = value,
            });
            MarkLocalChanged();
            return true;
        }

        public bool Insert(int index, T value) {
            if ((uint)index > (uint)_items.Count)
                throw new ArgumentOutOfRangeException(nameof(index),
                    $"SyncList insert index {index} outside Count {_items.Count}.");
            if (!CanAddWithinCapacity(_items.Count)) {
                WarnCapacityExceeded("SyncList");
                return false;
            }

            _items.Insert(index, value);
            _ops.Add(new Operation {
                Op = OpCode.Insert,
                Index = index,
                Value = value,
            });
            MarkLocalChanged();
            return true;
        }

        public bool Remove(T value) {
            int index = _items.IndexOf(value);
            if (index < 0)
                return false;

            RemoveAt(index);
            return true;
        }

        public void RemoveAt(int index) {
            if ((uint)index >= (uint)_items.Count)
                throw new ArgumentOutOfRangeException(nameof(index),
                    $"SyncList remove index {index} outside Count {_items.Count}.");

            _items.RemoveAt(index);
            _ops.Add(new Operation {
                Op = OpCode.RemoveAt,
                Index = index,
            });
            MarkLocalChanged();
        }

        public bool Contains(T value) => _items.Contains(value);
        public int IndexOf(T value) => _items.IndexOf(value);

        public void Clear() {
            if (_items.Count == 0)
                return;

            _items.Clear();
            _ops.Add(new Operation { Op = OpCode.Clear });
            MarkLocalChanged();
        }

        public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override bool __SegNetIsDirty => _fullDirty || _ops.Count > 0;

        public override void __SegNetMarkFullDirty() {
            _fullDirty = true;
            _ops.Clear();
        }

        public override void __SegNetClearDirty() {
            _fullDirty = false;
            _ops.Clear();
        }

        public override void __SegNetSerializeFull(NetworkWriter writer) {
            writer.WriteInt(_items.Count);
            for (int i = 0; i < _items.Count; i++)
                NetworkSerializer<T>.Write(writer, _items[i]);
        }

        public override void __SegNetSerializeDelta(NetworkWriter writer) {
            writer.WriteBool(_fullDirty);
            if (_fullDirty) {
                __SegNetSerializeFull(writer);
                return;
            }

            writer.WriteInt(_ops.Count);
            for (int i = 0; i < _ops.Count; i++) {
                Operation op = _ops[i];
                writer.WriteByte((byte)op.Op);
                switch (op.Op) {
                    case OpCode.Set:
                    case OpCode.Insert:
                        writer.WriteInt(op.Index);
                        NetworkSerializer<T>.Write(writer, op.Value);
                        break;
                    case OpCode.Add:
                        NetworkSerializer<T>.Write(writer, op.Value);
                        break;
                    case OpCode.RemoveAt:
                        writer.WriteInt(op.Index);
                        break;
                }
            }
        }

        public override void __SegNetDeserializeFull(NetworkReader reader) {
            int count = reader.ReadInt();
            _items.Clear();

            for (int i = 0; i < count; i++) {
                T value = NetworkSerializer<T>.Read(reader);
                if (HasCapacityLimit && _items.Count >= Capacity) {
                    WarnCapacityExceeded("SyncList");
                    continue;
                }
                _items.Add(value);
            }

            MarkRemoteChanged();
        }

        public override void __SegNetDeserializeDelta(NetworkReader reader) {
            bool full = reader.ReadBool();
            if (full) {
                __SegNetDeserializeFull(reader);
                return;
            }

            int opCount = reader.ReadInt();
            bool changed = false;

            for (int i = 0; i < opCount; i++) {
                var op = (OpCode)reader.ReadByte();
                switch (op) {
                    case OpCode.Set: {
                        int index = reader.ReadInt();
                        T value = NetworkSerializer<T>.Read(reader);
                        if ((uint)index >= (uint)_items.Count) {
                            Debug.LogWarning(
                                $"[SyncList] Ignoring set for index {index}; Count is {_items.Count}.");
                            break;
                        }
                        if (!EqualityComparer<T>.Default.Equals(_items[index], value)) {
                            _items[index] = value;
                            changed = true;
                        }
                        break;
                    }
                    case OpCode.Add: {
                        T value = NetworkSerializer<T>.Read(reader);
                        if (!CanAddWithinCapacity(_items.Count)) {
                            WarnCapacityExceeded("SyncList");
                            break;
                        }
                        _items.Add(value);
                        changed = true;
                        break;
                    }
                    case OpCode.Insert: {
                        int index = reader.ReadInt();
                        T value = NetworkSerializer<T>.Read(reader);
                        if ((uint)index > (uint)_items.Count) {
                            Debug.LogWarning(
                                $"[SyncList] Ignoring insert for index {index}; Count is {_items.Count}.");
                            break;
                        }
                        if (!CanAddWithinCapacity(_items.Count)) {
                            WarnCapacityExceeded("SyncList");
                            break;
                        }
                        _items.Insert(index, value);
                        changed = true;
                        break;
                    }
                    case OpCode.RemoveAt: {
                        int index = reader.ReadInt();
                        if ((uint)index >= (uint)_items.Count) {
                            Debug.LogWarning(
                                $"[SyncList] Ignoring remove for index {index}; Count is {_items.Count}.");
                            break;
                        }
                        _items.RemoveAt(index);
                        changed = true;
                        break;
                    }
                    case OpCode.Clear:
                        changed |= _items.Count > 0;
                        _items.Clear();
                        break;
                    default:
                        Debug.LogWarning($"[SyncList] Unknown delta op {op}.");
                        break;
                }
            }

            if (changed)
                MarkRemoteChanged();
        }

        protected override void ResizeForCapacity(int capacity) {
            if (capacity > 0 && _items.Capacity < capacity)
                _items.Capacity = capacity;
            if (capacity <= 0 || _items.Count <= capacity)
                return;

            _items.RemoveRange(capacity, _items.Count - capacity);
            WarnCapacityExceeded("SyncList");
        }
    }

    public sealed class SyncDict<TKey, TValue> : SyncCollection, IEnumerable<KeyValuePair<TKey, TValue>> {
        private enum OpCode : byte {
            Set = 1,
            Remove = 2,
            Clear = 3,
        }

        private struct Operation {
            public OpCode Op;
            public TKey Key;
            public TValue Value;
        }

        private readonly Dictionary<TKey, TValue> _items = new Dictionary<TKey, TValue>();
        private readonly List<Operation> _ops = new List<Operation>();
        private bool _fullDirty;

        public int Count => _items.Count;
        public IEnumerable<TKey> Keys => _items.Keys;
        public IEnumerable<TValue> Values => _items.Values;

        public TValue this[TKey key] {
            get => _items[key];
            set => Set(key, value);
        }

        public TValue Get(TKey key) => _items[key];
        public bool HasKey(TKey key) => _items.ContainsKey(key);
        public bool ContainsKey(TKey key) => _items.ContainsKey(key);
        public bool TryGetValue(TKey key, out TValue value) => _items.TryGetValue(key, out value);

        public void Set(TKey key, TValue value) {
            bool exists = _items.TryGetValue(key, out var current);
            if (!exists && !CanAddWithinCapacity(_items.Count)) {
                LogCapacityExceeded("SyncDict");
                return;
            }

            if (exists && EqualityComparer<TValue>.Default.Equals(current, value))
                return;

            _items[key] = value;
            _ops.Add(new Operation {
                Op = OpCode.Set,
                Key = key,
                Value = value,
            });
            MarkLocalChanged();
        }

        public bool Add(TKey key, TValue value) {
            if (_items.ContainsKey(key))
                return false;

            Set(key, value);
            return _items.ContainsKey(key);
        }

        public bool Remove(TKey key) {
            if (!_items.Remove(key))
                return false;

            _ops.Add(new Operation {
                Op = OpCode.Remove,
                Key = key,
            });
            MarkLocalChanged();
            return true;
        }

        public void Clear() {
            if (_items.Count == 0)
                return;

            _items.Clear();
            _ops.Add(new Operation { Op = OpCode.Clear });
            MarkLocalChanged();
        }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override bool __SegNetIsDirty => _fullDirty || _ops.Count > 0;

        public override void __SegNetMarkFullDirty() {
            _fullDirty = true;
            _ops.Clear();
        }

        public override void __SegNetClearDirty() {
            _fullDirty = false;
            _ops.Clear();
        }

        public override void __SegNetSerializeFull(NetworkWriter writer) {
            writer.WriteInt(_items.Count);
            foreach (var kvp in _items) {
                NetworkSerializer<TKey>.Write(writer, kvp.Key);
                NetworkSerializer<TValue>.Write(writer, kvp.Value);
            }
        }

        public override void __SegNetSerializeDelta(NetworkWriter writer) {
            writer.WriteBool(_fullDirty);
            if (_fullDirty) {
                __SegNetSerializeFull(writer);
                return;
            }

            writer.WriteInt(_ops.Count);
            for (int i = 0; i < _ops.Count; i++) {
                Operation op = _ops[i];
                writer.WriteByte((byte)op.Op);
                switch (op.Op) {
                    case OpCode.Set:
                        NetworkSerializer<TKey>.Write(writer, op.Key);
                        NetworkSerializer<TValue>.Write(writer, op.Value);
                        break;
                    case OpCode.Remove:
                        NetworkSerializer<TKey>.Write(writer, op.Key);
                        break;
                }
            }
        }

        public override void __SegNetDeserializeFull(NetworkReader reader) {
            int count = reader.ReadInt();
            _items.Clear();

            for (int i = 0; i < count; i++) {
                TKey key = NetworkSerializer<TKey>.Read(reader);
                TValue value = NetworkSerializer<TValue>.Read(reader);
                if (HasCapacityLimit && _items.Count >= Capacity) {
                    LogCapacityExceeded("SyncDict");
                    continue;
                }
                _items[key] = value;
            }

            MarkRemoteChanged();
        }

        public override void __SegNetDeserializeDelta(NetworkReader reader) {
            bool full = reader.ReadBool();
            if (full) {
                __SegNetDeserializeFull(reader);
                return;
            }

            int opCount = reader.ReadInt();
            bool changed = false;

            for (int i = 0; i < opCount; i++) {
                var op = (OpCode)reader.ReadByte();
                switch (op) {
                    case OpCode.Set: {
                        TKey key = NetworkSerializer<TKey>.Read(reader);
                        TValue value = NetworkSerializer<TValue>.Read(reader);
                        bool exists = _items.ContainsKey(key);
                        if (!exists && !CanAddWithinCapacity(_items.Count)) {
                            LogCapacityExceeded("SyncDict");
                            break;
                        }
                        _items[key] = value;
                        changed = true;
                        break;
                    }
                    case OpCode.Remove: {
                        TKey key = NetworkSerializer<TKey>.Read(reader);
                        changed |= _items.Remove(key);
                        break;
                    }
                    case OpCode.Clear:
                        changed |= _items.Count > 0;
                        _items.Clear();
                        break;
                    default:
                        Debug.LogWarning($"[SyncDict] Unknown delta op {op}.");
                        break;
                }
            }

            if (changed)
                MarkRemoteChanged();
        }

        protected override void ResizeForCapacity(int capacity) {
            if (capacity <= 0 || _items.Count <= capacity)
                return;

            int removeCount = _items.Count - capacity;
            var keysToRemove = new List<TKey>(removeCount);
            foreach (TKey key in _items.Keys) {
                keysToRemove.Add(key);
                if (keysToRemove.Count == removeCount)
                    break;
            }

            for (int i = 0; i < keysToRemove.Count; i++)
                _items.Remove(keysToRemove[i]);

            LogCapacityExceeded("SyncDict");
        }
    }

    public sealed class SyncHashSet<T> : SyncCollection, IEnumerable<T> {
        private enum OpCode : byte {
            Add = 1,
            Remove = 2,
            Clear = 3,
        }

        private struct Operation {
            public OpCode Op;
            public T Value;
        }

        private readonly HashSet<T> _items = new HashSet<T>();
        private readonly List<Operation> _ops = new List<Operation>();
        private bool _fullDirty;

        public int Count => _items.Count;

        public bool Add(T value) {
            if (_items.Contains(value))
                return false;

            if (!CanAddWithinCapacity(_items.Count)) {
                LogCapacityExceeded("SyncHashSet");
                return false;
            }

            _items.Add(value);
            _ops.Add(new Operation {
                Op = OpCode.Add,
                Value = value,
            });
            MarkLocalChanged();
            return true;
        }

        public bool Remove(T value) {
            if (!_items.Remove(value))
                return false;

            _ops.Add(new Operation {
                Op = OpCode.Remove,
                Value = value,
            });
            MarkLocalChanged();
            return true;
        }

        public bool Contains(T value) => _items.Contains(value);
        public bool Has(T value) => _items.Contains(value);

        public void Clear() {
            if (_items.Count == 0)
                return;

            _items.Clear();
            _ops.Add(new Operation { Op = OpCode.Clear });
            MarkLocalChanged();
        }

        public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override bool __SegNetIsDirty => _fullDirty || _ops.Count > 0;

        public override void __SegNetMarkFullDirty() {
            _fullDirty = true;
            _ops.Clear();
        }

        public override void __SegNetClearDirty() {
            _fullDirty = false;
            _ops.Clear();
        }

        public override void __SegNetSerializeFull(NetworkWriter writer) {
            writer.WriteInt(_items.Count);
            foreach (T value in _items)
                NetworkSerializer<T>.Write(writer, value);
        }

        public override void __SegNetSerializeDelta(NetworkWriter writer) {
            writer.WriteBool(_fullDirty);
            if (_fullDirty) {
                __SegNetSerializeFull(writer);
                return;
            }

            writer.WriteInt(_ops.Count);
            for (int i = 0; i < _ops.Count; i++) {
                Operation op = _ops[i];
                writer.WriteByte((byte)op.Op);
                if (op.Op != OpCode.Clear)
                    NetworkSerializer<T>.Write(writer, op.Value);
            }
        }

        public override void __SegNetDeserializeFull(NetworkReader reader) {
            int count = reader.ReadInt();
            _items.Clear();

            for (int i = 0; i < count; i++) {
                T value = NetworkSerializer<T>.Read(reader);
                if (HasCapacityLimit && _items.Count >= Capacity) {
                    LogCapacityExceeded("SyncHashSet");
                    continue;
                }
                _items.Add(value);
            }

            MarkRemoteChanged();
        }

        public override void __SegNetDeserializeDelta(NetworkReader reader) {
            bool full = reader.ReadBool();
            if (full) {
                __SegNetDeserializeFull(reader);
                return;
            }

            int opCount = reader.ReadInt();
            bool changed = false;

            for (int i = 0; i < opCount; i++) {
                var op = (OpCode)reader.ReadByte();
                switch (op) {
                    case OpCode.Add: {
                        T value = NetworkSerializer<T>.Read(reader);
                        bool exists = _items.Contains(value);
                        if (!exists && !CanAddWithinCapacity(_items.Count)) {
                            LogCapacityExceeded("SyncHashSet");
                            break;
                        }
                        changed |= _items.Add(value);
                        break;
                    }
                    case OpCode.Remove: {
                        T value = NetworkSerializer<T>.Read(reader);
                        changed |= _items.Remove(value);
                        break;
                    }
                    case OpCode.Clear:
                        changed |= _items.Count > 0;
                        _items.Clear();
                        break;
                    default:
                        Debug.LogWarning($"[SyncHashSet] Unknown delta op {op}.");
                        break;
                }
            }

            if (changed)
                MarkRemoteChanged();
        }

        protected override void ResizeForCapacity(int capacity) {
            if (capacity <= 0 || _items.Count <= capacity)
                return;

            int removeCount = _items.Count - capacity;
            var itemsToRemove = new List<T>(removeCount);
            foreach (T value in _items) {
                itemsToRemove.Add(value);
                if (itemsToRemove.Count == removeCount)
                    break;
            }

            for (int i = 0; i < itemsToRemove.Count; i++)
                _items.Remove(itemsToRemove[i]);

            LogCapacityExceeded("SyncHashSet");
        }
    }

    internal static class NetworkSerializer<T> {
        public static readonly Action<NetworkWriter, T> Write = CreateWriter();
        public static readonly Func<NetworkReader, T> Read = CreateReader();

        private static Action<NetworkWriter, T> CreateWriter() {
            Type type = typeof(T);

            if (type == typeof(bool)) return (writer, value) => writer.WriteBool((bool)(object)value);
            if (type == typeof(byte)) return (writer, value) => writer.WriteByte((byte)(object)value);
            if (type == typeof(sbyte)) return (writer, value) => writer.WriteSByte((sbyte)(object)value);
            if (type == typeof(short)) return (writer, value) => writer.WriteShort((short)(object)value);
            if (type == typeof(ushort)) return (writer, value) => writer.WriteUShort((ushort)(object)value);
            if (type == typeof(int)) return (writer, value) => writer.WriteInt((int)(object)value);
            if (type == typeof(uint)) return (writer, value) => writer.WriteUInt((uint)(object)value);
            if (type == typeof(long)) return (writer, value) => writer.WriteLong((long)(object)value);
            if (type == typeof(ulong)) return (writer, value) => writer.WriteULong((ulong)(object)value);
            if (type == typeof(float)) return (writer, value) => writer.WriteFloat((float)(object)value);
            if (type == typeof(double)) return (writer, value) => writer.WriteDouble((double)(object)value);
            if (type == typeof(string)) return (writer, value) => writer.WriteString((string)(object)value);

            if (type == typeof(Vector2)) return (writer, value) => writer.WriteVector2((Vector2)(object)value);
            if (type == typeof(Vector3)) return (writer, value) => writer.WriteVector3((Vector3)(object)value);
            if (type == typeof(Vector4)) return (writer, value) => writer.WriteVector4((Vector4)(object)value);
            if (type == typeof(Quaternion)) return (writer, value) => writer.WriteQuaternion((Quaternion)(object)value);
            if (type == typeof(Color)) return (writer, value) => writer.WriteColor((Color)(object)value);
            if (type == typeof(Color32)) return (writer, value) => writer.WriteColor32((Color32)(object)value);
            if (type == typeof(Vector2Int)) return (writer, value) => writer.WriteVector2Int((Vector2Int)(object)value);
            if (type == typeof(Vector3Int)) return (writer, value) => writer.WriteVector3Int((Vector3Int)(object)value);

            if (typeof(NetworkBehaviour).IsAssignableFrom(type))
                return (writer, value) => writer.WriteNetworkBehaviour((NetworkBehaviour)(object)value);

            if (type == typeof(NetworkPlayer))
                return (writer, value) => writer.WriteNetworkPlayer((NetworkPlayer)(object)value);

            if (type.IsEnum)
                return (writer, value) => writer.WriteLong(Convert.ToInt64(value));

            throw new NotSupportedException($"Sync collection element type '{type.FullName}' is not supported.");
        }

        private static Func<NetworkReader, T> CreateReader() {
            Type type = typeof(T);

            if (type == typeof(bool)) return reader => (T)(object)reader.ReadBool();
            if (type == typeof(byte)) return reader => (T)(object)reader.ReadByte();
            if (type == typeof(sbyte)) return reader => (T)(object)reader.ReadSByte();
            if (type == typeof(short)) return reader => (T)(object)reader.ReadShort();
            if (type == typeof(ushort)) return reader => (T)(object)reader.ReadUShort();
            if (type == typeof(int)) return reader => (T)(object)reader.ReadInt();
            if (type == typeof(uint)) return reader => (T)(object)reader.ReadUInt();
            if (type == typeof(long)) return reader => (T)(object)reader.ReadLong();
            if (type == typeof(ulong)) return reader => (T)(object)reader.ReadULong();
            if (type == typeof(float)) return reader => (T)(object)reader.ReadFloat();
            if (type == typeof(double)) return reader => (T)(object)reader.ReadDouble();
            if (type == typeof(string)) return reader => (T)(object)reader.ReadString();

            if (type == typeof(Vector2)) return reader => (T)(object)reader.ReadVector2();
            if (type == typeof(Vector3)) return reader => (T)(object)reader.ReadVector3();
            if (type == typeof(Vector4)) return reader => (T)(object)reader.ReadVector4();
            if (type == typeof(Quaternion)) return reader => (T)(object)reader.ReadQuaternion();
            if (type == typeof(Color)) return reader => (T)(object)reader.ReadColor();
            if (type == typeof(Color32)) return reader => (T)(object)reader.ReadColor32();
            if (type == typeof(Vector2Int)) return reader => (T)(object)reader.ReadVector2Int();
            if (type == typeof(Vector3Int)) return reader => (T)(object)reader.ReadVector3Int();

            if (typeof(NetworkBehaviour).IsAssignableFrom(type))
                return reader => (T)(object)reader.ReadNetworkBehaviour();

            if (type == typeof(NetworkPlayer))
                return reader => (T)(object)reader.ReadNetworkPlayer();

            if (type.IsEnum)
                return reader => (T)Enum.ToObject(type, reader.ReadLong());

            throw new NotSupportedException($"Sync collection element type '{type.FullName}' is not supported.");
        }
    }
}
