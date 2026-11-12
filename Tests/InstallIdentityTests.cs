using System;
using System.IO;
using RevitWebAppSync.Services;
using Xunit;

namespace Tests
{
    /// <summary>F5 install_id: a random GUID in &lt;root&gt;\telemetry.id, stable per install.</summary>
    public class InstallIdentityTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bina-iid-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public void Creates_a_guid_once_and_returns_it_again()
        {
            var path = Path.Combine(_dir, "telemetry.id");

            var first = InstallIdentity.ReadOrCreate(path);
            var second = InstallIdentity.ReadOrCreate(path);

            Assert.True(Guid.TryParse(first, out _));
            Assert.Equal(first, second);
            Assert.Equal(first, File.ReadAllText(path).Trim());
        }

        [Fact]
        public void A_corrupt_id_file_is_replaced()
        {
            Directory.CreateDirectory(_dir);
            var path = Path.Combine(_dir, "telemetry.id");
            File.WriteAllText(path, "ali@JKR-PC-07");

            var id = InstallIdentity.ReadOrCreate(path);

            Assert.True(Guid.TryParse(id, out _));
        }

        [Fact]
        public void An_unwritable_location_still_yields_an_id()
        {
            var id = InstallIdentity.ReadOrCreate(Path.Combine(_dir, "\0bad", "telemetry.id"));
            Assert.True(Guid.TryParse(id, out _));
        }
    }
}
