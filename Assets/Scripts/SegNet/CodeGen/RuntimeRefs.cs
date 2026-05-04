using System;
using Mono.Cecil;
using SegNet;
using UnityEngine;

namespace SegNet.CodeGen {

    /// <summary>
    /// Cache of all SegNet runtime / framework / mscorlib references the weaver needs
    /// to emit IL into a target module. One instance is built per processed assembly.
    ///
    /// Everything here is pre-imported into the target module, so the weaver can drop
    /// these straight into IL operands without additional ImportReference calls.
    ///
    /// Implementation note — why we go through <see cref="TypeReference.Resolve"/> +
    /// Definition walks instead of <c>module.ImportReference(MethodInfo)</c>:
    /// Cecil's reflection importer scopes primitive parameter types (int, ushort,
    /// bool, ...) to the host runtime's corlib, which at ILPP runtime is
    /// <c>System.Private.CoreLib</c>. That assembly is NOT referenced by any
    /// Unity-compiled target, so every subsequent ILPP (JobsILPP, BurstILPP, the
    /// player-build UnityLinker) fails to resolve the phantom reference and the
    /// whole pipeline explodes. Resolving declaring types via Cecil's assembly
    /// resolver and importing <see cref="MethodDefinition"/>s keeps Cecil on the
    /// metadata importer path, which correctly remaps types through the target's
    /// existing corlib reference.
    /// </summary>
    internal sealed class RuntimeRefs {

        public ModuleDefinition Module { get; }

        // ---- SegNet runtime ----

        public TypeReference NetworkBehaviourType { get; }
        public TypeReference NetworkPlayerType { get; }
        public TypeReference NetworkWriterType { get; }
        public TypeReference NetworkReaderType { get; }
        public TypeReference SyncCollectionType { get; }
        public TypeReference RpcDirectionType { get; }
        public TypeReference ChannelTypeType { get; }

        public MethodReference NetworkWriterCtor { get; }       // NetworkWriter..ctor()
        public MethodReference NetworkWriterCtorCapacity { get; } // NetworkWriter..ctor(int)

        public MethodReference SendRpcInternal { get; }         // NetworkBehaviour.SendRpcInternal(uint, RpcDirection, ChannelType, NetworkWriter)
        public MethodReference SendRpcInternalTo { get; }       // NetworkBehaviour.SendRpcInternalTo(uint, ChannelType, NetworkWriter, NetworkPlayer)
        public MethodReference IsHostGetter { get; }
        public MethodReference IsClientGetter { get; }
        public MethodReference IsServerGetter { get; }
        public MethodReference OwnerPlayerGetter { get; }       // NetworkBehaviour.OwnerPlayer
        public MethodReference NetworkPlayerIsLocalGetter { get; } // NetworkPlayer.IsLocal
        public MethodReference SetDirtyMethod { get; }          // NetworkBehaviour.SetDirty()

        public MethodReference SyncCollectionInitialize { get; }      // SyncCollection.__SegNetInitialize(NetworkBehaviour, int, Action)
        public MethodReference SyncCollectionIsDirtyGetter { get; }   // SyncCollection.__SegNetIsDirty
        public MethodReference SyncCollectionMarkFullDirty { get; }   // SyncCollection.__SegNetMarkFullDirty()
        public MethodReference SyncCollectionClearDirty { get; }      // SyncCollection.__SegNetClearDirty()
        public MethodReference SyncCollectionSerializeFull { get; }   // SyncCollection.__SegNetSerializeFull(NetworkWriter)
        public MethodReference SyncCollectionSerializeDelta { get; }  // SyncCollection.__SegNetSerializeDelta(NetworkWriter)
        public MethodReference SyncCollectionDeserializeFull { get; } // SyncCollection.__SegNetDeserializeFull(NetworkReader)
        public MethodReference SyncCollectionDeserializeDelta { get; } // SyncCollection.__SegNetDeserializeDelta(NetworkReader)

        public MethodReference RpcRegistryRegister { get; }     // RpcRegistry.Register(uint, Action<NetworkBehaviour, NetworkReader>)

        // ---- mscorlib / netstandard ----

        public TypeReference VoidType { get; }
        public TypeReference ObjectType { get; }
        public TypeReference IntPtrType { get; }
        public TypeReference ActionType { get; }                 // System.Action
        public TypeReference Action2OpenType { get; }           // open generic System.Action`2
        public MethodReference ActionCtor { get; }               // Action..ctor(object, IntPtr)

