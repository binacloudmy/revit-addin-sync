#nullable enable
// Live round 9 (F-t04): two set_parameters calls in one batch reached the same types. The second found every
// value already set; the engine turned it into noop, but it still said "Set … on 3 types; read back 3 of 3"
// with data.changed 3. WriteEnvelopes.MarkUnchanged makes it say nothing changed. Invented names.

using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class UnchangedWriteEnvelopeTests
    {
        private static Envelope SecondCall() => new Envelope
        {
            Ok = true, Status = EnvelopeStatus.Noop, Phase = EnvelopePhase.Commit, Count = 3,
            Summary = "Set Rated Current = 0.04 A on 3 types; read back 3 of 3. Type parameter: changes every instance of those types (40 Lighting Fixtures in the model).",
            Next = "One Undo in the receipt reverts this batch.",
            Data = JsonDocument.Parse("{\"param\":\"Rated Current\",\"type_param\":true,\"matched\":40,\"changed\":3,\"unchanged\":1,\"skipped_count\":0}").RootElement.Clone(),
            Verified = new VerifiedDto { Method = VerifyMethods.ParamReadback, Checked = 3 },
        };

        [Fact]
        public void A_write_that_changed_nothing_says_so()
        {
            var env = SecondCall();
            WriteEnvelopes.MarkUnchanged(env);

            Assert.Equal("Nothing to change: all 3 types already had Rated Current = 0.04 A.", env.Summary);
            Assert.DoesNotContain("read back", env.Summary);
            Assert.Equal(WriteEnvelopes.NoChangeNext, env.Next);
            var data = env.Data!.Value;
            Assert.Equal(0, data.GetProperty("changed").GetInt32());
            Assert.Equal(4, data.GetProperty("unchanged").GetInt32());
            Assert.Equal("Rated Current", data.GetProperty("param").GetString());
            Assert.Empty(EnvelopeInvariants.Violations(env));
        }

        [Fact]
        public void An_unknown_summary_still_reads_as_nothing_changed()
        {
            var env = SecondCall();
            env.Summary = "Something else.";
            WriteEnvelopes.MarkUnchanged(env);
            Assert.Equal("Nothing to change: every target already had the value.", env.Summary);
        }
    }
}
