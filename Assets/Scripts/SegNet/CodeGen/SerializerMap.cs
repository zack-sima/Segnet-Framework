using System;
using System.Collections.Generic;
using Mono.Cecil;
using SegNet;
using UnityEngine;

namespace SegNet.CodeGen {

    /// <summary>
    /// Type → (NetworkWriter.Write*, NetworkReader.Read*) lookup table used by the IL
    /// weaver. One instance is built per assembly being processed; all returned
    /// MethodReferences are pre-imported into that assembly's main module so the
    /// weaver can emit them directly without additional import calls.
    ///
    /// Supported types:
    ///   - Primitives:  bool, byte, sbyte, short, ushort, int, uint, long, ulong, float, double, string
    ///   - Unity value: Vector2, Vector3, Vector4, Quaternion, Color, Color32, Vector2Int, Vector3Int
    ///   - Enums:       resolved to their underlying primitive (int, byte, etc.)
    ///   - References:  NetworkBehaviour and any subclass; NetworkPlayer
    ///
    /// For NetworkBehaviour subclasses, TryGet returns the methods for the base
    /// NetworkBehaviour helper. The caller must check <see cref="RequiresReadCast"/>
    /// and emit a castclass to the actual subclass after the read call.
    ///
    /// Anything not in the table = compile error (the weaver should report a
    /// DiagnosticType.Error and refuse to weave that method/field).
    /// </summary>
    public class SerializerMap {

        private readonly ModuleDefinition _module;

        // Keyed by TypeReference.FullName (matches typeof(T).FullName for our supported types).
        private readonly Dictionary<string, MethodReference> _writers =
            new Dictionary<string, MethodReference>(StringComparer.Ordinal);
        private readonly Dictionary<string, MethodReference> _readers =
            new Dictionary<string, MethodReference>(StringComparer.Ordinal);

        // Cached for the NetworkBehaviour-subclass fast path in TryGet.
        private readonly MethodReference _writeNetworkBehaviour;
        private readonly MethodReference _readNetworkBehaviour;

        // Full names we recognize as the "base ref" types (used by IsNetworkBehaviourSubclass etc).
        private const string NetworkBehaviourFullName = "SegNet.NetworkBehaviour";
        private const string NetworkPlayerFullName = "SegNet.NetworkPlayer";

        public SerializerMap(ModuleDefinition targetModule) {
            _module = targetModule ?? throw new ArgumentNullException(nameof(targetModule));

            // Primitives
            AddInstanceMethod<bool>(nameof(NetworkWriter.WriteBool), nameof(NetworkReader.ReadBool));
            AddInstanceMethod<byte>(nameof(NetworkWriter.WriteByte), nameof(NetworkReader.ReadByte));
            AddInstanceMethod<sbyte>(nameof(NetworkWriter.WriteSByte), nameof(NetworkReader.ReadSByte));
            AddInstanceMethod<short>(nameof(NetworkWriter.WriteShort), nameof(NetworkReader.ReadShort));
            AddInstanceMethod<ushort>(nameof(NetworkWriter.WriteUShort), nameof(NetworkReader.ReadUShort));
            AddInstanceMethod<int>(nameof(NetworkWriter.WriteInt), nameof(NetworkReader.ReadInt));
            AddInstanceMethod<uint>(nameof(NetworkWriter.WriteUInt), nameof(NetworkReader.ReadUInt));
            AddInstanceMethod<long>(nameof(NetworkWriter.WriteLong), nameof(NetworkReader.ReadLong));
            AddInstanceMethod<ulong>(nameof(NetworkWriter.WriteULong), nameof(NetworkReader.ReadULong));
            AddInstanceMethod<float>(nameof(NetworkWriter.WriteFloat), nameof(NetworkReader.ReadFloat));
            AddInstanceMethod<double>(nameof(NetworkWriter.WriteDouble), nameof(NetworkReader.ReadDouble));
            AddInstanceMethod<string>(nameof(NetworkWriter.WriteString), nameof(NetworkReader.ReadString));

            // Unity value types
            AddInstanceMethod<Vector2>(nameof(NetworkWriter.WriteVector2), nameof(NetworkReader.ReadVector2));
            AddInstanceMethod<Vector3>(nameof(NetworkWriter.WriteVector3), nameof(NetworkReader.ReadVector3));
            AddInstanceMethod<Vector4>(nameof(NetworkWriter.WriteVector4), nameof(NetworkReader.ReadVector4));
            AddInstanceMethod<Quaternion>(nameof(NetworkWriter.WriteQuaternion), nameof(NetworkReader.ReadQuaternion));
            AddInstanceMethod<Color>(nameof(NetworkWriter.WriteColor), nameof(NetworkReader.ReadColor));
            AddInstanceMethod<Color32>(nameof(NetworkWriter.WriteColor32), nameof(NetworkReader.ReadColor32));
            AddInstanceMethod<Vector2Int>(nameof(NetworkWriter.WriteVector2Int), nameof(NetworkReader.ReadVector2Int));
            AddInstanceMethod<Vector3Int>(nameof(NetworkWriter.WriteVector3Int), nameof(NetworkReader.ReadVector3Int));

            // Reference types (serialized by stable ID)
            AddInstanceMethod<NetworkBehaviour>(
                nameof(NetworkWriter.WriteNetworkBehaviour),
                nameof(NetworkReader.ReadNetworkBehaviour));
            AddInstanceMethod<NetworkPlayer>(
                nameof(NetworkWriter.WriteNetworkPlayer),
                nameof(NetworkReader.ReadNetworkPlayer));

            // Cache the NetworkBehaviour entries for the subclass fast path.
            _writeNetworkBehaviour = _writers[NetworkBehaviourFullName];
            _readNetworkBehaviour = _readers[NetworkBehaviourFullName];
        }