        // The Action<NetworkBehaviour, NetworkReader> instance + its constructor.
        // Pre-built because every dispatch handler we register uses the same signature.
        public GenericInstanceType DispatchActionType { get; }
        public MethodReference DispatchActionCtor { get; }      // Action<NB, NR>..ctor(object, IntPtr)

        // ---- Unity ----

        public TypeReference RuntimeInitializeOnLoadAttrType { get; }
        public MethodReference RuntimeInitializeOnLoadAttrCtor { get; } // (RuntimeInitializeLoadType)
        public TypeReference RuntimeInitializeLoadTypeRef { get; }      // for the ctor arg

        // The integer value of RuntimeInitializeLoadType.SubsystemRegistration. Captured
        // here so we can emit `ldc.i4 <value>` without baking the magic number into the
        // weaver. Resolved via reflection at construction time.
        public int SubsystemRegistrationValue { get; }

        // ----------------------------------------------------------------
        //  Construction
        // ----------------------------------------------------------------

        public RuntimeRefs(ModuleDefinition module) {
            Module = module ?? throw new ArgumentNullException(nameof(module));

            // ---- mscorlib basics ----
            // Use the module's TypeSystem rather than reflection-based ImportReference,
            // because the host runtime executing this ILPP (System.Private.CoreLib) is
            // *not* the same corlib the target assembly references (mscorlib/netstandard).
            // Reflection imports would graft in a phantom System.Private.CoreLib assembly
            // reference that subsequent ILPPs (e.g. JobsILPP) cannot resolve.
            VoidType = module.TypeSystem.Void;
            ObjectType = module.TypeSystem.Object;
            IntPtrType = module.TypeSystem.IntPtr;

            // Whichever corlib (mscorlib / netstandard / System.Runtime) the target
            // already references — borrowed from System.Object's scope. Used below
            // when we hand-build references to System.Action`2.
            var corlibScope = ObjectType.Scope;

            // ---- SegNet types ----
            // Importing a TypeReference (no method signature) is safe: Cecil only adds
            // the containing assembly (SegNet.Runtime) to the reference list, which the
            // target already references. No primitive types are touched.
            NetworkBehaviourType = module.ImportReference(typeof(NetworkBehaviour));
            NetworkPlayerType = module.ImportReference(typeof(NetworkPlayer));
            NetworkWriterType = module.ImportReference(typeof(NetworkWriter));
            NetworkReaderType = module.ImportReference(typeof(NetworkReader));
            SyncCollectionType = module.ImportReference(typeof(SyncCollection));
            RpcDirectionType = module.ImportReference(typeof(RpcDirection));
            ChannelTypeType = module.ImportReference(typeof(ChannelType));

            // Resolve to TypeDefinitions so we can look up method members directly.
            // We deliberately go through the Cecil assembly resolver (not reflection)
            // for anything with a method signature that touches primitive types — the
            // reflection importer would scope those primitives to the host runtime's
            // System.Private.CoreLib and poison the output assembly's reference table.
            var nbDef = ResolveOrThrow(NetworkBehaviourType, "SegNet.NetworkBehaviour");
            var npDef = ResolveOrThrow(NetworkPlayerType, "SegNet.NetworkPlayer");
            var writerDef = ResolveOrThrow(NetworkWriterType, "SegNet.NetworkWriter");
            var syncCollectionDef = ResolveOrThrow(SyncCollectionType, "SegNet.SyncCollection");

            // ---- NetworkWriter constructors ----
            var writerCtor0 = FindMethod(writerDef, ".ctor")
                ?? throw new InvalidOperationException("NetworkWriter() ctor not found");
            var writerCtorN = FindMethod(writerDef, ".ctor", "System.Int32")
                ?? throw new InvalidOperationException("NetworkWriter(int) ctor not found");
            NetworkWriterCtor = module.ImportReference(writerCtor0);
            NetworkWriterCtorCapacity = module.ImportReference(writerCtorN);

            // ---- NetworkBehaviour entry points ----
            var sendRpcDef = FindMethod(nbDef, "SendRpcInternal",
                "System.UInt32", "SegNet.RpcDirection", "SegNet.ChannelType", "SegNet.NetworkWriter")
                ?? throw new InvalidOperationException(
                    "NetworkBehaviour.SendRpcInternal(uint, RpcDirection, ChannelType, NetworkWriter) not found");
            SendRpcInternal = module.ImportReference(sendRpcDef);

            var sendRpcToDef = FindMethod(nbDef, "SendRpcInternalTo",
                "System.UInt32", "SegNet.ChannelType", "SegNet.NetworkWriter", "SegNet.NetworkPlayer")
                ?? throw new InvalidOperationException(
                    "NetworkBehaviour.SendRpcInternalTo(uint, ChannelType, NetworkWriter, NetworkPlayer) not found");
            SendRpcInternalTo = module.ImportReference(sendRpcToDef);

            IsHostGetter = module.ImportReference(GetPropertyGetterOrThrow(nbDef, "IsHost"));
            IsClientGetter = module.ImportReference(GetPropertyGetterOrThrow(nbDef, "IsClient"));
            IsServerGetter = module.ImportReference(GetPropertyGetterOrThrow(nbDef, "IsServer"));
            OwnerPlayerGetter = module.ImportReference(GetPropertyGetterOrThrow(nbDef, "OwnerPlayer"));
            NetworkPlayerIsLocalGetter = module.ImportReference(GetPropertyGetterOrThrow(npDef, "IsLocal"));

            var setDirtyDef = FindMethod(nbDef, "SetDirty")
                ?? throw new InvalidOperationException("NetworkBehaviour.SetDirty() not found");
            SetDirtyMethod = module.ImportReference(setDirtyDef);

            var syncInitDef = FindMethod(syncCollectionDef, "__SegNetInitialize",
                "SegNet.NetworkBehaviour", "System.Int32", "System.Action")
                ?? throw new InvalidOperationException(
                    "SyncCollection.__SegNetInitialize(NetworkBehaviour, int, Action) not found");
            SyncCollectionInitialize = module.ImportReference(syncInitDef);

            SyncCollectionIsDirtyGetter =
                module.ImportReference(GetPropertyGetterOrThrow(syncCollectionDef, "__SegNetIsDirty"));

            SyncCollectionMarkFullDirty = module.ImportReference(
                FindMethod(syncCollectionDef, "__SegNetMarkFullDirty")
                ?? throw new InvalidOperationException("SyncCollection.__SegNetMarkFullDirty() not found"));
            SyncCollectionClearDirty = module.ImportReference(
                FindMethod(syncCollectionDef, "__SegNetClearDirty")
                ?? throw new InvalidOperationException("SyncCollection.__SegNetClearDirty() not found"));
            SyncCollectionSerializeFull = module.ImportReference(
                FindMethod(syncCollectionDef, "__SegNetSerializeFull", "SegNet.NetworkWriter")
                ?? throw new InvalidOperationException("SyncCollection.__SegNetSerializeFull(NetworkWriter) not found"));
            SyncCollectionSerializeDelta = module.ImportReference(
                FindMethod(syncCollectionDef, "__SegNetSerializeDelta", "SegNet.NetworkWriter")
                ?? throw new InvalidOperationException("SyncCollection.__SegNetSerializeDelta(NetworkWriter) not found"));
            SyncCollectionDeserializeFull = module.ImportReference(
                FindMethod(syncCollectionDef, "__SegNetDeserializeFull", "SegNet.NetworkReader")
                ?? throw new InvalidOperationException("SyncCollection.__SegNetDeserializeFull(NetworkReader) not found"));
            SyncCollectionDeserializeDelta = module.ImportReference(
                FindMethod(syncCollectionDef, "__SegNetDeserializeDelta", "SegNet.NetworkReader")
                ?? throw new InvalidOperationException("SyncCollection.__SegNetDeserializeDelta(NetworkReader) not found"));

            // ---- Action`2 (open) — built by hand against the target's corlib scope ----
            // We deliberately do NOT call module.ImportReference(typeof(Action<,>)) here:
            // Cecil would tag the resulting TypeReference with the host runtime's corlib
            // (System.Private.CoreLib), and downstream ILPPs would fail to resolve it
            // because the target assembly never references that DLL.
            ActionType = new TypeReference(
                "System", "Action", module, corlibScope, valueType: false);

            var action0Ctor = new MethodReference(".ctor", VoidType, ActionType) {
                HasThis = true,
                ExplicitThis = false,
                CallingConvention = MethodCallingConvention.Default,
            };
            action0Ctor.Parameters.Add(new ParameterDefinition(ObjectType));
            action0Ctor.Parameters.Add(new ParameterDefinition(IntPtrType));
            ActionCtor = action0Ctor;

            var action2Ref = new TypeReference(
                "System", "Action`2", module, corlibScope, valueType: false);
            action2Ref.GenericParameters.Add(new GenericParameter("T1", action2Ref));
            action2Ref.GenericParameters.Add(new GenericParameter("T2", action2Ref));
            Action2OpenType = action2Ref;

            // Closed instance: Action<NetworkBehaviour, NetworkReader>
            var actionInstance = new GenericInstanceType(action2Ref);
            actionInstance.GenericArguments.Add(NetworkBehaviourType);
            actionInstance.GenericArguments.Add(NetworkReaderType);
            DispatchActionType = actionInstance;

            // Ctor for the closed instance — Cecil won't auto-bind, build by hand.
            var actionCtor = new MethodReference(".ctor", VoidType, actionInstance) {
                HasThis = true,
                ExplicitThis = false,
                CallingConvention = MethodCallingConvention.Default,
            };
            actionCtor.Parameters.Add(new ParameterDefinition(ObjectType));
            actionCtor.Parameters.Add(new ParameterDefinition(IntPtrType));
            DispatchActionCtor = actionCtor;

            // ---- RpcRegistry.Register ----
            // Built by hand: importing the MethodInfo via reflection would transitively
            // import its Action<NB,NR> parameter through the host runtime's corlib,
            // re-introducing the same resolution bug. We keep the DeclaringType import
            // (safe — it's a SegNet type) but attach the parameters ourselves using
            // module-local TypeReferences.
            var rpcRegistryType = module.ImportReference(typeof(RpcRegistry));
            var registerRef = new MethodReference("Register", VoidType, rpcRegistryType) {
                HasThis = false,
                ExplicitThis = false,
                CallingConvention = MethodCallingConvention.Default,
            };
            registerRef.Parameters.Add(new ParameterDefinition(module.TypeSystem.UInt32));
            registerRef.Parameters.Add(new ParameterDefinition(actionInstance));
            RpcRegistryRegister = registerRef;

            // ---- Unity RuntimeInitializeOnLoadMethodAttribute ----
            // Type-only imports are safe (UnityEngine.CoreModule is already referenced).
            // The ctor is built by hand so we don't let Cecil re-scope the enum param
            // through the host runtime.
            RuntimeInitializeOnLoadAttrType =
                module.ImportReference(typeof(RuntimeInitializeOnLoadMethodAttribute));
            RuntimeInitializeLoadTypeRef = module.ImportReference(typeof(RuntimeInitializeLoadType));

            var riolCtor = new MethodReference(".ctor", VoidType, RuntimeInitializeOnLoadAttrType) {
                HasThis = true,
                ExplicitThis = false,
                CallingConvention = MethodCallingConvention.Default,
            };
            riolCtor.Parameters.Add(new ParameterDefinition(RuntimeInitializeLoadTypeRef));
            RuntimeInitializeOnLoadAttrCtor = riolCtor;

            SubsystemRegistrationValue = (int)RuntimeInitializeLoadType.SubsystemRegistration;
        }

