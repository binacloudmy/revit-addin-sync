// AI-204 (X-07): the link tool said "reloaded" without checking the result of
// Reload(). LinkResults is the pure seam that decides what to report.

#nullable enable

using BinaVibe.Mcp.Tools;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class LinkResultsTests
    {
        [Fact]
        public void Reload_LinkLoaded_IsReloaded()
        {
            var (status, reason) = LinkResults.ReloadStatus("LinkLoaded");
            Assert.Equal("reloaded", status);
            Assert.Null(reason);
        }

        [Theory]
        [InlineData("LinkNotFound")]
        [InlineData("LinkNotOpenable")]
        [InlineData("LinkCannotBeLoaded")]
        [InlineData("SameModelAsHost")]
        public void Reload_AnyOtherResult_IsFailedWithReason(string loadResult)
        {
            var (status, reason) = LinkResults.ReloadStatus(loadResult);
            Assert.Equal("failed", status);
            Assert.Contains(loadResult, reason);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Reload_NoResult_IsFailed(string? loadResult)
        {
            var (status, reason) = LinkResults.ReloadStatus(loadResult);
            Assert.Equal("failed", status);
            Assert.Contains("no result", reason);
        }

        [Fact]
        public void Batch_AllFailed_IsNotOk()
        {
            Assert.Equal((false, "failed"), LinkResults.BatchVerdict(created: 0, reloaded: 0, failed: 2));
        }

        [Fact]
        public void Batch_SomeFailed_IsPartial()
        {
            Assert.Equal((true, "partial"), LinkResults.BatchVerdict(created: 1, reloaded: 0, failed: 1));
            Assert.Equal((true, "partial"), LinkResults.BatchVerdict(created: 0, reloaded: 1, failed: 1));
        }

        [Fact]
        public void Batch_NoneFailed_IsCompleted()
        {
            Assert.Equal((true, "completed"), LinkResults.BatchVerdict(created: 1, reloaded: 2, failed: 0));
            Assert.Equal((true, "completed"), LinkResults.BatchVerdict(created: 0, reloaded: 0, failed: 0));
        }
    }
}
