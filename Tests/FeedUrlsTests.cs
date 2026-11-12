using RevitWebAppSync.Services;
using Xunit;

namespace Tests
{
    /// <summary>
    /// F3 feed fallback + server-driven feed move: which feed URLs the updater
    /// tries, in what order, and what a feed may persist into feed.json.
    /// </summary>
    public class FeedUrlsTests
    {
        private const string Primary = "https://bina-ai-prod-ready.binacloud.ai/addin/version.json";
        private const string Fallback = "https://backup.example/addin/version.json";

        [Fact]
        public void Baked_primary_then_fallback()
        {
            Assert.Equal(new[] { Primary, Fallback },
                FeedUrls.Order(null, null, Primary, Fallback));
        }

        [Fact]
        public void Blank_fallback_is_skipped()
        {
            Assert.Equal(new[] { Primary }, FeedUrls.Order(null, null, Primary, ""));
        }

        [Fact]
        public void Server_persisted_urls_go_before_the_baked_ones()
        {
            var moved = new[] { "https://feed.new-host.example/version.json", Primary };

            Assert.Equal(new[] { "https://feed.new-host.example/version.json", Primary, Fallback },
                FeedUrls.Order(null, moved, Primary, Fallback));
        }

        [Fact]
        public void A_custom_config_json_feed_still_wins_first()
        {
            const string custom = "https://my.cdn.example/version.json";
            var persisted = new[] { "https://feed.new-host.example/version.json" };

            Assert.Equal(custom, FeedUrls.Order(custom, persisted, Primary, Fallback)[0]);
        }

        [Fact]
        public void Persisted_non_https_urls_are_ignored()
        {
            var persisted = new[] { "http://evil.example/version.json", "file:///C:/x.json", "not a url" };

            Assert.Equal(new[] { Primary, Fallback }, FeedUrls.Order(null, persisted, Primary, Fallback));
        }

        [Fact]
        public void Duplicates_collapse_case_insensitively()
        {
            Assert.Single(FeedUrls.Order(Primary.ToUpperInvariant(), null, Primary, Primary));
            Assert.Single(FeedUrls.Order(null, new[] { Primary }, Primary, null));
        }

        [Fact]
        public void Sanitize_keeps_https_only_and_caps_the_list()
        {
            var many = Enumerable.Range(0, 9).Select(i => $"https://h{i}.example/version.json").ToList();
            many.Insert(0, "http://plain.example/version.json");

            var clean = FeedUrls.Sanitize(many);

            Assert.Equal(FeedUrls.MaxUrls, clean.Count);
            Assert.All(clean, u => Assert.StartsWith("https://", u));
        }

        [Fact]
        public void Old_feed_without_feed_urls_persists_nothing()
        {
            Assert.Empty(FeedUrls.Sanitize(null));
        }

        [Fact]
        public void Feed_json_round_trips_and_garbage_reads_empty()
        {
            var json = FeedUrls.ToJson(new[] { "https://a.example/version.json" });

            Assert.Equal(new[] { "https://a.example/version.json" }, FeedUrls.Parse(json));
            Assert.Contains("\"feed_urls\"", json);
            Assert.Empty(FeedUrls.Parse("{garbage"));
            Assert.Empty(FeedUrls.Parse(null));
            Assert.Empty(FeedUrls.Parse("{\"feed_urls\":[\"http://x.example/v.json\"]}"));
        }
    }
}
