// AI-647: which engine reads a DWG. dwgReader (config.json "cadToBim" section):
//   auto      ODA when it is installed, else ACadSharp. Not installed = silent, identical output.
//             Installed but broken, or too little memory = ACadSharp plus one note.
//   oda       ODA or an error, never a silent fallback.
//   acadsharp never touches ODA.
// ODA lives outside the OTA versions folder: %LocalAppData%\Bina\RevitSync\oda\27.10\{bin\, oda.key},
// overridable with odaDir / odaKeyFile.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using BinaVibe.Copilot.Writes;

namespace RevitWebAppSync.Services.CadToBim.Dwg
{
    public static class DwgReaders
    {
        public static string DefaultOdaDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bina", "RevitSync", "oda", "27.10");

        // shortcut: provisional until the AI-647 harness measures (peak - before) x 1.5 per engine; replace with the measured figures.
        internal static long OdaNeedBytes = 1536L << 20;
        internal static long AcadSharpNeedBytes = 1024L << 20;

        private static readonly AcadSharpDwgReader AcadSharp = new AcadSharpDwgReader();

        public static DwgDrawing Read(string path, CadToBimSettings settings)
        {
            settings ??= new CadToBimSettings();
            string dir = string.IsNullOrWhiteSpace(settings.OdaDir) ? DefaultOdaDir : settings.OdaDir;
            string key = string.IsNullOrWhiteSpace(settings.OdaKeyFile) ? Path.Combine(dir, "oda.key") : settings.OdaKeyFile;
            bool present = Directory.Exists(Path.Combine(dir, "bin")) && File.Exists(key);
#if HAS_ODA
            Func<IDwgReader> oda = () => new OdaDwgReader(Path.Combine(dir, "bin"), key);
#else
            Func<IDwgReader> oda = null;
            present = false;
#endif
            return Read(path, settings.DwgReader, present, oda, AcadSharp, FreeBytes);
        }

        /// <summary>The selection itself, with every dependency passed in (tests drive it with fakes).</summary>
        internal static DwgDrawing Read(string path, string mode, bool odaPresent, Func<IDwgReader> oda,
                                        IDwgReader acadSharp, Func<long> freeBytes)
        {
            mode = (mode ?? "auto").Trim().ToLowerInvariant();
            if (mode == "acadsharp") return acadSharp.Read(path);
            bool forced = mode == "oda";

            if (!odaPresent || oda == null)
            {
                if (forced) throw new InvalidOperationException(oda == null
                    ? "dwgReader is \"oda\" but this build has no ODA reader."
                    : "dwgReader is \"oda\" but ODA isn't installed (no bin folder or oda.key).");
                return acadSharp.Read(path);
            }

            long free = freeBytes();
            if (free < OdaNeedBytes)
            {
                string memory = "only " + Gb(free) + " GB of memory free, ODA needs " + Gb(OdaNeedBytes) + " GB";
                if (forced || free < AcadSharpNeedBytes)
                    throw new InsufficientMemoryException("Not enough free memory to read this drawing (" + Gb(free)
                        + " GB free). Close other programs and try again.");
                return Fallback(acadSharp, path, memory);
            }

            try
            {
                return oda().Read(path);
            }
            catch (Exception ex) when (!forced && !(ex is OutOfMemoryException))
            {
                return Fallback(acadSharp, path, ex.Message);
            }
        }

        public static string FallbackNote(string reason) =>
            "Read with the built-in reader: ODA isn't set up correctly (" + DialogRedaction.RedactPaths(reason)
            + "). Proxy and xref handling may differ.";

        private static DwgDrawing Fallback(IDwgReader acadSharp, string path, string reason)
        {
            DwgDrawing drawing = acadSharp.Read(path);
            drawing.Notes.Add(FallbackNote(reason));
            return drawing;
        }

        private static string Gb(long bytes) => (bytes / (double)(1L << 30)).ToString("0.0");

        [StructLayout(LayoutKind.Sequential)]
        private sealed class MemoryStatusEx
        {
            public uint Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
            public uint MemoryLoad;
            public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

        /// <summary>Free physical memory; long.MaxValue when Windows won't say (never block a read on a failed probe).</summary>
        public static long FreeBytes()
        {
            var status = new MemoryStatusEx();
            return GlobalMemoryStatusEx(status) ? (long)status.AvailPhys : long.MaxValue;
        }
    }
}
