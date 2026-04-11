using System;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Unity.CompilationPipeline.Common.Diagnostics;

namespace SegNet.CodeGen {

    /// <summary>
    /// Weaves [SyncVar] fields on a NetworkBehaviour subclass.
    ///
    /// For each type with at least one [SyncVar] field, this:
    ///   1. Validates the fields (≤ 64 per class, supported type, hook signature if any).
    ///   2. Adds a private <c>__segnetSyncVarDirty</c> ulong field to the class. Each
    ///      [SyncVar] gets a sequential bit index 0..63 within this class — derived
    ///      classes get their own dirty mask, so inheritance never collides.
    ///   3. Generates a per-field setter <c>__segnetSetSyncVar_&lt;name&gt;(value)</c>
    ///      that does an early-out equality check, stores the new value, ORs the bit,
    ///      and calls <see cref="NetworkBehaviour.SetDirty"/>.
    ///   4. Rewrites every <c>stfld syncVarField</c> in the type's existing methods
    ///      (excluding constructors) with a <c>call</c> to the generated setter, so
    ///      ordinary user code like <c>health = 50</c> transparently marks dirty.
    ///   5. Generates per-class overrides of <c>OnSerialize</c> and <c>OnDeserialize</c>
    ///      that chain into base.OnSerialize/Deserialize first (so inherited SyncVars
    ///      replicate too), then either write/read all fields (initial state) or write
    ///      a 64-bit dirty mask plus only the dirty fields (delta updates), and finally
    ///      zero the per-class mask.
    ///
    /// Hooks: <c>[SyncVar(hook = nameof(OnX))]</c> calls a user-defined instance method
    /// <c>void OnX(T oldValue, T newValue)</c> or the no-arg form <c>void OnX()</c>
    /// whenever the field changes. The hook fires from BOTH the generated setter (so the
    /// host and dedicated server see their own changes) AND from OnDeserialize (so remote
    /// clients see incoming changes). On a host the receiving-side OnDeserialize is
    /// short-circuited by ServerManager's IsHost guard, so each change still produces
    /// exactly one hook call.
    ///
    /// Inheritance ordering: <see cref="SegNetILPostProcessor"/> processes types
    /// base-to-derived so that when we generate Derived.OnSerialize, the resolver can
    /// find Base's already-woven OnSerialize and emit a non-virtual base call to it.
    ///
    /// Type support is delegated to <see cref="SerializerMap"/> (the same map RPCs use):
    /// primitives, Unity value types, NetworkPlayer, and any NetworkBehaviour subclass.
    /// </summary>
    internal sealed class SyncVarWeaver {

        private const string SyncVarAttrName = "SegNet.SyncVarAttribute";
        private const string DirtyMaskFieldName = "__segnetSyncVarDirty";
        private const string SetterPrefix = "__segnetSetSyncVar_";

        private const int MaxSyncVarsPerClass = 64;

        private readonly RuntimeRefs _refs;
        private readonly SerializerMap _serializers;
        private readonly List<DiagnosticMessage> _diagnostics;

        // ulong writer/reader, looked up once per module via the SerializerMap.
        private readonly MethodReference _writeUlong;
        private readonly MethodReference _readUlong;

        // Cached op_Equality lookups, keyed by type FullName. Resolved on demand from
        // the type's TypeDefinition (not via reflection, so we stay on Cecil's metadata
        // importer path and don't leak host-corlib references into the woven assembly).
        private readonly Dictionary<string, MethodReference> _opEqualityCache =
            new Dictionary<string, MethodReference>(StringComparer.Ordinal);

        public SyncVarWeaver(RuntimeRefs refs, SerializerMap serializers,
            List<DiagnosticMessage> diagnostics) {
            _refs = refs ?? throw new ArgumentNullException(nameof(refs));
            _serializers = serializers ?? throw new ArgumentNullException(nameof(serializers));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));

