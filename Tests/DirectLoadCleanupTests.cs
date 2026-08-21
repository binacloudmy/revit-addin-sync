using System;
using System.IO;
using RevitWebAppSync.Services;
using Xunit;

namespace Tests
{
    // Self-heal for legacy direct-load installs: stale RevitWebAppSync
    // binaries + direct-load manifests are removed from Addins\<year>\;
    // the loader pair and unrelated third-party addins survive.
    public class DirectLoadCleanupTests : IDisposable
    {
        private readonly string _root;

        public DirectLoadCleanupTests()
        {
            _root = Path.Combine(Path.GetTempPath(),
                "bina-cleanup-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "2027"));
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private string Write(string name, string content = "x")
        {
            var p = Path.Combine(_root, "2027", name);
            File.WriteAllText(p, content);
            return p;
        }

        [Fact]
        public void RemovesStalePayloadAndDirectManifest_KeepsLoaderPair()
        {
            Write("RevitWebAppSync.dll");
            Write("RevitWebAppSync.pdb");
            Write("RevitWebAppSync.addin",
                "<RevitAddIns><Assembly>RevitWebAppSync.dll</Assembly></RevitAddIns>");
            var loaderDll = Write("BinaLoader.dll");
            var loaderManifest = Write("BinaSync.addin",
                "<RevitAddIns><Assembly>BinaLoader.dll</Assembly></RevitAddIns>");

            int removed = DirectLoadCleanup.CleanRoot(_root);

            Assert.Equal(3, removed);
            Assert.True(File.Exists(loaderDll));
            Assert.True(File.Exists(loaderManifest));
            Assert.False(File.Exists(Path.Combine(_root, "2027", "RevitWebAppSync.dll")));
            Assert.False(File.Exists(Path.Combine(_root, "2027", "RevitWebAppSync.addin")));
        }

        [Fact]
        public void LeavesThirdPartyAddinsAndOrphanDepsAlone()
        {
            var other = Write("SomeOtherTool.addin",
                "<RevitAddIns><Assembly>SomeOtherTool.dll</Assembly></RevitAddIns>");
            var dep = Write("Newtonsoft.Json.dll");

            int removed = DirectLoadCleanup.CleanRoot(_root);

            Assert.Equal(0, removed);
            Assert.True(File.Exists(other));
            Assert.True(File.Exists(dep));
        }

        [Fact]
        public void BinaSyncManifestNameGuarded_EvenIfItMentionsPayload()
        {
            // Defensive: even a BinaSync.addin that mentions the payload DLL
            // in a comment must never be deleted.
            var loader = Write("BinaSync.addin",
                "<!-- replaces RevitWebAppSync.dll --><Assembly>BinaLoader.dll</Assembly>");

            Assert.Equal(0, DirectLoadCleanup.CleanRoot(_root));
            Assert.True(File.Exists(loader));
        }

        [Fact]
        public void OnlySelfHealsWhenLoaderManaged()
        {
            // The guard that stops a direct-load install from deleting its own
            // manifest + .deps.json / .dll.config sidecars mid-session — which
            // left every ribbon button throwing "Wrong Full Class Name" on the
            // DevColocate box (2026-08-18).
            var versions = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Bina", "RevitSync", "versions");

            Assert.True(DirectLoadCleanup.IsLoaderManaged(
                Path.Combine(versions, "0.0.49", "net10.0", "RevitWebAppSync.dll")));

            // The dev PostBuild target's destination: never self-deletes.
            Assert.False(DirectLoadCleanup.IsLoaderManaged(
                Path.Combine(_root, "2027", "RevitWebAppSync.dll")));

            // Single-file/in-memory hosts report an empty Location.
            Assert.False(DirectLoadCleanup.IsLoaderManaged(""));
            Assert.False(DirectLoadCleanup.IsLoaderManaged(null));
        }

        [Fact]
        public void MissingRoot_ReturnsZero_NeverThrows()
        {
            Assert.Equal(0, DirectLoadCleanup.CleanRoot(
                Path.Combine(_root, "does-not-exist")));
        }
    }
}
