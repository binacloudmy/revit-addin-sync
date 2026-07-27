using System;
using System.Windows;

namespace RevitWebAppSync.Helpers
{
    /// <summary>
    /// Guarantees a live <see cref="Application"/> so the design-system dictionaries
    /// have somewhere to merge into.
    ///
    /// Revit hosts WPF without ever constructing a System.Windows.Application, so
    /// Application.Current is null in an add-in unless some other add-in happened to
    /// create one first. Both theme loaders used to bail out on that null and leave
    /// their dictionaries unmerged — the panel then parsed fine right up to the first
    /// `{StaticResource Cp.*}`, which threw XamlParseException ("Cannot find resource
    /// named 'Cp.SlimScrollBar'") on the UI thread and took Revit down with it
    /// (Revit 2024, 2026-07-23). Whether the null showed up was pure luck of which
    /// add-ins loaded alongside us, which is why the same build survived elsewhere.
    ///
    /// Creating the Application is enough — Run() is never called, and
    /// OnExplicitShutdown keeps closing one of our windows from shutting down the
    /// instance (and with it Revit's own WPF plumbing).
    /// </summary>
    internal static class WpfAppBootstrap
    {
        /// <summary>True when an Application exists after the call. Must run on the
        /// UI thread — every caller is a panel/pane constructor, which is.</summary>
        public static bool Ensure()
        {
            if (Application.Current != null) return true;

            try
            {
                // The ctor alone publishes the instance as Application.Current.
                new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            }
            catch (Exception ex)
            {
                // Racing with a host or another add-in that created one between the
                // check and here is benign — Current is set either way.
                System.Diagnostics.Debug.WriteLine($"[BINA] WpfAppBootstrap failed: {ex.Message}");
            }

            return Application.Current != null;
        }
    }
}
