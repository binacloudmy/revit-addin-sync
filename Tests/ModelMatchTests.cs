using System;
using System.Collections.Generic;
using System.IO;
using RevitWebAppSync.Services;
using Xunit;

namespace Tests
{
    /// <summary>
    /// Sync dialog pre-selection (14zefhy56zu): which existing model the file
    /// being synced is, where it was downloaded from, and readable failures.
    /// </summary>
    public class ModelMatchTests
    {
        private static List<ModelMatch.Candidate> Folder() => new List<ModelMatch.Candidate>
        {
            new ModelMatch.Candidate { Name = "jkrAR27_2b_(BEsm_19-001)_A-PTBR_w01 (1).rvt", LineageId = "L1", DesignId = 11 },
            new ModelMatch.Candidate { Name = "jkrAR27_2b_(BEsm_19-001)_A-PTBR_w01.rvt", LineageId = "L2", DesignId = 12 },
            new ModelMatch.Candidate { Name = "jkrST27_2a_(BEpj00_p19-211441)_A-PTBR_w01_(K).rvt", LineageId = "L3", DesignId = 13, DocGuid = "G3" },
            new ModelMatch.Candidate { Name = "Tower-A.rvt", LineageId = "L4", DesignId = 14 },
        };

        [Fact]
        public void Lineage_wins_over_a_different_name()
        {
            var r = ModelMatch.Pick(Folder(), new ModelMatch.Known { FileName = "something-else.rvt", LineageId = "L4" });
            Assert.Equal(3, r.Index);
            Assert.Equal(ModelMatch.Kind.Lineage, r.Kind);
        }

        [Fact]
        public void Design_id_and_doc_guid_identify_the_model()
        {
            Assert.Equal(1, ModelMatch.Pick(Folder(), new ModelMatch.Known { DesignId = 12 }).Index);
            var byGuid = ModelMatch.Pick(Folder(), new ModelMatch.Known { DocGuid = "g3" });
            Assert.Equal(2, byGuid.Index);
            Assert.Equal(ModelMatch.Kind.DocGuid, byGuid.Kind);
        }

        [Fact]
        public void Exact_name_is_picked_case_insensitively()
        {
            var r = ModelMatch.Pick(Folder(), new ModelMatch.Known { FileName = "JKRST27_2A_(BEPJ00_P19-211441)_A-PTBR_W01_(K).RVT" });
            Assert.Equal(2, r.Index);
            Assert.Equal(ModelMatch.Kind.ExactName, r.Kind);
        }

        [Fact]
        public void A_single_similar_name_is_picked()
        {
            var r = ModelMatch.Pick(Folder(), new ModelMatch.Known { FileName = "Copy of tower_a (2)-v3.rvt" });
            Assert.Equal(3, r.Index);
            Assert.Equal(ModelMatch.Kind.SimilarName, r.Kind);
        }

        [Fact]
        public void Several_similar_names_are_only_suggested()
        {
            // " (1)" and the plain name normalise the same: two candidates, no pick.
            var r = ModelMatch.Pick(Folder(), new ModelMatch.Known { FileName = "jkrAR27_2b_(BEsm_19-001)_A-PTBR_w01-v2.rvt" });
            Assert.Equal(-1, r.Index);
            Assert.Equal(new[] { 0, 1 }, r.Suggested);
        }

        [Fact]
        public void Nothing_alike_picks_nothing()
        {
            var r = ModelMatch.Pick(Folder(), new ModelMatch.Known { FileName = "Bridge.rvt" });
            Assert.Equal(-1, r.Index);
            Assert.Empty(r.Suggested);
        }

        [Theory]
        [InlineData("Copy of Tower_A (2)-v3.rvt", "tower a")]
        [InlineData("tower a.rvt", "tower a")]
        [InlineData("Tower-A - Copy.rvt", "tower a")]
        [InlineData("", "")]
        public void Normalize_drops_copy_and_download_noise(string name, string expected)
        {
            Assert.Equal(expected, ModelMatch.Normalize(name));
        }
    }

    public class ModelRecordStoreTests : IDisposable
    {
        private readonly string _store = Path.Combine(Path.GetTempPath(), "bina-models-" + Guid.NewGuid().ToString("N") + ".json");

        public void Dispose()
        {
            try { File.Delete(_store); } catch { }
        }

        [Fact]
        public void Saves_and_finds_by_path_ignoring_case()
        {
            ModelRecordStore.Save(new ModelRecord { Path = @"C:\Models\Tower-v1.rvt", ProjectId = 429, FolderId = 7, LineageId = "L1", Source = "download" }, _store);

            var found = ModelRecordStore.Find(@"c:\models\TOWER-v1.rvt", _store);
            Assert.NotNull(found);
            Assert.Equal(429, found.ProjectId);
            Assert.Equal(7, found.FolderId);
            Assert.Equal("L1", found.LineageId);
        }

        [Fact]
        public void A_later_save_replaces_the_record_for_the_same_file()
        {
            ModelRecordStore.Save(new ModelRecord { Path = @"C:\M\a.rvt", ProjectId = 1, Source = "download" }, _store);
            ModelRecordStore.Save(new ModelRecord { Path = @"C:\M\a.rvt", ProjectId = 2, Source = "sync" }, _store);

            var found = ModelRecordStore.Find(@"C:\M\a.rvt", _store);
            Assert.Equal(2, found.ProjectId);
            Assert.Equal("sync", found.Source);
        }

        [Fact]
        public void Unknown_file_and_corrupt_store_give_null()
        {
            Assert.Null(ModelRecordStore.Find(@"C:\nowhere.rvt", _store));
            File.WriteAllText(_store, "{ not json");
            Assert.Null(ModelRecordStore.Find(@"C:\nowhere.rvt", _store));
        }

        [Fact]
        public void A_record_without_a_project_is_not_saved()
        {
            ModelRecordStore.Save(new ModelRecord { Path = @"C:\M\b.rvt", ProjectId = 0 }, _store);
            Assert.Null(ModelRecordStore.Find(@"C:\M\b.rvt", _store));
        }
    }

    public class SyncFailureTextTests
    {
        [Fact]
        public void Forbidden_names_the_role_problem_without_raw_json()
        {
            string text = SyncApiClient.DescribeFailure("sync/init", 403,
                "{\"statusCode\":403,\"message\":\"BIM_VIEWER cannot perform UPLOAD\",\"error\":\"Forbidden\"}");

            Assert.Contains("BIM_VIEWER cannot perform UPLOAD", text);
            Assert.Contains("role", text);
            Assert.DoesNotContain("{", text);
        }

        [Fact]
        public void Validation_arrays_are_joined()
        {
            string text = SyncApiClient.DescribeFailure("sync/commit", 400,
                "{\"statusCode\":400,\"message\":[\"name must be a string\",\"folderId is required\"]}");
            Assert.Contains("name must be a string; folderId is required", text);
        }

        [Fact]
        public void A_non_json_body_falls_back_to_the_status()
        {
            string text = SyncApiClient.DescribeFailure("sync/init", 502, "<html>Bad gateway</html>");
            Assert.Contains("HTTP 502", text);
            Assert.DoesNotContain("<html>", text);
        }
    }
}
