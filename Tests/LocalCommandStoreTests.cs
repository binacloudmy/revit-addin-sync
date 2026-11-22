#nullable enable
// Saved commands live on this PC (copilot-commands.json), not on the account: the store, its slugs, the template
// expansion that runs a command without the backend, the catalog merge, and that nothing here asks the backend.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    [Collection("ToolCatalog")]
    public sealed class LocalCommandStoreTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bina-cmds-" + Guid.NewGuid().ToString("N"));
        private string FilePath => Path.Combine(_dir, LocalCommandStore.FileName);

        public LocalCommandStoreTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (Exception) { } }

        private LocalCommandStore Store(params string[] reserved) => new LocalCommandStore(FilePath, () => reserved);

        private static SavedCommandDraft Draft(string name = "Fill lights for level", string template = "Fill the lights on {level} from the brochure", bool withInput = true)
        {
            var d = new SavedCommandDraft { Name = name, Template = template, ToolsCalled = new List<string> { "find_elements", "set_parameters" } };
            if (withInput) d.Inputs.Add(new SlashInput { Name = "level", Type = "text", Required = true, Label = "Level 1" });
            return d;
        }

        private static string Raw(string path) => File.ReadAllText(path);

        // ---- round trip ----

        [Fact]
        public void ASavedCommand_SurvivesANewStoreOnTheSameFile()
        {
            var t0 = new DateTime(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc);
            var (saved, error) = Store().Add(Draft(), t0);
            Assert.Null(error);
            Assert.Equal("my-fill-lights-for-level", saved!.Id);

            var back = Assert.Single(Store().All());     // a second add-in session, same Windows user
            Assert.Equal("my-fill-lights-for-level", back.Id);
            Assert.Equal("Fill lights for level", back.Name);
            Assert.Equal("Fill the lights on {level} from the brochure", back.Template);
            var input = Assert.Single(back.Inputs);
            Assert.Equal(("level", "text", true, "Level 1"), (input.Name, input.Type, input.Required, input.Label));
            Assert.Equal(new[] { "find_elements", "set_parameters" }, back.ToolsCalled.ToArray());
            Assert.Equal(t0, back.CreatedUtc);
            Assert.Equal(0, back.RunCount);
            Assert.Equal(LocalCommandStore.SchemaVersion, JsonConvert.DeserializeObject<Dictionary<string, object>>(Raw(FilePath))!["version"] is long v ? (int)v : -1);
        }

        [Fact]
        public void Update_KeepsTheSlug_ChangesTheRest_AndStampsTheEditTime()
        {
            var t0 = new DateTime(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc);
            var (saved, _) = Store().Add(Draft(), t0);
            var edit = SavedCommandDraft.FromTool(LocalCommandStore.ToTool(saved!));
            edit.Name = "Fill lights, any level";
            edit.Template = "Fill the lights on {level}";

            var (updated, error) = Store().Update(saved!.Id, edit, t0.AddHours(2));

            Assert.Null(error);
            Assert.Equal("my-fill-lights-for-level", updated!.Id);          // pins and history keep pointing at it
            var back = Assert.Single(Store().All());
            Assert.Equal("Fill lights, any level", back.Name);
            Assert.Equal(t0, back.CreatedUtc);
            Assert.Equal(t0.AddHours(2), back.EditedUtc);
        }

        [Fact]
        public void UpdatingACommandThatIsGone_IsNullWithoutAnError()
        {
            var (c, error) = Store().Update("my-nothing", Draft());
            Assert.Null(c);
            Assert.Null(error);
        }

        [Fact]
        public void Delete_RemovesIt_AndSaysWhenItWasAlreadyGone()
        {
            var store = Store();
            var (a, _) = store.Add(Draft("One"));
            store.Add(Draft("Two"));

            Assert.True(store.Delete(a!.Id));
            Assert.False(store.Delete(a.Id));
            Assert.Equal(new[] { "my-two" }, Store().All().Select(c => c.Id).ToArray());
        }

        [Fact]
        public void CountRun_AddsOne_AndPersists_ForASavedCommandOnly()
        {
            var store = Store();
            var (c, _) = store.Add(Draft());

            Assert.True(store.CountRun(c!.Id));
            Assert.True(store.CountRun(c.Id));
            Assert.False(store.CountRun("quick-create"));       // a curated command is not ours to count
            Assert.False(store.CountRun(null));

            Assert.Equal(2, Store().Find(c.Id)!.RunCount);
        }

        // ---- the file is missing, empty or broken ----

        [Fact]
        public void AMissingFile_IsAnEmptyStore_AndNothingIsCreatedByReading()
        {
            Assert.Empty(Store().All());
            Assert.False(File.Exists(FilePath));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   \n")]
        public void AnEmptyFile_IsAnEmptyStore(string text)
        {
            File.WriteAllText(FilePath, text);
            Assert.Empty(Store().All());
        }

        [Theory]
        [InlineData("not json at all")]
        [InlineData("[]")]
        [InlineData("{\"version\":1,\"commands\":\"nope\"}")]
        [InlineData("{\"version\":1,\"commands\":[{\"id\":5}")]
        public void ACorruptFile_IsAnEmptyStore_ItIsKeptBesideAsBad_AndTheStoreStillWorks(string text)
        {
            File.WriteAllText(FilePath, text);
            var store = Store();

            Assert.Empty(store.All());
            Assert.Equal(text, Raw(FilePath + ".bad"));

            var (saved, error) = store.Add(Draft());
            Assert.Null(error);
            Assert.Equal(saved!.Id, Assert.Single(Store().All()).Id);              // a good file now
            Assert.Equal(text, Raw(FilePath + ".bad"));                             // the broken one was not lost
        }

        [Fact]
        public void RowsWithoutAnIdOrAName_AreSkipped_TheRestRead()
        {
            File.WriteAllText(FilePath, "{\"version\":1,\"commands\":[{\"id\":\"\",\"name\":\"x\"},{\"id\":\"my-a\",\"name\":\"A\",\"template\":\"t\"},{\"id\":\"my-b\",\"name\":\"\"}]}");

            Assert.Equal("my-a", Assert.Single(Store().All()).Id);
        }

        [Fact]
        public void AFileFromANewerAddIn_IsReadAsFarAsItGoes_AndKeptBeside()
        {
            File.WriteAllText(FilePath, "{\"version\":9,\"commands\":[{\"id\":\"my-a\",\"name\":\"A\",\"template\":\"t\",\"future_field\":1}]}");

            Assert.Equal("my-a", Assert.Single(Store().All()).Id);
            Assert.True(File.Exists(FilePath + ".bad"));
        }

        [Fact]
        public void AWrite_LeavesNoTempFileBehind_AndReplacesThePreviousFileWhole()
        {
            var store = Store();
            store.Add(Draft("One"));
            store.Add(Draft("Two"));

            Assert.False(File.Exists(FilePath + ".tmp"));
            Assert.Equal(2, JsonConvert.DeserializeObject<LocalCommandFileProbe>(Raw(FilePath))!.Commands.Count);
        }

        private sealed class LocalCommandFileProbe { [JsonProperty("commands")] public List<object> Commands = new List<object>(); }

        [Fact]
        public void WhenTheFolderCannotBeWritten_AddSaysSo_InsteadOfThrowing()
        {
            // The "folder" is a file: the directory cannot be created.
            var blocked = Path.Combine(_dir, "blocked");
            File.WriteAllText(blocked, "x");
            var store = new LocalCommandStore(Path.Combine(blocked, LocalCommandStore.FileName));

            var (c, error) = store.Add(Draft());

            Assert.Null(c);
            Assert.StartsWith("Could not save on this PC", error);
        }

        // ---- slugs ----

        [Fact]
        public void TheSameNameTwice_GetsNumberedSlugs()
        {
            var store = Store();

            var ids = new[] { store.Add(Draft("Fill lights")).Command!.Id, store.Add(Draft("Fill lights")).Command!.Id, store.Add(Draft("fill   LIGHTS!")).Command!.Id };

            Assert.Equal(new[] { "my-fill-lights", "my-fill-lights-2", "my-fill-lights-3" }, ids);
        }

        [Fact]
        public void ASlugNeverTakesAnIdTheCuratedTierHas()
        {
            var store = Store("my-fill-lights");

            Assert.Equal("my-fill-lights-2", store.Add(Draft("Fill lights")).Command!.Id);
        }

        [Fact]
        public void ADeletedSlug_CanBeTakenAgain_AndALongNameStaysWithinTheLimit()
        {
            var store = Store();
            var a = store.Add(Draft("Fill lights")).Command!;
            store.Delete(a.Id);
            Assert.Equal("my-fill-lights", store.Add(Draft("Fill lights")).Command!.Id);

            var longName = new string('x', 70);
            var first = store.Add(Draft(longName)).Command!.Id;
            var second = store.Add(Draft(longName)).Command!.Id;
            Assert.Equal(43, first.Length);                                         // my- plus 40
            Assert.NotEqual(first, second);
            Assert.True(second.Length <= 43);
            Assert.EndsWith("-2", second);
        }

        [Theory]
        [InlineData("my-a", new[] { "my-a" }, "my-a-2")]
        [InlineData("my-a", new[] { "my-a", "my-a-2" }, "my-a-3")]
        [InlineData("my-a", new string[0], "my-a")]
        public void UniqueSlug_NumbersFromTwo(string slug, string[] taken, string expected)
        {
            Assert.Equal(expected, LocalCommandStore.UniqueSlug(slug, taken));
        }

        // ---- what is refused ----

        [Fact]
        public void ADraftWithoutANameOrAPrompt_IsRefusedWithASentence_AndWritesNothing()
        {
            var store = Store();

            Assert.Equal("Give the command a name.", store.Add(Draft("  ")).Error);
            Assert.Equal("The command needs a prompt.", store.Add(Draft("Name", "  ")).Error);
            Assert.StartsWith("The name can be at most", store.Add(Draft(new string('n', 81))).Error);
            Assert.StartsWith("The prompt can be at most", store.Add(Draft("Name", new string('p', 4001), withInput: false)).Error);
            var many = Draft();
            for (var i = 0; i < 9; i++) many.Inputs.Add(new SlashInput { Name = "i" + i });
            Assert.StartsWith("At most 8 inputs", store.Add(many).Error);
            Assert.False(File.Exists(FilePath));
        }

        // ---- the catalog's Mine tier ----

        [Fact]
        public void TheCatalogsMineTier_IsTheCommandsOfThisPC_FirstThenCurated()
        {
            var store = Store();
            var (c, _) = store.Add(Draft());
            store.CountRun(c!.Id);
            try
            {
                ToolCatalog.MergeMine(store.ToTools());

                Assert.Equal("my-fill-lights-for-level", ToolCatalog.All[0].Id);
                var tool = ToolCatalog.ById("my-fill-lights-for-level")!;
                Assert.Equal("Mine", tool.Category);
                Assert.True(tool.Editable);
                Assert.Equal(1, tool.RunCount);
                Assert.Equal("Fill the lights on {level} from the brochure", tool.PromptTemplate);
                var input = Assert.Single(tool.Inputs);
                Assert.Equal(("level", "Level 1"), (input.Name, input.Label));
                Assert.Equal(ToolCatalog.Curated.Count + 1, ToolCatalog.All.Count);       // the curated tier is untouched
                Assert.NotNull(ToolCatalog.ById("create"));
            }
            finally { ToolCatalog.MergeMine(Array.Empty<SlashTool>()); }
        }

        [Fact]
        public void ADeletedCommand_LeavesTheCatalogOnTheNextMerge()
        {
            var store = Store();
            var (c, _) = store.Add(Draft());
            try
            {
                ToolCatalog.MergeMine(store.ToTools());
                store.Delete(c!.Id);
                ToolCatalog.MergeMine(store.ToTools());

                Assert.Null(ToolCatalog.ById(c.Id));
                Assert.Equal(ToolCatalog.Curated.Count, ToolCatalog.All.Count);
            }
            finally { ToolCatalog.MergeMine(Array.Empty<SlashTool>()); }
        }

        [Fact]
        public void TheDefaultStore_LivesBesideThePrefsFile_AndReservesTheCuratedIds()
        {
            var path = LocalCommandStore.Default.FilePath;

            Assert.Equal("copilot-commands.json", Path.GetFileName(path));
            Assert.Equal("RevitWebAppSync", Path.GetFileName(Path.GetDirectoryName(path)));
            Assert.Equal(Path.GetDirectoryName(path), Path.GetDirectoryName(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitWebAppSync", "copilot-prefs.json")));
        }

        // ---- running one: the template is expanded here, the backend never sees a command id ----

        [Fact]
        public void TheTemplateIsFilledWithTheTypedInputs_ThenTheFreeText()
        {
            var text = SlashPromptText.Build("Fill lights", "Fill the lights on {level} using {sheet}", "and skip the ones on the roof",
                new Dictionary<string, object> { ["level"] = "Level 3", ["sheet"] = 12 });

            Assert.Equal("Fill the lights on Level 3 using 12\nand skip the ones on the roof", text);
        }

        [Fact]
        public void AHoleWithNoValue_StaysAsItIs_AndAValueThatLooksLikeAHole_IsNeverFilledAgain()
        {
            var text = SlashPromptText.Build("c", "A {one} B {two}", null, new Dictionary<string, object> { ["one"] = "{two}" });

            Assert.Equal("A {two} B {two}", text);
        }

        [Fact]
        public void ANullValue_FillsAnEmptyString_AndNoFreeTextAddsNothing()
        {
            Assert.Equal("Fill ", SlashPromptText.Build("c", "Fill {x}", "  ", new Dictionary<string, object> { ["x"] = null! }));
        }

        [Fact]
        public void NoTemplate_FallsBackToTheNameAndTheTypedText()
        {
            Assert.Equal("Open View", SlashPromptText.Build("Open View", null, "", null));
            Assert.Equal("Open View: Level 1", SlashPromptText.Build("Open View", " ", "Level 1", null));
        }

        [Fact]
        public void ASavedCommandRoundTrip_AsTheSheetEditsIt_RunsToTheSamePromptAsBefore()
        {
            var (saved, _) = Store().Add(Draft());
            var tool = LocalCommandStore.ToTool(saved!);

            var prompt = SlashPromptText.Build(tool.Name, tool.PromptTemplate, "", new Dictionary<string, object> { ["level"] = "Level 2" });

            Assert.Equal("Fill the lights on Level 2 from the brochure", prompt);
        }

        // ---- nothing here asks the backend ----

        private static string Root()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return dir?.FullName ?? "/Users/ashraf/development/bina/lh/addin-jumble";
        }

        [Fact]
        public void SaveUpdateDeleteListAndRun_MakeNoBackendCall()
        {
            var commands = File.ReadAllText(Path.Combine(Root(), "UI/Copilot/CopilotViewModel.Commands.cs"));
            foreach (var call in new[] { "AIService", ".SaveCommandAsync", ".UpdateCommandAsync", ".DeleteCommandAsync(", ".GetCommandsAsync", "HttpClient", "SavedCommandsEtag = svc" })
                Assert.DoesNotContain(call, commands);
            Assert.Contains("LocalCommandStore.Default.Delete(", commands);
            Assert.Contains("store.Add(d)", commands);
            Assert.Contains("store.Update(d.EditingId, d)", commands);
            Assert.Contains("ToolCatalog.MergeMine(LocalCommandStore.Default.ToTools())", commands);

            // Running expands the template in the add-in and sends it as a normal prompt; no command id leaves the pane.
            var vm = File.ReadAllText(Path.Combine(Root(), "UI/Copilot/CopilotViewModel.cs"));
            Assert.Contains("SlashPromptText.Build(tool?.Name, tool?.PromptTemplate, typed, inputs)", vm);
            Assert.DoesNotContain(".BackendId", vm);
            Assert.DoesNotContain("command_id", vm);
        }

        [Fact]
        public void TheSaveSheetsGateAndMessages_AreUnchanged()
        {
            var commands = File.ReadAllText(Path.Combine(Root(), "UI/Copilot/CopilotViewModel.Commands.cs"));

            Assert.Contains("Sign in to save commands.", commands);
            Assert.Contains("Saved — run it any time with `/{command.Id}`.", commands);
            Assert.Contains("Updated `/{command.Id}`.", commands);
            Assert.Equal("my-fill-lights", SavedCommandDraft.SuggestSlug("Fill lights"));    // the sheet's slug helper
        }

        [Fact]
        public void TheOldBackendMineTierIsNotReadAnyMore_AndItsCacheIsDropped()
        {
            var commands = File.ReadAllText(Path.Combine(Root(), "UI/Copilot/CopilotViewModel.Commands.cs"));

            Assert.DoesNotContain("RestoreCachedMine", commands);
            Assert.DoesNotContain("FromCatalogEntry", commands);
            Assert.Contains("prefs.SavedCommandsJson = \"\";", commands);
        }
    }
}
