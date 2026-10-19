using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Reads a response body so that a cancellation token (the 90 second pull
    /// timeout, or the Cancel button) also stops a slow body read. net48 has no
    /// ReadAsStringAsync(CancellationToken), and a plain ReadAsStringAsync() on
    /// any framework ignores the token, so a 20000-row answer that stalls
    /// mid-body would outlive the timeout. Cancelling disposes the stream, which
    /// aborts the pending read on every target. Revit-free.
    /// </summary>
    public static class HttpBodyReader
    {
        public static async Task<string> ReadStringAsync(HttpContent content, CancellationToken ct)
        {
            if (content == null) return string.Empty;
            ct.ThrowIfCancellationRequested();

            using (var stream = await content.ReadAsStreamAsync().ConfigureAwait(false))
            using (ct.Register(() => { try { stream.Dispose(); } catch { } }))
            {
                var buffer = new byte[81920];
                var ms = new MemoryStream();
                try
                {
                    int n;
                    while ((n = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        ms.Write(buffer, 0, n);
                    }
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    // A disposed stream surfaces as ObjectDisposedException/IOException:
                    // report what actually happened, a cancellation.
                    throw new OperationCanceledException(ct);
                }

                return CharsetOf(content).GetString(ms.GetBuffer(), 0, (int)ms.Length);
            }
        }

        private static Encoding CharsetOf(HttpContent content)
        {
            try
            {
                string cs = content.Headers?.ContentType?.CharSet;
                if (!string.IsNullOrWhiteSpace(cs)) return Encoding.GetEncoding(cs.Trim('"'));
            }
            catch { /* unknown charset: JSON is UTF-8 */ }
            return new UTF8Encoding(false);
        }
    }
}
