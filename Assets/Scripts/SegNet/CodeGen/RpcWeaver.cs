using System;
using System.Collections.Generic;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Unity.CompilationPipeline.Common.Diagnostics;

namespace SegNet.CodeGen {

    /// <summary>
    /// Weaves [Rpc] methods on a NetworkBehaviour subclass.
    ///
    /// For each [Rpc] method <c>Foo</c>, this:
    ///   1. Validates the signature (void return, instance, non-generic, all params serializable).
    ///   2. Computes a stable ushort rpcId via FNV-1a of the qualified signature.
    ///   3. Clones the original body into a new <c>__SegNetRpcImpl_Foo_XXXX</c> method (XXXX = rpcId hex,
    ///      so overloads of the same name produce distinct impl method names).
    ///   4. Replaces the original body with a wrapper:
    ///        - <b>ClientToServer</b>:      host shortcut → impl; else serialize + SendRpcInternal.
    ///        - <b>LocalClientToServer</b>: bail unless OwnerPlayer.IsLocal; then host shortcut or send.
    ///        - <b>ServerToClients</b>:     bail if not server; serialize + Broadcast; if host, also impl.
    ///        - <b>ServerToClient</b>:      bail if not server; if owner is null bail; if owner.IsLocal
    ///                                      (host owns it) call impl directly; else serialize + targeted send.
    ///   5. Generates a static dispatch handler <c>__SegNetRpcDispatch_Foo_XXXX(NetworkBehaviour, NetworkReader)</c>
    ///      that reads the args from the wire, casts the target, and calls impl.
    ///
    /// After all RPCs on a type are processed, the weaver calls <see cref="EmitRegistrationMethod"/>
    /// once to emit a per-type <c>[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]
    /// static __SegNetRegisterRpcs()</c> that registers every dispatch handler with RpcRegistry.
    /// Unity auto-discovers RuntimeInitializeOnLoadMethod attributes at startup, so the user
    /// doesn't have to manually wire anything up per script.
    ///
    /// Renaming the original method is deliberately avoided — that would invalidate any
    /// in-assembly MethodReferences pointing at it. Body cloning + body replacement keeps
    /// the original MethodDefinition identity intact.
    /// </summary>
    internal sealed class RpcWeaver {

        private const string RpcAttrName = "SegNet.RpcAttribute";
        private const string ImplPrefix = "__SegNetRpcImpl_";
        private const string DispatchPrefix = "__SegNetRpcDispatch_";
        private const string RegisterMethodName = "__SegNetRegisterRpcs";

        private readonly RuntimeRefs _refs;
        private readonly SerializerMap _serializers;
        private readonly List<DiagnosticMessage> _diagnostics;

        // Per-type accumulator: rpcId → dispatch handler. Drained by EmitRegistrationMethod.
        private readonly List<(ushort rpcId, MethodDefinition dispatch)> _pendingRegistrations =
            new List<(ushort, MethodDefinition)>();

        public RpcWeaver(RuntimeRefs refs, SerializerMap serializers,
            List<DiagnosticMessage> diagnostics) {
            _refs = refs ?? throw new ArgumentNullException(nameof(refs));
            _serializers = serializers ?? throw new ArgumentNullException(nameof(serializers));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        }

        // ----------------------------------------------------------------
        //  Public entry points
        // ----------------------------------------------------------------

        /// <summary>Reset per-type state. Call before iterating a new type's RPCs.</summary>
        public void BeginType() {
            _pendingRegistrations.Clear();
        }

