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

        // Resolved once up-front so every AddInstanceMethod call can walk the Methods
        // collection directly. Resolving via Cecil's assembly resolver (instead of
        // reflection) avoids having the reflection importer graft
        // System.Private.CoreLib into the target assembly's reference table whenever
        // a writer parameter is a primitive type.
        private readonly TypeDefinition _writerDef;
        private readonly TypeDefinition _readerDef;

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

            // Import the declaring types (type-only imports are safe — they don't touch
            // primitive types). Then resolve each to a TypeDefinition so we can walk its
            // Methods collection without falling through Cecil's reflection importer.
            var writerRef = _module.ImportReference(typeof(NetworkWriter));
            var readerRef = _module.ImportReference(typeof(NetworkReader));
            _writerDef = writerRef.Resolve()
                ?? throw new InvalidOperationException(
                    "[SerializerMap] Could not resolve SegNet.NetworkWriter via Cecil. " +
                    "Is SegNet.Runtime.dll in the ILPP search path?");
            _readerDef = readerRef.Resolve()
                ?? throw new InvalidOperationException(
                    "[SerializerMap] Could not resolve SegNet.NetworkReader via Cecil. " +
                    "Is SegNet.Runtime.dll in the ILPP search path?");

            // Primitives — pass the exact FullName Cecil uses for the parameter type.
            AddInstanceMethod<bool>("System.Boolean",
                nameof(NetworkWriter.WriteBool), nameof(NetworkReader.ReadBool));
            AddInstanceMethod<byte>("System.Byte",
                nameof(NetworkWriter.WriteByte), nameof(NetworkReader.ReadByte));
            AddInstanceMethod<sbyte>("System.SByte",
                nameof(NetworkWriter.WriteSByte), nameof(NetworkReader.ReadSByte));
            AddInstanceMethod<short>("System.Int16",
                nameof(NetworkWriter.WriteShort), nameof(NetworkReader.ReadShort));
            AddInstanceMethod<ushort>("System.UInt16",
                nameof(NetworkWriter.WriteUShort), nameof(NetworkReader.ReadUShort));
            AddInstanceMethod<int>("System.Int32",
                nameof(NetworkWriter.WriteInt), nameof(NetworkReader.ReadInt));
            AddInstanceMethod<uint>("System.UInt32",
                nameof(NetworkWriter.WriteUInt), nameof(NetworkReader.ReadUInt));
            AddInstanceMethod<long>("System.Int64",
                nameof(NetworkWriter.WriteLong), nameof(NetworkReader.ReadLong));
            AddInstanceMethod<ulong>("System.UInt64",
                nameof(NetworkWriter.WriteULong), nameof(NetworkReader.ReadULong));
            AddInstanceMethod<float>("System.Single",
                nameof(NetworkWriter.WriteFloat), nameof(NetworkReader.ReadFloat));
            AddInstanceMethod<double>("System.Double",
                nameof(NetworkWriter.WriteDouble), nameof(NetworkReader.ReadDouble));
            AddInstanceMethod<string>("System.String",
                nameof(NetworkWriter.WriteString), nameof(NetworkReader.ReadString));

            // Unity value types
            AddInstanceMethod<Vector2>("UnityEngine.Vector2",
                nameof(NetworkWriter.WriteVector2), nameof(NetworkReader.ReadVector2));
            AddInstanceMethod<Vector3>("UnityEngine.Vector3",
                nameof(NetworkWriter.WriteVector3), nameof(NetworkReader.ReadVector3));
            AddInstanceMethod<Vector4>("UnityEngine.Vector4",
                nameof(NetworkWriter.WriteVector4), nameof(NetworkReader.ReadVector4));
            AddInstanceMethod<Quaternion>("UnityEngine.Quaternion",
                nameof(NetworkWriter.WriteQuaternion), nameof(NetworkReader.ReadQuaternion));
            AddInstanceMethod<Color>("UnityEngine.Color",
                nameof(NetworkWriter.WriteColor), nameof(NetworkReader.ReadColor));
            AddInstanceMethod<Color32>("UnityEngine.Color32",
                nameof(NetworkWriter.WriteColor32), nameof(NetworkReader.ReadColor32));
            AddInstanceMethod<Vector2Int>("UnityEngine.Vector2Int",
                nameof(NetworkWriter.WriteVector2Int), nameof(NetworkReader.ReadVector2Int));
            AddInstanceMethod<Vector3Int>("UnityEngine.Vector3Int",
                nameof(NetworkWriter.WriteVector3Int), nameof(NetworkReader.ReadVector3Int));

            // Reference types (serialized by stable ID)
            AddInstanceMethod<NetworkBehaviour>(NetworkBehaviourFullName,
                nameof(NetworkWriter.WriteNetworkBehaviour),
                nameof(NetworkReader.ReadNetworkBehaviour));
            AddInstanceMethod<NetworkPlayer>(NetworkPlayerFullName,
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
        /// Find <c>NetworkWriter.{writeName}({paramTypeFullName})</c> and
        /// <c>NetworkReader.{readName}()</c> on the resolved TypeDefinitions, then
        /// import the MethodDefinitions into the target module. Using
        /// MethodDefinition-based import keeps Cecil on the metadata importer path
        /// rather than the reflection importer, so primitive parameter types stay
        /// scoped to the target's corlib instead of getting grafted onto the host
        /// runtime's System.Private.CoreLib.
        /// </summary>
        private void AddInstanceMethod<T>(string paramTypeFullName, string writeName, string readName) {
            var writeDef = FindMethod(_writerDef, writeName, paramTypeFullName);
            if (writeDef == null) {
                throw new InvalidOperationException(
                    $"[SerializerMap] NetworkWriter.{writeName}({paramTypeFullName}) not found. " +
                    "Did the runtime API change?");
            }

            var readDef = FindMethod(_readerDef, readName /* no params */);
            if (readDef == null) {
                throw new InvalidOperationException(
                    $"[SerializerMap] NetworkReader.{readName}() not found. " +
                    "Did the runtime API change?");
            }

            string key = typeof(T).FullName;
            _writers[key] = _module.ImportReference(writeDef);
            _readers[key] = _module.ImportReference(readDef);
        }

        /// <summary>
        /// Walk a TypeDefinition's Methods collection to find one matching
        /// <paramref name="name"/> with exactly the given parameter type FullNames.
        /// Returns null on no match.
        /// </summary>
        private static MethodDefinition FindMethod(
            TypeDefinition type, string name, params string[] paramTypeFullNames) {

            foreach (var m in type.Methods) {
                if (m.Name != name) continue;
                if (m.Parameters.Count != paramTypeFullNames.Length) continue;

                bool allMatch = true;
                for (int i = 0; i < paramTypeFullNames.Length; i++) {
                    if (m.Parameters[i].ParameterType.FullName != paramTypeFullNames[i]) {
                        allMatch = false;
                        break;
                    }
                }
                if (allMatch) return m;
            }
            return null;
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
