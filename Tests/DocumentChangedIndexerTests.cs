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
    }
}
