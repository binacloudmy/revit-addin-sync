using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace Tests
{
    public class MentionSearchTests
    {
        private static MentionNode Type(string name, string mark = null, int? count = null, int? inView = null)
            => new MentionNode("type", name) { TypeMark = mark, Count = count, InView = inView };

        private static MentionNode Fam(string name, params MentionNode[] types)
            => new MentionNode("family", name, types, pickable: true);

        private static MentionNode Cat(string name, string key, params MentionNode[] fams)
            => new MentionNode("category", name, fams, pickable: true) { CategoryKey = key };

        // Project-Browser-shaped tree, as RevitMentionProvider builds it.
        private static List<MentionNode> Tree(MentionSelection sel = null)
        {
            var roots = new List<MentionNode>();
            if (sel != null) roots.Add(new MentionNode("selection", "Current selection") { Selection = sel });
            roots.Add(new MentionNode("view", "Aras 01 - Pelan") { IsActive = true });
            roots.Add(MentionNode.Group("Levels", new[] { MentionNode.Leaf("level", "Aras 01"), MentionNode.Leaf("level", "Aras 02") }));
            roots.Add(MentionNode.Group("Views", new[] { MentionNode.Group("Floor Plans", new[]
            {
                new MentionNode("view", "Aras 01 - Pelan") { IsActive = false },
                MentionNode.Leaf("view", "Aras 02 - Pelan"),
            }) }));
            roots.Add(MentionNode.Group("Sheets", new[] { MentionNode.Leaf("sheet", "A101 - Pelan Aras 01") }));
            roots.Add(MentionNode.Group("Families", new[]
            {
                Cat("Doors", "OST_Doors",
                    Fam("Pintu Tunggal",
                        Type("PTh300a", "D1", 24, 3),
                        Type("Pintu Tunggal 900x2100", "D2", 10, 0),
                        Type("Standard", null, 5, 0)),
                    Fam("Pintu Berkembar",
                        Type("PTh300a", "D7", 2, 0),
                        Type("Standard", null, 1, 0)),
                    Fam("Door - Single", Type("0915 x 2134mm", null, 40, 0))),
                Cat("Windows", "OST_Windows",
                    Fam("Tingkap Nako", Type("TN 600", "W1", 30, 12), Type("Standard", null, 3, 0))),
                Cat("Walls", "OST_Walls",
                    Fam("Basic Wall", Type("Dinding Bata 115", null, 200, 50), Type("Concrete 200", null, 80, 0))),
            }));
            return roots;
        }

        private static List<MentionItem> Items(MentionSelection sel = null) => MentionSearch.Index(Tree(sel));

        private static List<MentionItem> Find(string q, string message = null, MentionSelection sel = null)
            => MentionSearch.Search(Items(sel), q, message);

        // ── Forgiving match ──────────────────────────────────────────────────

        [Fact]
        public void Match_IgnoresCase()
        {
            Assert.Contains(Find("pth300A"), i => i.Name == "PTh300a");
        }

        [Fact]
        public void Match_IgnoresAccents()
        {
            var items = MentionSearch.Index(new[] { Cat("Doors", "OST_Doors", Fam("Porte", Type("Entrée 900"))) });
            Assert.Single(MentionSearch.Search(items, "entree"));
            Assert.Single(MentionSearch.Search(items, "ENTRÉE 9"));
        }

        [Fact]
        public void Match_AnywhereInTheName()
        {
            Assert.Contains(Find("2100"), i => i.Name == "Pintu Tunggal 900x2100");
        }

        [Fact]
        public void Match_WordsInAnyOrder()
        {
            var hits = Find("900 pintu");
            Assert.Equal("Pintu Tunggal 900x2100", hits.First().Name);
            Assert.Equal("Pintu Tunggal 900x2100", Find("pintu 900").First().Name);
        }

        [Fact]
        public void Match_FamilyWordsCount()
        {
            // "berkembar" is only in the family name.
            var hits = Find("berkembar pth");
            Assert.Equal("PTh300a", hits.First().Name);
            Assert.Equal("Pintu Berkembar", hits.First().Family);
        }

        [Fact]
        public void Match_TypeMark()
        {
            var hit = Find("D7").First();
            Assert.Equal("PTh300a", hit.Name);
            Assert.Equal("Pintu Berkembar", hit.Family);
        }

        [Fact]
        public void Match_CategoryWord_EnglishAndMalay()
        {
            Assert.Contains(Find("door standard"), i => i.Family == "Pintu Tunggal" && i.Name == "Standard");
            Assert.DoesNotContain(Find("door standard"), i => i.Family == "Tingkap Nako");
            Assert.Contains(Find("tingkap standard"), i => i.Family == "Tingkap Nako" && i.Name == "Standard");
        }

        [Fact]
        public void EmptyQuery_GivesNoFlatRows()
        {
            Assert.Empty(Find(""));
            Assert.Empty(Find("  "));
        }

        [Fact]
        public void NoMatch_GivesNothing()
        {
            Assert.Empty(Find("zzz"));
        }

        [Fact]
        public void ActiveViewListedTwice_IsOneItem()
        {
            var views = Items().Where(i => i.Kind == "view" && i.Name == "Aras 01 - Pelan").ToList();
            Assert.Single(views);
            Assert.True(views[0].IsActive);
        }

        // ── Ranking ──────────────────────────────────────────────────────────

        [Fact]
        public void Rank_StartsWithBeforeContains()
        {
            var items = MentionSearch.Index(new[] { Cat("Doors", null, Fam("F",
                Type("Kayu Pintu"), Type("Pintu Kayu"))) });
            var hits = MentionSearch.Search(items, "pintu");
            Assert.Equal(new[] { "Pintu Kayu", "Kayu Pintu" }, hits.Select(h => h.Name));
        }

        [Fact]
        public void Rank_WordStartBeforeMidWord()
        {
            var items = MentionSearch.Index(new[] { Cat("Doors", null, Fam("F",
                Type("Satu Aras"), Type("Paras Satu"))) });
            // both contain "aras"; "Satu Aras" has it as a word, "Paras" mid-word.
            Assert.Equal(new[] { "Satu Aras", "Paras Satu" }, MentionSearch.Search(items, "aras").Select(h => h.Name));
        }

        [Fact]
        public void Rank_ActiveViewBeforeTheRest()
        {
            // "t" starts TN 600 (12 in the active view) and every Tingkap Nako
            // name; the one with instances in the active view leads.
            Assert.Equal("TN 600", Find("t").First().Name);
            // The active view leads the other views that start the same.
            var views = MentionSearch.Index(new[]
            {
                MentionNode.Leaf("view", "Aras 01 - Pelan"),
                new MentionNode("view", "Aras 02 - Pelan") { IsActive = true },
            });
            Assert.Equal("Aras 02 - Pelan", MentionSearch.Search(views, "aras").First().Name);
        }

        [Fact]
        public void Rank_InViewTypeFirstAmongEqualMatches()
        {
            var hits = Find("pth300a");
            Assert.Equal("Pintu Tunggal", hits[0].Family);    // 3 in view
            Assert.Equal("Pintu Berkembar", hits[1].Family);  // none in view
        }

        [Fact]
        public void Rank_TheCategoryTheMessageNamesFirst()
        {
            // Without a hint the door "Standard" types lead on count; a message
            // about windows (Malay "tingkap") puts the window type first.
            Assert.Equal("Pintu Tunggal", Find("standard").First().Family);
            Assert.Equal("Tingkap Nako", Find("standard", "tukar tingkap ").First().Family);
            Assert.Equal("Tingkap Nako", Find("standard", "swap the windows ").First().Family);
        }

        [Fact]
        public void Rank_KindOrder_TypeBeforeLevelBeforeView()
        {
            var items = MentionSearch.Index(new List<MentionNode>
            {
                MentionNode.Leaf("view", "Aras 01"),
                MentionNode.Leaf("level", "Aras 01"),
                Cat("Doors", null, Fam("F", Type("Aras 01"))),
            });
            Assert.Equal(new[] { "type", "level", "view" }, MentionSearch.Search(items, "aras 01").Select(h => h.Kind));
        }

        [Fact]
        public void Rank_SelectionFirstWhenAskedFor()
        {
            var sel = new MentionSelection(new[] { new MentionSelectionGroup("Doors", "Pintu Tunggal", "PTh300a", 1) });
            Assert.Equal("selection", Find("sel", null, sel).First().Kind);
        }

        [Fact]
        public void Results_AreCapped()
        {
            var fams = Enumerable.Range(0, 12).Select(f => Fam("Family " + f,
                Enumerable.Range(0, 30).Select(t => Type("Type " + t)).ToArray())).ToArray();
            var items = MentionSearch.Index(new[] { Cat("Doors", null, fams) });
            Assert.Equal(360, items.Count(i => i.Kind == "type"));
            Assert.Equal(MentionSearch.MaxResults, MentionSearch.Search(items, "type").Count);
        }

        // ── Two families, one type name ──────────────────────────────────────

        [Fact]
        public void SharedTypeName_IsMarked_AndTheSentTextTellsThemApart()
        {
            var both = Find("pth300a");
            Assert.Equal(2, both.Count);
            Assert.All(both, i => Assert.True(i.SharedName));
            var sent = both.Select(MentionText.Sent).ToList();
            Assert.Contains("Pintu Tunggal: PTh300a (door type)", sent);
            Assert.Contains("Pintu Berkembar: PTh300a (door type)", sent);
            // The chips differ too, so the drafter can see which one they picked.
            Assert.Equal(2, both.Select(MentionText.Chip).Distinct().Count());
        }

        [Fact]
        public void UniqueTypeName_ChipIsTheShortName()
        {
            var tn = Find("tn 600").First();
            Assert.False(tn.SharedName);
            Assert.Equal("TN 600", MentionText.Chip(tn));
            Assert.Equal("Tingkap Nako: TN 600 (window type)", MentionText.Sent(tn));
        }

        [Fact]
        public void FamilyThatNamesItsCategory_SendsNoExtraWord()
        {
            var t = Find("0915").First();
            Assert.Equal("Door - Single: 0915 x 2134mm", MentionText.Sent(t));
        }

        [Fact]
        public void LevelViewSheetCategory_SendTheirExactName()
        {
            Assert.Equal("Aras 02", MentionText.Sent(Find("aras 02").First(i => i.Kind == "level")));
            Assert.Equal("Aras 02 - Pelan", MentionText.Sent(Find("aras 02 pelan").First(i => i.Kind == "view")));
            Assert.Equal("A101 - Pelan Aras 01", MentionText.Sent(Find("a101").First()));
            Assert.Equal("Windows", MentionText.Sent(Items().First(i => i.Kind == "category" && i.Name == "Windows")));
        }

        [Fact]
        public void Detail_ShowsFamilyCategoryMarkAndCounts()
        {
            var t = Find("D1").First();
            Assert.Equal("Pintu Tunggal · Doors · Mark D1", t.Detail);
            Assert.Equal("24 · 3 here", t.CountText);
            Assert.Equal("10", Find("D2").First().CountText);
            var uncounted = MentionSearch.Index(new[] { Cat("Doors", null, Fam("F", Type("X"))) }).First(i => i.Kind == "type");
            Assert.Equal("", uncounted.CountText);
        }
    }

    public class MentionTextTests
    {
        [Theory]
        [InlineData("Doors", "door")]
        [InlineData("Walls", "wall")]
        [InlineData("Structural Columns", "structural column")]
        [InlineData("Furniture", "furniture")]
        [InlineData("Casework", "casework")]
        [InlineData("Plumbing Fixtures", "plumbing fixture")]
        [InlineData("Generic Models", "generic model")]
        [InlineData("Assemblies", "assembly")]
        [InlineData("Mechanical Equipment", "mechanical equipment")]
        public void Singular(string category, string expected) => Assert.Equal(expected, MentionText.Singular(category));

        private static MentionSelection Sel(params MentionSelectionGroup[] g) => new MentionSelection(g);

        [Fact]
        public void Selection_OneElement()
        {
            var s = Sel(new MentionSelectionGroup("Doors", "Pintu Tunggal", "PTh300a", 1));
            Assert.Equal("1 door · Pintu Tunggal: PTh300a", MentionText.SelectionLabel(s));
            Assert.Equal("the selected door of type Pintu Tunggal: PTh300a", MentionText.SelectionSent(s));
        }

        [Fact]
        public void Selection_SeveralOfOneType()
        {
            var s = Sel(new MentionSelectionGroup("Doors", "Pintu Tunggal", "PTh300a", 4));
            Assert.Equal("4 doors · Pintu Tunggal: PTh300a", MentionText.SelectionLabel(s));
            Assert.Equal("the 4 selected doors of type Pintu Tunggal: PTh300a", MentionText.SelectionSent(s));
        }

        [Fact]
        public void Selection_OneCategorySeveralTypes()
        {
            var s = Sel(new MentionSelectionGroup("Walls", "Basic Wall", "A", 6),
                        new MentionSelectionGroup("Walls", "Basic Wall", "B", 4),
                        new MentionSelectionGroup("Walls", "Basic Wall", "C", 2));
            Assert.Equal("12 walls of 3 types", MentionText.SelectionLabel(s));
            Assert.Equal("the 12 selected walls (3 types: Basic Wall: A, Basic Wall: B, Basic Wall: C)", MentionText.SelectionSent(s));
        }

        [Fact]
        public void Selection_ManyTypes_ListIsShortened()
        {
            var s = Sel(Enumerable.Range(1, 6).Select(i => new MentionSelectionGroup("Walls", "W", "T" + i, 7 - i)).ToArray());
            Assert.Equal("the 21 selected walls (6 types: W: T1, W: T2, W: T3, W: T4 and 2 more)", MentionText.SelectionSent(s));
        }

        [Fact]
        public void Selection_SeveralCategories()
        {
            var s = Sel(new MentionSelectionGroup("Walls", "Basic Wall", "A", 12),
                        new MentionSelectionGroup("Doors", "Pintu Tunggal", "PTh300a", 3));
            Assert.Equal("15 elements: 12 walls, 3 doors", MentionText.SelectionLabel(s));
            Assert.Equal("the 15 selected elements (12 walls, 3 doors)", MentionText.SelectionSent(s));
        }

        [Fact]
        public void Selection_Untyped_AndEmpty()
        {
            var rooms = Sel(new MentionSelectionGroup("Rooms", "", "", 1));
            Assert.Equal("1 room", MentionText.SelectionLabel(rooms));
            Assert.Equal("the selected room", MentionText.SelectionSent(rooms));
            Assert.Equal("Nothing selected", MentionText.SelectionLabel(new MentionSelection()));
            Assert.Equal("the selected elements (nothing is selected)", MentionText.SelectionSent(null));
        }
    }

    public class MentionChipsTests
    {
        private static MentionItem DoorType(string family = "Pintu Tunggal", bool shared = false) => new MentionItem
        { Kind = "type", Name = "PTh300a", Family = family, Category = "Doors", SharedName = shared };

        private static MentionItem Level(string name) => new MentionItem { Kind = "level", Name = name };

        [Fact]
        public void Insert_ShowsShortChip_SendsFullForm()
        {
            var chips = new MentionChips();
            var (text, caret) = chips.Insert("swap @pth", 5, 9, DoorType());
            Assert.Equal("swap @PTh300a ", text);
            Assert.Equal(text.Length, caret);
            Assert.Equal("swap Pintu Tunggal: PTh300a (door type) ", chips.Expand(text));
            var m = Assert.Single(chips.Mentions());
            Assert.Equal("type", m.kind);
            Assert.Equal("Pintu Tunggal: PTh300a (door type)", m.sent);
        }

        [Fact]
        public void Insert_SharedName_ChipShowsTheFamily()
        {
            var chips = new MentionChips();
            var (text, _) = chips.Insert("@pth", 0, 4, DoorType("Pintu Berkembar", shared: true));
            Assert.Equal("@Pintu Berkembar: PTh300a ", text);
            Assert.Equal("Pintu Berkembar: PTh300a (door type) ", chips.Expand(text));
        }

        [Fact]
        public void TypingAfterAChip_KeepsIt_AndEarlierEditsMoveIt()
        {
            var chips = new MentionChips();
            var (text, _) = chips.Insert("on @ar", 3, 6, Level("Aras 01"));
            Assert.Equal("on @Aras 01 ", text);
            text = text + "please";                 // typed after
            chips.Edited(12, 0, 6);
            text = "Doors " + text;                 // typed before
            chips.Edited(0, 0, 6);
            Assert.Equal("Doors on Aras 01 please", chips.Expand(text));
            Assert.True(chips.Covers(9));
            Assert.False(chips.Covers(8));
        }

        [Fact]
        public void EditInsideAChip_TurnsItIntoPlainText()
        {
            var chips = new MentionChips();
            var (text, _) = chips.Insert("@pth", 0, 4, DoorType());
            text = text.Insert(3, "X");
            chips.Edited(3, 0, 1);
            Assert.Empty(chips.All);
            Assert.Equal("@PTXh300a ", chips.Expand(text));
        }

        [Fact]
        public void OneBackspace_RemovesTheWholeChip()
        {
            var chips = new MentionChips();
            var (text, caret) = chips.Insert("swap @pth", 5, 9, DoorType());
            // Right after the pick the caret sits after the chip's space.
            var target = chips.BackspaceTarget(text, caret);
            Assert.NotNull(target);
            var (after, newCaret) = chips.Remove(text, target);
            Assert.Equal("swap ", after);
            Assert.Equal(5, newCaret);
            Assert.Empty(chips.All);
        }

        [Fact]
        public void Backspace_InPlainTextIsNotAChip()
        {
            var chips = new MentionChips();
            var (text, _) = chips.Insert("@pth", 0, 4, DoorType());
            text += "ok";
            chips.Edited(text.Length - 2, 0, 2);
            Assert.Null(chips.BackspaceTarget(text, text.Length));
        }

        [Fact]
        public void Delete_BeforeAChip_RemovesIt_AndLaterChipsMove()
        {
            var chips = new MentionChips();
            var (t1, c1) = chips.Insert("@a", 0, 2, Level("Aras 01"));
            var (t2, _) = chips.Insert(t1 + "and @b", c1 + 4, c1 + 6, Level("Aras 02"));
            Assert.Equal("@Aras 01 and @Aras 02 ", t2);
            var (t3, _) = chips.Remove(t2, chips.DeleteTarget(0));
            Assert.Equal("and @Aras 02 ", t3);
            Assert.Equal("and Aras 02 ", chips.Expand(t3));
        }

        [Fact]
        public void Reconcile_DropsChipsTheTextNoLongerHolds()
        {
            var chips = new MentionChips();
            chips.Insert("@a", 0, 2, Level("Aras 01"));
            Assert.Equal("something else", chips.Expand("something else"));
            Assert.Empty(chips.All);
        }

        [Fact]
        public void SelectionChip_SendsTheSelectionAtSendTime()
        {
            var chips = new MentionChips();
            var picked = new MentionItem
            {
                Kind = "selection", Name = "Current selection",
                Selection = new MentionSelection(new[] { new MentionSelectionGroup("Doors", "Pintu Tunggal", "PTh300a", 1) }),
            };
            var (text, _) = chips.Insert("change @sel", 7, 11, picked);
            Assert.Equal("change @Selection ", text);
            Assert.Equal("change the selected door of type Pintu Tunggal: PTh300a ", chips.Expand(text));

            var now = new MentionSelection(new[]
            {
                new MentionSelectionGroup("Walls", "Basic Wall", "A", 8),
                new MentionSelectionGroup("Walls", "Basic Wall", "B", 4),
            });
            Assert.Equal("change the 12 selected walls (2 types: Basic Wall: A, Basic Wall: B) ", chips.Expand(text, now));
        }
    }
}
