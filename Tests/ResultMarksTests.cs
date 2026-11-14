#nullable enable
// Result marks in the model, build step 1 ("Name it and keep it"): the mark groups from a turn's result and the
// add-in's own commit lists, the key's words and counts for each state this round covers, the corner the key takes,
// the mark size, and the life of the result (nothing fades; hide, show again, gone after Undo).
// Fixtures copy the shape of the live round-9 exports (a code write that tags every door of a plan: one commit whose
// envelope lists 50 of its 58 added ids, touched_by_category 58, and a server result with a view row and one tag row
// per door type whose ids are cut to what the envelope showed). The names are invented.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Writes;
using RevitWebAppSync.UI.Copilot.Highlights;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ResultMarksTests
    {
        private const long ThisView = 7001, SitePlan = 9001;
        private const string ThisViewName = "Level 1 - Plan (WIP)";

        // Ten door types, 58 tags: the counts of the live run, invented names.
        private static readonly (string Type, int Count)[] DoorTypes =
        {
            ("(DA20) 750 x 2100 single", 20), ("(DB12) 3700 x 2100 double", 12), ("(DC08) 900 x 2100 single", 8),
            ("(DD04) 1000 x 2100 sliding", 4), ("(DE04) 450 x 2100 access", 4), ("(DF03) 1800 x 2100 pair", 3),
            ("(DG02) 900 x 2100 glazed", 2), ("(DH02) 1500 x 2100 pair", 2), ("(DI02) 1950 x 2400 pair", 2),
            ("(DJ01) 900 x 2100 access", 1),
        };

        private static List<long> AllTagIds() => Enumerable.Range(0, 58).Select(i => 300000L + i).ToList();

        /// <summary>The commit as the engine leaves it: 50 rows in touched.added, the full 58 kept by CommitIds when
        /// <paramref name="keepFull"/> (the add-in's own commit), not when the envelope came from elsewhere (history).</summary>
        internal static (ResultCardModel Card, Envelope Env) TagRun(bool keepFull = true, string viewRowTitle = ThisViewName)
        {
            var ids = AllTagIds();
            var env = new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Ran: Tag all untagged doors (added 58).",
                Data = JsonDocument.Parse("{\"mode\":\"write\"}").RootElement.Clone(),
                Changes = new ChangeReportDto
                {
                    Touched = new ChangesDto { Added = ids.Take(EnvelopeLimits.MaxRows).Select(i => new ElementRef(i, null)).ToList() },
                    TouchedByCategory = new SideEffectsDto { AddedByCategory = { ["Door Tags"] = 58 } },
                },
            };
            if (keepFull)
                CommitIds.Remember(env, new ChangesDto { Added = ids.Select(i => new ElementRef(i, null)).ToList() },
                    ids.ToDictionary(i => i, _ => "Door Tags"));
            var call = new ToolCallDto { CallId = "c1", IdempotencyKey = "k", Tool = ToolNames.RunRevitCode, Args = JsonDocument.Parse("{\"intent\":\"Tag doors\"}").RootElement.Clone() };
            var ev = new TurnEvidence();
            ev.Add(new ToolRequestDto { RequestId = "r1", Kind = ToolRequestKinds.Write, Calls = { call } }, new[] { env });

            // The server's rows: a view row, then one tag row per door type whose ids stop where the envelope's 50 stopped.
            var items = new List<object> { new { kind = "view", title = viewRowTitle, ids = new[] { ThisView }, cells = new Dictionary<string, string> { ["tags added"] = "58" } } };
            var at = 0;
            foreach (var (type, n) in DoorTypes)
            {
                var rowIds = ids.Skip(at).Take(n).Where(i => i < 300000 + EnvelopeLimits.MaxRows).ToList();
                if (type.StartsWith("(DD04)")) rowIds = rowIds.Take(1).ToList();   // the live "count 4 with 1 id"
                items.Add(new { kind = "tag", title = type, count = n, ids = rowIds });
                at += n;
            }
            var json = JsonSerializer.Serialize(new { kind = "created", lang = "en", reply = "Added 58 door tags in " + viewRowTitle + ".", items, counts = new { created = 58 } });
            var result = CopilotJson.Deserialize<TurnResultDto>(json);
            var card = ResultCardModel.From(ev, ReceiptLanguage.En).WithServerResult(result);
            return (card, env);
        }

        internal static HighlightSet Set(bool keepFull = true)
        {
            var (card, env) = TagRun(keepFull);
            var set = HighlightSet.From(card, new[] { env });
            set.TurnId = "t1"; set.Time = "2:35 PM";
            return set;
        }

        private static List<ElementPlace> Places(HighlightSet set, System.Func<long, ElementPlace> where)
        {
            var list = new List<ElementPlace>();
            for (var g = 0; g < set.Kinds.Count; g++)
                foreach (var id in set.Kinds[g].Ids) { var p = where(id); p.Id = id; p.Group = g; list.Add(p); }
            return list;
        }

        // ─── Mark groups ──────────────────────────────────────────────────────

        [Fact]
        public void TheTagRun_IsOneMadeGroupOf58_FromTheAddInsOwnList_NotTheEnvelopes50()
        {
            var set = Set();

            Assert.Equal(50, TagRun().Env.Changes!.Touched.Added.Count);   // what goes to the backend is unchanged
            var made = Assert.Single(set.Kinds);
            Assert.Equal(MarkOutcome.Made, made.Outcome);
            Assert.Equal(MarkKind.Annotation, made.Kind);
            Assert.Equal("Door Tags", made.Category);
            Assert.Equal(58, made.Count);
            Assert.Equal(58, made.Ids.Distinct().Count());
            Assert.True(set.FullLists);
        }

        [Fact]
        public void WithoutTheAddInsList_AnEnvelopeFromHistory_MarksWhatTheEnvelopeListed()
        {
            var set = Set(keepFull: false);
            Assert.Equal(50, Assert.Single(set.Kinds).Count);
            Assert.False(set.FullLists);
        }

        [Fact]
        public void EachCardRow_IsItsOwnGroup_WithItsTitleCountAndIds_AndTheViewRowIsNone()
        {
            var set = Set();

            Assert.Equal(10, set.Rows.Count);
            Assert.DoesNotContain(set.Rows, r => r.Title == ThisViewName);
            Assert.Equal(DoorTypes.Select(t => t.Type), set.Rows.Select(r => r.Title));
            Assert.Equal(DoorTypes.Select(t => t.Count), set.Rows.Select(r => r.Count));
            var dd = set.Rows.Single(r => r.Title.StartsWith("(DD04)"));
            Assert.Equal(4, dd.Count);            // the row's own count …
            Assert.Single(dd.Ids);                // … though the server kept one id (step 2 asks the backend for the rest)
            Assert.All(set.Rows, r => Assert.Equal(MarkOutcome.Made, r.Outcome));
            Assert.Equal(20, set.Rows[0].Ids.Count);
        }

        [Fact]
        public void TheCardAndTheKey_AgreeOn58()
        {
            var (card, env) = TagRun();
            var set = HighlightSet.From(card, new[] { env });
            var key = MarkKey.Build(set, null, ThisViewName);

            Assert.Equal(58, card.CreatedBy!.Total);
            Assert.Equal(58, key.Total);
            Assert.Equal(58, key.Lines[0].Count);
            Assert.StartsWith("58 ", key.Title);
        }

        [Fact]
        public void AChangeByCode_IsVioletByCategory_WithTheFullCount()
        {
            var ids = Enumerable.Range(0, 120).Select(i => 500000L + i).ToList();
            var env = new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Ran: set comments.",
                Data = JsonDocument.Parse("{\"mode\":\"write\"}").RootElement.Clone(),
                Changes = new ChangeReportDto { TouchedByCategory = new SideEffectsDto { ModifiedByCategory = { ["Walls"] = 120 } } },
            };
            CommitIds.Remember(env, new ChangesDto { Modified = ids.Select(i => new FieldChangeDto { Id = i }).ToList() }, ids.ToDictionary(i => i, _ => "Walls"));
            var card = new ResultCardModel { Kind = ResultKind.Changes, CategoryCounts = { ("Walls", 120) } };

            var set = HighlightSet.From(card, new[] { env });

            var g = Assert.Single(set.Kinds);
            Assert.Equal(MarkOutcome.Changed, g.Outcome);
            Assert.Equal(120, g.Count);
            var key = MarkKey.Build(set, null, ThisViewName);
            Assert.Equal("120 walls changed", key.Title);
            Assert.Equal("Changed wall", key.Lines[0].Label);
        }

        // ─── The key, state by state ──────────────────────────────────────────

        [Fact]
        public void JustFinished_AllInThisView_NamesWhatWhereAndEachKind()
        {
            var set = Set();
            var key = MarkKey.Build(set, Places(set, _ => new ElementPlace { InActiveView = true }), ThisViewName);

            Assert.Equal("Copilot result · 2:35 PM", key.Header);
            Assert.Equal(MarkOutcome.Made, key.HeadOutcome);
            Assert.Equal("58 door tags made", key.Title);
            Assert.Equal("in " + ThisViewName + ", this view", key.Where);
            var line = Assert.Single(key.Lines);
            Assert.Equal("New door tag", line.Label);
            Assert.Equal(58, line.Count);
            Assert.Equal(MarkOutcome.Made, line.Outcome);
            Assert.Null(line.Small);
            Assert.Equal("Showing all 58 · 10 groups in the pane", key.Focus);
            Assert.Equal("all 58", key.FocusBold);
            Assert.Null(key.OpenViewId);
            Assert.Equal(58, key.Here);
        }

        [Fact]
        public void InAnotherView_NothingHere_SaysWhereWithOneOpen()
        {
            var set = Set();
            var key = MarkKey.Build(set, Places(set, _ => new ElementPlace { InActiveView = false, ViewId = SitePlan, ViewName = "Site Plan", IsPlan = true }), "Level 2 - Plan");

            Assert.Equal("58 door tags made", key.Title);
            Assert.Equal("in Site Plan. None are in this view, so nothing is marked here.", key.Where);
            Assert.Equal("Site Plan", key.WhereBold);
            var line = Assert.Single(key.Lines);
            Assert.Equal("in Site Plan", line.Small);
            Assert.Equal(58, line.Count);
            Assert.Equal("Open Site Plan and show them", key.Focus);
            Assert.Equal(SitePlan, key.OpenViewId);
            Assert.Equal(0, key.Here);
        }

        [Fact]
        public void SeveralViews_SomeHere_CountsTheRestByView()
        {
            // 412 room tags: 64 here, 348 in five other plans.
            var ids = Enumerable.Range(0, 412).Select(i => 800000L + i).ToList();
            var set = new HighlightSet { Time = "2:35 PM" };
            set.Kinds.Add(new MarkGroup { Title = "Room Tags", Category = "Room Tags", Outcome = MarkOutcome.Made, Kind = MarkKind.Annotation, Ids = ids, Count = 412 });
            var split = new[] { (0L, 64), (101L, 72), (102L, 81), (103L, 69), (104L, 66), (105L, 60) };
            var places = new List<ElementPlace>();
            var at = 0;
            foreach (var (view, n) in split)
            {
                foreach (var id in ids.Skip(at).Take(n))
                    places.Add(view == 0
                        ? new ElementPlace { Id = id, InActiveView = true }
                        : new ElementPlace { Id = id, ViewId = view, ViewName = "Plan " + view, IsPlan = true });
                at += n;
            }

            var key = MarkKey.Build(set, places, ThisViewName);

            Assert.Equal("412 room tags made", key.Title);
            Assert.Equal("in 6 plans; 64 in this view", key.Where);
            Assert.Equal(2, key.Lines.Count);
            Assert.Equal(("New room tag", "in this view", 64), (key.Lines[0].Label, key.Lines[0].Small, key.Lines[0].Count));
            Assert.Equal(("In 5 other plans", 348), (key.Lines[1].Label, key.Lines[1].Count));
            Assert.Null(key.Lines[1].Outcome);
            Assert.Equal("Showing 64 here", key.Focus);                             // the 348 are on the "In 5 other plans" line
            Assert.Null(key.OpenViewId);
            Assert.Equal(64, key.Here);
            Assert.Equal(412, key.Total);
        }

        [Fact]
        public void SeveralViews_NoneHere_OpensTheViewWithTheMost()
        {
            var set = Set();
            var key = MarkKey.Build(set, Places(set, id => new ElementPlace { ViewId = id % 2 == 0 ? 11 : 12, ViewName = id % 2 == 0 ? "North Plan" : "South Plan", IsPlan = true }), ThisViewName);

            Assert.Equal("in 2 plans. None are in this view, so nothing is marked here.", key.Where);
            Assert.Equal("in 2 plans", key.Lines[0].Small);
            Assert.NotNull(key.OpenViewId);
            Assert.StartsWith("Open ", key.Focus);
            Assert.Contains("which has 29 of them", key.Focus);
        }

        [Fact]
        public void ModelElementsNotVisibleHere_AreSaidSo_WithNoViewToOpen()
        {
            var set = Set();
            var key = MarkKey.Build(set, Places(set, id => new ElementPlace { InActiveView = id < 300040 }), ThisViewName);

            Assert.Equal("40 of 58 are visible in this view", key.Where);
            Assert.Equal("in this view", key.Lines[0].Small);
            Assert.Equal(40, key.Lines[0].Count);
            Assert.Null(key.OpenViewId);
        }

        [Fact]
        public void Hidden_IsThePillWithTheSameWords()
        {
            var key = MarkKey.Build(Set(), null, ThisViewName);
            Assert.Equal("Copilot result hidden · 58 door tags made", key.Pill);
        }

        [Fact]
        public void NoFixedLabel_IsMalay_AndNoneSaysItFades()
        {
            var src = File.ReadAllText(Path.Combine(Root(), "UI/Copilot/Highlights/MarkKey.cs"));
            foreach (var word in new[] { "dicipta", "diubah", "Buka", "Sembunyi", "fades", "seconds" })
                Assert.DoesNotContain(word, src);
        }

        // ─── Life of the result: nothing fades ───────────────────────────────

        // ─── The mark's file: Revit takes a .bmp only (live round 13: PNG files, no mark was ever drawn) ───

        private static string RepoFile(params string[] parts)
        {
            var root = new System.IO.DirectoryInfo(System.IO.Directory.GetCurrentDirectory());
            while (root != null && !System.IO.File.Exists(System.IO.Path.Combine(root.FullName, "RevitWebAppSync.csproj"))) root = root.Parent;
            return System.IO.File.ReadAllText(System.IO.Path.Combine(new[] { root!.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
        }

        [Fact]
        public void AMark_IsA24BitBitmap_WithRevitsClearColour_AndNoBlendWithIt()
        {
            // 3 x 2 premultiplied BGRA, rows top-down: clear, opaque violet, a faint edge / half-opaque green, the clear colour itself, clear.
            var px = new byte[]
            {
                0, 0, 0, 0,        237, 58, 124, 255,   20, 5, 10, 60,
                0, 80, 0, 128,     128, 128, 0, 255,    0, 0, 0, 0,
            };
            var bmp = MarkBitmap.Encode(px, 3, 2);
            Assert.Equal((byte)'B', bmp[0]); Assert.Equal((byte)'M', bmp[1]);
            Assert.Equal(54 + 2 * 12, bmp.Length);                                  // each row of 9 bytes is padded to 12
            Assert.Equal(bmp.Length, System.BitConverter.ToInt32(bmp, 2));
            Assert.Equal(54, System.BitConverter.ToInt32(bmp, 10));
            Assert.Equal(3, System.BitConverter.ToInt32(bmp, 18)); Assert.Equal(2, System.BitConverter.ToInt32(bmp, 22));
            Assert.Equal(24, System.BitConverter.ToInt16(bmp, 28)); Assert.Equal(0, System.BitConverter.ToInt32(bmp, 30));
            (byte B, byte G, byte R) At(int x, int y) { var at = 54 + (2 - 1 - y) * 12 + x * 3; return (bmp[at], bmp[at + 1], bmp[at + 2]); }
            var clear = (MarkBitmap.ClearB, MarkBitmap.ClearG, MarkBitmap.ClearR);
            Assert.Equal(clear, At(0, 0));                                          // nothing there
            Assert.Equal(((byte)237, (byte)58, (byte)124), At(1, 0));               // the colour as it is
            Assert.Equal(clear, At(2, 0));                                          // a faint edge is dropped, never blended
            Assert.Equal(((byte)0, (byte)159, (byte)0), At(0, 1));                  // half opaque: the colour at full strength
            Assert.NotEqual(clear, At(1, 1));                                       // a real pixel is never the clear colour
            Assert.Equal(clear, At(2, 1));
            Assert.Equal((0, 128, 128), ((int)MarkBitmap.ClearR, (int)MarkBitmap.ClearG, (int)MarkBitmap.ClearB));
            Assert.Throws<System.ArgumentException>(() => MarkBitmap.Encode(new byte[4], 2, 2));

            var images = RepoFile("UI", "Copilot", "Highlights", "MarkImages.cs");
            Assert.Contains("MarkFocus.ToneSuffix(tone) + MarkBitmap.Extension;", images);
            Assert.Contains("return MarkBitmap.Encode(pixels, px, px);", images);
            Assert.DoesNotContain("PngBitmapEncoder", images);
            Assert.DoesNotContain(".png", images);
            Assert.Equal(".bmp", MarkBitmap.Extension);
        }

        [Fact]
        public void TheKey_NeverSaysShowing_WhenRevitDrewNoMark()
        {
            var key = new KeyText { Focus = "Showing all 16", FocusBold = "all 16", ZoomLabel = "Zoom to all", ZoomProminent = true, ShowAll = true };
            MarkKey.ApplyNotDrawn(key, places: 16, drawn: 0);
            Assert.Equal("The marks could not be drawn in this view", key.Focus);
            Assert.Null(key.FocusBold); Assert.Null(key.ZoomLabel); Assert.False(key.ZoomProminent); Assert.False(key.ShowAll);

            var drawn = new KeyText { Focus = "Showing all 16", FocusBold = "all 16" };
            MarkKey.ApplyNotDrawn(drawn, places: 16, drawn: 16);
            Assert.Equal("Showing all 16", drawn.Focus);
            var none = new KeyText { Focus = "Open Aras 01, which has 60 of them" };
            MarkKey.ApplyNotDrawn(none, places: 0, drawn: 0);                       // nothing of it is in this view: no mark was due
            Assert.Equal("Open Aras 01, which has 60 of them", none.Focus);

            var inModel = RepoFile("UI", "Copilot", "Highlights", "InModelResult.cs");
            Assert.Contains("if (!Hidden) MarkKey.ApplyNotDrawn(key, _marks.Count, _controls.Count);", inModel);
        }

        [Fact]
        public void AMarkThatWasAddedChangedOrRemoved_IsPainted_ByARealRedrawOfTheView()
        {
            // The 0.0.110 probe in Revit: AddControl took 16 marks, RefreshActiveView painted none, one notch of zoom painted all.
            var s = RepoFile("UI", "Copilot", "Highlights", "InModelResult.cs");
            var add = s.Substring(s.IndexOf("private void AddMarks(", System.StringComparison.Ordinal));
            add = add.Substring(0, add.IndexOf("\n        }\n", System.StringComparison.Ordinal));
            Assert.Contains("Repaint(uidoc, view);", add);
            Assert.DoesNotContain("RefreshActiveView", add);                                     // it painted nothing
            var repaint = s.Substring(s.IndexOf("private void Repaint(", System.StringComparison.Ordinal));
            repaint = repaint.Substring(0, repaint.IndexOf("\n        }\n", System.StringComparison.Ordinal));
            Assert.Contains("ui.ZoomAndCenterRectangle(corners[0] - nudge, corners[1] + nudge);", repaint);
            Assert.Contains("_nudgeOut = !_nudgeOut;", repaint);                                 // out and in by turns: the view never drifts
            Assert.Contains("_ownMove = true;", repaint);
            Assert.Contains("if (changed) RepaintActive(app);", s);                              // a tone change
            Assert.Contains("app => { RemoveMarks(app, ids); RepaintActive(app); }", s);         // marks taken away
            Assert.Contains("if (!own && !Hidden && SizeMarks(uidoc, view) && _controls.Count > 0)", s);   // the nudge never sizes and draws again
            Assert.InRange(MarkKey.RepaintNudge, 0.0001, 0.005);
            Assert.Contains("if (select) { uidoc.Selection.SetElementIds(eids); Repaint(uidoc, view); }", s);   // a selection shows at a redraw too
            Assert.Contains("ApplyTones(app);\n                    RepaintActive(app);", s);            // a click on a mark: its one element replaces what was selected
        }

        [Fact]
        public void AMarksTooltip_SaysWhatItIs_NotRevitsControl()
        {
            var look = new MarkGroup { Title = "Can't keep elements joined.", Outcome = MarkOutcome.NeedsLook, Kind = MarkKind.Element, Count = 16, Named = true };
            Assert.Equal(MarkKey.LineLabel(look).TrimEnd('.') + " · click to select", MarkKey.Tooltip(look));
            Assert.EndsWith(" · click to select", MarkKey.Tooltip(look));
            Assert.DoesNotContain(". ·", MarkKey.Tooltip(look));
            var inModel = RepoFile("UI", "Copilot", "Highlights", "InModelResult.cs");
            Assert.Contains("SetTooltip?.Invoke(tgm, new object[] { index, MarkKey.Tooltip(_set.Kinds[m.Group]) });", inModel);
            Assert.Contains("typeof(TemporaryGraphicsManager).GetMethod(\"SetTooltip\"", inModel);   // not in the older hosts' API: never linked
        }

        [Fact]
        public void TheKeyFollowsTheView_ThroughTheAddInsOnePermanentIdlingSubscription()
        {
            // Live round 13. From the pane's thread Revit's Idling event never took (0.0.107: plan -> 3D -> plan and the
            // key named the old view). From the work Revit runs it is refused whenever that work runs inside an Idling
            // call (0.0.112's log: "Can not subscribe to an event during execution of that event!"), so it only worked
            // when the work happened to run from the kick (0.0.109, 0.0.110). The dispatcher's own subscription, made at
            // startup, hands every Idling call on.
            var s = RepoFile("UI", "Copilot", "Highlights", "InModelResult.cs");
            Assert.Contains("BinaVibe.Copilot.Writes.RevitThread.Idle += OnIdle;", s);
            Assert.DoesNotContain("app.Idling +=", s);                                          // never Revit's own event here
            Assert.DoesNotContain("Idling -= OnIdling", s);
            Assert.Contains("if (_idlingApp != null) return;", s);                              // one listener for the session
            Assert.Contains("if (_idlingApp == null) StartIdling(app);", s);
            Assert.Contains("if (_set == null) return;", s.Substring(s.IndexOf("private void OnIdling(", System.StringComparison.Ordinal)));
            var thread = RepoFile("BinaVibe", "Copilot", "Writes", "RevitThread.cs");
            Assert.Contains("public static event Action<UIApplication, IdlingEventArgs>? Idle;", thread);
            var idling = thread.Substring(thread.IndexOf("private static void OnIdling(", System.StringComparison.Ordinal));
            idling = idling.Substring(0, idling.IndexOf("\n        }\n", System.StringComparison.Ordinal));
            Assert.True(idling.IndexOf("DrainOne(app);", System.StringComparison.Ordinal) < idling.IndexOf("Idle?.Invoke(app, e);", System.StringComparison.Ordinal));
            Assert.Contains("try { Idle?.Invoke(app, e); }", idling);                           // a listener's fault never reaches Revit
            Assert.Contains("app.Idling += OnIdling;", thread.Substring(thread.IndexOf("public static void Attach(", System.StringComparison.Ordinal)));
        }

        [Fact]
        public void AnUndoOfTheShownTurn_ByRevitsOwnCtrlZToo_TakesTheResultOutOfTheView()
        {
            // Live round 13: Ctrl+Z after "Set Comments to Done on the doors": the card said Undone, the key still read
            // "60 doors changed" and offered Undo.
            BinaVibe.Copilot.Undo.UndoChange Change(BinaVibe.Copilot.Undo.UndoChangeKind kind, string turn, bool byPane = false) =>
                new BinaVibe.Copilot.Undo.UndoChange { Kind = kind, TurnId = turn, ByPane = byPane };
            var s = new MarkSession();
            Assert.False(s.GoneWith(Change(BinaVibe.Copilot.Undo.UndoChangeKind.Undone, "t1")));   // nothing is shown
            s.Show("t1");
            Assert.True(s.GoneWith(Change(BinaVibe.Copilot.Undo.UndoChangeKind.Undone, "t1")));                // Revit's Ctrl+Z
            Assert.True(s.GoneWith(Change(BinaVibe.Copilot.Undo.UndoChangeKind.Undone, "t1", byPane: true)));  // the card's button
            Assert.False(s.GoneWith(Change(BinaVibe.Copilot.Undo.UndoChangeKind.Undone, "t0")));               // another turn's change
            Assert.False(s.GoneWith(Change(BinaVibe.Copilot.Undo.UndoChangeKind.Redone, "t1")));
            Assert.False(s.GoneWith(null));
            s.Hide();
            Assert.True(s.GoneWith(Change(BinaVibe.Copilot.Undo.UndoChangeKind.Undone, "t1")));                // the pill goes too

            var root = new System.IO.DirectoryInfo(System.IO.Directory.GetCurrentDirectory());
            while (root != null && !System.IO.File.Exists(System.IO.Path.Combine(root.FullName, "RevitWebAppSync.csproj"))) root = root.Parent;
            var vm = System.IO.File.ReadAllText(System.IO.Path.Combine(root!.FullName, "UI", "Copilot", "CopilotViewModel.cs"));
            Assert.Contains("if (InModel?.GoneWith(change) == true) Revit.ClearMarks(); UndoStateChanged?.Invoke();", vm);
        }

        [Fact]
        public void TheResult_StaysUntilHidden_ShowsAgain_AndIsGoneAfterUndo()
        {
            var s = new MarkSession();
            Assert.Equal(MarksState.None, s.StateOf("t1"));
            Assert.Equal("Show in model", s.CardButton("t1", undone: false));

            s.Show("t1");
            Assert.True(s.KeyVisible);
            Assert.Equal(MarksState.Shown, s.StateOf("t1"));
            Assert.Equal(MarksState.None, s.StateOf("t0"));                       // an older turn's card
            Assert.Equal("Hide marks", s.CardButton("t1", undone: false));

            Assert.True(s.Hide());                                                // the key's close, or the card's Hide marks
            Assert.True(s.PillVisible);
            Assert.False(s.KeyVisible);
            Assert.Equal("Show in model", s.CardButton("t1", undone: false));

            Assert.True(s.ShowAgain());                                           // the pill's Show again
            Assert.Equal(MarksState.Shown, s.StateOf("t1"));
            Assert.False(s.ShowAgain());

            s.Clear();                                                            // Undo (cleared first), the next turn, the pill's close
            Assert.Equal(MarksState.None, s.StateOf("t1"));
            Assert.False(s.KeyVisible || s.PillVisible);
            Assert.Null(s.CardButton("t1", undone: true));                        // undone: nothing left to show
        }

        [Fact]
        public void InModelResult_HasNoFadeTimer_KeepsTheResultOnAViewChange_AndClearsBeforeUndo()
        {
            var s = File.ReadAllText(Path.Combine(Root(), "UI/Copilot/Highlights/InModelResult.cs"));
            Assert.DoesNotContain("FadeSeconds", s);
            Assert.DoesNotContain("DispatcherTimer", s);
            Assert.DoesNotContain("StartFade", s);
            // A view change re-resolves; it does not clear.
            var idle = s.Substring(s.IndexOf("private void OnIdling", System.StringComparison.Ordinal));
            var change = idle.Substring(idle.IndexOf("if (view.Id.Value != _viewId)", System.StringComparison.Ordinal));
            change = change.Substring(0, change.IndexOf("return;", System.StringComparison.Ordinal));
            Assert.Contains("Resolve(app, _set);", change);
            Assert.DoesNotMatch(@"(?<![\w.])Clear\(\)", change);
            // Undo from the key: Clear first, then Undo.
            var undo = s.Substring(s.IndexOf("private void UndoFromKey", System.StringComparison.Ordinal));
            Assert.True(undo.IndexOf("Clear();", System.StringComparison.Ordinal) < undo.IndexOf("u();", System.StringComparison.Ordinal));
            // Marks only for elements in the active view: a view-filtered collector and the owner view.
            Assert.Contains("new FilteredElementCollector(doc, view.Id).WherePasses(new ElementIdSetFilter(all))", s);
            Assert.Contains("e.OwnerViewId", s);
            Assert.Contains("uidoc.RequestViewChange(v)", s);
        }

        [Fact]
        public void TheKey_ShowsWithThePaneOpen_TheToastAndCalloutsAreGone()
        {
            var w = File.ReadAllText(Path.Combine(Root(), "UI/Copilot/Highlights/HighlightOverlayWindow.xaml.cs"))
                    + File.ReadAllText(Path.Combine(Root(), "UI/Copilot/Highlights/HighlightOverlayWindow.xaml"));
            Assert.DoesNotContain("ToastPolicy", w);
            Assert.DoesNotContain("CopilotPaneOpen", w);
            Assert.DoesNotContain("Callout", w);
            Assert.DoesNotContain("x:Name=\"Toast\"", w);
            Assert.Contains("x:Name=\"KeyLayer\"", w);
            Assert.Contains("MarkKey.Corner(", w);
        }

        // ─── Corner, pill, size ──────────────────────────────────────────────

        [Fact]
        public void TheKey_TakesTheFirstCornerWithNoMarkUnderIt()
        {
            const double W = 900, H = 600, KW = 300, KH = 180;
            Assert.Equal(0, MarkKey.Corner(W, H, KW, KH, new (double, double)[0]).Corner);
            Assert.Equal((12.0, 12.0), (MarkKey.Corner(W, H, KW, KH, null).X, MarkKey.Corner(W, H, KW, KH, null).Y));

            var topLeftBusy = new[] { (100.0, 100.0) };
            var c = MarkKey.Corner(W, H, KW, KH, topLeftBusy);
            Assert.Equal(1, c.Corner);
            Assert.Equal((W - KW - 12, 12.0), (c.X, c.Y));

            var topsBusy = new[] { (100.0, 100.0), (800.0, 50.0) };
            Assert.Equal(2, MarkKey.Corner(W, H, KW, KH, topsBusy).Corner);

            // Just outside the key's box but inside its clearance still counts as under it.
            Assert.Equal(1, MarkKey.Corner(W, H, KW, KH, new[] { (12 + KW + 10, 50.0) }).Corner);

            var everywhere = new[] { (100.0, 100.0), (800.0, 50.0), (100.0, 550.0), (800.0, 550.0) };
            Assert.Equal(0, MarkKey.Corner(W, H, KW, KH, everywhere).Corner);
        }

        [Fact]
        public void ThePill_SitsBottomLeft()
        {
            Assert.Equal((12.0, 600 - 30 - 12.0), MarkKey.PillAt(2, 900, 600, 320, 30));
        }

        [Theory]
        [InlineData(0, 0, 22)]
        [InlineData(10, 6, 22)]
        [InlineData(20, 10, 30)]
        [InlineData(30, 12, 40)]
        [InlineData(40, 20, 52)]
        [InlineData(400, 8, 52)]
        public void TheMark_IsALittleBiggerThanTheThing_WithinBounds(double w, double h, int size) => Assert.Equal(size, MarkKey.SizeFor(w, h));

        [Fact]
        public void TagsAndDimensions_GetAFrame_ElementsARing()
        {
            Assert.True(MarkKey.IsFrame(MarkKind.Annotation));
            Assert.True(MarkKey.IsFrame(MarkKind.Dimension));
            Assert.False(MarkKey.IsFrame(MarkKind.Element));
        }

        private static string Root()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return dir!.FullName;
        }
    }
}
