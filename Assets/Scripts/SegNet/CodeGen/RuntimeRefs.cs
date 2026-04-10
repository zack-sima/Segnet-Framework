using System;
using Mono.Cecil;
using SegNet;
using UnityEngine;

namespace SegNet.CodeGen {

    /// <summary>
    /// Cache of all SegNet runtime / framework / mscorlib references the weaver needs
    /// to emit IL into a target module. One instance is built per processed assembly.
    ///
    /// Everything here has been pre-imported into the target module via reflection
    /// (typeof / GetMethod), so the weaver can drop these straight into IL operands
    /// without additional ImportReference calls.
    /// </summary>
    internal sealed class RuntimeRefs {

        public ModuleDefinition Module { get; }

        // ---- SegNet runtime ----

        public TypeReference NetworkBehaviourType { get; }
        public TypeReference NetworkPlayerType { get; }
        public TypeReference NetworkWriterType { get; }
        public TypeReference NetworkReaderType { get; }
        public TypeReference RpcDirectionType { get; }
        public TypeReference ChannelTypeType { get; }

        public MethodReference NetworkWriterCtor { get; }       // NetworkWriter..ctor()
        public MethodReference NetworkWriterCtorCapacity { get; } // NetworkWriter..ctor(int)

        public MethodReference SendRpcInternal { get; }         // NetworkBehaviour.SendRpcInternal(ushort, RpcDirection, ChannelType, NetworkWriter)
        public MethodReference SendRpcInternalTo { get; }       // NetworkBehaviour.SendRpcInternalTo(ushort, ChannelType, NetworkWriter, NetworkPlayer)
        public MethodReference IsHostGetter { get; }
        public MethodReference IsClientGetter { get; }
        public MethodReference IsServerGetter { get; }
        public MethodReference OwnerPlayerGetter { get; }       // NetworkBehaviour.OwnerPlayer
        public MethodReference NetworkPlayerIsLocalGetter { get; } // NetworkPlayer.IsLocal

        public MethodReference RpcRegistryRegister { get; }     // RpcRegistry.Register(ushort, Action<NetworkBehaviour, NetworkReader>)

        // ---- mscorlib / netstandard ----

        public TypeReference VoidType { get; }
        public TypeReference ObjectType { get; }
        public TypeReference IntPtrType { get; }
        public TypeReference Action2OpenType { get; }           // open generic System.Action`2

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
            NetworkBehaviourType = module.ImportReference(typeof(NetworkBehaviour));
            NetworkPlayerType = module.ImportReference(typeof(NetworkPlayer));
            NetworkWriterType = module.ImportReference(typeof(NetworkWriter));
            NetworkReaderType = module.ImportReference(typeof(NetworkReader));
            RpcDirectionType = module.ImportReference(typeof(RpcDirection));
            ChannelTypeType = module.ImportReference(typeof(ChannelType));

            // ---- NetworkWriter constructors ----
            NetworkWriterCtor = module.ImportReference(
                typeof(NetworkWriter).GetConstructor(Type.EmptyTypes)
                ?? throw new InvalidOperationException("NetworkWriter parameterless ctor not found"));
            NetworkWriterCtorCapacity = module.ImportReference(
                typeof(NetworkWriter).GetConstructor(new[] { typeof(int) })
                ?? throw new InvalidOperationException("NetworkWriter(int) ctor not found"));

            // ---- NetworkBehaviour entry points ----
            // SendRpcInternal is non-public (protected) — need BindingFlags.NonPublic.
            const System.Reflection.BindingFlags InstanceAny =
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic;

            var sendRpcInfo = typeof(NetworkBehaviour).GetMethod(
                "SendRpcInternal",
                InstanceAny,
                binder: null,
                types: new[] { typeof(ushort), typeof(RpcDirection), typeof(ChannelType), typeof(NetworkWriter) },
                modifiers: null);
            if (sendRpcInfo == null)
                throw new InvalidOperationException(
                    "NetworkBehaviour.SendRpcInternal(ushort, RpcDirection, ChannelType, NetworkWriter) not found");
            SendRpcInternal = module.ImportReference(sendRpcInfo);

            var sendRpcToInfo = typeof(NetworkBehaviour).GetMethod(
                "SendRpcInternalTo",
                InstanceAny,
                binder: null,
                types: new[] { typeof(ushort), typeof(ChannelType), typeof(NetworkWriter), typeof(NetworkPlayer) },
                modifiers: null);
            if (sendRpcToInfo == null)
                throw new InvalidOperationException(
                    "NetworkBehaviour.SendRpcInternalTo(ushort, ChannelType, NetworkWriter, NetworkPlayer) not found");
            SendRpcInternalTo = module.ImportReference(sendRpcToInfo);

            IsHostGetter = ImportPropertyGetter(module, typeof(NetworkBehaviour), "IsHost");
            IsClientGetter = ImportPropertyGetter(module, typeof(NetworkBehaviour), "IsClient");
            IsServerGetter = ImportPropertyGetter(module, typeof(NetworkBehaviour), "IsServer");
            OwnerPlayerGetter = ImportPropertyGetter(module, typeof(NetworkBehaviour), "OwnerPlayer");
            NetworkPlayerIsLocalGetter = ImportPropertyGetter(module, typeof(NetworkPlayer), "IsLocal");

            // ---- Action`2 (open) — built by hand against the target's corlib scope ----
            // We deliberately do NOT call module.ImportReference(typeof(Action<,>)) here:
            // Cecil would tag the resulting TypeReference with the host runtime's corlib
            // (System.Private.CoreLib), and downstream ILPPs would fail to resolve it
            // because the target assembly never references that DLL.
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
            // Built by hand for the same reason as Action`2: importing the MethodInfo
            // via reflection would transitively import its Action<NB,NR> parameter type
            // through the host runtime's corlib, re-introducing the resolution bug.
            var rpcRegistryType = module.ImportReference(typeof(RpcRegistry));
            var registerRef = new MethodReference("Register", VoidType, rpcRegistryType) {
                HasThis = false,
                ExplicitThis = false,
                CallingConvention = MethodCallingConvention.Default,
            };
            registerRef.Parameters.Add(new ParameterDefinition(module.TypeSystem.UInt16));
            registerRef.Parameters.Add(new ParameterDefinition(actionInstance));
            RpcRegistryRegister = registerRef;

            // ---- Unity RuntimeInitializeOnLoadMethodAttribute ----
            RuntimeInitializeOnLoadAttrType =
                module.ImportReference(typeof(RuntimeInitializeOnLoadMethodAttribute));
            var attrCtor = typeof(RuntimeInitializeOnLoadMethodAttribute)
                .GetConstructor(new[] { typeof(RuntimeInitializeLoadType) });
            if (attrCtor == null)
                throw new InvalidOperationException(
                    "RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType) ctor not found");
            RuntimeInitializeOnLoadAttrCtor = module.ImportReference(attrCtor);
            RuntimeInitializeLoadTypeRef = module.ImportReference(typeof(RuntimeInitializeLoadType));

            SubsystemRegistrationValue = (int)RuntimeInitializeLoadType.SubsystemRegistration;
        }

        // ----------------------------------------------------------------
        //  Helpers
        // ----------------------------------------------------------------

        private static MethodReference ImportPropertyGetter(
            ModuleDefinition module, Type declaringType, string propertyName) {

            var prop = declaringType.GetProperty(propertyName);
            if (prop == null || prop.GetGetMethod() == null)
                throw new InvalidOperationException(
                    $"Property {declaringType.Name}.{propertyName} (or its getter) not found");
            return module.ImportReference(prop.GetGetMethod());
        }
    }
}
