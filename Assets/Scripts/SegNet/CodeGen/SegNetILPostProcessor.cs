using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Unity.CompilationPipeline.Common.Diagnostics;
using Unity.CompilationPipeline.Common.ILPostProcessing;

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
    ///   - SyncVar weaving: TODO (next milestone).
    ///
    /// On any validation error, an Error diagnostic is reported and that specific method/
    /// field is left untouched. The rest of the assembly is still woven.
    /// </summary>
    public class SegNetILPostProcessor : ILPostProcessor {

        private const string RuntimeAssemblyName = "SegNet.Runtime";
        private const string SyncVarAttrName = "SegNet.SyncVarAttribute";
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

                foreach (var type in GetAllTypes(module)) {
                    if (!DerivesFromNetworkBehaviour(type))
                        continue;

                    rpcWeaver.BeginType();
                    bool typeChanged = false;

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
                        changed = true;
                    }

                    // [SyncVar] fields — milestone 4, scan-only for now.
                    foreach (var field in type.Fields) {
                        if (!HasAttribute(field, SyncVarAttrName))
                            continue;
                        Info(diagnostics,
                            $"[SegNet ILPP] [SyncVar] (not yet woven) {type.FullName}.{field.Name}");
                    }
                }
            }

            return changed;
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

        // ----------------------------------------------------------------
        //  Diagnostic helpers
        // ----------------------------------------------------------------

        private static void Info(List<DiagnosticMessage> diags, string message) {
            diags.Add(new DiagnosticMessage {
                DiagnosticType = DiagnosticType.Warning, // Warning so it shows in Console
                MessageData = message
            });
        }
    }
}
