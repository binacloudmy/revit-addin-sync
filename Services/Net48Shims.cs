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

namespace Autodesk.Revit.DB
{
    /// <summary>Revit 2024 renamed ElementId's payload: <c>IntegerValue</c>
    /// (int) became <c>Value</c> (long). The codebase is written against the
    /// 2024+ shape; this C#-14 extension property supplies <c>.Value</c> on
    /// the 2023 refs. Declared in the Autodesk.Revit.DB namespace so every
    /// file that uses ElementId already has it in scope. Returns INT (2023's
    /// native width): it widens implicitly wherever the code expects long,
    /// and keeps <c>new ElementId(x.Value)</c> binding to 2023's int ctor.</summary>
    public static class ElementIdCompatExtensions
    {
        extension(ElementId id)
        {
            public int Value => id.IntegerValue;
        }
    }
}

namespace System.Collections.Generic
{
    /// <summary>net48 lacks CollectionExtensions.GetValueOrDefault and the
    /// KeyValuePair.Deconstruct that `foreach (var (k, v) in dict)` needs.</summary>
    internal static class DictionaryCompatExtensions
    {
        public static void Deconstruct<TKey, TValue>(
            this KeyValuePair<TKey, TValue> kv, out TKey key, out TValue value)
        {
            key = kv.Key;
            value = kv.Value;
        }

        public static TValue GetValueOrDefault<TKey, TValue>(
            this Dictionary<TKey, TValue> d, TKey key)
            => d.TryGetValue(key, out var v) ? v : default;

        public static TValue GetValueOrDefault<TKey, TValue>(
            this Dictionary<TKey, TValue> d, TKey key, TValue fallback)
            => d.TryGetValue(key, out var v) ? v : fallback;
    }
}
#endif
