using System;
using System.IO;
using BinaOta;
using Xunit;

namespace Tests
{
    /// <summary>
    /// F1 crash guard: the .launch / .healthy / .bad markers inside each
    /// versions\&lt;ver&gt;\ folder, the bad-versions.json block list, and the
    /// loader's candidate ordering. Shared source with BinaLoader, so this is
    /// the loader's own decision table. Temp dirs only — no Revit.
    /// </summary>
    public class VersionHealthTests : IDisposable
    {
        private static readonly DateTime Now = new DateTime(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc);
        private readonly string _root;

        public VersionHealthTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "bina-vh-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private string Version(string v)
        {
            var dir = Path.Combine(_root, "versions", v);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, ".complete"), v);
            return dir;
        }

        [Fact]
        public void Fresh_version_is_not_bad_and_a_launch_is_counted()
        {
            var dir = Version("0.0.40");

            Assert.False(VersionHealth.Assess(_root, dir, Now));
            VersionHealth.RecordLaunch(dir);

            Assert.Equal(1, VersionHealth.ReadLaunchCount(dir));
        }

        [Fact]
        public void Two_launches_that_never_became_healthy_mark_the_version_bad()
        {
            var dir = Version("0.0.40");
            VersionHealth.RecordLaunch(dir);
            Assert.False(VersionHealth.Assess(_root, dir, Now)); // one crash: still allowed
            VersionHealth.RecordLaunch(dir);

            Assert.True(VersionHealth.Assess(_root, dir, Now));
            Assert.True(File.Exists(Path.Combine(dir, VersionHealth.BadMarker)));
            Assert.Contains("2 launch", File.ReadAllText(Path.Combine(dir, VersionHealth.BadMarker)));
            Assert.Contains("0.0.40", VersionHealth.ReadBadList(_root));
        }

        [Fact]
        public void A_healthy_version_is_never_marked_bad()
        {
            var dir = Version("0.0.40");
            VersionHealth.MarkHealthy(_root, dir);
            for (var i = 0; i < 5; i++) VersionHealth.RecordLaunch(dir);

            Assert.False(VersionHealth.Assess(_root, dir, Now));
            Assert.False(File.Exists(Path.Combine(dir, VersionHealth.BadMarker)));
            Assert.False(VersionHealth.IsBlocked(_root, "0.0.40"));
        }

        [Fact]
        public void MarkHealthy_clears_launch_and_bad_and_unlists()
        {
            var dir = Version("0.0.40");
            VersionHealth.RecordLaunch(dir);
            VersionHealth.MarkBad(_root, dir, "OnStartup threw", Now);
            Assert.True(VersionHealth.IsBlocked(_root, "0.0.40"));

            VersionHealth.MarkHealthy(_root, dir);

            Assert.True(File.Exists(Path.Combine(dir, VersionHealth.HealthyMarker)));
            Assert.False(File.Exists(Path.Combine(dir, VersionHealth.LaunchMarker)));
            Assert.False(File.Exists(Path.Combine(dir, VersionHealth.BadMarker)));
            Assert.DoesNotContain("0.0.40", VersionHealth.ReadBadList(_root));
            Assert.False(VersionHealth.IsBlocked(_root, "0.0.40"));
        }

        [Fact]
        public void A_pruned_bad_version_stays_blocked_through_the_list()
        {
            var dir = Version("0.0.40");
            VersionHealth.MarkBad(_root, dir, "crashed", Now);
            Directory.Delete(dir, recursive: true);

            Assert.True(VersionHealth.IsBlocked(_root, "0.0.40"));
            Assert.False(VersionHealth.IsBlocked(_root, "0.0.41"));
        }

        [Fact]
        public void MarkBad_is_idempotent_in_the_list()
        {
            var dir = Version("0.0.40");
            VersionHealth.MarkBad(_root, dir, "a", Now);
            VersionHealth.MarkBad(_root, dir, "b", Now);
            VersionHealth.AddToBadList(_root, "0.0.40");

            Assert.Single(VersionHealth.ReadBadList(_root));
        }

        [Fact]
        public void Unreadable_launch_counts_as_one_attempt()
        {
            var dir = Version("0.0.40");
            File.WriteAllText(Path.Combine(dir, VersionHealth.LaunchMarker), "garbage");

            Assert.Equal(1, VersionHealth.ReadLaunchCount(dir));
        }

        [Fact]
        public void Corrupt_bad_list_reads_as_empty_and_is_repaired_on_write()
        {
            File.WriteAllText(Path.Combine(_root, VersionHealth.BadListFile), "{not json");
            Assert.Empty(VersionHealth.ReadBadList(_root));

            VersionHealth.AddToBadList(_root, "0.0.41");
            Assert.Equal(new[] { "0.0.41" }, VersionHealth.ReadBadList(_root));
        }

        [Fact]
        public void Order_puts_healthy_candidates_newest_first_and_bad_ones_last_as_a_last_resort()
        {
            var items = new[]
            {
                (V: new Version(0, 0, 38), Bad: false),
                (V: new Version(0, 0, 40), Bad: true),
                (V: new Version(0, 0, 39), Bad: false),
                (V: new Version(0, 0, 41), Bad: true),
            };

            var ordered = VersionHealth.Order(items, x => x.V, x => x.Bad);

            Assert.Equal(new[] { "0.0.39", "0.0.38", "0.0.41", "0.0.40" },
                ordered.Select(x => x.V.ToString()).ToArray());
        }

        [Fact]
        public void SelectPrunable_never_prunes_the_loaded_version()
        {
            var all = new[] { "0.0.41", "0.0.40", "0.0.39", "0.0.38", "0.0.37" }.Select(System.Version.Parse);

            var prune = VersionHealth.SelectPrunable(all, loaded: new Version(0, 0, 38), keep: 2);

            Assert.Equal(new[] { "0.0.39", "0.0.37" }, prune.Select(v => v.ToString()).ToArray());
        }

        [Fact]
        public void SelectPrunable_without_a_loaded_version_keeps_the_newest()
        {
            var all = new[] { "0.0.41", "0.0.40", "0.0.39" }.Select(System.Version.Parse);

            var prune = VersionHealth.SelectPrunable(all, loaded: null, keep: 2);

            Assert.Equal(new[] { "0.0.39" }, prune.Select(v => v.ToString()).ToArray());
        }

        [Fact]
        public void VersionRootOf_walks_up_from_a_payload_subfolder()
        {
            var dir = Version("0.0.40");
            var payload = Path.Combine(dir, "net8.0");
            Directory.CreateDirectory(payload);

            Assert.Equal(dir, VersionHealth.VersionRootOf(Path.Combine(_root, "versions"), payload));
            Assert.Equal(dir, VersionHealth.VersionRootOf(Path.Combine(_root, "versions"), dir));
            Assert.Null(VersionHealth.VersionRootOf(Path.Combine(_root, "versions"), Path.GetTempPath()));
        }
    }
}
