using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Fetches and decodes the preview pictures the Download Model dialog shows.
    ///
    /// The links are presigned object-storage URLs that bina-be signs on every
    /// list read and that expire in about five minutes, so a picture is fetched
    /// when its row is first shown and then kept for the life of the dialog.
    ///
    /// The HttpClient here is deliberately not SyncApiClient's: that one carries
    /// the BINA bearer token, and the token must never reach the storage host.
    ///
    /// Every failure (expired link, 403, timeout, an image WPF cannot decode,
    /// e.g. WebP on a machine without the codec) ends in null, and the caller
    /// shows the placeholder. A missing picture never surfaces as an error.
    /// </summary>
    public sealed class ThumbnailLoader : IDisposable
    {
        // bina-be accepts uploads up to 2 MB. Anything far past that is not a
        // thumbnail, and is not worth holding in memory to find out.
        internal const long MaxBytes = 4L * 1024 * 1024;

        private const int MaxParallel = 4;

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        private readonly SemaphoreSlim _gate = new SemaphoreSlim(MaxParallel, MaxParallel);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        // Keyed by design id and decode width: the model tile and the version
        // preview decode the same version at different sizes. The task is cached,
        // not the result, so two rows asking at once share one download.
        private readonly Dictionary<string, Task<ImageSource>> _cache =
            new Dictionary<string, Task<ImageSource>>();

        /// <summary>
        /// The picture for <paramref name="designId"/>, decoded to at most
        /// <paramref name="decodeWidth"/> pixels wide; null when there is none or
        /// it could not be loaded. Call from the UI thread; the result is frozen
        /// and safe to bind anywhere.
        /// </summary>
        public Task<ImageSource> LoadAsync(int designId, string url, int decodeWidth)
        {
            if (string.IsNullOrWhiteSpace(url) || _cts.IsCancellationRequested)
                return Task.FromResult<ImageSource>(null);

            string key = designId + "@" + decodeWidth;
            if (_cache.TryGetValue(key, out var existing)) return existing;

            var task = FetchAsync(url, decodeWidth, _cts.Token);
            _cache[key] = task;
            return task;
        }

        private async Task<ImageSource> FetchAsync(string url, int decodeWidth, CancellationToken ct)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                return null;

            try
            {
                await _gate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            try
            {
                byte[] bytes;
                using (var resp = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct)
                           .ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) return null;

                    long? length = resp.Content.Headers.ContentLength;
                    if (length.HasValue && length.Value > MaxBytes) return null;

                    bytes = await ReadCappedAsync(resp.Content, ct).ConfigureAwait(false);
                }

                return bytes == null || bytes.Length == 0 ? null : Decode(bytes, decodeWidth);
            }
            catch (Exception)
            {
                // Cancellation on close, network failure, an undecodable image:
                // all mean "show the placeholder".
                return null;
            }
            finally
            {
                _gate.Release();
            }
        }

        private static async Task<byte[]> ReadCappedAsync(HttpContent content, CancellationToken ct)
        {
            using (var source = await content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var buffer = new MemoryStream())
            {
                var chunk = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(chunk, 0, chunk.Length, ct).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaxBytes) return null;
                    buffer.Write(chunk, 0, read);
                }
                return buffer.ToArray();
            }
        }

        /// <summary>
        /// Decoded off the UI thread and frozen, so the bitmap can cross back to
        /// it. DecodePixelWidth keeps a 2 MB render from sitting in memory at
        /// full size behind a 64-pixel tile.
        /// </summary>
        private static ImageSource Decode(byte[] bytes, int decodeWidth)
        {
            var image = new BitmapImage();
            using (var stream = new MemoryStream(bytes))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
                image.StreamSource = stream;
                image.EndInit();
            }
            image.Freeze();
            return image;
        }

        /// <summary>Stops pending downloads. Results still in flight come back null.</summary>
        public void Dispose()
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            _cts.Dispose();
        }
    }
}