        /// <summary>
        /// Weave a single [Rpc] method. Returns true on success; false on validation failure
        /// (in which case a diagnostic was emitted and the method was left untouched).
        /// </summary>
        public bool WeaveRpc(TypeDefinition declaringType, MethodDefinition rpcMethod) {
            if (!ValidateRpc(declaringType, rpcMethod))
                return false;

            // Read attribute args (direction + channel) once.
            var attr = rpcMethod.GetAttributeOfType(RpcAttrName);
            if (attr == null) {
                Error(rpcMethod, $"[Rpc] attribute missing on '{rpcMethod.FullName}' (internal weaver error).");
                return false;
            }

            int directionVal = (int)attr.ConstructorArguments[0].Value;
            int channelVal = ReadChannelArg(attr); // defaults to 0 (Reliable) if not specified

            ushort rpcId = ComputeRpcId(rpcMethod);

            // 1. Move original body to impl method (name includes rpcId for overload uniqueness).
            MethodDefinition implMethod = MoveBodyToImpl(declaringType, rpcMethod, rpcId);

            // 2. Replace original body with wrapper.
            WriteWrapperBody(rpcMethod, implMethod, rpcId, directionVal, channelVal);

            // 3. Generate dispatch handler (also rpcId-suffixed).
            MethodDefinition dispatch = GenerateDispatchHandler(declaringType, rpcMethod, implMethod, rpcId);

            _pendingRegistrations.Add((rpcId, dispatch));

            Info($"[SegNet ILPP] Wove [Rpc] {rpcMethod.FullName} → id 0x{rpcId:X4}");
            return true;
        }

        /// <summary>
        /// Emit the per-type registration method (one [RuntimeInitializeOnLoadMethod]
        /// per type with at least one RPC). Returns null if there are no RPCs to register.
        /// </summary>
        public MethodDefinition EmitRegistrationMethod(TypeDefinition declaringType) {
            if (_pendingRegistrations.Count == 0) return null;

            // If a previous weave already added a registration method (e.g. recompile
            // without domain reload), reuse it: erase its body and rewrite. Cecil sees
            // a fresh AssemblyDefinition each invocation so this only matters when the
            // user is iterating with the same source on disk + ILPP rerun.
            MethodDefinition reg = null;
            foreach (var m in declaringType.Methods) {
                if (m.Name == RegisterMethodName && m.IsStatic && m.Parameters.Count == 0) {
                    reg = m;
                    reg.Body = new MethodBody(reg);
                    break;
                }
            }

            if (reg == null) {
                reg = new MethodDefinition(
                    RegisterMethodName,
                    MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
                    _refs.VoidType);
                declaringType.Methods.Add(reg);

                // [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
                var attr = new CustomAttribute(_refs.RuntimeInitializeOnLoadAttrCtor);
                attr.ConstructorArguments.Add(new CustomAttributeArgument(
                    _refs.RuntimeInitializeLoadTypeRef,
                    _refs.SubsystemRegistrationValue));
                reg.CustomAttributes.Add(attr);
            }

            var il = reg.Body.GetILProcessor();
            foreach (var (rpcId, dispatch) in _pendingRegistrations) {
                // RpcRegistry.Register(rpcId, new Action<NetworkBehaviour, NetworkReader>(dispatch));
                il.Emit(OpCodes.Ldc_I4, (int)rpcId);
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ldftn, dispatch);
                il.Emit(OpCodes.Newobj, _refs.DispatchActionCtor);
                il.Emit(OpCodes.Call, _refs.RpcRegistryRegister);
            }
            il.Emit(OpCodes.Ret);

            return reg;
        }

        // ----------------------------------------------------------------
        //  Validation
        // ----------------------------------------------------------------

        private bool ValidateRpc(TypeDefinition declaringType, MethodDefinition method) {
            if (method.IsStatic) {
                Error(method, $"[Rpc] '{method.FullName}' must be an instance method.");
                return false;
            }
            if (method.HasGenericParameters) {
                Error(method, $"[Rpc] '{method.FullName}' must not be generic.");
                return false;
            }
            if (method.ReturnType.FullName != _refs.VoidType.FullName) {
                Error(method, $"[Rpc] '{method.FullName}' must return void.");
                return false;
            }
            if (method.IsAbstract) {
                Error(method, $"[Rpc] '{method.FullName}' must not be abstract.");
                return false;
            }

            // Param types must all be in the serializer map.
            for (int i = 0; i < method.Parameters.Count; i++) {
                var p = method.Parameters[i];
                if (p.IsOut || p.ParameterType.IsByReference) {
                    Error(method, $"[Rpc] '{method.FullName}': parameter '{p.Name}' cannot be ref/out.");
                    return false;
                }
                if (!_serializers.TryGet(p.ParameterType, out _, out _)) {
                    Error(method,
                        $"[Rpc] '{method.FullName}': parameter '{p.Name}' has unsupported type '{p.ParameterType.FullName}'. " +
                        "See SerializerMap for supported types.");
                    return false;
                }
            }

            // Sanity: detect re-entry (the weaver already ran on this assembly). Re-entry is
            // diagnosed by checking for an existing impl method with the rpcId suffix.
            string implName = ImplPrefix + method.Name + "_" + ComputeRpcId(method).ToString("X4");
            foreach (var m in declaringType.Methods) {
                if (m.Name == implName) {
                    Error(method,
                        $"[Rpc] '{method.FullName}': impl method '{implName}' already exists. " +
                        "Did the weaver already run on this assembly?");
                    return false;
                }
            }

            return true;
        }