        // ----------------------------------------------------------------
        //  Public API
        // ----------------------------------------------------------------

        /// <summary>
        /// Look up the writer/reader pair for a Cecil type. Handles enums (recurses on
        /// underlying type) and NetworkBehaviour subclasses (returns the base helpers).
        /// Returns false if the type is unsupported — caller should emit a compile error.
        /// </summary>
        public bool TryGet(TypeReference type, out MethodReference writeMethod, out MethodReference readMethod) {
            writeMethod = null;
            readMethod = null;

            if (type == null) return false;

            // Direct lookup by full name first (covers all primitives, Unity types,
            // exact NetworkBehaviour, exact NetworkPlayer).
            if (_writers.TryGetValue(type.FullName, out writeMethod)
                && _readers.TryGetValue(type.FullName, out readMethod)) {
                return true;
            }

            // Need TypeDefinition for the more involved checks below.
            TypeDefinition resolved = SafeResolve(type);
            if (resolved == null) return false;

            // Enums → underlying primitive
            if (resolved.IsEnum) {
                TypeReference underlying = GetEnumUnderlyingType(resolved);
                if (underlying == null) return false;
                return TryGet(underlying, out writeMethod, out readMethod);
            }

            // NetworkBehaviour subclass → use the base helper (caller emits castclass after read)
            if (IsNetworkBehaviourSubclass(resolved)) {
                writeMethod = _writeNetworkBehaviour;
                readMethod = _readNetworkBehaviour;
                return true;
            }

            return false;
        }

        /// <summary>
        /// True iff the read method for this type returns a base type that the weaver
        /// must downcast to the actual declared type. Currently only NetworkBehaviour
        /// subclasses (other than NetworkBehaviour itself) require this.
        /// </summary>
        public bool RequiresReadCast(TypeReference type) {
            if (type == null) return false;
            if (type.FullName == NetworkBehaviourFullName) return false;

            TypeDefinition resolved = SafeResolve(type);
            if (resolved == null) return false;
            return IsNetworkBehaviourSubclass(resolved);
        }

        /// <summary>
        /// Whether this exact type (not a subclass) is one of the directly supported
        /// primitive/Unity/reference types. Useful for diagnostics and validation passes.
        /// </summary>
        public bool IsDirectlySupported(TypeReference type) {
            return type != null && _writers.ContainsKey(type.FullName);
        }

        /// <summary>Enumerable of the directly supported type full names. For diagnostics.</summary>
        public IEnumerable<string> SupportedTypeNames => _writers.Keys;

        // ----------------------------------------------------------------
        //  Population helper
        // ----------------------------------------------------------------

        /// <summary>
        /// Resolve <c>NetworkWriter.{writeName}({T})</c> and <c>NetworkReader.{readName}()</c>
        /// via reflection, import them into the target module, and store under T's full name.
        /// </summary>
        private void AddInstanceMethod<T>(string writeName, string readName) {
            Type t = typeof(T);

            var writeMethod = typeof(NetworkWriter).GetMethod(writeName, new[] { t });
            if (writeMethod == null) {
                throw new InvalidOperationException(
                    $"[SerializerMap] NetworkWriter.{writeName}({t.Name}) not found via reflection. " +
                    "Did the runtime API change?");
            }

            var readMethod = typeof(NetworkReader).GetMethod(readName, Type.EmptyTypes);
            if (readMethod == null) {
                throw new InvalidOperationException(
                    $"[SerializerMap] NetworkReader.{readName}() not found via reflection. " +
                    "Did the runtime API change?");
            }

            _writers[t.FullName] = _module.ImportReference(writeMethod);
            _readers[t.FullName] = _module.ImportReference(readMethod);
        }

        // ----------------------------------------------------------------
        //  Cecil helpers
        // ----------------------------------------------------------------

        private static TypeDefinition SafeResolve(TypeReference type) {
            try {
                return type.Resolve();
            } catch {
                return null;
            }
        }

        private static TypeReference GetEnumUnderlyingType(TypeDefinition enumType) {
            // Enums in IL store their underlying value in a single non-static field
            // named "value__". Find it without depending on Cecil extension methods
            // that may not be present in this assembly resolution context.
            foreach (var field in enumType.Fields) {
                if (!field.IsStatic)
                    return field.FieldType;
            }
            return null;
        }

        private static bool IsNetworkBehaviourSubclass(TypeDefinition type) {
            TypeDefinition current = type;
            while (current != null) {
                if (current.FullName == NetworkBehaviourFullName)
                    return true;
                if (current.BaseType == null)
                    return false;
                current = SafeResolve(current.BaseType);
            }
            return false;
        }
    }
}