        // ----------------------------------------------------------------
        //  Helpers — Definition walks (no reflection-based import)
        // ----------------------------------------------------------------

        /// <summary>
        /// Resolve a TypeReference to its TypeDefinition via Cecil's assembly resolver,
        /// throwing a clear error if the declaring assembly cannot be loaded. Used so
        /// the constructor can walk Methods/Properties directly instead of using the
        /// reflection importer (which would graft in host-corlib refs for primitive
        /// parameter types).
        /// </summary>
        private static TypeDefinition ResolveOrThrow(TypeReference typeRef, string friendlyName) {
            var def = typeRef.Resolve();
            if (def == null)
                throw new InvalidOperationException(
                    $"[RuntimeRefs] Could not resolve {friendlyName} — Cecil assembly " +
                    "resolver did not find the declaring assembly. Check ILPP search dirs.");
            return def;
        }

        /// <summary>
        /// Find a method on a TypeDefinition by name + parameter type FullNames.
        /// Returns null on no match so callers can throw context-specific errors.
        /// Matches by <see cref="TypeReference.FullName"/>, which is stable for both
        /// primitives ("System.Int32") and namespaced types ("SegNet.NetworkWriter").
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

        /// <summary>Get a property's getter MethodDefinition, throwing if missing.</summary>
        private static MethodDefinition GetPropertyGetterOrThrow(
            TypeDefinition type, string propertyName) {

            foreach (var p in type.Properties) {
                if (p.Name == propertyName) {
                    if (p.GetMethod == null)
                        throw new InvalidOperationException(
                            $"[RuntimeRefs] Property {type.FullName}.{propertyName} has no getter.");
                    return p.GetMethod;
                }
            }
            throw new InvalidOperationException(
                $"[RuntimeRefs] Property {type.FullName}.{propertyName} not found.");
        }
    }
}
