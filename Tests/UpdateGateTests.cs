using System;
using RevitWebAppSync.Services;
using Xunit;

namespace Tests
{
    /// <summary>
    /// The forced-update gate: one state every entry point (ribbon, panes, AI
    /// tools, tunnel, engine) reads. Pure policy — no Revit, no disk, no HTTP.
    /// </summary>
    public class UpdateGateTests
    {
        private static readonly Version Installed = new Version(0, 0, 77);

        // --- decision from a feed answer -------------------------------------

        [Fact]
        public void Up_to_date_is_open()
        {
            Assert.Equal(UpdateGateState.Open,
                UpdateGatePolicy.FromFeed(Installed, new Version(0, 0, 77), mandatory: true, stagedOnDisk: false));
        }

        [Fact]
        public void Newer_optional_is_open()
        {
            Assert.Equal(UpdateGateState.Open,
                UpdateGatePolicy.FromFeed(Installed, new Version(0, 0, 79), mandatory: false, stagedOnDisk: false));
        }

        [Fact]
        public void Newer_forced_blocks()
        {
            Assert.Equal(UpdateGateState.Blocked,
                UpdateGatePolicy.FromFeed(Installed, new Version(0, 0, 79), mandatory: true, stagedOnDisk: false));
        }

        [Fact]
        public void Newer_forced_already_downloaded_needs_a_restart()
        {
            Assert.Equal(UpdateGateState.RestartRequired,
                UpdateGatePolicy.FromFeed(Installed, new Version(0, 0, 79), mandatory: true, stagedOnDisk: true));
        }

        [Fact]
        public void Older_feed_version_never_blocks()
        {
            // A pin to an older build cannot downgrade a machine (loader runs newest on disk).
            Assert.Equal(UpdateGateState.Open,
                UpdateGatePolicy.FromFeed(Installed, new Version(0, 0, 76), mandatory: true, stagedOnDisk: false));
        }

        // --- the first seconds after Revit opens (remembered answer) ---------

        [Fact]
        public void Remembered_forced_update_blocks_before_the_feed_answers()
        {
            var cached = new UpdateGateMemory("0.0.79", mandatory: true);
            Assert.Equal(UpdateGateState.Blocked,
                UpdateGatePolicy.FromMemory(Installed, cached, stagedOnDisk: false));
        }

        [Fact]
        public void Remembered_forced_update_already_downloaded_needs_a_restart()
        {
            var cached = new UpdateGateMemory("0.0.79", mandatory: true);
            Assert.Equal(UpdateGateState.RestartRequired,
                UpdateGatePolicy.FromMemory(Installed, cached, stagedOnDisk: true));
        }

        [Fact]
        public void Remembered_version_already_installed_is_open()
        {
            // The machine restarted onto the forced build: nothing left to block.
            var cached = new UpdateGateMemory("0.0.77", mandatory: true);
            Assert.Equal(UpdateGateState.Open,
                UpdateGatePolicy.FromMemory(Installed, cached, stagedOnDisk: false));
        }

        [Fact]
        public void No_memory_or_optional_memory_is_open()
        {
            Assert.Equal(UpdateGateState.Open, UpdateGatePolicy.FromMemory(Installed, null, stagedOnDisk: false));
            Assert.Equal(UpdateGateState.Open,
                UpdateGatePolicy.FromMemory(Installed, new UpdateGateMemory("0.0.79", mandatory: false), stagedOnDisk: false));
        }

        [Fact]
        public void Garbage_memory_is_open()
        {
            Assert.Equal(UpdateGateState.Open,
                UpdateGatePolicy.FromMemory(Installed, new UpdateGateMemory("not-a-version", mandatory: true), stagedOnDisk: false));
        }

        // --- offline ---------------------------------------------------------

        [Fact]
        public void Feed_unreachable_never_locks_the_machine()
        {
            // Even with a remembered forced update: an office with no internet
            // cannot download it, so locking would stop work with no way out.
            Assert.Equal(UpdateGateState.Open, UpdateGatePolicy.WhenFeedUnreachable());
        }

        // --- memory file round-trip -----------------------------------------

        [Fact]
        public void Memory_round_trips_through_json()
        {
            var json = new UpdateGateMemory("0.0.79", mandatory: true).ToJson();
            var back = UpdateGateMemory.Parse(json);
            Assert.NotNull(back);
            Assert.Equal("0.0.79", back.Version);
            Assert.True(back.Mandatory);
        }

        [Fact]
        public void Corrupt_memory_file_parses_to_null()
        {
            Assert.Null(UpdateGateMemory.Parse("{not json"));
            Assert.Null(UpdateGateMemory.Parse(""));
            Assert.Null(UpdateGateMemory.Parse(null));
        }

        // --- the state everything listens to --------------------------------

        [Fact]
        public void Gate_raises_changed_only_on_a_real_change()
        {
            UpdateGate.ResetForTest();
            var seen = 0;
            UpdateGate.Changed += _ => seen++;
            try
            {
                UpdateGate.Set(UpdateGateState.Open);          // already open: no event
                UpdateGate.Set(UpdateGateState.Blocked);
                UpdateGate.Set(UpdateGateState.Blocked);       // same: no event
                UpdateGate.Set(UpdateGateState.RestartRequired);
                Assert.Equal(2, seen);
                Assert.True(UpdateGate.IsBlocked);             // restart-required is still blocked
            }
            finally { UpdateGate.ResetForTest(); }
        }

        [Fact]
        public void Refusal_message_names_the_way_out()
        {
            UpdateGate.ResetForTest();
            try
            {
                UpdateGate.Set(UpdateGateState.Blocked, "0.0.79");
                Assert.Contains("0.0.79", UpdateGate.RefusalMessage);
                Assert.Contains("Update now", UpdateGate.RefusalMessage);
                UpdateGate.Set(UpdateGateState.RestartRequired, "0.0.79");
                Assert.Contains("Restart Revit", UpdateGate.RefusalMessage);
            }
            finally { UpdateGate.ResetForTest(); }
        }
    }
}
