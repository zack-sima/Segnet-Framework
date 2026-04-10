using System;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace SegNet.CodeGen {

    /// <summary>
    /// Small Cecil convenience helpers used by the SegNet IL weaver.
    ///
    /// The headline feature here is <see cref="CloneBodyTo"/>, which we use to lift the
    /// original IL of an [Rpc] method into a freshly created __SegNetRpcImpl_* method
    /// without disturbing existing in-assembly MethodReferences pointing at the original
    /// method (renaming would invalidate those).
    /// </summary>
    internal static class CecilExtensions {

        // ----------------------------------------------------------------
        //  Body cloning
        // ----------------------------------------------------------------

        /// <summary>
        /// Deep-clone the body of <paramref name="source"/> into <paramref name="dest"/>.
        ///
        /// Both methods are assumed to live in the same module and have an identical
        /// signature (return type + parameter types) — the weaver enforces that.
        ///
        /// Locals, exception handlers, and instructions are copied. Instruction operands
        /// that reference other instructions or locals/parameters of the source body are
        /// remapped to the corresponding entities in the destination body. After this
        /// returns, the destination is fully self-contained.
        /// </summary>
        public static void CloneBodyTo(this MethodDefinition source, MethodDefinition dest) {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (dest == null) throw new ArgumentNullException(nameof(dest));
            if (!source.HasBody)
                throw new InvalidOperationException(
                    $"Cannot clone body: source method '{source.FullName}' has no body.");

            MethodBody srcBody = source.Body;
            MethodBody dstBody = dest.Body;

            dstBody.MaxStackSize = srcBody.MaxStackSize;
            dstBody.InitLocals = srcBody.InitLocals;

            // ---- Locals ----
            var localMap = new Dictionary<VariableDefinition, VariableDefinition>(srcBody.Variables.Count);
            foreach (var v in srcBody.Variables) {
                var nv = new VariableDefinition(v.VariableType);
                dstBody.Variables.Add(nv);
                localMap[v] = nv;
            }

            // ---- Parameters ----
            // Source parameters at index i correspond to destination parameters at index i.
            // Cecil's Body.ThisParameter handles the implicit `this` slot for instance methods.
            var paramMap = new Dictionary<ParameterDefinition, ParameterDefinition>(source.Parameters.Count);
            for (int i = 0; i < source.Parameters.Count; i++)
                paramMap[source.Parameters[i]] = dest.Parameters[i];

            // ---- Instructions (first pass: clone with placeholder operands) ----
            var il = dstBody.GetILProcessor();
            var instrMap = new Dictionary<Instruction, Instruction>(srcBody.Instructions.Count);

            foreach (var src in srcBody.Instructions) {
                // Use a placeholder for instruction-targeting operands; we fix up in pass 2.
                Instruction clone;
                if (src.Operand == null) {
                    clone = il.Create(src.OpCode);
                } else if (src.Operand is Instruction) {
                    // Branch placeholder — fixed in pass 2.
                    clone = il.Create(src.OpCode, Instruction.Create(OpCodes.Nop));
                } else if (src.Operand is Instruction[] arr) {
                    // Switch placeholder — fixed in pass 2. Need to match the array length.
                    var placeholder = new Instruction[arr.Length];
                    for (int i = 0; i < placeholder.Length; i++)
                        placeholder[i] = Instruction.Create(OpCodes.Nop);
                    clone = il.Create(src.OpCode, placeholder);
                } else {
                    clone = CloneInstructionWithStableOperand(src, localMap, paramMap, dstBody);
                }
                clone.Offset = src.Offset;
                dstBody.Instructions.Add(clone);
                instrMap[src] = clone;
            }

            // ---- Instructions (second pass: fix branch / switch operands) ----
            for (int i = 0; i < srcBody.Instructions.Count; i++) {
                var src = srcBody.Instructions[i];
                var dst = dstBody.Instructions[i];

                switch (src.Operand) {
                    case Instruction targetInstr:
                        dst.Operand = instrMap[targetInstr];
                        break;
                    case Instruction[] targets:
                        var newTargets = new Instruction[targets.Length];
                        for (int t = 0; t < targets.Length; t++)
                            newTargets[t] = instrMap[targets[t]];
                        dst.Operand = newTargets;
                        break;
                }
            }

            // ---- Exception handlers ----
            foreach (var eh in srcBody.ExceptionHandlers) {
                var copy = new ExceptionHandler(eh.HandlerType) {
                    CatchType = eh.CatchType,
                    TryStart = eh.TryStart != null ? instrMap[eh.TryStart] : null,
                    TryEnd = eh.TryEnd != null ? instrMap[eh.TryEnd] : null,
                    HandlerStart = eh.HandlerStart != null ? instrMap[eh.HandlerStart] : null,
                    HandlerEnd = eh.HandlerEnd != null ? instrMap[eh.HandlerEnd] : null,
                    FilterStart = eh.FilterStart != null ? instrMap[eh.FilterStart] : null,
                };
                dstBody.ExceptionHandlers.Add(copy);
            }
        }

        /// <summary>
        /// Clone an instruction whose operand is *not* an Instruction or Instruction[]
        /// (those are fixed up in pass 2). Local- and parameter-bearing operands get
        /// remapped to the destination body's locals/parameters.
        /// </summary>
        private static Instruction CloneInstructionWithStableOperand(
            Instruction src,
            Dictionary<VariableDefinition, VariableDefinition> localMap,
            Dictionary<ParameterDefinition, ParameterDefinition> paramMap,
            MethodBody dstBody) {

            var il = dstBody.GetILProcessor();

            switch (src.Operand) {
                case VariableDefinition v:
                    return il.Create(src.OpCode, localMap[v]);
                case ParameterDefinition p:
                    // `this` parameter is special — it's not in the parameters list.
                    if (paramMap.TryGetValue(p, out var mapped))
                        return il.Create(src.OpCode, mapped);
                    return il.Create(src.OpCode, dstBody.ThisParameter);
                case TypeReference tr:
                    return il.Create(src.OpCode, tr);
                case MethodReference mr:
                    return il.Create(src.OpCode, mr);
                case FieldReference fr:
                    return il.Create(src.OpCode, fr);
                case string s:
                    return il.Create(src.OpCode, s);
                case sbyte sb:
                    return il.Create(src.OpCode, sb);
                case byte b:
                    return il.Create(src.OpCode, b);
                case int i:
                    return il.Create(src.OpCode, i);
                case long l:
                    return il.Create(src.OpCode, l);
                case float f:
                    return il.Create(src.OpCode, f);
                case double d:
                    return il.Create(src.OpCode, d);
                case CallSite cs:
                    return il.Create(src.OpCode, cs);
                default:
                    // Instruction / Instruction[] are routed to dedicated placeholder paths
                    // in CloneBodyTo and never reach this method. Anything else is unknown.
                    throw new NotSupportedException(
                        $"CloneBodyTo: unsupported operand type '{src.Operand.GetType().FullName}' " +
                        $"on instruction '{src.OpCode.Name}'.");
            }
        }

        // ----------------------------------------------------------------
        //  Misc helpers
        // ----------------------------------------------------------------

        /// <summary>
        /// True if any custom attribute on the provider has a matching full name.
        /// </summary>
        public static bool HasAttributeOfType(this ICustomAttributeProvider provider, string fullName) {
            if (provider == null || !provider.HasCustomAttributes) return false;
            for (int i = 0; i < provider.CustomAttributes.Count; i++) {
                if (provider.CustomAttributes[i].AttributeType.FullName == fullName)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Get the first custom attribute with a matching full name, or null.
        /// </summary>
        public static CustomAttribute GetAttributeOfType(this ICustomAttributeProvider provider, string fullName) {
            if (provider == null || !provider.HasCustomAttributes) return null;
            for (int i = 0; i < provider.CustomAttributes.Count; i++) {
                var a = provider.CustomAttributes[i];
                if (a.AttributeType.FullName == fullName)
                    return a;
            }
            return null;
        }
    }
}
