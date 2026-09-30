using System;
using System.IO;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Anonymous per-install id (OTA self-heal F5): a random GUID kept in
    /// &lt;root&gt;\telemetry.id. Joins telemetry, feed requests and diagnostics
    /// uploads from one machine without naming it. Revit-free; never throws.
    /// </summary>
    public static class InstallIdentity
    {
        public static string ReadOrCreate(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var existing = File.ReadAllText(path).Trim();
                    if (Guid.TryParse(existing, out var g)) return g.ToString("D");
                }
            }
            catch { }

            var id = Guid.NewGuid().ToString("D");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, id);
            }
            catch { /* unwritable: still report an id for this session */ }
            return id;
        }
    }
}
