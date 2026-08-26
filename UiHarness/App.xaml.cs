using System.IO;
using System.Runtime.Loader;
using System.Windows;

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

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Headless render mode for review/CI: `UiHarness --shot <dir>` writes
            // PNGs of the Copilot panel (light + dark + rate sheet) and exits,
            // so UI changes can be eyeballed without a desktop session. No arg =
            // the normal interactive launcher.
            if (e.Args.Length >= 1 && e.Args[0] == "--shot")
            {
                // Each capture opens+closes its own window; without this the first
                // close would trigger OnLastWindowClose shutdown mid-run.
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var dir = e.Args.Length >= 2 ? e.Args[1] : System.AppContext.BaseDirectory;
                HarnessShots.Capture(dir);
                Shutdown();
                return;
            }

            // Same idea for the WIP browser: `UiHarness --shot-wip <dir>` renders
            // it against the stub backend (folders, models, versions, the 403
            // folder and the browse-only row) and exits.
            if (e.Args.Length >= 1 && e.Args[0] == "--shot-wip")
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var wipDir = e.Args.Length >= 2 ? e.Args[1] : System.AppContext.BaseDirectory;
                WipBrowseShots.Capture(wipDir);
                Shutdown();
                return;
            }

            // And the JKR Audit Copilot panel: `UiHarness --shot-jkr <dir>` renders
            // every screen (S1/S2/S3/S4-detail/S5/S6) against FixtureCopilotSource
            // so the build can be diffed 1:1 against the Claude Design canvas.
            if (e.Args.Length >= 1 && e.Args[0] == "--shot-jkr")
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var jkrDir = e.Args.Length >= 2 ? e.Args[1] : System.AppContext.BaseDirectory;
                JkrShots.Capture(jkrDir);
                Shutdown();
                return;
            }

            new LauncherWindow().Show();
        }
    }
}
