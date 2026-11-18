// Sync dialog progress: the step/byte model and the real byte callbacks that feed it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using RevitWebAppSync.Services;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class SyncProgressTests
    {
        [Fact]
        public void Percent_starts_at_zero_and_saving_is_the_last_slice()
        {
            Assert.Equal(0, new SyncProgress(SyncPhase.Hashing, 0, 100).Percent);
            Assert.Equal(95, new SyncProgress(SyncPhase.Finalizing).Percent);   // 100 is the outcome view
        }

        [Fact]
        public void Percent_never_goes_backwards_across_a_full_run()
        {
            var steps = new[]
            {
                new SyncProgress(SyncPhase.Hashing, 0, 1000), new SyncProgress(SyncPhase.Hashing, 1000, 1000),
                new SyncProgress(SyncPhase.Registering),
                new SyncProgress(SyncPhase.Uploading, 0, 1000), new SyncProgress(SyncPhase.Uploading, 500, 1000),
                new SyncProgress(SyncPhase.Uploading, 1000, 1000),
                new SyncProgress(SyncPhase.Finalizing)
            };
            int last = -1;
            foreach (var s in steps) { Assert.True(s.Percent >= last, s.Text); last = s.Percent; }
        }

        [Fact]
        public void Upload_text_shows_megabytes_and_percent()
        {
            var p = new SyncProgress(SyncPhase.Uploading, 42L * 1048576, 130L * 1048576);
            Assert.Equal("Uploading 42 MB of 130 MB · 32%", p.Text);
        }

        [Fact]
        public void Bytes_are_clamped_and_a_zero_total_does_not_divide_by_zero()
        {
            Assert.Equal(100, new SyncProgress(SyncPhase.Uploading, 999, 100).BytesDone);
            Assert.Equal(15, new SyncProgress(SyncPhase.Uploading, 5, 0).Percent);
        }

        private static string TempFile(int bytes)
        {
            string path = Path.GetTempFileName();
            File.WriteAllBytes(path, new byte[bytes]);
            return path;
        }

        private sealed class Collect<T> : IProgress<T>
        {
            public readonly List<T> Items = new List<T>();
            public void Report(T v) => Items.Add(v);
        }

        private static string PatternFile(int bytes)
        {
            string path = Path.GetTempFileName();
            var data = new byte[bytes];
            for (int i = 0; i < bytes; i++) data[i] = (byte)(i * 31 + 7);
            File.WriteAllBytes(path, data);
            return path;
        }

        private static string Sha(string path)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1000)]                  // under 1 MB: no progress reports, hash must still be right
        [InlineData(3 * 1048576 + 17)]
        public void Hash_matches_an_independent_sha256_and_reports_rising_bytes(int size)
        {
            string path = PatternFile(size);
            try
            {
                var seen = new Collect<long>();
                Assert.Equal(Sha(path), SyncApiClient.ComputeFileHash(path, seen));
                Assert.True(seen.Items.Count <= size / 1048576);
                if (size >= 2 * 1048576) Assert.NotEmpty(seen.Items);
                for (int i = 1; i < seen.Items.Count; i++) Assert.True(seen.Items[i] > seen.Items[i - 1]);
                if (seen.Items.Count > 0) Assert.True(seen.Items[seen.Items.Count - 1] <= size);
            }
            finally { File.Delete(path); }
        }

        private sealed class DrainHandler : HttpMessageHandler
        {
            public long Received;
            public bool Resend;
            public HttpMethod Method;
            public long? Length;
            public HttpStatusCode Status = HttpStatusCode.OK;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            {
                var ms = new MemoryStream();
                await r.Content.CopyToAsync(ms);
                if (Resend) await r.Content.CopyToAsync(ms);   // what HttpClient does on a redirect
                Received = ms.Length;
                Method = r.Method; Length = r.Content.Headers.ContentLength;
                return new HttpResponseMessage(Status);
            }
        }

        [Fact]
        public async Task Upload_reports_bytes_sent_and_the_server_gets_every_byte()
        {
            const int size = 5 * 1048576 + 3;
            string path = TempFile(size);
            try
            {
                var h = new DrainHandler();
                var api = new SyncApiClient("https://be.test", "t", new HttpClient(h));
                var seen = new Collect<long>();

                Assert.True(await api.UploadAsync("https://store.test/put", path, seen));

                Assert.Equal(size, h.Received);
                Assert.Equal(HttpMethod.Put, h.Method);
                Assert.Equal(size, h.Length);
                Assert.Equal(size, seen.Items[seen.Items.Count - 1]);   // final report is the full size
                Assert.True(seen.Items.Count >= 3);                     // and it climbed on the way
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public async Task Upload_that_storage_refuses_returns_false()
        {
            string path = TempFile(2 * 1048576);
            try
            {
                var h = new DrainHandler { Status = HttpStatusCode.Forbidden };
                var api = new SyncApiClient("https://be.test", "t", new HttpClient(h));

                Assert.False(await api.UploadAsync("https://store.test/put", path));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public async Task A_body_sent_twice_is_complete_both_times()
        {
            const int size = 2 * 1048576 + 5;
            string path = TempFile(size);
            try
            {
                var h = new DrainHandler { Resend = true };
                var api = new SyncApiClient("https://be.test", "t", new HttpClient(h));
                Assert.True(await api.UploadAsync("https://store.test/put", path));
                Assert.Equal(2L * size, h.Received);
            }
            finally { File.Delete(path); }
        }

        [Theory]
        [InlineData(SyncPhase.Hashing, 100, 10)]
        [InlineData(SyncPhase.Registering, 0, 10)]
        [InlineData(SyncPhase.Uploading, 0, 15)]
        [InlineData(SyncPhase.Uploading, 50, 55)]
        [InlineData(SyncPhase.Uploading, 100, 95)]
        [InlineData(SyncPhase.Finalizing, 0, 95)]
        public void Each_step_owns_its_slice_of_the_bar(SyncPhase phase, int of100, int expected)
            => Assert.Equal(expected, new SyncProgress(phase, of100, 100).Percent);

        // Fake BINA server + storage. Records every host called and the commit body.
        private sealed class FakeServer : HttpMessageHandler
        {
            public readonly List<string> Hosts = new List<string>();
            public string CommitBody;
            public HttpStatusCode StorageStatus = HttpStatusCode.OK;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            {
                Hosts.Add(r.RequestUri.Host);
                string url = r.RequestUri.ToString();
                if (r.RequestUri.Host == "store.test")
                {
                    await r.Content.CopyToAsync(Stream.Null);
                    return new HttpResponseMessage(StorageStatus);
                }
                string json;
                if (url.EndsWith("sync/init")) json = "{\"uploadUrl\":\"https://store.test/put\",\"fileKey\":\"k1\"}";
                else if (url.EndsWith("sync/commit"))
                {
                    CommitBody = await r.Content.ReadAsStringAsync();
                    json = "{\"status\":\"created\",\"designId\":7,\"version\":1,\"name\":\"m.rvt\"}";
                }
                else json = "{}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                };
            }
        }

        private static SyncRunner.Request Req(SyncApiClient api, string path, IProgress<SyncProgress> p) =>
            new SyncRunner.Request
            {
                Api = api, UploadPath = path, FileName = "m.rvt", ProjectId = 1, DisciplineType = "Architecture",
                Progress = p
            };

        [Fact]
        public async Task A_sync_runs_hash_contact_upload_save_in_order_and_never_calls_autodesk()
        {
            string path = TempFile(3 * 1048576);
            try
            {
                var server = new FakeServer();
                var api = new SyncApiClient("https://be.test", "t", new HttpClient(server));
                var seen = new Collect<SyncProgress>();

                var result = await SyncRunner.RunAsync(Req(api, path, seen));

                Assert.True(result.Succeeded, result.Message);
                // Phases only ever move forward and appear exactly in this order.
                var order = new List<SyncPhase>();
                foreach (var p in seen.Items) if (order.Count == 0 || order[order.Count - 1] != p.Phase) order.Add(p.Phase);
                Assert.Equal(new[] { SyncPhase.Hashing, SyncPhase.Registering, SyncPhase.Uploading, SyncPhase.Finalizing }, order);
                int last = -1;
                foreach (var p in seen.Items) { Assert.True(p.Percent >= last); last = p.Percent; }

                Assert.DoesNotContain(server.Hosts, h => h.Contains("autodesk"));
                Assert.Equal(new[] { "be.test", "store.test", "be.test" }, server.Hosts);   // init, upload, commit
                Assert.DoesNotContain("\"urnInBase64\":\"", server.CommitBody ?? "");
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public async Task A_refused_upload_fails_the_sync_at_the_upload_step_and_never_commits()
        {
            string path = TempFile(1048576);
            try
            {
                var server = new FakeServer { StorageStatus = HttpStatusCode.Forbidden };
                var api = new SyncApiClient("https://be.test", "t", new HttpClient(server));
                var seen = new Collect<SyncProgress>();

                var result = await SyncRunner.RunAsync(Req(api, path, seen));

                Assert.False(result.Succeeded);
                Assert.Equal(SyncPhase.Uploading, seen.Items[seen.Items.Count - 1].Phase);   // what "Stopped at" shows
                Assert.Null(server.CommitBody);
            }
            finally { File.Delete(path); }
        }
    }
}
