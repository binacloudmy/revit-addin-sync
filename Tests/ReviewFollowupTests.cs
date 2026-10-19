// Follow-ups to the review of PR #196 (ClickUp 14zefhy1rfg): timeout vs Cancel,
// no raw server text in the button's dialog, measurable parameters, and a body
// read that honours the cancellation token. Revit-free.

using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RevitWebAppSync.Services;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class PullFailureDialogTests
    {
        // The 90 s timeout throws TaskCanceledException; it must not look like a Cancel click.
        [Fact]
        public void Timeout_shows_the_timeout_text_not_silence()
        {
            string text = PullFailureDialog.TextFor(new TaskCanceledException(), userCancelled: false);
            Assert.Equal(PullFailureText.For(PullFailure.Timeout), text);
            Assert.Contains("90", text);
        }

        [Fact]
        public void A_cancel_click_is_silent()
            => Assert.Null(PullFailureDialog.TextFor(new TaskCanceledException(), userCancelled: true));

        [Fact]
        public void The_dialog_never_carries_the_server_body()
        {
            var ex = new ParameterPullHttpException(500,
                "Could not load parameters (HTTP 500): {\"stack\":\"SELECT secret FROM users\"}");
            string text = PullFailureDialog.TextFor(ex, false);
            Assert.NotNull(text);
            Assert.DoesNotContain("SELECT", text);
            Assert.DoesNotContain("stack", text);
            Assert.Equal(PullFailureText.For(PullFailure.Server), text);
        }

        [Theory]
        [InlineData(401)]
        [InlineData(403)]
        [InlineData(404)]
        public void Http_errors_use_the_text_by_cause(int status)
        {
            string text = PullFailureDialog.TextFor(new ParameterPullHttpException(status, "body"), false);
            Assert.DoesNotContain("body", text);
            Assert.False(string.IsNullOrWhiteSpace(text));
        }
    }

    public class WriteFailureDialogTests
    {
        [Fact]
        public void A_failed_write_says_no_changes_were_kept_and_hides_the_exception()
        {
            string text = PullWriteFailure.TextFor(new InvalidOperationException("Revit did not commit: secret detail"));
            Assert.Contains("No changes were kept in the model.", text);
            Assert.DoesNotContain("secret detail", text);
            Assert.DoesNotContain("commit", text);
        }

        [Fact]
        public void The_text_follows_the_cause()
        {
            string text = PullWriteFailure.TextFor(new TaskCanceledException());
            Assert.StartsWith(PullFailureText.For(PullFailure.Timeout), text);
        }
    }

    public class MeasurableGuardTests
    {
        [Theory]
        [InlineData(CoerceKind.Double, true, true)]
        [InlineData(CoerceKind.Double, false, false)]
        [InlineData(CoerceKind.Integer, true, false)]
        [InlineData(CoerceKind.String, true, false)]
        public void Only_a_measurable_double_is_skipped(CoerceKind kind, bool measurable, bool skipped)
            => Assert.Equal(skipped, ValueCoercion.SkipsMeasurable(kind, measurable));

        [Fact]
        public void The_summary_names_the_measurable_skip()
        {
            var m = AutoPullMessages.Build(new AutoPullOutcome
            {
                Kind = AutoPullKind.Written, Applied = 1, ElementsTouched = 1, Skipped = 2, Total = 3
            });
            Assert.Contains("measurable", m.Content);
        }
    }

    public class FailedPathTextTests
    {
        [Fact]
        public void A_failed_write_says_no_changes_were_kept()
        {
            var m = AutoPullMessages.Build(new AutoPullOutcome
            {
                Kind = AutoPullKind.Failed, Failure = PullFailure.Unexpected, RolledBack = true
            });
            Assert.Contains("No parameter changes were kept", m.Content);
        }

        [Fact]
        public void A_failed_fetch_does_not_claim_a_rollback()
        {
            var m = AutoPullMessages.Build(new AutoPullOutcome
            {
                Kind = AutoPullKind.Failed, Failure = PullFailure.Network, RolledBack = false
            });
            Assert.DoesNotContain("No parameter changes were kept", m.Content);
        }
    }

    public class HttpBodyReaderTests
    {
        private sealed class TestContent : HttpContent
        {
            private readonly Func<Stream> _open;
            public TestContent(Func<Stream> open) { _open = open; }
            protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext context) => Task.CompletedTask;
            protected override bool TryComputeLength(out long length) { length = -1; return false; }
            protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(_open());
        }

        // A read that ignores its token, like net48's: only disposing the stream ends it.
        private sealed class StalledStream : Stream
        {
            private readonly TaskCompletionSource<int> _tcs = new TaskCompletionSource<int>();
            private readonly byte[] _first;
            private bool _gaveFirst;
            public StalledStream(byte[] first) { _first = first; }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
            public override Task<int> ReadAsync(byte[] b, int o, int c, CancellationToken ct)
            {
                if (!_gaveFirst)
                {
                    _gaveFirst = true;
                    int n = Math.Min(c, _first.Length);
                    Array.Copy(_first, 0, b, o, n);
                    return Task.FromResult(n);
                }
                return _tcs.Task;
            }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
            protected override void Dispose(bool disposing)
            {
                _tcs.TrySetException(new ObjectDisposedException("stalled"));
                base.Dispose(disposing);
            }
        }

        [Fact]
        public async Task Reads_the_whole_body_as_utf8()
        {
            string json = "{\"parameters\":[{\"name\":\"Lebar dinding é\"}]}";
            var content = new TestContent(() => new MemoryStream(Encoding.UTF8.GetBytes(json)));
            Assert.Equal(json, await HttpBodyReader.ReadStringAsync(content, CancellationToken.None));
        }

        // ReadAsStringAsync strips a UTF-8 BOM; a BOM left in the text breaks Newtonsoft.
        [Fact]
        public async Task A_utf8_bom_is_stripped_and_the_body_parses()
        {
            string json = "{\"parameters\":[{\"name\":\"Lebar\"}]}";
            var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(json)).ToArray();
            var content = new TestContent(() => new MemoryStream(bytes));
            string text = await HttpBodyReader.ReadStringAsync(content, CancellationToken.None);
            Assert.Equal(json, text);
            Assert.NotEqual('\uFEFF', text[0]);
            Assert.NotNull(Newtonsoft.Json.Linq.JObject.Parse(text));
        }

        [Fact]
        public async Task A_body_without_a_bom_is_unchanged()
        {
            string json = "{\"a\":1}";
            var content = new TestContent(() => new MemoryStream(Encoding.UTF8.GetBytes(json)));
            Assert.Equal(json, await HttpBodyReader.ReadStringAsync(content, CancellationToken.None));
        }

        [Fact]
        public async Task The_declared_charset_is_honoured()
        {
            string json = "{\"n\":\"caf\u00e9\"}";
            var content = new TestContent(() => new MemoryStream(Encoding.GetEncoding("iso-8859-1").GetBytes(json)));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "iso-8859-1" };
            Assert.Equal(json, await HttpBodyReader.ReadStringAsync(content, CancellationToken.None));
        }

        [Fact]
        public async Task A_utf16_bom_body_is_decoded()
        {
            string json = "{\"a\":\"x\"}";
            var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(json)).ToArray();
            var content = new TestContent(() => new MemoryStream(bytes));
            Assert.Equal(json, await HttpBodyReader.ReadStringAsync(content, CancellationToken.None));
        }

        [Fact]
        public async Task A_large_body_is_read_completely()
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < 20000; i++) sb.Append("{\"v\":").Append(i).Append("},");
            sb.Append("0]");
            var content = new TestContent(() => new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString())));
            Assert.Equal(sb.ToString(), await HttpBodyReader.ReadStringAsync(content, CancellationToken.None));
        }

        [Fact]
        public async Task A_stalled_body_is_stopped_by_the_token()
        {
            var content = new TestContent(() => new StalledStream(Encoding.UTF8.GetBytes("{\"par")));
            using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => HttpBodyReader.ReadStringAsync(content, cts.Token));
                Assert.True(sw.ElapsedMilliseconds < 5000, "the read outlived the token");
            }
        }

        [Fact]
        public async Task An_already_cancelled_token_reads_nothing()
        {
            var content = new TestContent(() => new MemoryStream(new byte[] { 1 }));
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => HttpBodyReader.ReadStringAsync(content, cts.Token));
            }
        }

        [Fact]
        public async Task The_timeout_from_a_stalled_body_classifies_as_a_timeout()
        {
            var content = new TestContent(() => new StalledStream(new byte[] { 0x7b }));
            using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
            {
                var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => HttpBodyReader.ReadStringAsync(content, cts.Token));
                Assert.Equal(PullFailure.Timeout, PullPolicy.Classify(ex, false));
            }
        }
    }
}
