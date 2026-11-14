#nullable enable
// Result marks in the model ("Rows and marks point at each other"): what a row click focuses (its marks as usual, the
// others dim), what a mark click finds (the row, "3 of 8", the line the card row flashes), what the bar says about what
// is on screen, what each card key does, and the section header's buttons. The fixture is ResultMarksTests' tag run (58 door tags in ten invented door types).

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
        public void ARowFocused_ItsMarksAreAsUsual_TheOthersDim_AndNoFocusIsTheUsualMark()
        {
            // Gabung (G2): no bright tone for dots; Revit's own selection shows the focused ones.
            var set = Set();
            var da = Row(set, "(DA20)");
            var focused = new HashSet<long>(MarkFocus.ElementIds(da, set)!);
            var tones = set.Kinds[0].Ids.Select(id => MarkFocus.ToneOf(id, focused)).ToList();

            Assert.Equal(20, tones.Count(t => t == MarkTone.Normal));
            Assert.Equal(38, tones.Count(t => t == MarkTone.Dim));
            Assert.All(set.Kinds[0].Ids, id => Assert.Equal(MarkTone.Normal, MarkFocus.ToneOf(id, null)));
        }

        [Fact]
        public void TheToneIsOneMoreKeyInTheImageName_AndTheUsualImageKeepsStepOnesName()
        {
            Assert.Equal("", MarkFocus.ToneSuffix(MarkTone.Normal));
            Assert.Equal("-dim", MarkFocus.ToneSuffix(MarkTone.Dim));
            Assert.Equal(new[] { MarkTone.Normal, MarkTone.Dim }, (MarkTone[])System.Enum.GetValues(typeof(MarkTone)));
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
        public void AClickedMark_FindsItsRowAndPlace_AndTheWordsForTheCard()
        {
            var set = Set();
            // The third tag of the third door type (DA20 has tags 0-19, DB12 20-31, DC08 32-39).
            var at = MarkFocus.Locate(300034, Groups(set));
            Assert.Equal((2, 3, 8), at);

            var row = set.Rows[at!.Value.Index];
            var click = MarkFocus.Click("t1", 300034, 77, "Door Tags", row, at.Value.N, at.Value.M);
            Assert.Equal("Tag 3 of 8 selected in Revit", click.Line);
            Assert.Equal("(DC08) 900 x 2100 single", click.RowTitle);
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

            var alone = MarkFocus.Click("t1", 999, -1, "Walls", null, 0, 0);
            Assert.Equal("Wall selected in Revit", alone.Line);
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
        [InlineData("Door Tags", "Tag")]
        [InlineData("Walls", "Wall")]
        [InlineData("Lighting Fixtures", "Fixture")]
        [InlineData("", "Element")]
        public void TheWordForOneElement(string category, string word) => Assert.Equal(word, MarkFocus.Word(category));

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
        public void AllOnScreen_TheBarSaysNoMore()
        {
            var key = Key52();
            MarkKey.ApplyOnScreen(key, drawn: 52, onScreen: 52);
            Assert.Equal("52 lighting fixtures changed", key.Title);
            Assert.Equal("", key.State);
            Assert.Null(key.ZoomLabel);
        }

        [Fact]
        public void SomeOffScreen_ShowsHowManyAreOnScreen_WithZoomToAll()
        {
            var key = Key52();
            MarkKey.ApplyOnScreen(key, 52, 20);
            Assert.Equal("20 of 52 on screen", key.State);
            Assert.Equal("Zoom to all", key.ZoomLabel);
            Assert.False(key.ZoomProminent);
        }

        [Fact]
        public void NoneOnScreen_SaysSo_AndTheZoomIsTheBarsProminentAction()
        {
            var key = Key52();
            MarkKey.ApplyOnScreen(key, 52, 0);
            Assert.Equal("None of the 52 is on screen", key.State);
            Assert.Equal("Zoom to them", key.ZoomLabel);
            Assert.True(key.ZoomProminent);
        }

        [Fact]
        public void NotKnown_AsInAPerspectiveView_LeavesTheLine_AndTheKeyNeverZoomsByItself()
        {
            var key = Key52();
            MarkKey.ApplyOnScreen(key, 52, null);
            Assert.Equal("", key.State);
            Assert.Null(key.ZoomLabel);
            // The key only offers the zoom: nothing in InModelResult zooms when a result is shown.
            var src = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "UI", "Copilot", "Highlights", "InModelResult.cs")).Replace("\r\n", "\n");
            var show = src.Substring(src.IndexOf("public void Show(HighlightSet set", System.StringComparison.Ordinal));
            show = show.Substring(0, show.IndexOf("public void Hide()", System.StringComparison.Ordinal));
            Assert.DoesNotContain("ZoomTo", show);
            Assert.Contains("MarkKey.ApplyOnScreen(key, _marks.Count, OnScreenCount(app))", src);
        }

        // ─── One way in, read from the card bodies and the Revit side ─────────

        internal static string Src(params string[] parts) =>
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
        public void EachBody_MakesItsRowsTheButton_ThatSelectsAndZooms_WithNoArrow()
        {
            Assert.Contains("actions.Rows?.Link(line, Highlights.MarkFocus.ForItemRow(row))", Src("UI", "Copilot", "Controls", "ItemsCard.cs"));
            Assert.Contains("actions.Rows?.Button(line,", Src("UI", "Copilot", "Controls", "ItemsCard.cs"));          // view and sheet rows open
            Assert.Contains("MarkFocus.ForCategory(g.Category, g.Count)", Src("UI", "Copilot", "Controls", "CreatedCard.cs"));
            Assert.Contains("MarkFocus.ForElements(c.Label, new[] { c.Id })", Src("UI", "Copilot", "Controls", "CreatedCard.cs"));
            Assert.Contains("MarkFocus.ForElements(f.Title, f.Ids)", Src("UI", "Copilot", "Controls", "FindingsBody.cs"));
            Assert.Contains("actions.Rows.Add(t.Box, Highlights.MarkFocus.ForTypeGroup(g)", Src("UI", "Copilot", "Controls", "ChangesBody.cs"));
            Assert.Contains("return g => { actions.SelectGroup?.Invoke(g); actions.ZoomGroup?.Invoke(g); };", Src("UI", "Copilot", "Controls", "SelectionBody.cs"));
            var rows = Src("UI", "Copilot", "Controls", "CardRows.cs");
            Assert.Contains("Add(row, target, () => _actions.ShowGroup!(target), BorderPaint(row), Text(note));", rows);   // a click selects and zooms
            Assert.Contains("public void ShowGroup(MarkGroup target) => _inModel()?.Focus(target, select: true);", Src("UI", "Copilot", "Ports", "RevitActions.cs"));
            foreach (var body in Bodies.Concat(new[] { "CardRows.cs" }))
                Assert.DoesNotContain("Arrow(", Src("UI", "Copilot", "Controls", body));
            Assert.Contains("CardKeys.For(key.ToString()", rows);
            Assert.Contains("MarkFocus.PickRow(", rows);
            Assert.Contains("_actions.WatchMarkClicks?.Invoke(", rows);
            // Kept on the action bars: each has no row to live on.
            foreach (var kept in CardButtons.Kept(ResultKind.Changes)) Assert.Contains("\"" + kept + "\"", Src("UI", "Copilot", "Controls", "ChangesBody.cs"));
            Assert.Contains("\"Copy list\"", Src("UI", "Copilot", "Controls", "CreatedCard.cs"));
        }

        [Fact]
        public void TheRevitSide_MapsAControlToItsMarks_RedrawsOnATone_AndChangesNothingInTheModel()
        {
            var s = Src("UI", "Copilot", "Highlights", "InModelResult.cs");
            Assert.Contains("_byControl[index] = d;", s);                          // the control id to its dot, frame or badge, filled in AddMarks
            Assert.Contains("if (_controls.Count == 0 || _marks.All(m => m.Tone == MarkFocus.ToneOf(m.Id, _focusIds))) return;", s);
            Assert.Contains("Select(app, eids, \"badge-click\", generation);", s);   // a badge's elements in one call
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
        [InlineData("Enter", true, CardKeyAction.Show)]           // Gabung, G6: no Shift+Enter variant, the row has one action
        [InlineData("Return", true, CardKeyAction.Show)]
        [InlineData("A", false, CardKeyAction.SelectAll)]
        [InlineData("H", false, CardKeyAction.ToggleMarks)]
        [InlineData("A", true, CardKeyAction.None)]
        [InlineData("Escape", false, CardKeyAction.None)]
        [InlineData("Left", false, CardKeyAction.None)]
        public void EachKeyOnTheCard(string key, bool shift, CardKeyAction expected) =>
            Assert.Equal(expected, CardKeys.For(key, shift, ctrlOrAlt: false, typing: false));

        [Fact]
        public void TheFirstClickOnARow_IsNotLostToAScroll()
        {
            // Live rounds 14 and 15: the first click on a type tile only scrolled the pane; the second one worked. The press
            // gave the card the focus, WPF brought the whole card into view, and the release landed on something else.
            var rows = Src("UI", "Copilot", "Controls", "CardRows.cs");
            var hook = rows.Substring(rows.IndexOf("public void Hook(FrameworkElement card)", System.StringComparison.Ordinal));
            hook = hook.Substring(0, hook.IndexOf("private void OnKey(", System.StringComparison.Ordinal));
            Assert.Contains("card.MouseLeftButtonDown += (_, e) => { if (!card.IsKeyboardFocusWithin) card.Focus(); };", hook);
            Assert.Contains("card.RequestBringIntoView += (_, e) => { if (ReferenceEquals(e.TargetObject, card)) e.Handled = true; };", hook);
            Assert.Contains("next.Row.BringIntoView();", rows);   // a row asked for by the keys still scrolls into view
        }

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
