using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RevitWebAppSync.Services;
using Xunit;

namespace Tests
{
    public class TelemetryEventTests
    {
        [Fact]
        public void MachineId_IsStable16HexLowercase()
        {
            var a = TelemetryIdentity.MachineId("PC-01", "drafter");
            var b = TelemetryIdentity.MachineId("PC-01", "drafter");
            Assert.Equal(a, b);
            Assert.Equal(16, a.Length);
            Assert.Matches("^[0-9a-f]{16}$", a);
            Assert.NotEqual(a, TelemetryIdentity.MachineId("PC-02", "drafter"));
        }

        [Fact]
        public void Create_ShapesWireContract()
        {
            var ev = TelemetryEvent.Create(
                "startup", "ready", "abc123", "ali@JKR-PC-07", "0.0.12", "2026", "1.2.0",
                new { error_class = "IOException" },
                new DateTime(2026, 7, 17, 3, 0, 0, DateTimeKind.Utc));
            var batch = JObject.Parse(TelemetryBatch.ToJson(new List<TelemetryEvent> { ev }));
            var j = (JObject)batch["events"][0];
            Assert.Equal("startup", (string)j["kind"]);
            Assert.Equal("ready", (string)j["stage"]);
            Assert.Equal("abc123", (string)j["machine_id"]);
            Assert.Equal("ali@JKR-PC-07", (string)j["operator"]);
            Assert.Equal("0.0.12", (string)j["addin_version"]);
            Assert.Equal("2026", (string)j["revit_year"]);
            Assert.Equal("1.2.0", (string)j["engine_version"]);
            Assert.Equal("IOException", (string)j["payload"]["error_class"]);
            Assert.Equal("2026-07-17T03:00:00Z", (string)j["occurred_at"]);
        }

        [Fact]
        public void Create_NullPayload_SerializesEmptyObject()
        {
            var ev = TelemetryEvent.Create("shutdown", "clean", "m", "op@pc", "1", "2026", "",
                null, DateTime.UtcNow);
            var j = JObject.Parse(TelemetryBatch.ToJson(new[] { ev }));
            Assert.Equal(JTokenType.Object, j["events"][0]["payload"].Type);
        }
    }
}
