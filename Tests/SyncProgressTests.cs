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
                new SyncProgress(SyncPhase.PreparingViewer), new SyncProgress(SyncPhase.Finalizing)
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
    }
}
