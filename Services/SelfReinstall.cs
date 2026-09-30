using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Silent self-reinstall (OTA self-heal F2), the side-effecting half of
    /// <see cref="ReinstallPolicy"/>. When the feed's min_loader_version is
    /// newer than the running BinaLoader, download the setup EXE to
    /// &lt;root&gt;\installer\, verify it (SHA-256 always, Authenticode on Release),
    /// and hand it to a hidden PowerShell helper that waits for Revit to exit
    /// and runs it with /VERYSILENT. The installer re-applies everything the
    /// OTA zip cannot: the loader for every Revit year, the .addin manifests,
    /// the TrustedPublisher cert, engine-boot.ps1 and the logon task.
    ///
    /// The helper is spawned right after the download (it waits for the Revit
    /// PID anyway), so a Revit that crashes instead of closing still gets fixed.
    /// reinstall.json caps it at 3 attempts per installer version, 6 h apart.
    /// </summary>
    internal static class SelfReinstall
    {
        private static int _ranThisSession;

        public static async Task MaybeScheduleAsync(UpdateService.UpdateFeed feed, string root, Action<string> log)
        {
            if (feed == null) return;

            var statePath = Path.Combine(root, "reinstall.json");
            ReinstallState state = null;
            try { if (File.Exists(statePath)) state = ReinstallState.Parse(File.ReadAllText(statePath)); }
            catch { }

            var loader = UpdateService.LoaderVersion;
            var decision = ReinstallPolicy.Decide(loader, feed.MinLoaderVersion, feed.InstallerUrl,
                feed.InstallerSha256, feed.Version, IsDebugBuild, state, DateTime.UtcNow);
            if (decision == ReinstallDecision.NotNeeded) return;

            // One decision per Revit session: the feed is re-read by the engine
            // preflight too, and each read must not re-report or re-download.
            if (System.Threading.Interlocked.Exchange(ref _ranThisSession, 1) == 1) return;

            var sameVersion = state != null &&
                              string.Equals(state.Version, feed.Version, StringComparison.OrdinalIgnoreCase);
            if (sameVersion && (decision == ReinstallDecision.Schedule || decision == ReinstallDecision.AttemptsExhausted))
            {
                // An earlier silent install for this version ran, yet the loader is
                // still old: that attempt did not take.
                TelemetryService.Track("reinstall", "failed", new
                {
                    to_version = feed.Version,
                    loader_version = loader,
                    attempts = state.Attempts,
                    error_class = "LoaderStillOld",
                });
            }

            if (decision != ReinstallDecision.Schedule)
            {
                log($"reinstall: loader {loader} < min {feed.MinLoaderVersion} but {decision}");
                return;
            }

            var dir = Path.Combine(root, "installer");
            Directory.CreateDirectory(dir);
            var safeVersion = Regex.Replace(feed.Version ?? "unknown", "[^0-9A-Za-z.\\-]", "_");
            var exe = Path.Combine(dir, $"BinaSync-{safeVersion}-setup.exe");
            var tmp = exe + ".part";

            log($"reinstall: loader {loader} < min {feed.MinLoaderVersion} — downloading installer {feed.Version}");
            try
            {
                foreach (var old in Directory.GetFiles(dir, "*.exe").Where(f => !string.Equals(f, exe, StringComparison.OrdinalIgnoreCase)))
                    try { File.Delete(old); } catch { }

                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
                using (var resp = await http.GetAsync(feed.InstallerUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    using (var src = await resp.Content.ReadAsStreamAsync())
                    using (var dst = File.Create(tmp))
                        await src.CopyToAsync(dst);
                }

                // Fail closed: a mismatch never runs.
                string actual;
                using (var f = File.OpenRead(tmp))
                    actual = RuntimeCompat.ToHexString(await RuntimeCompat.Sha256Async(f));
                if (!actual.Equals(feed.InstallerSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("installer SHA-256 mismatch");

                if (File.Exists(exe)) File.Delete(exe);
                File.Move(tmp, exe);

                if (RequireAuthenticode)
                {
                    var why = Authenticode.CheckSameSigner(exe, LoaderAssemblyPath());
                    if (why != null) throw new InvalidDataException("installer signature rejected: " + why);
                }
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                try { if (File.Exists(exe)) File.Delete(exe); } catch { }
                log($"reinstall: installer {feed.Version} not usable: {ex.GetType().Name}: {ex.Message}");
                TelemetryService.Track("reinstall", "failed",
                    new { to_version = feed.Version, loader_version = loader, error_class = ex.GetType().Name });
                return;
            }

            // Count the attempt BEFORE spawning: a helper that kills the session
            // somehow must still not become a loop.
            var next = ReinstallState.Next(state, feed.Version, DateTime.UtcNow);
            File.WriteAllText(statePath, next.ToJson());

            var script = ReinstallPolicy.BuildHelperScript(
                Process.GetCurrentProcess().Id, exe, Path.Combine(dir, $"setup-{safeVersion}.log"));
            var psi = new ProcessStartInfo("powershell.exe", ReinstallPolicy.BuildPowerShellArguments(script))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = dir,
            };
            using (Process.Start(psi)) { }

            log($"reinstall: scheduled {exe} after Revit exits (attempt {next.Attempts}/{ReinstallPolicy.MaxAttempts})");
            TelemetryService.Track("reinstall", "scheduled", new
            {
                to_version = feed.Version,
                loader_version = loader,
                min_loader_version = feed.MinLoaderVersion,
                attempts = next.Attempts,
            });
        }

        private static bool IsDebugBuild
        {
            get
            {
#if DEBUG
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>Release only: staging mixes signed (sign-release.ps1) and
        /// unsigned (CI) installers by design, so a signer check there would
        /// strand machines between the two.</summary>
        private static bool RequireAuthenticode
        {
            get
            {
#if DEBUG || STAGING
                return false;
#else
                return true;
#endif
            }
        }

        private static string LoaderAssemblyPath()
        {
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "BinaLoader", StringComparison.OrdinalIgnoreCase));
                return asm == null || string.IsNullOrEmpty(asm.Location) ? null : asm.Location;
            }
            catch { return null; }
        }
    }

    /// <summary>Windows Authenticode checks for the downloaded installer.</summary>
    internal static class Authenticode
    {
        /// <summary>Null when <paramref name="file"/> may run: a trusted signature
        /// whose signer subject equals the loader DLL's. An unsigned (or unknown)
        /// loader means a staging/dev install — nothing to compare against, so
        /// the SHA-256 check alone applies. Otherwise the reason it may not.</summary>
        public static string CheckSameSigner(string file, string loaderPath)
        {
            var loaderSubject = SignerSubject(loaderPath);
            if (loaderSubject == null) return null;

            if (!IsTrusted(file)) return "no valid Authenticode signature";
            var subject = SignerSubject(file);
            if (!string.Equals(subject, loaderSubject, StringComparison.Ordinal))
                return $"signed by '{subject}', loader signed by '{loaderSubject}'";
            return null;
        }

        public static string SignerSubject(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile: the only BCL read of an Authenticode signer
                return X509Certificate.CreateFromSignedFile(path).Subject;
#pragma warning restore SYSLIB0057
            }
            catch { return null; }
        }

        private static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        public static bool IsTrusted(string path)
        {
            var fileInfo = new WinTrustFileInfo
            {
                cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo)),
                pcwszFilePath = path,
            };
            var pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustFileInfo)));
            var pData = IntPtr.Zero;
            try
            {
                Marshal.StructureToPtr(fileInfo, pFile, false);
                var data = new WinTrustData
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustData)),
                    dwUIChoice = 2,            // WTD_UI_NONE
                    fdwRevocationChecks = 0,   // WTD_REVOKE_NONE: no network hang inside Revit
                    dwUnionChoice = 1,         // WTD_CHOICE_FILE
                    pFile = pFile,
                    dwStateAction = 0,         // WTD_STATEACTION_IGNORE
                    dwProvFlags = 0x80,        // WTD_REVOCATION_CHECK_NONE
                };
                pData = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustData)));
                Marshal.StructureToPtr(data, pData, false);
                return WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, pData) == 0;
            }
            catch { return false; }
            finally
            {
                if (pData != IntPtr.Zero) Marshal.FreeHGlobal(pData);
                Marshal.DestroyStructure(pFile, typeof(WinTrustFileInfo));
                Marshal.FreeHGlobal(pFile);
            }
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr hwnd,
            [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, IntPtr pWVTData);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }
    }
}
