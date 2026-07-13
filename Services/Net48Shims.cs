#if NETFRAMEWORK
// Shims that let the net8-era codebase compile for Revit 2023/2024
// (.NET Framework 4.8). Compiled out entirely on net8.0/net10.0-windows.
using System.IO;
using System.Reflection;

namespace System.Runtime.CompilerServices
{
    /// <summary>C# 9 record/init-only marker type — in-box on .NET 5+, absent
    /// on .NET Framework. Declaring it internally is the documented shim
    /// (fixes every CS0518 across the assembly).</summary>
    internal static class IsExternalInit { }
}

namespace RevitWebAppSync.Services
{
    /// <summary>net48 stand-in for System.Runtime.Loader.AssemblyLoadContext
    /// (CodeExecutor). Loads the compiled snippet into the AppDomain, never
    /// unloadable — the classic pre-Core Revit addin behavior: one dynamic
    /// assembly per generated snippet lives until Revit exits, bounded by
    /// snippets per session.</summary>
    internal sealed class AssemblyLoadContext
    {
        public AssemblyLoadContext(string name, bool isCollectible) { }

        public Assembly LoadFromStream(MemoryStream ms) => Assembly.Load(ms.ToArray());

        public void Unload() { /* not supported on .NET Framework */ }
    }
}
#endif
