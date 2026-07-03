using System.IO;
using System.Runtime.Loader;

namespace UiHarness
{
    public partial class App : System.Windows.Application
    {
        static App()
        {
            // The RevitApiStubs DLLs are copied next to the exe as loose files,
            // so they are NOT in UiHarness.deps.json — and the .NET host only
            // binds assemblies listed there. Resolve them from the app folder
            // ourselves, or opening any window whose ctor touches Revit types
            // dies with FileNotFoundException 'RevitAPIUI'.
            AssemblyLoadContext.Default.Resolving += (ctx, name) =>
            {
                var candidate = Path.Combine(System.AppContext.BaseDirectory, name.Name + ".dll");
                return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
            };
        }
    }
}
