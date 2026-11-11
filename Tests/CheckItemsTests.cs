#nullable enable
// The server's checks with their buttons, and the next steps (contracts 38ba371f): read, vetted, never run blind.
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class CheckItemsTests
    {
        private static TurnResultDto Read(string json) =>
            TurnResultReader.TryRead(JsonDocument.Parse(json).RootElement.Clone())!;

        private const string Result = @"{
          ""reply"": ""52 downlights isolated."", ""kind"": ""selection"",
          ""checks"": [
            { ""severity"": ""warning"", ""text"": ""2 up/down lights were left out. They are a different family."",
              ""actions"": [
                { ""label"": ""Add these 2"", ""kind"": ""set_parameters"", ""args"": { ""target"": { ""ids"": [11, 12] }, ""param"": ""Comments"", ""value"": ""in"" } },
                { ""label"": ""Show them"", ""kind"": ""prompt"", ""args"": { ""text"": ""Show the 2 up/down lights"" } },
                { ""label"": ""Leave out"", ""kind"": ""dismiss"" } ] }
          ],
          ""next"": [""Fill these from a brochure"", ""Which have empty Model?""]
        }";

        [Fact]
        public void TheReaderKeepsNext_AndTheChecksWithTheirActions()
        {
            var r = Read(Result);

            Assert.Equal(new[] { "Fill these from a brochure", "Which have empty Model?" }, r.Next);
            Assert.Equal(3, Assert.Single(r.Checks!).Actions!.Count);
            Assert.Empty(TurnResultInvariants.Violations(r));
        }

        [Fact]
        public void MoreThanThreeNext_OrALongOne_IsAViolation()
        {
            var r = Read(@"{""reply"":""x"",""kind"":""changes"",""next"":[""a"",""b"",""c"",""d""]}");
            Assert.Contains(TurnResultInvariants.Violations(r), v => v.Contains("next has more than 3"));

            var long61 = Read(@"{""reply"":""x"",""kind"":""changes"",""next"":[""" + new string('x', 61) + @"""]}");
            Assert.Contains(TurnResultInvariants.Violations(long61), v => v.Contains("next[0]"));

            var empty = Read(@"{""reply"":""x"",""kind"":""changes"",""next"":[""""]}");
            Assert.Contains(TurnResultInvariants.Violations(empty), v => v.Contains("next[0]"));
        }

        [Fact]
        public void ANullInsideNext_IsDropped()
        {
            Assert.Equal(new[] { "a" }, Read(@"{""reply"":""x"",""kind"":""changes"",""next"":[null,""a""]}").Next);
        }

        [Fact]
        public void TheCardGetsTheChecks_AndTheNextSteps()
        {
            var card = new ResultCardModel { Kind = ResultKind.Selection }.WithServerResult(Read(Result));

            var check = Assert.Single(card.Checks);
            Assert.Equal("warning", check.Severity);
            Assert.Equal(new[] { "Add these 2", "Show them", "Leave out" }, check.Actions.Select(a => a.Label).ToArray());
            Assert.Equal(2, card.Next.Count);
        }

        [Fact]
        public void NoServerResult_MeansNothingExtraOnTheCard()
        {
            var card = new ResultCardModel { Kind = ResultKind.Changes }.WithServerResult(null);

            Assert.Empty(card.Checks);
            Assert.Empty(card.Next);
        }

        [Fact]
        public void OnlyThreeNextAreKept_AndBlankOnesGo()
        {
            var r = new TurnResultDto { Reply = "x", Kind = "changes", Next = new List<string> { " a ", "", "b", "c", "d" } };

            Assert.Equal(new[] { "a", "b", "c" }, CheckItems.NextOf(r));
        }

        [Theory]
        [InlineData("set_parameters", @"{""target"":{""ids"":[1]},""param"":""P"",""value"":""v""}", true)]
        [InlineData("set_parameters", @"{""from_table"":{""table"":""t""},""param"":""P""}", false)]     // never from the drafter's table
        [InlineData("set_parameters", null, false)]
        [InlineData("prompt", @"{""text"":""Go on""}", true)]
        [InlineData("prompt", @"{""text"":""  ""}", false)]
        [InlineData("prompt", @"{}", false)]
        [InlineData("dismiss", null, true)]
        [InlineData("run_code", @"{}", false)]                                                           // an unknown kind is never offered
        public void OnlyAButtonThePaneCanRunIsOffered(string kind, string? args, bool offered)
        {
            var a = new CheckAction { Label = "x", Kind = kind, Args = args == null ? null : JsonDocument.Parse(args).RootElement.Clone() };

            Assert.Equal(offered, CheckItems.Vet(a) == null);
        }

        [Fact]
        public void AnUnrunnableButton_IsLeftOutOfTheCheck_TheTextStays()
        {
            var r = Read(@"{""reply"":""x"",""kind"":""changes"",""checks"":[{""severity"":""info"",""text"":""Look"",""actions"":[
              {""label"":""Bad"",""kind"":""run_code"",""args"":{}},{""label"":""Ok"",""kind"":""dismiss""}]}]}");

            var c = Assert.Single(CheckItems.From(r));
            Assert.Equal("Look", c.Text);
            Assert.Equal("Ok", Assert.Single(c.Actions).Label);
        }

        [Fact]
        public void ASetParametersButton_RunsItsArgsAsTheServerSentThem_UnderACardId()
        {
            var a = new CheckAction { Label = "Add these 2", Kind = "set_parameters", Args = JsonDocument.Parse(@"{""target"":{""ids"":[11,12]},""param"":""Comments"",""value"":""in""}").RootElement.Clone() };

            var call = CheckItems.SetParametersCall(a)!;

            Assert.Equal(ToolNames.SetParameters, call.Tool);
            Assert.StartsWith("card_", call.CallId);
            Assert.StartsWith("card:", call.IdempotencyKey);
            Assert.Equal("Comments", call.Args.GetProperty("param").GetString());
            Assert.Equal(2, call.Args.GetProperty("target").GetProperty("ids").GetArrayLength());
            Assert.Null(CheckItems.SetParametersCall(new CheckAction { Kind = "prompt" }));
        }

        [Fact]
        public void APromptButton_SendsItsText()
        {
            var a = new CheckAction { Kind = "prompt", Args = JsonDocument.Parse(@"{""text"":"" Show the 2 ""}").RootElement.Clone() };

            Assert.Equal("Show the 2", CheckItems.PromptText(a));
        }
    }
}
