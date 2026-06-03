// Pure unit tests for DocumentChangedIndexer.BuildBulkPayload.
// No Revit SDK references — these must run on any platform.

using System.Collections.Generic;
using BinaVibe.Indexer;
using Xunit;

namespace Tests
{
    public class DocumentChangedIndexerTests
    {
        private static SnapshotDoc MakeDoc(string id, string category, string name = "test") =>
            new SnapshotDoc(
                Id: id,
                Text: $"{category} {name}",
                Metadata: new Dictionary<string, object>
                {
                    ["category"] = category,
                    ["element_id"] = id,
                    ["params"] = new Dictionary<string, object> { ["name"] = name },
                });

        [Fact]
        public void BuildBulkPayload_TagsModeAndCategoryMetadata()
        {
            var docs = new List<SnapshotDoc>
            {
                MakeDoc("1", "Level",  "Ground Floor"),
                MakeDoc("2", "Door",   "Double Door"),
                MakeDoc("3", "Wall",   "Basic Wall"),
            };

            var payload = DocumentChangedIndexer.BuildBulkPayload(docs, version: 1);

            // Mode must be "bulk"
            Assert.Equal("bulk", payload.Mode);

            // Version must be passed through
            Assert.Equal(1, payload.Version);

            // All docs present
            Assert.Equal(3, payload.Docs.Count);

            // deleted_ids must be empty (not null)
            Assert.NotNull(payload.DeletedIds);
            Assert.Empty(payload.DeletedIds);

            // Each doc carries the expected category in its Metadata
            Assert.Equal("Level", payload.Docs[0].Metadata["category"]);
            Assert.Equal("Door",  payload.Docs[1].Metadata["category"]);
            Assert.Equal("Wall",  payload.Docs[2].Metadata["category"]);

            // Each doc carries element_id in its Metadata
            Assert.Equal("1", payload.Docs[0].Metadata["element_id"]);
        }

        [Fact]
        public void BuildBulkPayload_NullDocsProducesEmptyList()
        {
            var payload = DocumentChangedIndexer.BuildBulkPayload(null, version: 0);

            Assert.Equal("bulk", payload.Mode);
            Assert.NotNull(payload.Docs);
            Assert.Empty(payload.Docs);
        }

        [Fact]
        public void BuildBulkPayload_VersionIsPreserved()
        {
            var payload = DocumentChangedIndexer.BuildBulkPayload(new List<SnapshotDoc>(), version: 42);
            Assert.Equal(42, payload.Version);
        }

        // ── Pause/Resume (master-plan Item 8) ───────────────────────────────
        // No Revit SDK in this project: construct with a null app/http so the
        // pause gate can be exercised without a live document or network.
        private static DocumentChangedIndexer NewIndexer() =>
            new DocumentChangedIndexer(
                app: null, http: null, baseUrl: "http://localhost",
                tenantId: "t", projectId: "p");

        [Fact]
        public void Pause_SetsIsPaused_AndFlushIsSuppressedWhilePaused()
        {
            var ix = NewIndexer();

            Assert.False(ix.IsPaused);

            ix.Pause();
            Assert.True(ix.IsPaused);

            // While paused, FlushAsync must NOT ship anything (and must not throw
            // even with a null HttpClient) — it returns 0 because the gate is up.
            Assert.Equal(0, ix.FlushAsync().GetAwaiter().GetResult());
        }

        [Fact]
        public async System.Threading.Tasks.Task ResumeAsync_ClearsPause_AndOnlyOutermostResumeFlushes()
        {
            var ix = NewIndexer();

            // Nested pauses must balance: depth 2 -> first Resume stays paused.
            ix.Pause();
            ix.Pause();
            Assert.True(ix.IsPaused);

            var innerFlushed = await ix.ResumeAsync();
            Assert.True(ix.IsPaused);          // still paused (depth 1)
            Assert.Equal(0, innerFlushed);     // inner Resume does not flush

            // Outermost Resume clears the gate and performs the single flush.
            // Nothing was buffered (no Revit doc), so the flush count is 0.
            var outerFlushed = await ix.ResumeAsync();
            Assert.False(ix.IsPaused);
            Assert.Equal(0, outerFlushed);
        }

        [Fact]
        public async System.Threading.Tasks.Task ResumeAsync_WhenNotPaused_IsNoOp()
        {
            var ix = NewIndexer();

            Assert.False(ix.IsPaused);
            var flushed = await ix.ResumeAsync();   // stray Resume
            Assert.False(ix.IsPaused);
            Assert.Equal(0, flushed);               // must not flush / underflow
        }
    }
}