        // ----------------------------------------------------------------
        //  Step 1: hash → ushort rpcId
        // ----------------------------------------------------------------

        private static ushort ComputeRpcId(MethodDefinition method) {
            var sb = new StringBuilder();
            sb.Append(method.DeclaringType.FullName);
            sb.Append("::");
            sb.Append(method.Name);
            sb.Append('(');
            for (int i = 0; i < method.Parameters.Count; i++) {
                if (i > 0) sb.Append(',');
                sb.Append(method.Parameters[i].ParameterType.FullName);
            }
            sb.Append(')');
            return Fnv1a16(sb.ToString());
        }

        private static ushort Fnv1a16(string s) {
            const uint offset = 2166136261u;
            const uint prime = 16777619u;
            uint hash = offset;
            for (int i = 0; i < s.Length; i++) {
                hash ^= (byte)s[i];
                hash *= prime;
            }
            return (ushort)((hash >> 16) ^ (hash & 0xFFFF));
        }

        // ----------------------------------------------------------------
        //  Step 2: clone original body into a new __SegNetRpcImpl_X method
        // ----------------------------------------------------------------

        private MethodDefinition MoveBodyToImpl(
            TypeDefinition declaringType, MethodDefinition original, ushort rpcId) {
            // Create the impl method with identical signature (private so it never appears
            // in IntelliSense and can't be called from user code). The rpcId hex suffix
            // disambiguates overloads (same method name, different param signatures).
            var impl = new MethodDefinition(
                ImplPrefix + original.Name + "_" + rpcId.ToString("X4"),
                MethodAttributes.Private | MethodAttributes.HideBySig,
                original.ReturnType);

            foreach (var p in original.Parameters)
                impl.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));

            declaringType.Methods.Add(impl);

            // Clone the body (locals, instructions, exception handlers).
            original.CloneBodyTo(impl);

            // Wipe original body — caller will write a fresh wrapper into it.
            original.Body = new MethodBody(original);
            return impl;
        }

        // ----------------------------------------------------------------
        //  Step 3: write wrapper IL into the (now empty) original method
        // ----------------------------------------------------------------

        private void WriteWrapperBody(
            MethodDefinition wrapper,
            MethodDefinition impl,
            ushort rpcId,
            int directionVal,
            int channelVal) {

            wrapper.Body.InitLocals = true;
            var il = wrapper.Body.GetILProcessor();

            // Local 0: NetworkWriter for serialized args (only used in the send path,
            // but we always declare it for simplicity).
            var writerLocal = new VariableDefinition(_refs.NetworkWriterType);
            wrapper.Body.Variables.Add(writerLocal);

            // Direction values must match the SegNet.RpcDirection enum.
            switch (directionVal) {
                case 0: // ClientToServer
                    EmitClientToServerWrapper(il, wrapper, impl, writerLocal, rpcId, channelVal,
                        /*ownerCheck*/ false);
                    break;
                case 3: // LocalClientToServer
                    EmitClientToServerWrapper(il, wrapper, impl, writerLocal, rpcId, channelVal,
                        /*ownerCheck*/ true);
                    break;
                case 1: // ServerToClients
                    EmitServerToClientsWrapper(il, wrapper, impl, writerLocal, rpcId, channelVal);
                    break;
                case 2: // ServerToClient (single target — owner of this object)
                    EmitServerToClientWrapper(il, wrapper, impl, writerLocal, rpcId, channelVal);
                    break;
                default:
                    Error(wrapper,
                        $"[Rpc] '{wrapper.FullName}': unknown RpcDirection value {directionVal}.");
                    il.Emit(OpCodes.Ret); // emit something so the method is well-formed
                    break;
            }
        }

        private void EmitClientToServerWrapper(
            ILProcessor il,
            MethodDefinition wrapper,
            MethodDefinition impl,
            VariableDefinition writerLocal,
            ushort rpcId,
            int channelVal,
            bool ownerCheck) {

            // LocalClientToServer prologue (ownerCheck = true):
            //   var owner = this.OwnerPlayer;
            //   if (owner == null) return;
            //   if (!owner.IsLocal) return;
            // Bytes never hit the wire from a non-owner, so a malicious client cannot
            // impersonate another player by calling this RPC against an object they
            // do not own. The server still re-validates ownership on receive.
            if (ownerCheck) {
                var ownerLocal = new VariableDefinition(_refs.NetworkPlayerType);
                wrapper.Body.Variables.Add(ownerLocal);

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Callvirt, _refs.OwnerPlayerGetter);
                il.Emit(OpCodes.Stloc, ownerLocal);

                // if (owner == null) return;
                var ownerNotNull = Instruction.Create(OpCodes.Nop);
                il.Emit(OpCodes.Ldloc, ownerLocal);
                il.Emit(OpCodes.Brtrue, ownerNotNull);
                il.Emit(OpCodes.Ret);
                il.Append(ownerNotNull);

                // if (!owner.IsLocal) return;
                var isLocalOk = Instruction.Create(OpCodes.Nop);
                il.Emit(OpCodes.Ldloc, ownerLocal);
                il.Emit(OpCodes.Callvirt, _refs.NetworkPlayerIsLocalGetter);
                il.Emit(OpCodes.Brtrue, isLocalOk);
                il.Emit(OpCodes.Ret);
                il.Append(isLocalOk);
            }

            // if (this.IsHost) { this.impl(args); return; }
            var notHost = Instruction.Create(OpCodes.Nop);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Callvirt, _refs.IsHostGetter);
            il.Emit(OpCodes.Brfalse, notHost);

            // Direct impl call: ldarg.0, ldarg.1, ..., call impl, ret
            EmitImplCall(il, wrapper, impl);
            il.Emit(OpCodes.Ret);

            // not-host:
            il.Append(notHost);

            // if (!this.IsClient) return;
            var isClientOk = Instruction.Create(OpCodes.Nop);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Callvirt, _refs.IsClientGetter);
            il.Emit(OpCodes.Brtrue, isClientOk);
            il.Emit(OpCodes.Ret);
            il.Append(isClientOk);

            // var w = new NetworkWriter();
            il.Emit(OpCodes.Newobj, _refs.NetworkWriterCtor);
            il.Emit(OpCodes.Stloc, writerLocal);

            // Serialize each arg.
            EmitSerializeArgs(il, wrapper, writerLocal);

            // SendRpcInternal(rpcId, ClientToServer | LocalClientToServer, channel, w)
            // Both directions take the same wire path on the runtime side, but we pass
            // the truthful enum value so server-side logging stays accurate.
            int directionVal = ownerCheck ? 3 : 0;
            EmitSendRpcInternal(il, writerLocal, rpcId, directionVal, channelVal);
            il.Emit(OpCodes.Ret);
        }

        private void EmitServerToClientWrapper(
            ILProcessor il,
            MethodDefinition wrapper,
            MethodDefinition impl,
            VariableDefinition writerLocal,
            ushort rpcId,
            int channelVal) {

            // if (!this.IsServer) return;
            var serverOk = Instruction.Create(OpCodes.Nop);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Callvirt, _refs.IsServerGetter);
            il.Emit(OpCodes.Brtrue, serverOk);
            il.Emit(OpCodes.Ret);
            il.Append(serverOk);

            // var owner = this.OwnerPlayer;
            var ownerLocal = new VariableDefinition(_refs.NetworkPlayerType);
            wrapper.Body.Variables.Add(ownerLocal);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Callvirt, _refs.OwnerPlayerGetter);
            il.Emit(OpCodes.Stloc, ownerLocal);

            // if (owner == null) return;
            // ServerToClient against an unowned object is silently dropped — there's no
            // sensible target. Devs should use ServerToClients for world-broadcast events.
            var ownerNotNull = Instruction.Create(OpCodes.Nop);
            il.Emit(OpCodes.Ldloc, ownerLocal);
            il.Emit(OpCodes.Brtrue, ownerNotNull);
            il.Emit(OpCodes.Ret);
            il.Append(ownerNotNull);

            // Host shortcut: if (owner.IsLocal) { this.impl(args); return; }
            // The host owns this object — bypass the wire and call impl directly so the
            // host's local client side observes the RPC like any remote client would.
            var notHostOwned = Instruction.Create(OpCodes.Nop);
            il.Emit(OpCodes.Ldloc, ownerLocal);
            il.Emit(OpCodes.Callvirt, _refs.NetworkPlayerIsLocalGetter);
            il.Emit(OpCodes.Brfalse, notHostOwned);
            EmitImplCall(il, wrapper, impl);
            il.Emit(OpCodes.Ret);
            il.Append(notHostOwned);

            // var w = new NetworkWriter();
            il.Emit(OpCodes.Newobj, _refs.NetworkWriterCtor);
            il.Emit(OpCodes.Stloc, writerLocal);

            // Serialize each arg.
            EmitSerializeArgs(il, wrapper, writerLocal);

            // this.SendRpcInternalTo(rpcId, channel, w, owner);
            il.Emit(OpCodes.Ldarg_0);                       // this
            il.Emit(OpCodes.Ldc_I4, (int)rpcId);            // rpcId
            il.Emit(OpCodes.Ldc_I4, channelVal);            // channel
            il.Emit(OpCodes.Ldloc, writerLocal);            // writer
            il.Emit(OpCodes.Ldloc, ownerLocal);             // target
            il.Emit(OpCodes.Call, _refs.SendRpcInternalTo);
            il.Emit(OpCodes.Ret);
        }

        private void EmitServerToClientsWrapper(
            ILProcessor il,
            MethodDefinition wrapper,
            MethodDefinition impl,
            VariableDefinition writerLocal,
            ushort rpcId,
            int channelVal) {

            // if (!this.IsServer) return;
            var serverOk = Instruction.Create(OpCodes.Nop);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Callvirt, _refs.IsServerGetter);
            il.Emit(OpCodes.Brtrue, serverOk);
            il.Emit(OpCodes.Ret);
            il.Append(serverOk);

            // var w = new NetworkWriter();
            il.Emit(OpCodes.Newobj, _refs.NetworkWriterCtor);
            il.Emit(OpCodes.Stloc, writerLocal);

            // Serialize each arg.
            EmitSerializeArgs(il, wrapper, writerLocal);

            // SendRpcInternal(rpcId, ServerToClients, channel, w)
            EmitSendRpcInternal(il, writerLocal, rpcId, /*direction*/1, channelVal);

            // if (this.IsHost) impl(args)
            var done = Instruction.Create(OpCodes.Ret);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Callvirt, _refs.IsHostGetter);
            il.Emit(OpCodes.Brfalse, done);

            EmitImplCall(il, wrapper, impl);
            il.Append(done);
        }

        /// <summary>
        /// Push 'this' + every original argument and call the impl method.
        /// Used by both wrapper paths for the local-side execution branch.
        /// </summary>
        private static void EmitImplCall(ILProcessor il, MethodDefinition wrapper, MethodDefinition impl) {
            il.Emit(OpCodes.Ldarg_0);
            for (int i = 0; i < wrapper.Parameters.Count; i++)
                il.Emit(OpCodes.Ldarg, wrapper.Parameters[i]);
            il.Emit(OpCodes.Call, impl);
        }

        /// <summary>
        /// Serialize each parameter of the wrapper into the local NetworkWriter.
        /// Assumes ValidateRpc has already confirmed every type is in the serializer map.
        /// </summary>
        private void EmitSerializeArgs(
            ILProcessor il, MethodDefinition wrapper, VariableDefinition writerLocal) {

            for (int i = 0; i < wrapper.Parameters.Count; i++) {
                var p = wrapper.Parameters[i];
                if (!_serializers.TryGet(p.ParameterType, out var write, out _)) {
                    // Already validated; this would be a weaver bug.
                    Error(wrapper,
                        $"[Rpc] '{wrapper.FullName}': internal: no writer for '{p.ParameterType.FullName}'.");
                    continue;
                }
                il.Emit(OpCodes.Ldloc, writerLocal);
                il.Emit(OpCodes.Ldarg, p);
                il.Emit(OpCodes.Callvirt, write);
            }
        }

        /// <summary>
        /// Emit: this.SendRpcInternal((ushort)rpcId, (RpcDirection)dirVal, (ChannelType)channelVal, writer);
        /// </summary>
        private void EmitSendRpcInternal(
            ILProcessor il, VariableDefinition writerLocal,
            ushort rpcId, int directionVal, int channelVal) {

            il.Emit(OpCodes.Ldarg_0);                       // this
            il.Emit(OpCodes.Ldc_I4, (int)rpcId);            // rpcId (uint16 promoted to int32 on stack)
            il.Emit(OpCodes.Ldc_I4, directionVal);          // direction enum
            il.Emit(OpCodes.Ldc_I4, channelVal);            // channel enum
            il.Emit(OpCodes.Ldloc, writerLocal);            // writer
            il.Emit(OpCodes.Call, _refs.SendRpcInternal);
        }

        // ----------------------------------------------------------------
        //  Step 4: generate static dispatch handler
        // ----------------------------------------------------------------

        private MethodDefinition GenerateDispatchHandler(
            TypeDefinition declaringType,
            MethodDefinition wrapper,
            MethodDefinition impl,
            ushort rpcId) {

            var dispatch = new MethodDefinition(
                DispatchPrefix + wrapper.Name + "_" + rpcId.ToString("X4"),
                MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
                _refs.VoidType);

            dispatch.Parameters.Add(new ParameterDefinition("target", ParameterAttributes.None, _refs.NetworkBehaviourType));
            dispatch.Parameters.Add(new ParameterDefinition("reader", ParameterAttributes.None, _refs.NetworkReaderType));

            declaringType.Methods.Add(dispatch);

            var body = dispatch.Body;
            body.InitLocals = true;
            var il = body.GetILProcessor();

            // Stack target: ((TDeclaring)target).Impl(reader.ReadX(), reader.ReadY(), ...)
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, declaringType);

            for (int i = 0; i < wrapper.Parameters.Count; i++) {
                var p = wrapper.Parameters[i];
                if (!_serializers.TryGet(p.ParameterType, out _, out var read)) {
                    Error(wrapper,
                        $"[Rpc] '{wrapper.FullName}': internal: no reader for '{p.ParameterType.FullName}'.");
                    continue;
                }
                il.Emit(OpCodes.Ldarg_1); // reader
                il.Emit(OpCodes.Callvirt, read);

                // For NetworkBehaviour subclasses (other than NetworkBehaviour itself), the
                // read returns the base type — we must castclass to the declared param type.
                if (_serializers.RequiresReadCast(p.ParameterType)) {
                    il.Emit(OpCodes.Castclass, p.ParameterType);
                }
            }

            il.Emit(OpCodes.Call, impl);
            il.Emit(OpCodes.Ret);

            return dispatch;
        }

        // ----------------------------------------------------------------
        //  Attribute parsing
        // ----------------------------------------------------------------

        /// <summary>
        /// Read the optional Channel = ChannelType.X named arg from an [Rpc] attribute.
        /// Defaults to 0 (Reliable) if not specified.
        /// </summary>
        private static int ReadChannelArg(CustomAttribute attr) {
            if (attr.HasProperties) {
                foreach (var prop in attr.Properties) {
                    if (prop.Name == "Channel")
                        return Convert.ToInt32(prop.Argument.Value);
                }
            }
            return 0;
        }

        // ----------------------------------------------------------------
        //  Diagnostics
        // ----------------------------------------------------------------

        private void Error(MethodDefinition method, string msg) {
            _diagnostics.Add(new DiagnosticMessage {
                DiagnosticType = DiagnosticType.Error,
                MessageData = msg,
            });
        }

        private void Warning(MethodDefinition method, string msg) {
            _diagnostics.Add(new DiagnosticMessage {
                DiagnosticType = DiagnosticType.Warning,
                MessageData = msg,
            });
        }

        private void Info(string msg) {
            _diagnostics.Add(new DiagnosticMessage {
                DiagnosticType = DiagnosticType.Warning,
                MessageData = msg,
            });
        }
    }
}
