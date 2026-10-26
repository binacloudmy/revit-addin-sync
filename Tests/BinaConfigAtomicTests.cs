using System;
using System.IO;
using System.Linq;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// AI-64: config.json writes are atomic, and a corrupt file is backed up
    /// (its bytes preserved) instead of being silently replaced by defaults.
    /// </summary>
    public class BinaConfigAtomicTests
    {
        private static string NewTempDir() =>
            Path.Combine(Path.GetTempPath(), "bina-config-test-" + Guid.NewGuid().ToString("N"));

        private static string ConfigIn(string dir) => Path.Combine(dir, "config.json");

        [Fact]
        public void Save_then_load_round_trips()
        {
            var dir = NewTempDir();
            Directory.CreateDirectory(dir);
            try
            {
                var cfg = new BinaConfig
                {
                    ProjectId = 42,
                    ProjectName = "Project",
                    EngineSecret = "secret",
                    AutoConfiguredAt = DateTime.UtcNow
                };
                cfg.SaveTo(ConfigIn(dir));

                var loaded = BinaConfig.LoadCore(ConfigIn(dir));

                Assert.NotNull(loaded);
                Assert.Equal(42, loaded.ProjectId);
                Assert.Equal("Project", loaded.ProjectName);
                Assert.Equal("secret", loaded.EngineSecret);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void Save_leaves_no_tmp_file_behind()
        {
            var dir = NewTempDir();
            Directory.CreateDirectory(dir);
            try
            {
                new BinaConfig { ProjectId = 1 }.SaveTo(ConfigIn(dir));

                Assert.True(File.Exists(ConfigIn(dir)));
                Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void Existing_config_is_replaced_not_merged()
        {
            var dir = NewTempDir();
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(ConfigIn(dir), "{\"ProjectId\":1,\"ProjectName\":\"old\"}");
                new BinaConfig { ProjectId = 2 }.SaveTo(ConfigIn(dir));

                var loaded = BinaConfig.LoadCore(ConfigIn(dir));
                Assert.Equal(2, loaded.ProjectId);
                Assert.Null(loaded.ProjectName);   // the whole file is replaced
                Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void Corrupt_file_is_backed_up_with_its_original_content()
        {
            var dir = NewTempDir();
            Directory.CreateDirectory(dir);
            try
            {
                const string corrupt = "{ this is not json";
                File.WriteAllText(ConfigIn(dir), corrupt);

                var loaded = BinaConfig.LoadCore(ConfigIn(dir));

                Assert.Null(loaded);   // the caller falls back to defaults
                Assert.True(File.Exists(ConfigIn(dir)), "the original must not be deleted");

                var backup = Directory.GetFiles(dir, "config.json.bak-*").Single();
                Assert.Equal(corrupt, File.ReadAllText(backup));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void A_missing_file_is_not_backed_up()
        {
            var dir = NewTempDir();
            Directory.CreateDirectory(dir);
            try
            {
                Assert.Null(BinaConfig.LoadCore(ConfigIn(dir)));
                Assert.Empty(Directory.GetFiles(dir, "config.json.bak-*"));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
