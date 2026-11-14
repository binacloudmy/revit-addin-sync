#nullable enable
// Result marks in the model, build step 2 ("Rows and marks point at each other"): what a row click focuses (bright and
// dim marks, the label outside the group and where it goes, the key's focus line with Show all), what a mark click
// finds (the row, "3 of 8", the line the card row flashes and the label next to the mark), what each card key does,
// and the section header's buttons. The fixture is ResultMarksTests' tag run (58 door tags in ten invented door types).

using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Copilot.Highlights;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ResultMarksFocusTests
    {
        private static HighlightSet Set() => ResultMarksTests.Set();
        private static MarkGroup Row(HighlightSet set, string code) => set.Rows.Single(r => r.Title.StartsWith(code));

        // ─── A row clicked (state 2) ──────────────────────────────────────────

        [Fact]
        public void ARowFocused_ItsMarksAreBright_TheOthersDim_AndNoFocusIsTheUsualMark()
        {
            var set = Set();
            var da = Row(set, "(DA20)");
            var focused = new HashSet<long>(MarkFocus.ElementIds(da, set)!);
            var tones = set.Kinds[0].Ids.Select(id => MarkFocus.ToneOf(id, focused)).ToList();

            Assert.Equal(20, tones.Count(t => t == MarkTone.Bright));
            Assert.Equal(38, tones.Count(t => t == MarkTone.Dim));
            Assert.All(set.Kinds[0].Ids, id => Assert.Equal(MarkTone.Normal, MarkFocus.ToneOf(id, null)));
        }

        [Fact]
        public void TheToneIsOneMoreKeyInTheImageName_AndTheUsualImageKeepsStepOnesName()
        {
            Assert.Equal("", MarkFocus.ToneSuffix(MarkTone.Normal));
            Assert.Equal("-bright", MarkFocus.ToneSuffix(MarkTone.Bright));
            Assert.Equal("-dim", MarkFocus.ToneSuffix(MarkTone.Dim));
        }

        [Fact]
        public void TheGroupLabel_NamesTheRowAndCountsItsElements_AndSaysWhenOnlySomeWereFound()
        {
            var set = Set();
            var da = Row(set, "(DA20)");
            var label = MarkFocus.GroupLabel(da, 20, MarkFocus.CategoryOf(da, set));
            Assert.Equal("(DA20) 750 x 2100 single · 20 door tags", label.Text);
            Assert.Equal("(DA20) 750 x 2100 single", label.Bold);
            Assert.False(label.Below);

            // The server kept one id of four (the backend ask): the label does not claim four.
            var dd = Row(set, "(DD04)");
            Assert.Equal("(DD04) 1000 x 2100 sliding · 1 of 4 door tags", MarkFocus.GroupLabel(dd, 1, MarkFocus.CategoryOf(dd, set)).Text);
            Assert.Equal("(DJ01) 900 x 2100 access · 1 door tag", MarkFocus.GroupLabel(new MarkGroup { Title = "(DJ01) 900 x 2100 access", Count = 1 }, 1, "Door Tags").Text);
            Assert.Equal("Wall group · 3 elements", MarkFocus.GroupLabel(new MarkGroup { Title = "Wall group", Count = 3 }, 3, "").Text);
        }

        [Fact]
        public void TheKeysFocusLine_ShowsTheRowsShortNameAndCount_WithShowAll()
        {
            var set = Set();
            var key = MarkKey.Build(set, null, "Level 1 - Plan (WIP)");
            Assert.False(key.ShowAll);
            MarkFocus.Apply(key, Row(set, "(DA20)"), 20, null);

            Assert.Equal("Showing (DA20) · 20 of 58", key.Focus);
            Assert.Equal("(DA20)", key.FocusBold);
            Assert.True(key.ShowAll);
        }

        [Theory]
        [InlineData("(DA20) 750 x 2100 single", "(DA20)")]
        [InlineData("Basic Wall: Generic 200", "Basic Wall: Generic 200")]
        [InlineData("(not a code at all because it is long) x", "(not a co…ause it is long) x")]
        public void TheShortName_IsTheLeadingCode_ElseTheNameCutInItsMiddle(string title, string expected) =>
            Assert.Equal(expected, MarkFocus.ShortTitle(title));

        [Fact]
        public void TheLabel_GoesAboveTheGroup_BelowWhenThereIsNoRoom_AndStaysInsideTheView()
        {
            // A group in the middle of a 1000 x 600 view: centred above it.
            Assert.Equal((440.0, 172.0), MarkFocus.LabelAt(400, 200, 600, 300, 120, 20, 1000, 600, below: false));
            // A group at the top edge: under it.
            Assert.Equal((440.0, 108.0), MarkFocus.LabelAt(400, 10, 600, 100, 120, 20, 1000, 600, below: false));
            // A clicked mark: under it; at the bottom edge, above it.
            Assert.Equal((440.0, 308.0), MarkFocus.LabelAt(400, 200, 600, 300, 120, 20, 1000, 600, below: true));
            Assert.Equal((440.0, 552.0), MarkFocus.LabelAt(400, 580, 600, 590, 120, 20, 1000, 600, below: true));
            // At the right edge: pulled in.
            Assert.Equal(872.0, MarkFocus.LabelAt(960, 200, 1000, 240, 120, 20, 1000, 600, below: false).X);
            Assert.Null(MarkFocus.Box(new List<(double, double)>(), 11));
            Assert.Equal((89.0, 9.0, 211.0, 61.0), MarkFocus.Box(new[] { (100.0, 20.0), (200.0, 50.0) }, 11));
        }

        [Fact]
        public void ACategoryRow_ShowsTheResultsGroupOfThatCategory_AndARowWithoutIdsHasNothingToShow()
        {
            var set = Set();
            var cat = MarkFocus.ForCategory("Door Tags", 58);
            Assert.True(MarkFocus.CanShow(cat));
            Assert.Equal(58, MarkFocus.ElementIds(cat, set)!.Count);

            var noIds = MarkFocus.ForItemRow(new ItemRow { Kind = "tag", Title = "(DG02) 900 x 2100 glazed", Count = 2 });
            Assert.False(MarkFocus.CanShow(noIds));
            Assert.Equal(MarkKind.Annotation, noIds.Kind);
            Assert.Equal(2, noIds.Count);

            var type = MarkFocus.ForTypeGroup(new TypeGroup { TypeName = "Standard", Label = "Alpha Downlight: Standard", IsType = true, Ids = { 11 }, Instances = 26, Category = "Lighting Fixtures" });
            Assert.Equal(MarkKind.TypeInstances, type.Kind);
            Assert.Null(MarkFocus.ElementIds(type, set));   // Revit finds a type's instances
            Assert.Equal(26, type.Count);
            Assert.Equal("Alpha Downlight: Standard", type.Title);
        }

        // ─── A mark clicked (state 3) ─────────────────────────────────────────

        private static List<(MarkGroup, IReadOnlyList<long>)> Groups(HighlightSet set) =>
            set.Rows.Select(r => (r, (IReadOnlyList<long>)r.Ids)).Concat(set.Kinds.Select(k => (k, (IReadOnlyList<long>)k.Ids))).ToList();

        [Fact]
        public void AClickedMark_FindsItsRowAndPlace_AndTheWordsForTheCardAndTheLabel()
        {
            var set = Set();
            // The third tag of the third door type (DA20 has tags 0-19, DB12 20-31, DC08 32-39).
            var at = MarkFocus.Locate(300034, Groups(set));
            Assert.Equal((2, 3, 8), at);

            var row = set.Rows[at!.Value.Index];
            var click = MarkFocus.Click("t1", 300034, 77, "Door Tags", "P3", row, at.Value.N, at.Value.M);
            Assert.Equal("Tag 3 of 8 selected in Revit", click.Line);
            Assert.Equal("Door tag P3 · (DC08) 900 x 2100 single · 3 of 8", click.Label);
            var label = MarkFocus.ClickLabel(click);
            Assert.Equal("Door tag P3", label.Bold);
            Assert.Equal(" · (DC08) 900 x 2100 single · 3 of 8", label.Rest);
            Assert.True(label.Below);

            var key = MarkKey.Build(set, null, "Level 1 - Plan (WIP)");
            MarkFocus.Apply(key, row, 8, click);
            Assert.Equal("Showing (DC08) · tag 3 of 8 selected", key.Focus);
            Assert.Equal("(DC08)", key.FocusBold);
            Assert.True(key.ShowAll);
        }

        [Fact]
        public void AMarkInNoRow_FallsBackToItsKind_AndAnElementInNoGroupIsJustSelected()
        {
            var set = Set();
            // Tag 55 is past the ids the server kept: no row has it, the made group does.
            var at = MarkFocus.Locate(300055, Groups(set));
            Assert.Equal(set.Rows.Count, at!.Value.Index);
            Assert.Equal((56, 58), (at.Value.N, at.Value.M));
            Assert.Null(MarkFocus.Locate(999, Groups(set)));

            var alone = MarkFocus.Click("t1", 999, -1, "Walls", null, null, 0, 0);
            Assert.Equal("Wall selected in Revit", alone.Line);
            Assert.Equal("Wall", alone.Label);
            Assert.Equal("Showing wall selected", MarkFocus.ClickFocusLine(alone).Line);
        }

        [Fact]
        public void TheCardScrollsToTheRowOfTheClick_ByIdsByTypeOrByCategory()
        {
            var (card, _) = ResultMarksTests.TagRun();
            var rows = card.Items!.Sections.SelectMany(s => s.Rows).Select(r => (MarkGroup?)MarkFocus.ForItemRow(r)).ToList();
            var click = new MarkClick { Id = 300034, RowTitle = "(DC08) 900 x 2100 single", Category = "Door Tags" };
            Assert.Equal("(DC08) 900 x 2100 single", rows[MarkFocus.PickRow(rows, click)]!.Title);

            // A category row (the Created card's "Door Tags 58") and a type row (its instance's type).
            var cat = new List<MarkGroup?> { null, MarkFocus.ForCategory("Door Tags", 58) };
            Assert.Equal(1, MarkFocus.PickRow(cat, new MarkClick { Id = 300055, Category = "Door Tags" }));
            var types = new List<MarkGroup?> { MarkFocus.ForTypeGroup(new TypeGroup { TypeName = "A", IsType = true, Ids = { 11 } }), MarkFocus.ForTypeGroup(new TypeGroup { TypeName = "B", IsType = true, Ids = { 12 } }) };
            Assert.Equal(1, MarkFocus.PickRow(types, new MarkClick { Id = 5000, TypeId = 12 }));
            Assert.Equal(-1, MarkFocus.PickRow(types, new MarkClick { Id = 5000, TypeId = 13 }));
        }

        [Theory]
        [InlineData("Door Tags", "Tag", "Door tag")]
        [InlineData("Walls", "Wall", "Wall")]
        [InlineData("Lighting Fixtures", "Fixture", "Lighting fixture")]
        [InlineData("", "Element", "Element")]
        public void TheWordsForOneElement(string category, string word, string full)
        {
            Assert.Equal(word, MarkFocus.Word(category));
            Assert.Equal(full, MarkFocus.Full(category));
        }

        // ─── What is on screen (live 14 Nov: "Showing all 52" over an empty canvas) ──

        private static readonly Vec3 Right = new Vec3(1, 0, 0), Up = new Vec3(0, 1, 0);

        [Fact]
        public void OnScreen_CountsTheMarksInsideTheViewsVisibleRectangle()
        {
            var marks = new[] { new Vec3(10, 10, 0), new Vec3(50, 50, 3), new Vec3(150, 50, 0), new Vec3(-5, 20, 0) };
            Assert.Equal(2, MarkKey.OnScreen(marks, new Vec3(0, 0, 0), new Vec3(100, 100, 0), Right, Up));
            Assert.Equal(2, MarkKey.OnScreen(marks, new Vec3(100, 100, 0), new Vec3(0, 0, 0), Right, Up));   // corner order
            // The view zoomed far away from the model: none.
            Assert.Equal(0, MarkKey.OnScreen(marks, new Vec3(5000, 5000, 0), new Vec3(5100, 5100, 0), Right, Up));
            // A degenerate rectangle shows none.
            Assert.Equal(0, MarkKey.OnScreen(marks, new Vec3(0, 0, 0), new Vec3(0, 100, 0), Right, Up));
        }

        private static KeyText Key52()
        {
            var set = new HighlightSet { Kinds = { new MarkGroup { Title = "Lighting Fixtures", Category = "Lighting Fixtures", Outcome = MarkOutcome.Changed, Ids = Enumerable.Range(1, 52).Select(i => (long)i).ToList(), Count = 52 } } };
            var places = set.Kinds[0].Ids.Select(id => new ElementPlace { Id = id, Group = 0, InActiveView = true }).ToList();
            return MarkKey.Build(set, places, "{3D}");
        }

        [Fact]
        public void AllOnScreen_TheFocusLineIsAsBefore()
        {
            var key = Key52();
            var before = key.Focus;
            MarkKey.ApplyOnScreen(key, drawn: 52, onScreen: 52);
            Assert.Equal(before, key.Focus);
            Assert.StartsWith("Showing all 52", key.Focus);
            Assert.Null(key.ZoomLabel);
        }

        [Fact]
        public void SomeOffScreen_ShowsHowManyAreOnScreen_WithZoomToAll()
        {
            var key = Key52();
            MarkKey.ApplyOnScreen(key, 52, 20);
            Assert.Equal("Showing 20 of 52 on screen", key.Focus);
            Assert.Equal("Zoom to all", key.ZoomLabel);
            Assert.False(key.ZoomProminent);
        }

        [Fact]
        public void NoneOnScreen_NeverSaysShowingAll_AndTheZoomIsTheKeysProminentAction()
        {
            var key = Key52();
            MarkKey.ApplyOnScreen(key, 52, 0);
            Assert.Equal("None of the 52 is on screen", key.Focus);
            Assert.DoesNotContain("Showing all", key.Focus);
            Assert.Equal("Zoom to them", key.ZoomLabel);
            Assert.True(key.ZoomProminent);
        }

        [Fact]
        public void NotKnown_AsInAPerspectiveView_LeavesTheLine_AndTheKeyNeverZoomsByItself()
        {
            var key = Key52();
            var before = key.Focus;
            MarkKey.ApplyOnScreen(key, 52, null);
            Assert.Equal(before, key.Focus);
            Assert.Null(key.ZoomLabel);
            // The key only offers the zoom: nothing in InModelResult zooms when a result is shown.
            var src = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "UI", "Copilot", "Highlights", "InModelResult.cs")).Replace("\r\n", "\n");
            var show = src.Substring(src.IndexOf("public void Show(HighlightSet set", System.StringComparison.Ordinal));
            show = show.Substring(0, show.IndexOf("public void Hide()", System.StringComparison.Ordinal));
            Assert.DoesNotContain("ZoomTo", show);
            Assert.Contains("MarkKey.ApplyOnScreen(key, _marks.Count, OnScreenCount(app))", src);
        }

        // ─── One way in, read from the card bodies and the Revit side ─────────

        private static string Src(params string[] parts) =>
            System.IO.File.ReadAllText(System.IO.Path.Combine(new[] { Root() }.Concat(parts).ToArray())).Replace("\r\n", "\n");

        private static readonly string[] Bodies = { "ItemsCard.cs", "CreatedCard.cs", "ChangesBody.cs", "FindingsBody.cs", "SelectionBody.cs", "RulesBlock.cs", "RepeatRows.cs" };

        [Fact]
        public void EveryBody_HasOneWayIn_TheOldSeparateButtonsAreGone()
        {
            foreach (var body in Bodies)
            {
                var s = Src("UI", "Copilot", "Controls", body);
                foreach (var gone in CardButtons.Removed) Assert.DoesNotContain("\"" + gone + "\"", s);
                Assert.DoesNotContain("ItemText.ButtonLabel(", s);       // "Select them in Revit" / "Open view" / "Open sheet" as buttons
                Assert.DoesNotContain("actions.ShowElements", s);          // a row shows its elements through the marks, not a bare select
                Assert.DoesNotContain("MarksButton.Build", s);            // Hide marks / Show in model is the card's one section header
                Assert.DoesNotContain("HighlightLabel", s);
            }
            Assert.Contains("MarksHeader(actions) is FrameworkElement header", Src("UI", "Copilot", "Controls", "ResultCardView.cs"));
            Assert.Contains("CardButtons.GoToView", Src("UI", "Copilot", "Controls", "ResultCardView.cs"));
        }

        [Fact]
        public void EachBody_MakesItsRowsTheButton_WithTheArrowThatSelects()
        {
            Assert.Contains("actions.Rows?.Link(line, Highlights.MarkFocus.ForItemRow(row))", Src("UI", "Copilot", "Controls", "ItemsCard.cs"));
            Assert.Contains("actions.Rows?.Button(line,", Src("UI", "Copilot", "Controls", "ItemsCard.cs"));          // view and sheet rows open
            Assert.Contains("MarkFocus.ForCategory(g.Category, g.Count)", Src("UI", "Copilot", "Controls", "CreatedCard.cs"));
            Assert.Contains("MarkFocus.ForElements(c.Label, new[] { c.Id })", Src("UI", "Copilot", "Controls", "CreatedCard.cs"));
            Assert.Contains("MarkFocus.ForElements(f.Title, f.Ids)", Src("UI", "Copilot", "Controls", "FindingsBody.cs"));
            Assert.Contains("actions.Rows.Add(t.Box, Highlights.MarkFocus.ForTypeGroup(g)", Src("UI", "Copilot", "Controls", "ChangesBody.cs"));
            Assert.Contains("CardRows.Arrow(() => actions.SelectGroup(g))", Src("UI", "Copilot", "Controls", "SelectionBody.cs"));
            var rows = Src("UI", "Copilot", "Controls", "CardRows.cs");
            Assert.Contains("var arrow = Arrow(() => _actions.SelectGroupInRevit(target));", rows);
            Assert.Contains("CardKeys.For(key.ToString()", rows);
            Assert.Contains("MarkFocus.PickRow(", rows);
            Assert.Contains("_actions.WatchMarkClicks?.Invoke(", rows);
            // Kept on the action bars: each has no row to live on.
            foreach (var kept in CardButtons.Kept(ResultKind.Changes)) Assert.Contains("\"" + kept + "\"", Src("UI", "Copilot", "Controls", "ChangesBody.cs"));
            Assert.Contains("\"Copy list\"", Src("UI", "Copilot", "Controls", "CreatedCard.cs"));
        }

        [Fact]
        public void TheRevitSide_MapsAControlToItsMark_SwapsTonesInPlace_AndChangesNothingInTheModel()
        {
            var s = Src("UI", "Copilot", "Highlights", "InModelResult.cs");
            Assert.Contains("_byControl[index] = m;", s);                          // the control id to mark map, filled in AddMarks
            Assert.Contains("tgm.UpdateControl(kv.Key", s);                        // bright and dim keep the control's index
            Assert.Contains("MarkClickHandler.Clicked += OnMarkClicked;", s);
            Assert.Contains("uidoc.RequestViewChange(other)", s);                  // another view opens, then the zoom follows
            Assert.Contains("_pendingZoom", s);
            Assert.DoesNotContain("new Transaction(", s);
            Assert.DoesNotContain("uidoc.ShowElements(", s);
            var h = Src("UI", "Copilot", "Highlights", "MarkClickHandler.cs");
            Assert.Contains(": ITemporaryGraphicsHandler", h);
            Assert.Contains("BuiltInExternalServices.TemporaryGraphicsHandlerService", h);
            Assert.Contains("Clicked?.Invoke(data.Document, data.Index)", h);
            Assert.DoesNotContain("new Transaction(", h);
        }

        internal static string Root()
        {
            var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "RevitWebAppSync.csproj"))) d = d.Parent;
            return d!.FullName;
        }

        // ─── The card's keys and buttons ──────────────────────────────────────

        [Theory]
        [InlineData("Up", false, CardKeyAction.Previous)]
        [InlineData("Down", false, CardKeyAction.Next)]
        [InlineData("Return", false, CardKeyAction.Show)]
        [InlineData("Enter", true, CardKeyAction.ShowAndSelect)]
        [InlineData("Return", true, CardKeyAction.ShowAndSelect)]
        [InlineData("A", false, CardKeyAction.ShowAll)]
        [InlineData("H", false, CardKeyAction.ToggleMarks)]
        [InlineData("A", true, CardKeyAction.None)]
        [InlineData("Escape", false, CardKeyAction.None)]
        [InlineData("Left", false, CardKeyAction.None)]
        public void EachKeyOnTheCard(string key, bool shift, CardKeyAction expected) =>
            Assert.Equal(expected, CardKeys.For(key, shift, ctrlOrAlt: false, typing: false));

        [Fact]
        public void TextBoxesAndRevitKeepTheirKeys()
        {
            Assert.Equal(CardKeyAction.None, CardKeys.For("A", false, ctrlOrAlt: false, typing: true));
            Assert.Equal(CardKeyAction.None, CardKeys.For("Return", false, ctrlOrAlt: false, typing: true));
            Assert.Equal(CardKeyAction.None, CardKeys.For("H", false, ctrlOrAlt: true, typing: false));
        }

        [Fact]
        public void UpAndDown_MoveBetweenRows_AndStopAtTheEnds()
        {
            Assert.Equal(0, CardKeys.Move(-1, 5, CardKeyAction.Next));
            Assert.Equal(4, CardKeys.Move(-1, 5, CardKeyAction.Previous));
            Assert.Equal(3, CardKeys.Move(2, 5, CardKeyAction.Next));
            Assert.Equal(4, CardKeys.Move(4, 5, CardKeyAction.Next));
            Assert.Equal(0, CardKeys.Move(0, 5, CardKeyAction.Previous));
            Assert.Equal(-1, CardKeys.Move(-1, 0, CardKeyAction.Next));
        }

        [Fact]
        public void TheSectionHeader_HasHideOrShow_AndGoToViewForAnotherView()
        {
            Assert.Equal(new[] { "Hide marks" }, CardButtons.Header(hasMarks: true, marksShown: true, inOtherView: false));
            Assert.Equal(new[] { "Go to view", "Show in model" }, CardButtons.Header(true, false, true));
            Assert.Empty(CardButtons.Header(false, false, true));
        }
    }
}
