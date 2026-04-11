using System;

namespace SegNet {

    /// <summary>
    /// Marks a field on a NetworkBehaviour for automatic server→client synchronization.
    ///
    /// The IL weaver will eventually:
    ///   1. Replace the field with a backing field + property.
    ///   2. Inject SetDirty() calls into the setter.
    ///   3. Generate serialization/deserialization code.
    ///
    /// Supported types (current and planned):
    ///   - Primitives: bool, byte, sbyte, short, ushort, int, uint, long, ulong, float, double
    ///   - String
    ///   - Unity types: Vector2/3/4, Quaternion, Color, Color32
    ///   - Enums
    ///   - NetworkBehaviour references (serialized by NetworkId)
    ///   - NetworkPlayer references (serialized by PlayerId)
    ///
    /// Usage:
    ///   [SyncVar] int health = 100;
    ///   [SyncVar(hook = nameof(OnHealthChanged))] int health = 100;
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public class SyncVarAttribute : Attribute {

        /// <summary>
        /// Optional name of a method to call when the value changes. Fires on every peer
        /// that observes the change — server (and host) from the setter, remote clients
        /// from OnDeserialize. Accepts either signature:
        ///   - void MethodName(T oldValue, T newValue)
        ///   - void MethodName()
        /// If both forms are present on the same class, the (old, new) form wins.
        /// </summary>
        public string hook;
    }
}
