// Compiled ONLY by the macOS harness (lh/harness/addin-tests-mac.py), never by Tests.csproj
// on Windows (it removes MacShim\**): the harness builds the tests without WPF, and the JKR
// pane's view-models name a handful of WPF types in brightness-only members (brushes, the
// toast timer). These empty stand-ins let IssueVm, JkrTheme, PanelVm and everything built
// on them compile and run on the Mac, so the pane's models are tested there too.
// Nothing here draws or ticks: Application.Current stays null, so brushes are transparent.
using System;
using System.Collections.Generic;

namespace System.Windows
{
    public enum ShutdownMode { OnLastWindowClose, OnMainWindowClose, OnExplicitShutdown }

    public class ResourceDictionary
    {
        public Uri Source { get; set; }
        public List<ResourceDictionary> MergedDictionaries { get; } = new List<ResourceDictionary>();
    }

    public class Application
    {
        public static Application Current => null;
        public ShutdownMode ShutdownMode { get; set; }
        public ResourceDictionary Resources { get; } = new ResourceDictionary();
        public object TryFindResource(object key) => null;
    }

    public class TextDecorationCollection { }

    public static class TextDecorations
    {
        public static TextDecorationCollection Strikethrough { get; } = new TextDecorationCollection();
    }
}

namespace System.Windows.Media
{
    public class Brush { }

    public static class Brushes
    {
        public static Brush Transparent { get; } = new Brush();
    }
}

namespace System.Windows.Data { }

namespace System.Windows.Threading
{
    public class DispatcherTimer
    {
        public TimeSpan Interval { get; set; }
        public event EventHandler Tick;
        public void Start() { }
        public void Stop() { }
    }
}