            // The SerializerMap already exposes ulong as System.UInt64, scoped to the
            // target's corlib. Reusing it avoids us having to do another import dance.
            if (!_serializers.TryGet(_refs.Module.TypeSystem.UInt64, out _writeUlong, out _readUlong)) {
                throw new InvalidOperationException(
                    "[SyncVarWeaver] SerializerMap is missing System.UInt64 entries — " +
                    "weaver cannot emit dirty masks.");
            }
        }

        // ----------------------------------------------------------------
        //  Per-field metadata collected during validation
        // ----------------------------------------------------------------

        private sealed class SyncVarSlot {
            public FieldDefinition Field;
            public MethodReference WriteMethod;
            public MethodReference ReadMethod;
            public bool RequiresReadCast;       // for NetworkBehaviour subclasses
            public MethodReference HookMethod;  // null if no hook
            public MethodDefinition Setter;     // populated after generation
        }

        // ----------------------------------------------------------------
        //  Public entry point
        // ----------------------------------------------------------------

        /// <summary>
        /// Weave all [SyncVar] fields on the given type. Returns true if the type was
        /// modified. False means either no SyncVars (skipped) or a validation error
        /// (already reported via diagnostics).
        /// </summary>
        public bool WeaveType(TypeDefinition type) {
            // Quick scan: any [SyncVar] fields at all?
            List<FieldDefinition> rawFields = null;
            foreach (var f in type.Fields) {
                if (f.HasAttributeOfType(SyncVarAttrName)) {
                    if (rawFields == null) rawFields = new List<FieldDefinition>();
                    rawFields.Add(f);
                }
            }
            if (rawFields == null) return false;

            // Re-entry guard: if our generated dirty-mask field already exists, the
            // weaver has run on this assembly before. Refuse to double-weave.
            foreach (var f in type.Fields) {
                if (f.Name == DirtyMaskFieldName) {
                    Error($"[SyncVar] '{type.FullName}': field '{DirtyMaskFieldName}' already exists. " +
                          "Did the weaver already run on this assembly?");
                    return false;
                }
            }

            if (rawFields.Count > MaxSyncVarsPerClass) {
                Error($"[SyncVar] '{type.FullName}': too many SyncVars ({rawFields.Count}). " +
                      $"Max is {MaxSyncVarsPerClass} per class.");
                return false;
            }

            // Disallow user-declared OnSerialize/OnDeserialize on classes with SyncVars —
            // the weaver owns those overrides on this class. (User can still override on
            // a parent that has no SyncVars.)
            if (HasUserOverride(type, "OnSerialize", "SegNet.NetworkWriter")) {
                Error($"[SyncVar] '{type.FullName}': cannot declare a manual OnSerialize override " +
                      "on a class that has [SyncVar] fields — the weaver generates this method.");
                return false;
            }
            if (HasUserOverride(type, "OnDeserialize", "SegNet.NetworkReader")) {
                Error($"[SyncVar] '{type.FullName}': cannot declare a manual OnDeserialize override " +
                      "on a class that has [SyncVar] fields — the weaver generates this method.");
                return false;
            }

            // Validate each field and gather metadata.
            var slots = new List<SyncVarSlot>(rawFields.Count);
            foreach (var field in rawFields) {
                var slot = ValidateField(type, field);
                if (slot == null) return false; // diagnostic already emitted
                slots.Add(slot);
            }

            // Snapshot the current methods BEFORE we add anything. Step 4 (stfld
            // rewrite) walks this snapshot, so it never sees the generated setters or
            // the generated OnSerialize/OnDeserialize.
            var methodsToRewrite = new List<MethodDefinition>(type.Methods.Count);
            foreach (var m in type.Methods)
                methodsToRewrite.Add(m);

            // Step 2: per-class dirty mask field.
            var dirtyMaskField = GenerateDirtyMaskField(type);

            // Step 3: per-field setters.
            for (int i = 0; i < slots.Count; i++) {
                slots[i].Setter = GenerateSetter(type, slots[i], dirtyMaskField, i);
            }

            // Step 4: rewrite stfld → call setter in pre-existing methods.
            var fieldNameToSetter = new Dictionary<string, MethodDefinition>(StringComparer.Ordinal);
            foreach (var s in slots)
                fieldNameToSetter[s.Field.FullName] = s.Setter;

            int totalRewrites = 0;
            foreach (var m in methodsToRewrite)
                totalRewrites += RewriteFieldStores(m, fieldNameToSetter);

            // Step 5: OnSerialize / OnDeserialize overrides.
            GenerateOnSerialize(type, slots, dirtyMaskField);
            GenerateOnDeserialize(type, slots);

            Info($"[SegNet ILPP] Wove {slots.Count} [SyncVar] field(s) on {type.FullName} " +
                 $"({totalRewrites} stfld rewrite(s))");
            return true;
        }

        // ----------------------------------------------------------------
        //  Validation
        // ----------------------------------------------------------------

        private SyncVarSlot ValidateField(TypeDefinition type, FieldDefinition field) {
            if (field.IsStatic) {
                Error($"[SyncVar] '{type.FullName}.{field.Name}' must not be static.");
                return null;
            }
            if (field.IsLiteral) {
                Error($"[SyncVar] '{type.FullName}.{field.Name}' must not be const.");
                return null;
            }
            if (field.IsInitOnly) {
                Error($"[SyncVar] '{type.FullName}.{field.Name}' must not be readonly.");
                return null;
            }

            if (!_serializers.TryGet(field.FieldType, out var write, out var read)) {
                Error($"[SyncVar] '{type.FullName}.{field.Name}': unsupported type " +
                      $"'{field.FieldType.FullName}'. See SerializerMap for supported types.");
                return null;
            }

            var slot = new SyncVarSlot {
                Field = field,
                WriteMethod = write,
                ReadMethod = read,
                RequiresReadCast = _serializers.RequiresReadCast(field.FieldType),
            };

            // Hook resolution.
            string hookName = ReadHookName(field.GetAttributeOfType(SyncVarAttrName));
            if (!string.IsNullOrEmpty(hookName)) {
                slot.HookMethod = FindHookMethod(type, hookName, field.FieldType);
                if (slot.HookMethod == null) {
                    Error($"[SyncVar] '{type.FullName}.{field.Name}': hook '{hookName}' not found. " +
                          $"Expected an instance method 'void {hookName}({field.FieldType.FullName} oldValue, " +
                          $"{field.FieldType.FullName} newValue)' or 'void {hookName}()'.");
                    return null;
                }
            }

            return slot;
        }

        private static string ReadHookName(CustomAttribute attr) {
            if (attr == null) return null;
            if (attr.HasFields) {
                foreach (var f in attr.Fields) {
                    if (f.Name == "hook")
                        return f.Argument.Value as string;
                }
            }
            return null;
        }

        /// <summary>
        /// Look for a hook method on <paramref name="type"/> or any ancestor. Accepts
        /// either <c>void Hook(T oldValue, T newValue)</c> or <c>void Hook()</c>.
        /// At each level the (T,T) form is preferred over the no-arg form because it
        /// carries more information; closer ancestors win over distant ones (matching
        /// C# overload resolution intuition).
        /// </summary>
        private MethodReference FindHookMethod(TypeDefinition type, string hookName, TypeReference fieldType) {
            string fieldFullName = fieldType.FullName;
            var current = type;
            while (current != null) {
                MethodDefinition twoArg = null;
                MethodDefinition zeroArg = null;
                foreach (var m in current.Methods) {
                    if (m.Name != hookName) continue;
                    if (m.IsStatic) continue;
                    if (m.HasGenericParameters) continue;
                    if (m.ReturnType.FullName != _refs.VoidType.FullName) continue;

                    if (m.Parameters.Count == 2 &&
                        m.Parameters[0].ParameterType.FullName == fieldFullName &&
                        m.Parameters[1].ParameterType.FullName == fieldFullName) {
                        if (twoArg == null) twoArg = m;
                    } else if (m.Parameters.Count == 0) {
                        if (zeroArg == null) zeroArg = m;
                    }
                }
                if (twoArg != null) return _refs.Module.ImportReference(twoArg);
                if (zeroArg != null) return _refs.Module.ImportReference(zeroArg);

                if (current.BaseType == null) break;
                try {
                    current = current.BaseType.Resolve();
                } catch {
                    break;
                }
            }
            return null;
        }

        /// <summary>
        /// True if the user has manually declared an override of (returnType=void)
        /// <paramref name="name"/>(firstParamFullName, bool) on this exact type. We use
        /// this to refuse weaving if the user already supplied OnSerialize/OnDeserialize
        /// since the weaver would clobber their code.
        /// </summary>
        private bool HasUserOverride(TypeDefinition type, string name, string firstParamFullName) {
            foreach (var m in type.Methods) {
                if (m.Name != name) continue;
                if (m.IsStatic) continue;
                if (m.Parameters.Count != 2) continue;
                if (m.Parameters[0].ParameterType.FullName != firstParamFullName) continue;
                if (m.Parameters[1].ParameterType.FullName != "System.Boolean") continue;
                return true;
            }
            return false;
        }

        // ----------------------------------------------------------------
        //  Field + setter generation
        // ----------------------------------------------------------------

        private FieldDefinition GenerateDirtyMaskField(TypeDefinition type) {
            var field = new FieldDefinition(
                DirtyMaskFieldName,
                FieldAttributes.Private,
                _refs.Module.TypeSystem.UInt64);
            type.Fields.Add(field);
            return field;
        }

        private MethodDefinition GenerateSetter(
            TypeDefinition type, SyncVarSlot slot, FieldDefinition dirtyMaskField, int bitIndex) {

            var setter = new MethodDefinition(
                SetterPrefix + slot.Field.Name,
                MethodAttributes.Private | MethodAttributes.HideBySig,
                _refs.VoidType);
            setter.Parameters.Add(new ParameterDefinition(
                "value", ParameterAttributes.None, slot.Field.FieldType));
            type.Methods.Add(setter);

            setter.Body.InitLocals = true;
            var il = setter.Body.GetILProcessor();

            // The early-out target. We jump here when (current == new) — nothing to do.
            var skipDirty = Instruction.Create(OpCodes.Ret);

            // Push (this.field == value) onto the stack as an int (1 = equal, 0 = not).
            EmitFieldEqualsValue(il, slot.Field);
            il.Emit(OpCodes.Brtrue, skipDirty);

            // Capture the old value into a local BEFORE we overwrite the field, so the
            // (T, T) hook form can receive it. Skipped for no-hook and no-arg-hook
            // setters to keep the IL minimal.
            VariableDefinition oldLocal = null;
            bool hookWantsOldNew =
                slot.HookMethod != null && slot.HookMethod.Parameters.Count == 2;
            if (hookWantsOldNew) {
                oldLocal = new VariableDefinition(slot.Field.FieldType);
                setter.Body.Variables.Add(oldLocal);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, slot.Field);
                il.Emit(OpCodes.Stloc, oldLocal);
            }

            // this.field = value;
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, slot.Field);

            // this.__segnetSyncVarDirty |= (1UL << bitIndex);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, dirtyMaskField);
            il.Emit(OpCodes.Ldc_I8, 1L << bitIndex);
            il.Emit(OpCodes.Or);
            il.Emit(OpCodes.Stfld, dirtyMaskField);

            // this.SetDirty();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, _refs.SetDirtyMethod);

            // Hook fires from the setter so the host (and dedicated server) sees the
            // change locally — the receiving-side OnDeserialize is short-circuited on
            // host via ServerManager's IsHost guard, so this is the host's only path.
            if (slot.HookMethod != null) {
                il.Emit(OpCodes.Ldarg_0);
                if (hookWantsOldNew) {
                    il.Emit(OpCodes.Ldloc, oldLocal);
                    il.Emit(OpCodes.Ldarg_1);
                }
                il.Emit(OpCodes.Callvirt, slot.HookMethod);
            }

            il.Append(skipDirty);
            return setter;
        }

        /// <summary>
        /// Push (this.field == argValue) on the stack as an int (1 if equal, 0 if not).
        /// Picks the right comparison strategy per type:
        ///   - primitives, enums:        ceq (intrinsic)
        ///   - System.String:            String.op_Equality
        ///   - Unity value structs:      type.op_Equality
        ///   - NetworkBehaviour / Player references: ceq (reference equality)
        ///   - last resort:              pop both, push 0 (always mark dirty)
        /// </summary>
        private void EmitFieldEqualsValue(ILProcessor il, FieldDefinition field) {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ldarg_1);

            var ft = field.FieldType;
            string fullName = ft.FullName;

            // Enums: underlying type is integer, ceq just works.
            if (ft.IsValueType) {
                var def = SafeResolve(ft);
                if (def != null && def.IsEnum) {
                    il.Emit(OpCodes.Ceq);
                    return;
                }
            }

            // Primitives — ceq is the cheapest correct comparison.
            switch (fullName) {
                case "System.Boolean":
                case "System.Byte":
                case "System.SByte":
                case "System.Int16":
                case "System.UInt16":
                case "System.Int32":
                case "System.UInt32":
                case "System.Int64":
                case "System.UInt64":
                case "System.Single":
                case "System.Double":
                    il.Emit(OpCodes.Ceq);
                    return;
            }

            // String: explicit op_Equality (ceq would be reference equality).
            if (fullName == "System.String") {
                var stringEq = GetOpEquality(ft);
                if (stringEq != null) {
                    il.Emit(OpCodes.Call, stringEq);
                    return;
                }
                // Falls through — should never happen, System.String always has op_Equality.
            }

            // Reference types — use ceq (reference equality). For NetworkBehaviour /
            // NetworkPlayer subclasses we deliberately avoid op_Equality, because
            // UnityEngine.Object overrides it with a destruction-aware comparison and
            // we want to detect "the user assigned a different reference" not
            // "Unity destroyed the previous one".
            if (!ft.IsValueType) {
                il.Emit(OpCodes.Ceq);
                return;
            }

            // Unity value structs (Vector2/3/4, Quaternion, Color, Color32, ...) all
            // expose a static op_Equality(T,T) → bool. Use it.
            var opEq = GetOpEquality(ft);
            if (opEq != null) {
                il.Emit(OpCodes.Call, opEq);
                return;
            }

            // Last resort: discard both and push false → setter always marks dirty.
            // This branch is unreachable for the SerializerMap-supported types, but
            // emitting safe IL keeps the body verifiable if a future type slips through.
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldc_I4_0);
        }

        /// <summary>
        /// Look up <c>static bool op_Equality(T, T)</c> on the given type via Cecil's
        /// resolver and import the MethodDefinition. Cached per FullName because we
        /// call this for every SyncVar of a given type. Returns null if absent.
        /// </summary>
        private MethodReference GetOpEquality(TypeReference type) {
            string key = type.FullName;
            if (_opEqualityCache.TryGetValue(key, out var cached))
                return cached;

            MethodReference imported = null;
            var def = SafeResolve(type);
            if (def != null) {
                foreach (var m in def.Methods) {
                    if (!m.IsStatic) continue;
                    if (m.Name != "op_Equality") continue;
                    if (m.Parameters.Count != 2) continue;
                    if (m.Parameters[0].ParameterType.FullName != key) continue;
                    if (m.Parameters[1].ParameterType.FullName != key) continue;
                    if (m.ReturnType.FullName != "System.Boolean") continue;
                    imported = _refs.Module.ImportReference(m);
                    break;
                }
            }

            _opEqualityCache[key] = imported;
            return imported;
        }

        // ----------------------------------------------------------------
        //  stfld → call setter rewrite
        // ----------------------------------------------------------------

        /// <summary>
        /// Replace every <c>stfld syncVarField</c> in the given method with a
        /// <c>call generatedSetter</c>. Both opcodes pop the same stack profile
        /// (instance + value), so an in-place Replace is sound.
        ///
        /// Skipped:
        ///   - constructors (.ctor / .cctor): field initializers and base ctor calls
        ///     should not mark dirty before the object is even spawned.
        ///   - the generated setter itself isn't in the snapshot, so it's never seen.
        /// </summary>
        private int RewriteFieldStores(
            MethodDefinition method, Dictionary<string, MethodDefinition> fieldNameToSetter) {

            if (!method.HasBody) return 0;
            if (method.IsConstructor) return 0;

            var il = method.Body.GetILProcessor();
            var instructions = method.Body.Instructions;

            // Collect work first to avoid mutating during iteration.
            List<(Instruction old, Instruction newInstr)> pending = null;
            foreach (var instr in instructions) {
                if (instr.OpCode != OpCodes.Stfld) continue;
                if (!(instr.Operand is FieldReference fr)) continue;
                if (!fieldNameToSetter.TryGetValue(fr.FullName, out var setter)) continue;

                var newInstr = il.Create(OpCodes.Call, setter);
                if (pending == null) pending = new List<(Instruction, Instruction)>();
                pending.Add((instr, newInstr));
            }

            if (pending == null) return 0;
            foreach (var (oldInstr, newInstr) in pending)
                il.Replace(oldInstr, newInstr);
            return pending.Count;
        }

        // ----------------------------------------------------------------
        //  OnSerialize / OnDeserialize generation
        // ----------------------------------------------------------------

        private MethodDefinition GenerateOnSerialize(
            TypeDefinition type, List<SyncVarSlot> slots, FieldDefinition dirtyMaskField) {

            var method = new MethodDefinition(
                "OnSerialize",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual,
                _refs.VoidType);
            method.Parameters.Add(new ParameterDefinition(
                "writer", ParameterAttributes.None, _refs.NetworkWriterType));
            method.Parameters.Add(new ParameterDefinition(
                "initialState", ParameterAttributes.None, _refs.Module.TypeSystem.Boolean));
            type.Methods.Add(method);

            method.Body.InitLocals = true;
            var il = method.Body.GetILProcessor();

            // base.OnSerialize(writer, initialState);
            // Walking up to the nearest defining ancestor finds (in inheritance order):
            //  a previously-woven generated override on a parent class, OR the empty
            //  virtual on NetworkBehaviour. Either way, the chain replicates inherited
            //  SyncVars before our own.
            var baseCall = FindNearestVirtual(type, "OnSerialize",
                _refs.NetworkWriterType.FullName, "System.Boolean");
            if (baseCall != null) {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Call, baseCall);
            }

            // if (initialState) goto initial; else goto delta;
            var deltaPath = Instruction.Create(OpCodes.Nop);
            var clearAndExit = Instruction.Create(OpCodes.Ldarg_0);

            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Brfalse, deltaPath);

            // ===== initial state path =====
            // Write every field unconditionally, no mask. After the spawn payload is
            // applied on the receiving side, the receiver's state matches ours, so any
            // bits we'd accumulated before spawn are stale and get cleared below.
            foreach (var slot in slots)
                EmitWriteField(il, slot);
            il.Emit(OpCodes.Br, clearAndExit);

            // ===== delta path =====
            il.Append(deltaPath);

            // writer.WriteULong(this.dirtyMask);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, dirtyMaskField);
            il.Emit(OpCodes.Callvirt, _writeUlong);

            for (int i = 0; i < slots.Count; i++) {
                var skip = Instruction.Create(OpCodes.Nop);
                // if ((this.dirtyMask & (1 << i)) == 0) skip;
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, dirtyMaskField);
                il.Emit(OpCodes.Ldc_I8, 1L << i);
                il.Emit(OpCodes.And);
                il.Emit(OpCodes.Brfalse, skip);

                EmitWriteField(il, slots[i]);

                il.Append(skip);
            }
            // Falls through to clearAndExit.

            // ===== clear + ret =====
            // Reset the per-class mask whether we took the initial or delta path. The
            // initial-state path needs this too: any bits set during construction (from
            // setters fired before IsSpawned was true) are now obsolete because the
            // receiving side just got the full state.
            // Emits: this.__segnetSyncVarDirty = 0L; ret
            il.Append(clearAndExit);                  // Ldarg_0
            il.Emit(OpCodes.Ldc_I8, 0L);
            il.Emit(OpCodes.Stfld, dirtyMaskField);
            il.Emit(OpCodes.Ret);

            return method;
        }

        private MethodDefinition GenerateOnDeserialize(TypeDefinition type, List<SyncVarSlot> slots) {

            var method = new MethodDefinition(
                "OnDeserialize",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual,
                _refs.VoidType);
            method.Parameters.Add(new ParameterDefinition(
                "reader", ParameterAttributes.None, _refs.NetworkReaderType));
            method.Parameters.Add(new ParameterDefinition(
                "initialState", ParameterAttributes.None, _refs.Module.TypeSystem.Boolean));
            type.Methods.Add(method);

            method.Body.InitLocals = true;
            var il = method.Body.GetILProcessor();

            // base.OnDeserialize(reader, initialState);
            var baseCall = FindNearestVirtual(type, "OnDeserialize",
                _refs.NetworkReaderType.FullName, "System.Boolean");
            if (baseCall != null) {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Call, baseCall);
            }

            var deltaPath = Instruction.Create(OpCodes.Nop);
            var endOfMethod = Instruction.Create(OpCodes.Ret);

            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Brfalse, deltaPath);

            // ===== initial state path =====
            // Read every field. Hooks fire too — clients want to react to initial values.
            foreach (var slot in slots)
                EmitReadAndApply(il, method, slot);
            il.Emit(OpCodes.Br, endOfMethod);

            // ===== delta path =====
            il.Append(deltaPath);

            var maskLocal = new VariableDefinition(_refs.Module.TypeSystem.UInt64);
            method.Body.Variables.Add(maskLocal);

            // ulong mask = reader.ReadULong();
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt, _readUlong);
            il.Emit(OpCodes.Stloc, maskLocal);

            for (int i = 0; i < slots.Count; i++) {
                var skip = Instruction.Create(OpCodes.Nop);

                il.Emit(OpCodes.Ldloc, maskLocal);
                il.Emit(OpCodes.Ldc_I8, 1L << i);
                il.Emit(OpCodes.And);
                il.Emit(OpCodes.Brfalse, skip);

                EmitReadAndApply(il, method, slots[i]);

                il.Append(skip);
            }

            il.Append(endOfMethod);
            return method;
        }

        private void EmitWriteField(ILProcessor il, SyncVarSlot slot) {
            // writer.WriteX(this.field);
            il.Emit(OpCodes.Ldarg_1);                  // writer
            il.Emit(OpCodes.Ldarg_0);                  // this
            il.Emit(OpCodes.Ldfld, slot.Field);
            il.Emit(OpCodes.Callvirt, slot.WriteMethod);
        }

        /// <summary>
        /// Emit:
        ///   T oldVal = this.field;          // only when the hook is the (T, T) form
        ///   T newVal = (T)reader.ReadX();   // castclass for NetworkBehaviour subclasses
        ///   this.field = newVal;            // direct stfld, NOT through the setter,
        ///                                   // so this never marks dirty on the receiver
        ///   if (hook != null) this.hook(oldVal, newVal);  // or this.hook() for no-arg form
        /// </summary>
        private void EmitReadAndApply(ILProcessor il, MethodDefinition owner, SyncVarSlot slot) {
            bool hookWantsOldNew =
                slot.HookMethod != null && slot.HookMethod.Parameters.Count == 2;

            VariableDefinition oldLocal = null;
            if (hookWantsOldNew) {
                oldLocal = new VariableDefinition(slot.Field.FieldType);
                owner.Body.Variables.Add(oldLocal);

                // oldVal = this.field;
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, slot.Field);
                il.Emit(OpCodes.Stloc, oldLocal);
            }

            var newLocal = new VariableDefinition(slot.Field.FieldType);
            owner.Body.Variables.Add(newLocal);

            // newVal = reader.ReadX(); [castclass if needed]
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt, slot.ReadMethod);
            if (slot.RequiresReadCast) {
                il.Emit(OpCodes.Castclass, slot.Field.FieldType);
            }
            il.Emit(OpCodes.Stloc, newLocal);

            // this.field = newVal;  (direct stfld; this method is generated, not in the
            // rewrite snapshot, so the rewrite pass never touches it.)
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldloc, newLocal);
            il.Emit(OpCodes.Stfld, slot.Field);

            // if (hook) this.hook(oldVal, newVal); / this.hook();
            if (slot.HookMethod != null) {
                il.Emit(OpCodes.Ldarg_0);
                if (hookWantsOldNew) {
                    il.Emit(OpCodes.Ldloc, oldLocal);
                    il.Emit(OpCodes.Ldloc, newLocal);
                }
                il.Emit(OpCodes.Callvirt, slot.HookMethod);
            }
        }

        // ----------------------------------------------------------------
        //  Cecil resolver helpers
        // ----------------------------------------------------------------

        /// <summary>
        /// Walk up the inheritance chain (starting from <paramref name="type"/>'s base)
        /// looking for the nearest ancestor that DEFINES a method matching the given
        /// signature. Returns the imported MethodReference. Used to emit non-virtual
        /// <c>base.OnSerialize</c> / <c>base.OnDeserialize</c> calls.
        ///
        /// We need the nearest *defining* ancestor (not just .BaseType) because IL
        /// `call` is non-virtual and the method token must point at a class that
        /// actually owns the method body.
        /// </summary>
        private MethodReference FindNearestVirtual(
            TypeDefinition type, string name, string firstParamFullName, string secondParamFullName) {

            TypeReference baseRef = type.BaseType;
            while (baseRef != null) {
                var baseDef = SafeResolve(baseRef);
                if (baseDef == null) return null;

                foreach (var m in baseDef.Methods) {
                    if (m.Name != name) continue;
                    if (m.IsStatic) continue;
                    if (m.Parameters.Count != 2) continue;
                    if (m.ReturnType.FullName != _refs.VoidType.FullName) continue;
                    if (m.Parameters[0].ParameterType.FullName != firstParamFullName) continue;
                    if (m.Parameters[1].ParameterType.FullName != secondParamFullName) continue;
                    return _refs.Module.ImportReference(m);
                }

                baseRef = baseDef.BaseType;
            }
            return null;
        }

        private static TypeDefinition SafeResolve(TypeReference type) {
            try {
                return type.Resolve();
            } catch {
                return null;
            }
        }

        // ----------------------------------------------------------------
        //  Diagnostics
        // ----------------------------------------------------------------

        private void Error(string msg) {
            _diagnostics.Add(new DiagnosticMessage {
                DiagnosticType = DiagnosticType.Error,
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
