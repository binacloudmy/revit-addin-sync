// "AutoSyncParameters": null (or absent) must not fail the whole config load.
using Newtonsoft.Json;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class AutoSyncConfigTests
    {
        private static BinaConfig Parse(string json) => JsonConvert.DeserializeObject<BinaConfig>(json);

        [Fact]
        public void Absent_key_defaults_to_enabled()
            => Assert.True(Parse("{\"ApiBaseUrl\":\"https://x.example\"}").AutoSyncParametersEnabled);

        [Fact]
        public void Null_defaults_to_enabled_and_keeps_the_rest_of_the_file()
        {
            var cfg = Parse("{\"ApiBaseUrl\":\"https://x.example\",\"AutoSyncParameters\":null}");
            Assert.True(cfg.AutoSyncParametersEnabled);
            Assert.Equal("https://x.example", cfg.ApiBaseUrl);
        }

        [Fact]
        public void False_turns_it_off()
            => Assert.False(Parse("{\"AutoSyncParameters\":false}").AutoSyncParametersEnabled);

        [Fact]
        public void True_keeps_it_on()
            => Assert.True(Parse("{\"AutoSyncParameters\":true}").AutoSyncParametersEnabled);

        [Fact]
        public void Default_is_not_written_back_as_null()
            => Assert.DoesNotContain("AutoSyncParameters", JsonConvert.SerializeObject(new BinaConfig()));
    }
}
