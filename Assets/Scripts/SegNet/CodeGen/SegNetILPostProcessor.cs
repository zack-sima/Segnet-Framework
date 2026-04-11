using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Unity.CompilationPipeline.Common.Diagnostics;
using Unity.CompilationPipeline.Common.ILPostProcessing;

// Mono.Cecil.Cil pulled in for PortablePdb{Reader,Writer}Provider used in symbol I/O.

namespace SegNet.CodeGen {

    /// <summary>
    /// IL post-processor for SegNet.
    ///
    /// For every assembly that references SegNet.Runtime, walks all NetworkBehaviour
    /// subclasses and applies:
    ///   - RPC weaving: every [Rpc] method gets its body replaced with a serialize+send
    ///     wrapper, plus a generated impl method (the original body) and a static dispatch
    ///     handler. Each type with RPCs also gets a [RuntimeInitializeOnLoadMethod]
    ///     registration method that wires the dispatch handlers into RpcRegistry at startup.
    ///   - SyncVar weaving: every [SyncVar] field gets a generated dirty-tracking setter,
    ///     all stfld instructions to that field outside of constructors are rewritten to
    ///     call the setter, and the type gets generated OnSerialize/OnDeserialize overrides
    ///     that handle initial-state and delta replication for the [SyncVar] fields.
    ///
    /// Types are processed base-to-derived so that SyncVarWeaver's generated OnSerialize
    /// override on a derived class can find (and emit a non-virtual base call to) the
    /// override the weaver just generated on its parent class.
    ///
    /// On any validation error, an Error diagnostic is reported and that specific method/
    /// field is left untouched. The rest of the assembly is still woven.
    /// </summary>
    public class SegNetILPostProcessor : ILPostProcessor {

        private const string RuntimeAssemblyName = "SegNet.Runtime";
        private const string RpcAttrName = "SegNet.RpcAttribute";
        private const string NetworkBehaviourName = "SegNet.NetworkBehaviour";

        public override ILPostProcessor GetInstance() => this;

        // ----------------------------------------------------------------
        //  Filter: only process assemblies that reference our runtime
        // ----------------------------------------------------------------

        public override bool WillProcess(ICompiledAssembly compiledAssembly) {
            // Never process the runtime assembly itself
            if (compiledAssembly.Name == RuntimeAssemblyName)
                return false;

            return compiledAssembly.References
                .Any(r => Path.GetFileNameWithoutExtension(r) == RuntimeAssemblyName);
        }

        // ----------------------------------------------------------------
        //  Main entry: load → scan → (future: weave) → write back
        // ----------------------------------------------------------------

        public override ILPostProcessResult Process(ICompiledAssembly compiledAssembly) {
            if (!WillProcess(compiledAssembly))
                return null;

            var diagnostics = new List<DiagnosticMessage>();

            AssemblyDefinition assemblyDef = LoadAssembly(compiledAssembly, diagnostics);
            if (assemblyDef == null)
                return new ILPostProcessResult(null, diagnostics);

            try {
                bool changed = WeaveAssembly(assemblyDef, diagnostics);

                // If nothing changed, return null so Unity reuses the original assembly bytes.
                if (!changed)
                    return new ILPostProcessResult(null, diagnostics);

                // --- Write assembly back ---

                var pe = new MemoryStream();
                var pdb = new MemoryStream();

                var writerParams = new WriterParameters();
                bool hasSymbols = compiledAssembly.InMemoryAssembly.PdbData != null
                                  && compiledAssembly.InMemoryAssembly.PdbData.Length > 0;
                if (hasSymbols) {
                    writerParams.SymbolWriterProvider = new PortablePdbWriterProvider();
                    writerParams.WriteSymbols = true;
                    writerParams.SymbolStream = pdb;
                }

                assemblyDef.Write(pe, writerParams);

                return new ILPostProcessResult(
                    new InMemoryAssembly(pe.ToArray(), pdb.ToArray()),
                    diagnostics);
            } finally {
                assemblyDef.Dispose();
            }
        }

        // ----------------------------------------------------------------
        //  Weave: per module, find NetworkBehaviours and weave their RPCs
        // ----------------------------------------------------------------

