using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Autodesk.Revit.DB
{
    /// <summary>ElementId factory bridging the ctor split: Revit 2023/2024
    /// refs expose ElementId(int), 2025+ expose ElementId(long). Call sites
    /// holding a long (JSON-parsed ids) route through here so one source
    /// compiles against both. On the net48 payload the narrowing cast is
    /// safe: 2023/2024 hosts mint int-range ids.</summary>
    public static class ElemIds
    {
        public static ElementId From(long id) =>
#if NETFRAMEWORK
            new ElementId((int)id);
#else
            new ElementId(id);
#endif
    }
}

namespace RevitWebAppSync.Services
{
    /// <summary>BCL members absent on .NET Framework 4.8, bridged so call
    /// sites stay single-source across net48/net8/net10 (Revit 2023-2027).
    /// Modern TFMs delegate to the real APIs.</summary>
    internal static class RuntimeCompat
    {
        public static string ToHexString(byte[] bytes) =>
#if NETFRAMEWORK
            BitConverter.ToString(bytes).Replace("-", "");
#else
            Convert.ToHexString(bytes);
#endif

        public static double Clamp(double value, double min, double max) =>
#if NETFRAMEWORK
            value < min ? min : value > max ? max : value;
#else
            Math.Clamp(value, min, max);
#endif

        public static int Clamp(int value, int min, int max) =>
#if NETFRAMEWORK
            value < min ? min : value > max ? max : value;
#else
            Math.Clamp(value, min, max);
#endif

        public static byte[] Sha256(byte[] data)
        {
#if NETFRAMEWORK
            using var sha = SHA256.Create();
            return sha.ComputeHash(data);
#else
            return SHA256.HashData(data);
#endif
        }

        public static async Task<byte[]> Sha256Async(Stream stream)
        {
#if NETFRAMEWORK
            using var sha = SHA256.Create();
            return await Task.Run(() => sha.ComputeHash(stream));
#else
            return await SHA256.HashDataAsync(stream);
#endif
        }

        public static Task<byte[]> ReadAllBytesAsync(string path) =>
#if NETFRAMEWORK
            Task.Run(() => File.ReadAllBytes(path));
#else
            File.ReadAllBytesAsync(path);
#endif

        public static Task WriteAllBytesAsync(string path, byte[] bytes) =>
#if NETFRAMEWORK
            Task.Run(() => File.WriteAllBytes(path, bytes));
#else
            File.WriteAllBytesAsync(path, bytes);
#endif

        /// <summary>Kill a process AND its children. The engine launcher is
        /// run-engine.cmd spawning python.exe — killing only the cmd would
        /// orphan the python (net48 Process.Kill has no entireProcessTree).</summary>
        public static void KillTree(System.Diagnostics.Process p)
        {
#if NETFRAMEWORK
            try
            {
                using var killer = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/PID {p.Id} /T /F",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
                killer?.WaitForExit(5000);
            }
            catch { /* fall through to direct kill */ }
            try { if (!p.HasExited) p.Kill(); } catch { }
#else
            p.Kill(entireProcessTree: true);
#endif
        }
    }
}
