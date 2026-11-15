// GatewayHealth decides two things: whether a cloud box may switch to engine
// mode (only after a recent passed check of the same gateway), and whether an
// engine-mode turn must run on the cloud because the gateway can't serve it.
// 0.0.86 switched on a gateway URL alone and every turn died 404
// "inference gateway disabled".

using System;
using RevitWebAppSync.Services;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class GatewayHealthTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

        private static GatewayHealthRecord Rec(GatewayState s, string url = "https://gw", double hoursAgo = 1) =>
            new GatewayHealthRecord { Url = url, State = s, CheckedUtc = Now.AddHours(-hoursAgo) };

        // ─── GET /gateway/health ───

        [Fact]
        public void Health_route_reports_inference_on()
            => Assert.Equal(GatewayState.Healthy, GatewayHealth.ClassifyHealth(200, "{\"ok\":true,\"inference_enabled\":true}"));

        [Fact]
        public void Health_route_reports_inference_off()
            => Assert.Equal(GatewayState.InferenceDisabled, GatewayHealth.ClassifyHealth(200, "{\"ok\":true,\"inference_enabled\":false}"));

        [Fact]
        public void Missing_health_route_asks_for_the_chat_probe()
            => Assert.Null(GatewayHealth.ClassifyHealth(404, "{\"detail\":\"Not Found\"}"));

        [Theory]
        [InlineData(200, "<html>captive portal</html>")]
        [InlineData(200, "{\"ok\":true}")]
        [InlineData(200, "{\"inference_enabled\":\"yes\"}")]
        [InlineData(500, "{\"inference_enabled\":true}")]
        [InlineData(502, "")]
        public void Odd_health_answers_are_unknown_not_healthy(int status, string body)
            => Assert.Equal(GatewayState.Unknown, GatewayHealth.ClassifyHealth(status, body));

        // ─── POST /gateway/v1/chat/completions without a token (older backends) ───

        [Fact]
        public void Chat_probe_404_means_inference_disabled()
            => Assert.Equal(GatewayState.InferenceDisabled, GatewayHealth.ClassifyChatProbe(404));

        [Theory]
        [InlineData(401)]
        [InlineData(402)]
        [InlineData(403)]
        public void Chat_probe_auth_refusal_means_inference_on(int status)
            => Assert.Equal(GatewayState.Healthy, GatewayHealth.ClassifyChatProbe(status));

        [Theory]
        [InlineData(200)]
        [InlineData(422)]
        [InlineData(500)]
        [InlineData(503)]
        public void Chat_probe_other_answers_are_unknown(int status)
            => Assert.Equal(GatewayState.Unknown, GatewayHealth.ClassifyChatProbe(status));

        // ─── Switching a cloud box to engine mode ───

        [Fact]
        public void Recent_healthy_check_of_the_same_gateway_allows_engine_mode()
            => Assert.True(GatewayHealth.AllowsEngineMode(Rec(GatewayState.Healthy), "https://gw/", Now));

        [Fact]
        public void No_check_yet_keeps_the_box_on_cloud()
            => Assert.False(GatewayHealth.AllowsEngineMode(null, "https://gw", Now));

        [Theory]
        [InlineData(GatewayState.InferenceDisabled)]
        [InlineData(GatewayState.Unreachable)]
        [InlineData(GatewayState.Unknown)]
        public void Anything_but_healthy_keeps_the_box_on_cloud(GatewayState s)
            => Assert.False(GatewayHealth.AllowsEngineMode(Rec(s), "https://gw", Now));

        [Fact]
        public void A_check_of_another_gateway_does_not_count()
            => Assert.False(GatewayHealth.AllowsEngineMode(Rec(GatewayState.Healthy, "https://staging-gw"), "https://prod-gw", Now));

        [Fact]
        public void A_stale_healthy_check_does_not_count()
            => Assert.False(GatewayHealth.AllowsEngineMode(Rec(GatewayState.Healthy, hoursAgo: 25), "https://gw", Now));

        [Fact]
        public void A_check_from_the_future_does_not_count()
            => Assert.False(GatewayHealth.AllowsEngineMode(Rec(GatewayState.Healthy, hoursAgo: -2), "https://gw", Now));

        // ─── Turns on a box already in engine mode ───

        [Theory]
        [InlineData(GatewayState.InferenceDisabled)]
        [InlineData(GatewayState.Unreachable)]
        public void Add_in_owned_engine_with_a_dead_gateway_runs_the_turn_on_cloud(GatewayState s)
            => Assert.True(GatewayHealth.TurnGoesToCloud(engineAutoSpawn: true, gatewayUrl: "https://gw", state: s));

        [Theory]
        [InlineData(GatewayState.Healthy)]
        [InlineData(GatewayState.Unknown)]   // a probe glitch never moves a working box
        public void Healthy_or_unknown_gateway_keeps_the_engine(GatewayState s)
            => Assert.False(GatewayHealth.TurnGoesToCloud(engineAutoSpawn: true, gatewayUrl: "https://gw", state: s));

        [Fact]
        public void Hand_started_engine_is_never_moved_to_cloud()
            // Without auto-spawn the engine was started by hand with its own
            // env; the config's gateway says nothing about what it uses.
            => Assert.False(GatewayHealth.TurnGoesToCloud(engineAutoSpawn: false, gatewayUrl: "https://gw", state: GatewayState.InferenceDisabled));

        // ─── Reuse of this process's answer ───

        [Fact]
        public void Healthy_answer_is_reused_for_ten_minutes()
        {
            var r = new GatewayHealthRecord { Url = "https://gw", State = GatewayState.Healthy, CheckedUtc = Now.AddMinutes(-9) };
            Assert.True(GatewayHealth.IsFresh(r, "https://gw", Now));
            r.CheckedUtc = Now.AddMinutes(-11);
            Assert.False(GatewayHealth.IsFresh(r, "https://gw", Now));
        }

        [Fact]
        public void Bad_answer_is_re_asked_after_two_minutes()
        {
            var r = new GatewayHealthRecord { Url = "https://gw", State = GatewayState.InferenceDisabled, CheckedUtc = Now.AddMinutes(-3) };
            Assert.False(GatewayHealth.IsFresh(r, "https://gw", Now));
        }

        [Fact]
        public void Answer_for_another_gateway_is_never_reused()
        {
            var r = new GatewayHealthRecord { Url = "https://a", State = GatewayState.Healthy, CheckedUtc = Now };
            Assert.False(GatewayHealth.IsFresh(r, "https://b", Now));
        }
    }
}
