// Download Model pulls BINA parameters and records the model's identity
// (ClickUp 14zefhy1rfg). These pin the Revit-free rules: when a stamp may be
// written, and what the single summary says.

using RevitWebAppSync.Services;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class AutoParameterPullTests
    {
        private const string Guid1 = "5f0c1e0a9a3b4a62a1d3f2f3a8f6c111";
        private const string Guid2 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        // ---- stamp policy

        [Fact]
        public void Stamp_written_when_file_has_none_and_row_has_a_docGuid()
            => Assert.Equal(StampAction.Write, DownloadStampPolicy.Decide(true, null, Guid1));

        [Fact]
        public void Stamp_written_when_existing_stamp_is_blank()
            => Assert.Equal(StampAction.Write, DownloadStampPolicy.Decide(true, "  ", Guid1));

        [Fact]
        public void Same_guid_is_left_alone_case_insensitive()
            => Assert.Equal(StampAction.AlreadyStamped,
                DownloadStampPolicy.Decide(true, Guid1.ToUpperInvariant(), Guid1));

        [Fact]
        public void A_different_existing_stamp_is_never_overwritten()
            => Assert.Equal(StampAction.SkipDifferentStamp, DownloadStampPolicy.Decide(true, Guid2, Guid1));

        [Fact]
        public void Web_upload_without_docGuid_is_not_stamped()
        {
            Assert.Equal(StampAction.SkipNoDocGuid, DownloadStampPolicy.Decide(true, null, null));
            Assert.Equal(StampAction.SkipNoDocGuid, DownloadStampPolicy.Decide(true, null, ""));
            Assert.Equal(StampAction.SkipNoDocGuid, DownloadStampPolicy.Decide(true, Guid2, null));
        }

        [Fact]
        public void Unreadable_stamp_is_not_written_blind()
            => Assert.Equal(StampAction.SkipUnreadable, DownloadStampPolicy.Decide(false, null, Guid1));

        // ---- summary

        [Fact]
        public void Disabled_and_nothing_to_write_are_silent()
        {
            Assert.Null(AutoPullMessages.Build(new AutoPullOutcome { Kind = AutoPullKind.Disabled }));
            Assert.Null(AutoPullMessages.Build(new AutoPullOutcome { Kind = AutoPullKind.NothingToWrite }));
        }

        [Fact]
        public void Written_reports_counts_and_model()
        {
            var m = AutoPullMessages.Build(new AutoPullOutcome
            {
                Kind = AutoPullKind.Written, Applied = 1, ElementsTouched = 1,
                ModelName = "Tower.rvt", VersionNumber = 4, Total = 1
            });
            Assert.Equal("1 parameter written onto 1 element", m.Instruction);
            Assert.Contains("\"Tower.rvt\" (v4)", m.Content);
            Assert.DoesNotContain("not written", m.Content);
        }

        [Fact]
        public void Written_plural_and_skipped_line()
        {
            var m = AutoPullMessages.Build(new AutoPullOutcome
            {
                Kind = AutoPullKind.Written, Applied = 12, ElementsTouched = 5, Skipped = 3, Total = 15
            });
            Assert.Equal("12 parameters written onto 5 elements", m.Instruction);
            Assert.Contains("3 of 15 were not written", m.Content);
        }

        [Fact]
        public void Truncated_is_warned()
        {
            var m = AutoPullMessages.Build(new AutoPullOutcome
            {
                Kind = AutoPullKind.Written, Applied = 2, ElementsTouched = 2, Truncated = true
            });
            Assert.Contains("only the first batch", m.Content);
        }

        [Fact]
        public void Nothing_applied_does_not_say_saved()
        {
            var m = AutoPullMessages.Build(new AutoPullOutcome
            {
                Kind = AutoPullKind.Written, Applied = 0, Skipped = 4, Total = 4
            });
            Assert.Equal("No parameters could be written", m.Instruction);
            Assert.DoesNotContain("Save the model", m.Content);
        }

        [Fact]
        public void Failure_keeps_model_and_gives_a_retry_hint()
        {
            var m = AutoPullMessages.Build(new AutoPullOutcome
            {
                Kind = AutoPullKind.Failed, Error = "Could not load parameters (HTTP 404): nope"
            });
            Assert.Contains("Model opened", m.Instruction);
            Assert.Contains("HTTP 404", m.Content);
            Assert.Contains("Sync Parameters", m.Content);
        }

        [Fact]
        public void Failure_without_text_still_says_something()
        {
            var m = AutoPullMessages.Build(new AutoPullOutcome { Kind = AutoPullKind.Failed });
            Assert.Contains("Unknown error", m.Content);
        }

        [Fact]
        public void Stamp_failure_alone_surfaces_when_otherwise_silent()
        {
            var m = AutoPullMessages.Build(new AutoPullOutcome
            {
                Kind = AutoPullKind.NothingToWrite, StampFailed = true
            });
            Assert.NotNull(m);
            Assert.Contains("identity", m.Content);
        }

        [Fact]
        public void Stamp_failure_is_appended_to_a_written_summary()
        {
            var m = AutoPullMessages.Build(new AutoPullOutcome
            {
                Kind = AutoPullKind.Written, Applied = 1, ElementsTouched = 1, StampFailed = true
            });
            Assert.Contains("1 parameter written", m.Instruction);
            Assert.Contains("identity", m.Content);
        }

        [Fact]
        public void No_document_says_open_it_then_retry()
        {
            var m = AutoPullMessages.Build(new AutoPullOutcome { Kind = AutoPullKind.NoDocument });
            Assert.Contains("Sync Parameters", m.Content);
        }
    }
}
