#nullable enable
// The metadata references run_revit_code compiles against. Revit-free.
//
// Deliberately narrow (spec §7.8 "allowed assemblies"): the BCL assemblies the
// policy lists, resolved from the running framework, plus the Revit API
// assemblies the caller passes (the loaded RevitAPI/RevitAPIUI in Revit; the
// Nice3point reference assemblies in tests). Nothing from the add-in or any
// third party is referenced, so most denied types do not even bind; CodePolicy
// still checks everything that does.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;

namespace BinaVibe.Copilot.Code
{
    public static class CodeReferences
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, IReadOnlyList<MetadataReference>> Cache =
            new Dictionary<string, IReadOnlyList<MetadataReference>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>BCL references from <see cref="CodePolicyData.References"/> plus the given Revit API files.
        /// Cached: building references reads files from disk, and the set never changes in a session.</summary>
        public static IReadOnlyList<MetadataReference> Build(IEnumerable<string> revitAssemblyPaths, CodePolicySet? policy = null)
        {
            policy ??= CodePolicySet.Default;
            var revit = revitAssemblyPaths.Where(p => !string.IsNullOrEmpty(p)).ToList();
            var key = string.Join("|", revit) + "#" + string.Join("|", policy.Data.References);
            lock (Gate)
            {
                if (Cache.TryGetValue(key, out var cached)) return cached;
                var built = BuildUncached(revit, policy);
                Cache[key] = built;
                return built;
            }
        }

        /// <summary>Simple name to file for every BCL assembly the policy names that this runtime has.</summary>
        public static IReadOnlyDictionary<string, string> BclFiles(CodePolicySet? policy = null)
        {
            policy ??= CodePolicySet.Default;
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var wanted = new HashSet<string>(policy.Data.References, StringComparer.OrdinalIgnoreCase);

            // Make sure the collection and LINQ assemblies are loaded, then take what is loaded.
            _ = typeof(Enumerable).Assembly;
            _ = typeof(Queue<>).Assembly;
            _ = typeof(HashSet<>).Assembly;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (asm.IsDynamic || string.IsNullOrEmpty(asm.Location)) continue;
                    var name = asm.GetName().Name;
                    if (name != null && wanted.Contains(name) && !found.ContainsKey(name)) found[name] = asm.Location;
                }
                catch
                {
                    // An assembly whose name or location can't be read is not one we want.
                }
            }

            // Facades (System.Runtime, netstandard, mscorlib on .NET) sit next to CoreLib.
            var dir = Path.GetDirectoryName(typeof(object).Assembly.Location);
            if (!string.IsNullOrEmpty(dir))
                foreach (var name in wanted)
                {
                    if (found.ContainsKey(name)) continue;
                    var file = Path.Combine(dir!, name + ".dll");
                    if (File.Exists(file)) found[name] = file;
                }
            return found;
        }

        private static IReadOnlyList<MetadataReference> BuildUncached(IReadOnlyList<string> revit, CodePolicySet policy)
        {
            var refs = new List<MetadataReference>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in BclFiles(policy).OrderBy(k => k.Key, StringComparer.Ordinal))
                if (names.Add(kv.Key)) refs.Add(MetadataReference.CreateFromFile(kv.Value));
            foreach (var path in revit)
            {
                // One reference per simple name, or Roslyn stops with CS1703.
                if (names.Add(Path.GetFileNameWithoutExtension(path))) refs.Add(MetadataReference.CreateFromFile(path));
            }
            return refs;
        }
    }
}
