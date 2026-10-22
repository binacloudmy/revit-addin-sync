using System.IO;
using Xunit;

namespace RevitAddinSync.Tests
{
    /// <summary>
    /// AI-58: the viewer upload must stream from disk. The old path copied the
    /// .rvt to a temp file and then read it into a byte[] — over 2 GB that always
    /// failed (.NET array limit), and at 1–2 GB it could exhaust Revit's memory.
    /// </summary>
    public class ViewerUploadStreamingTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj")))
                dir = dir.Parent;
            Assert.True(dir != null, "could not locate the repo root from " + AppContext.BaseDirectory);
            return dir!.FullName;
        }

        [Fact]
        public void Viewer_upload_streams_the_file_and_never_buffers_it()
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), "AutodeskApiService.cs"));

            // The old full-file path: a temp copy plus ReadAllBytesAsync.
            Assert.DoesNotContain("ReadAllBytesAsync", text);
            Assert.DoesNotContain("autodesk_upload_{", text);   // the temp-copy name

            // The streaming path, matching SyncApiClient.UploadAsync.
            Assert.Contains("new FileStream(", text);
            Assert.Contains("FileShare.ReadWrite", text);
            Assert.Contains("new StreamContent(", text);
        }
    }
}