        private bool WeaveAssembly(AssemblyDefinition assembly, List<DiagnosticMessage> diagnostics) {
            bool changed = false;

            foreach (var module in assembly.Modules) {
                // One serializer map + ref cache per module — they import into module-local metadata.
                var refs = new RuntimeRefs(module);
                var serializers = new SerializerMap(module);
                var rpcWeaver = new RpcWeaver(refs, serializers, diagnostics);
                var syncVarWeaver = new SyncVarWeaver(refs, serializers, diagnostics);

                // Collect all NetworkBehaviour subclasses, then sort base-to-derived.
                // SyncVarWeaver's generated OnSerialize/OnDeserialize emit non-virtual
                // base calls that resolve via Cecil.Resolve() at weave time, so a parent
                // class must already have its woven override in place when we process
                // a derived class. Sorting by inheritance depth guarantees this for any
                // types in the same module.
                var networkBehaviours = new List<TypeDefinition>();
                foreach (var type in GetAllTypes(module)) {
                    if (DerivesFromNetworkBehaviour(type))
                        networkBehaviours.Add(type);
                }
                networkBehaviours.Sort((a, b) =>
                    InheritanceDepth(a).CompareTo(InheritanceDepth(b)));

                foreach (var type in networkBehaviours) {
                    bool typeChanged = false;

                    // ---- RPC weaving ----
                    rpcWeaver.BeginType();

                    // Snapshot the method list — WeaveRpc adds new methods (impl + dispatch).
                    var methodSnapshot = type.Methods.ToList();
                    foreach (var method in methodSnapshot) {
                        if (!HasAttribute(method, RpcAttrName))
                            continue;
                        if (rpcWeaver.WeaveRpc(type, method))
                            typeChanged = true;
                    }

                    if (typeChanged) {
                        rpcWeaver.EmitRegistrationMethod(type);
                    }

                    // ---- SyncVar weaving ----
                    // Runs after RPC weaving so the stfld-rewrite pass also processes
                    // any [Rpc] impl methods that were just cloned out (the original
                    // user code might assign to a [SyncVar] inside an RPC body).
                    if (syncVarWeaver.WeaveType(type))
                        typeChanged = true;

                    if (typeChanged)
                        changed = true;
                }
            }

            return changed;
        }

        /// <summary>
        /// Walk a type's BaseType chain counting hops. NetworkBehaviour itself returns
        /// 0, direct subclasses 1, and so on. Used purely for inheritance-order sort.
        /// Returns int.MaxValue on resolve failure so unresolvable types sink to the
        /// bottom (they'll likely fail elsewhere too).
        /// </summary>
        private static int InheritanceDepth(TypeDefinition type) {
            int depth = 0;
            TypeReference current = type.BaseType;
            while (current != null) {
                if (current.FullName == NetworkBehaviourName)
                    return depth;
                try {
                    var def = current.Resolve();
                    if (def == null) return int.MaxValue;
                    current = def.BaseType;
                } catch {
                    return int.MaxValue;
                }
                depth++;
            }
            return int.MaxValue;
        }

        // ----------------------------------------------------------------
        //  Cecil helpers
        // ----------------------------------------------------------------

        /// <summary>Walk the inheritance chain to check for NetworkBehaviour.</summary>
        private static bool DerivesFromNetworkBehaviour(TypeDefinition type) {
            var current = type.BaseType;
            while (current != null) {
                if (current.FullName == NetworkBehaviourName)
                    return true;
                try {
                    current = current.Resolve()?.BaseType;
                } catch {
                    // Could not resolve (external assembly, etc.)
                    break;
                }
            }
            return false;
        }

        private static bool HasAttribute(ICustomAttributeProvider provider, string fullName) {
            if (!provider.HasCustomAttributes) return false;
            return provider.CustomAttributes
                .Any(a => a.AttributeType.FullName == fullName);
        }

        /// <summary>Iterate all types in a module, including nested types.</summary>
        private static IEnumerable<TypeDefinition> GetAllTypes(ModuleDefinition module) {
            foreach (var type in module.Types) {
                yield return type;
                foreach (var nested in GetNestedTypes(type))
                    yield return nested;
            }
        }

        private static IEnumerable<TypeDefinition> GetNestedTypes(TypeDefinition type) {
            foreach (var nested in type.NestedTypes) {
                yield return nested;
                foreach (var deeper in GetNestedTypes(nested))
                    yield return deeper;
            }
        }

        // ----------------------------------------------------------------
        //  Assembly loading
        // ----------------------------------------------------------------

        private static AssemblyDefinition LoadAssembly(
            ICompiledAssembly compiledAssembly,
            List<DiagnosticMessage> diagnostics) {

            var resolver = new DefaultAssemblyResolver();
            var searchDirs = new HashSet<string>();

            foreach (var reference in compiledAssembly.References) {
                var dir = Path.GetDirectoryName(reference);
                if (!string.IsNullOrEmpty(dir) && searchDirs.Add(dir))
                    resolver.AddSearchDirectory(dir);
            }

            var readerParams = new ReaderParameters {
                InMemory = true,
                AssemblyResolver = resolver,
                ReadingMode = ReadingMode.Immediate,
            };

            bool hasSymbols = compiledAssembly.InMemoryAssembly.PdbData != null
                              && compiledAssembly.InMemoryAssembly.PdbData.Length > 0;
            if (hasSymbols) {
                readerParams.SymbolReaderProvider = new PortablePdbReaderProvider();
                readerParams.ReadSymbols = true;
                readerParams.SymbolStream =
                    new MemoryStream(compiledAssembly.InMemoryAssembly.PdbData);
            }

            try {
                var peStream = new MemoryStream(compiledAssembly.InMemoryAssembly.PeData);
                return AssemblyDefinition.ReadAssembly(peStream, readerParams);
            } catch (System.Exception ex) {
                diagnostics.Add(new DiagnosticMessage {
                    DiagnosticType = DiagnosticType.Error,
                    MessageData = $"[SegNet ILPP] Failed to load assembly " +
                                  $"'{compiledAssembly.Name}': {ex.Message}"
                });
                return null;
            }
        }

    }
}
