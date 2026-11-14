#nullable enable
// The key in the corner of the view (result marks design, rules 1, 3, 5, 6): what the
// marks are in words, where they are, one line per kind with its count and swatch, and
// a focus line; or, hidden, the small pill. Also the corner it sits in and the size of
// a hollow mark. Everything here is decided from plain values: the mark groups, where
// Revit found each element (ElementPlace), pixel points. Revit-free and WPF-free.
// Every word is English (rule 11).

using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.UI.Copilot.Highlights
{
    /// <summary>What the model shows of a turn's result: nothing, the marks and key, or the hidden pill.</summary>
    public enum MarksState { None, Shown, Hidden }

    /// <summary>The life of the result in the view (rules 6 and 8): shown when the turn ends; hidden by the key's close or
    /// the card (the pill stays); shown again; gone when the pill is closed, the next turn starts, a new chat opens or Undo
    /// runs. Nothing goes by itself: no timer changes it.</summary>
    public sealed class MarkSession
    {
        public MarksState State { get; private set; } = MarksState.None;
        public string TurnId { get; private set; } = "";

        public void Show(string turnId) { TurnId = turnId ?? ""; State = MarksState.Shown; }
        public bool Hide() { if (State != MarksState.Shown) return false; State = MarksState.Hidden; return true; }
        public bool ShowAgain() { if (State != MarksState.Hidden) return false; State = MarksState.Shown; return true; }
        /// <summary>The pill's close, the next turn, a new chat, Undo: nothing of the result is left in the view.</summary>
        public void Clear() { TurnId = ""; State = MarksState.None; }

        public bool KeyVisible => State == MarksState.Shown;
        public bool PillVisible => State == MarksState.Hidden;

        /// <summary>What the model shows of this turn's result.</summary>
        public MarksState StateOf(string? turnId) => State == MarksState.None || string.IsNullOrEmpty(turnId) || turnId != TurnId ? MarksState.None : State;

        /// <summary>The card's button for this turn: "Hide marks" while its marks show, "Show in model" otherwise; none once
        /// the turn was undone (there is nothing left to show).</summary>
        public string? CardButton(string? turnId, bool undone) =>
            undone ? null : MarkKey.ButtonLabel(StateOf(turnId));
    }

    /// <summary>Where one marked element is, as Revit answered: in the active view, or owned by another view
    /// (a tag, a dimension: <see cref="ViewId"/> set), or not visible here with no one view to name (a model element).</summary>
    public sealed class ElementPlace
    {
        public long Id;
        /// <summary>Index of the group in <see cref="HighlightSet.Kinds"/>.</summary>
        public int Group;
        public bool InActiveView;
        public long? ViewId;
        public string? ViewName;
        /// <summary>The other view is a plan (the key then says "plans", not "views").</summary>
        public bool IsPlan;
    }

    public sealed class KeyLine
    {
        public MarkOutcome? Outcome;
        public MarkKind Kind;
        public string Label = "";
        public string? Small;
        public int Count;
    }

    public sealed class KeyText
    {
        /// <summary>"Copilot result · 2:35 PM" (drawn in capitals).</summary>
        public string Header = "";
        /// <summary>The header's dot: the first kind's outcome.</summary>
        public MarkOutcome HeadOutcome = MarkOutcome.Made;
        /// <summary>"58 door tags made".</summary>
        public string Title = "";
        /// <summary>"in Aras Tanah (01w_WIP), this view".</summary>
        public string Where = "";
        /// <summary>The part of <see cref="Where"/> drawn bold (the other view's name), or null.</summary>
        public string? WhereBold;
        public List<KeyLine> Lines = new List<KeyLine>();
        /// <summary>"Showing all 58 · 10 groups in the pane"; "Open Pelan Tapak and show them".</summary>
        public string Focus = "";
        public string? FocusBold;
        /// <summary>A group or a mark is focused: the focus line has "Show all" (step 2).</summary>
        public bool ShowAll;
        /// <summary>Some or all of the marks are off screen: the focus line's zoom button ("Zoom to all", "Zoom to them"), or null.</summary>
        public string? ZoomLabel;
        /// <summary>No mark is on screen: the zoom is the key's one prominent action.</summary>
        public bool ZoomProminent;
        /// <summary>The Open button's view, or null for no button.</summary>
        public long? OpenViewId;
        public string? OpenViewName;
        /// <summary>"Copilot result hidden · 58 door tags made".</summary>
        public string Pill = "";
        /// <summary>Elements marked in the active view, and all of them.</summary>
        public int Here;
        public int Total;
    }

    public static class MarkKey
    {
        /// <summary>The most kinds the key lists one by one; the rest are one "+ N more kinds" line.</summary>
        public const int MaxLines = 4;
        public const double Margin = 12;
        /// <summary>A mark nearer than this to the key's box counts as under it.</summary>
        public const double Clearance = 14;

        /// <summary>The result card's marks button: "Hide marks" while the marks show, else "Show in model".</summary>
        public static string ButtonLabel(MarksState state) => state == MarksState.Shown ? "Hide marks" : "Show in model";

        /// <summary>The key's words and counts. <paramref name="places"/> is where Revit found the elements (null: not
        /// resolved, everything counts as in this view).</summary>
        public static KeyText Build(HighlightSet set, IReadOnlyCollection<ElementPlace>? places, string activeViewName)
        {
            var key = new KeyText();
            if (set == null) return key;
            var kinds = set.Kinds.Where(k => k.Count > 0 || k.Ids.Count > 0).ToList();
            key.Header = "Copilot result" + (string.IsNullOrWhiteSpace(set.Time) ? "" : " · " + set.Time.Trim());
            // The header's dot: what was done (the first made or changed kind); amber only when a look is all there is.
            key.HeadOutcome = kinds.FirstOrDefault(k => k.Outcome != MarkOutcome.NeedsLook)?.Outcome
                              ?? (kinds.Count > 0 ? MarkOutcome.NeedsLook : MarkOutcome.Made);
            key.Title = Title(kinds);
            key.Pill = "Copilot result hidden · " + key.Title;

            // Where each kind is: here, in a named other view, or not visible here.
            var byGroup = (places ?? Array.Empty<ElementPlace>()).GroupBy(p => p.Group).ToDictionary(g => g.Key, g => g.ToList());
            var resolved = places != null;
            // The key's own numbers count ELEMENTS, each once: an element listed twice (in two kinds, or twice in one) is one
            // element, here or elsewhere. Live 13 Nov, F-t04b: "Showing 52 here · 52 not in this view" for 52 elements.
            var hereIds = new HashSet<long>(); var nowhereIds = new HashSet<long>(); var seen = new HashSet<long>();
            var viewIds = new Dictionary<long, HashSet<long>>();
            int sum = 0, sumHere = 0, unresolved = 0;
            var otherViews = new Dictionary<long, (string Name, bool Plan, int Count)>();
            var perKind = new List<(MarkGroup Kind, int Count, int Here, Dictionary<long, int> Views)>();
            for (var i = 0; i < set.Kinds.Count; i++)
            {
                var k = set.Kinds[i];
                if (!kinds.Contains(k)) continue;
                var views = new Dictionary<long, int>();
                int count, kHere;
                if (!resolved) { count = k.Count; kHere = count; }
                else
                {
                    var list = byGroup.TryGetValue(i, out var l) ? l : new List<ElementPlace>();
                    // A type's instances are counted as Revit found them; an element group keeps its true count.
                    var distinct = list.Select(p => p.Id).Distinct().Count();
                    count = k.Kind == MarkKind.TypeInstances ? distinct : Math.Max(k.Count, distinct);
                    if (k.Kind == MarkKind.TypeInstances && list.Count == 0) count = k.Count;
                    kHere = list.Where(p => p.InActiveView).Select(p => p.Id).Distinct().Count();
                    foreach (var p in list)
                    {
                        seen.Add(p.Id);
                        if (p.InActiveView) { hereIds.Add(p.Id); continue; }
                        if (p.ViewId is long vid)
                        {
                            views[vid] = (views.TryGetValue(vid, out var n) ? n : 0) + 1;
                            if (!viewIds.TryGetValue(vid, out var set1)) viewIds[vid] = set1 = new HashSet<long>();
                            set1.Add(p.Id);
                            otherViews[vid] = (p.ViewName ?? "another view", p.IsPlan, 0);
                        }
                        else nowhereIds.Add(p.Id);
                    }
                    // Elements the kind counts that Revit did not place (more than its ids, or ids it did not find).
                    unresolved += Math.Max(0, count - distinct);
                }
                sum += count; sumHere += kHere;
                perKind.Add((k, count, kHere, views));
            }
            foreach (var kv in viewIds) otherViews[kv.Key] = (otherViews[kv.Key].Name, otherViews[kv.Key].Plan, kv.Value.Count(id => !hereIds.Contains(id)));
            nowhereIds.ExceptWith(hereIds);
            foreach (var kv in viewIds) nowhereIds.ExceptWith(kv.Value);
            int total, here;
            if (resolved) { here = hereIds.Count; total = seen.Count + unresolved; }
            else { here = sumHere; total = sum; }
            key.Total = total; key.Here = here;
            var elsewhere = otherViews.Values.Sum(v => v.Count);
            // Not marked here and in no view the key can name: a model element hidden in this view, or one Revit did not place.
            var notHere = resolved ? nowhereIds.Count + unresolved : 0;
            var unknownElsewhere = notHere;
            var viewWord = otherViews.Count > 0 && otherViews.Values.All(v => v.Plan) ? "plan" : "view";
            var view = string.IsNullOrWhiteSpace(activeViewName) ? "this view" : activeViewName.Trim();

            // Where.
            if (here >= total || (here > 0 && elsewhere == 0 && unknownElsewhere == 0))
                key.Where = "in " + view + ", this view";
            else if (here == 0 && otherViews.Count == 1 && unknownElsewhere == 0)
            {
                var only = otherViews.Values.First().Name;
                key.Where = "in " + only + ". None are in this view, so nothing is marked here.";
                key.WhereBold = only;
            }
            else if (here == 0 && otherViews.Count > 1)
                key.Where = "in " + N(otherViews.Count) + " " + viewWord + "s. None are in this view, so nothing is marked here.";
            else if (here == 0)
                key.Where = "None are visible in this view, so nothing is marked here.";
            else if (otherViews.Count > 0)
                key.Where = "in " + N(otherViews.Count + 1) + " " + viewWord + "s; " + N(here) + " in this view";
            else
                key.Where = N(here) + " of " + N(total) + " are visible in this view";

            // One line per kind.
            foreach (var (k, count, kHere, views) in perKind.Take(MaxLines))
            {
                var line = new KeyLine { Outcome = k.Outcome, Kind = k.Kind, Label = LineLabel(k), Count = count };
                if (resolved && kHere < count)
                {
                    if (kHere > 0) { line.Small = "in this view"; line.Count = kHere; }
                    else if (views.Count == 1) line.Small = "in " + otherViews[views.Keys.First()].Name;
                    else if (views.Count > 1) line.Small = "in " + N(views.Count) + " " + viewWord + "s";
                    else line.Small = "not visible here";
                }
                else if (k.Outcome == MarkOutcome.NeedsLook) line.Small = "needs a look";
                key.Lines.Add(line);
            }
            if (perKind.Count > MaxLines)
                key.Lines.Add(new KeyLine { Label = "+ " + N(perKind.Count - MaxLines) + " more kinds", Count = perKind.Skip(MaxLines).Sum(p => p.Count) });
            if (here > 0 && elsewhere > 0)
                key.Lines.Add(new KeyLine { Label = "In " + N(otherViews.Count) + " other " + viewWord + (otherViews.Count == 1 ? "" : "s"),
                    Small = otherViews.Count == 1 ? otherViews.Values.First().Name : null, Count = elsewhere });

            // Focus line and the one Open button.
            if (here == 0 && otherViews.Count > 0)
            {
                var target = otherViews.OrderByDescending(v => v.Value.Count).ThenBy(v => v.Value.Name, StringComparer.Ordinal).First();
                key.OpenViewId = target.Key; key.OpenViewName = target.Value.Name;
                key.Focus = otherViews.Count == 1
                    ? "Open " + target.Value.Name + " and show them"
                    : "Open " + target.Value.Name + ", which has " + N(target.Value.Count) + " of them";
                key.FocusBold = "Open " + target.Value.Name;
            }
            else if (here == 0)
                key.Focus = "Nothing to mark in this view";
            else if (here >= total)
            {
                var rows = set.Rows.Count;
                key.FocusBold = "all " + N(total);
                key.Focus = "Showing all " + N(total) + (rows > 1 ? " · " + N(rows) + " groups in the pane" : "");
            }
            else
            {
                // Some here. Those in other views have their own line above ("In 2 other views"); the focus line adds only
                // the ones in no view it can name, and says nothing more when there are none.
                key.FocusBold = N(here) + " here";
                key.Focus = "Showing " + N(here) + " here" + (notHere > 0 ? " · " + N(notHere) + " not visible here" : "");
            }
            return key;
        }

        /// <summary>How many marks lie inside the view's visible rectangle: each mark's model point projected with the view's
        /// zoom corners and its right and up directions (<see cref="ViewProjection"/>). A degenerate rectangle shows none.</summary>
        public static int OnScreen(IEnumerable<Vec3> marks, Vec3 corner0, Vec3 corner1, Vec3 right, Vec3 up) =>
            (marks ?? Array.Empty<Vec3>()).Count(p => ViewProjection.InView(ViewProjection.ToPercent(p, corner0, corner1, right, up)));

        /// <summary>The key speaks about what is on screen (live 14 Nov: "Showing all 52" over a view zoomed away from the
        /// model). <paramref name="drawn"/> marks are drawn in this view and <paramref name="onScreen"/> of them lie inside its
        /// visible rectangle (null: not known, as in a perspective view; the line stays). All on screen: the line as it was.
        /// Some: "Showing 20 of 52 on screen" with Zoom to all. None: "None of the 52 is on screen" with Zoom to them, the
        /// key's prominent action. The view is never zoomed by itself: the key offers it.</summary>
        public static void ApplyOnScreen(KeyText key, int drawn, int? onScreen)
        {
            if (key == null || !onScreen.HasValue || drawn <= 0 || key.Here <= 0 || onScreen.Value >= drawn) return;
            var seen = onScreen.Value;
            // Past the marks' cap the drawn marks stand for every element here: count those, not the cap.
            var n = key.Here;
            if (seen <= 0)
            {
                key.Focus = "None of the " + N(n) + " is on screen";
                key.FocusBold = "None";
                key.ZoomLabel = "Zoom to them";
                key.ZoomProminent = true;
            }
            else
            {
                var shown = drawn >= n ? seen : (int)Math.Round(seen * (double)n / drawn);
                key.Focus = "Showing " + N(shown) + " of " + N(n) + " on screen";
                key.FocusBold = N(shown) + " of " + N(n);
                key.ZoomLabel = "Zoom to all";
                key.ZoomProminent = false;
            }
        }

        /// <summary>"58 door tags made"; "12 made · 34 changed"; "3 elements need a look".</summary>
        public static string Title(IReadOnlyList<MarkGroup> kinds)
        {
            int Sum(MarkOutcome o) => kinds.Where(k => k.Outcome == o).Sum(k => k.Count);
            var made = Sum(MarkOutcome.Made); var changed = Sum(MarkOutcome.Changed); var look = Sum(MarkOutcome.NeedsLook);
            string What(MarkOutcome o, int n)
            {
                var ks = kinds.Where(k => k.Outcome == o && k.Kind != MarkKind.TypeInstances).ToList();
                var cats = ks.Select(k => k.Category).Distinct().ToList();
                if (ks.Count > 0 && ks.Count == kinds.Count(k => k.Outcome == o) && cats.Count == 1 && cats[0].Length > 0 && cats[0] != "Elements")
                    return N(n) + " " + Lower(n == 1 ? Singular(cats[0]) : cats[0]);
                return N(n) + (n == 1 ? " element" : " elements");
            }
            if (made > 0 && changed > 0) return N(made) + " made · " + N(changed) + " changed";
            if (made > 0) return What(MarkOutcome.Made, made) + " made";
            if (changed > 0) return What(MarkOutcome.Changed, changed) + " changed";
            if (look > 0) return N(look) + (look == 1 ? " element needs a look" : " elements need a look");
            return "Nothing to mark";
        }

        /// <summary>The words beside a swatch, which also say what its colour means: "New door tag", "Changed wall",
        /// "Type 1X13W, changed", "Read back different".</summary>
        public static string LineLabel(MarkGroup k)
        {
            switch (k.Outcome)
            {
                case MarkOutcome.Made: return "New " + Lower(Singular(k.Category.Length > 0 ? k.Category : "Elements"));
                case MarkOutcome.Changed:
                    if (k.Kind == MarkKind.TypeInstances) return "Changed type " + ShortName(k.Title, MaxLineChars - "Changed type ".Length);
                    // A name from the model (a server row, "<old type> → <new type>"): as written, never lower-cased.
                    if (k.Named) return "Changed " + ShortChange(k.Title, MaxLineChars - "Changed ".Length);
                    return "Changed " + Lower(Singular(k.Category.Length > 0 ? k.Category : "Elements"));
                case MarkOutcome.NeedsLook: return k.Title.Length > 0 ? Trim(k.Title, 48) : "Needs a look";
                default: return "Skipped " + Lower(Singular(k.Category.Length > 0 ? k.Category : "Elements"));
            }
        }

        /// <summary>The first corner (top-left, top-right, bottom-left, bottom-right) whose key box, widened by
        /// <see cref="Clearance"/>, has no mark under it; top-left when every corner has one.</summary>
        public static (double X, double Y, int Corner) Corner(double viewW, double viewH, double keyW, double keyH, IEnumerable<(double X, double Y)>? marks)
        {
            var pts = (marks ?? Array.Empty<(double, double)>()).ToList();
            var corners = new[]
            {
                (Margin, Margin), (viewW - keyW - Margin, Margin),
                (Margin, viewH - keyH - Margin), (viewW - keyW - Margin, viewH - keyH - Margin),
            };
            for (var i = 0; i < corners.Length; i++)
            {
                var (x, y) = corners[i];
                var hit = pts.Any(p => p.X > x - Clearance && p.X < x + keyW + Clearance && p.Y > y - Clearance && p.Y < y + keyH + Clearance);
                if (!hit) return (Math.Max(0, x), Math.Max(0, y), i);
            }
            return (Margin, Margin, 0);
        }

        /// <summary>The pill's corner: where the key was, so hiding leaves the pill in the same place.</summary>
        public static (double X, double Y) PillAt(int corner, double viewW, double viewH, double pillW, double pillH)
        {
            var right = corner == 1 || corner == 3; var bottom = corner == 2 || corner == 3;
            return (right ? Math.Max(0, viewW - pillW - Margin) : Margin, bottom ? Math.Max(0, viewH - pillH - Margin) : Margin);
        }

        /// <summary>Mark sizes in pixels (DIP): a few steps, so the images are cached and reused.</summary>
        public static readonly int[] Sizes = { 22, 30, 40, 52 };

        /// <summary>The hollow mark's size for an element whose box is this big on screen: a little bigger than the box,
        /// so the frame goes round the tag and does not cover it; never smaller than a fingertip of 22 px, never more
        /// than 52 px (a long dimension string still gets one mark at its middle in this round; its track is step 3).</summary>
        public static int SizeFor(double boxWpx, double boxHpx)
        {
            var side = Math.Max(double.IsNaN(boxWpx) ? 0 : boxWpx, double.IsNaN(boxHpx) ? 0 : boxHpx) + 8;
            foreach (var s in Sizes) if (side <= s) return s;
            return Sizes[Sizes.Length - 1];
        }

        /// <summary>The frame (rounded square) is for an annotation or a dimension; an element gets a ring.</summary>
        public static bool IsFrame(MarkKind k) => k == MarkKind.Annotation || k == MarkKind.Dimension;

        internal static string Singular(string plural)
        {
            var p = (plural ?? "").Trim();
            if (p.EndsWith("ies", StringComparison.Ordinal) && p.Length > 3) return p.Substring(0, p.Length - 3) + "y";
            if (p.EndsWith("is", StringComparison.OrdinalIgnoreCase) || p.EndsWith("us", StringComparison.OrdinalIgnoreCase) || p.EndsWith("ss", StringComparison.Ordinal)) return p;
            if (p.EndsWith("s", StringComparison.Ordinal) && p.Length > 1) return p.Substring(0, p.Length - 1);
            return p;
        }

        /// <summary>"Door Tags" → "door tags"; an all-capitals word (MEP, HVAC) keeps its capitals.</summary>
        internal static string Lower(string s) =>
            string.Join(" ", (s ?? "").Split(' ').Select(w => w.Length > 1 && w.All(c => !char.IsLetter(c) || char.IsUpper(c)) ? w : w.ToLowerInvariant()));

        private static string Trim(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";

        /// <summary>About how many characters of a line's 12px label fit the key's two lines (the key is 300 px wide).</summary>
        public const int MaxLineChars = 62;

        /// <summary>A name cut in its middle, never at its end: two families' types that share a type name and a family prefix
        /// differ near the end of the family's name ("…_2X10W: Standard", "…_2X6W: Standard"), so the end stays (live 13 Nov,
        /// F-t04b: both lines read "Changed type LumCo_rcs-dl_rd_LED_2X…").</summary>
        public static string Middle(string s, int n)
        {
            s = (s ?? "").Trim();
            if (s.Length <= n || n < 8) return s;
            var head = Math.Max(4, (n - 1) / 3);
            var tail = n - 1 - head;
            return s.Substring(0, head) + "…" + s.Substring(s.Length - tail);
        }

        /// <summary>A "Family: Type" name in at most <paramref name="n"/> characters: whole when it fits; else the type's own
        /// name whole with the family cut in its middle (its end kept, where two families' names differ); else the type alone;
        /// else the name cut in its middle.</summary>
        public static string ShortName(string s, int n)
        {
            s = (s ?? "").Trim();
            if (s.Length <= n) return s;
            var colon = s.LastIndexOf(": ", StringComparison.Ordinal);
            if (colon > 0)
            {
                var type = s.Substring(colon + 2).Trim();
                if (type.Length > 0 && type.Length <= n)
                {
                    var room = n - type.Length - 2;
                    return room >= 8 ? Middle(s.Substring(0, colon), room) + ": " + type : type;
                }
            }
            return Middle(s, n);
        }

        /// <summary>"&lt;old&gt; → &lt;new&gt;" in at most <paramref name="n"/> characters, each side shortened alone so both
        /// type names stay readable.</summary>
        public static string ShortChange(string s, int n)
        {
            s = (s ?? "").Trim();
            if (s.Length <= n) return s;
            var at = s.IndexOf(" → ", StringComparison.Ordinal);
            if (at < 0) return ShortName(s, n);
            var side = (n - 3) / 2;
            return ShortName(s.Substring(0, at), side) + " → " + ShortName(s.Substring(at + 3), n - 3 - side);
        }

        /// <summary>A label as the key draws it, on up to two lines: a break WPF may take after every "_" (it has none
        /// there, so it cut "…_LED_2X10W" one character at a time). Reads exactly like the label.</summary>
        public static string Breakable(string s) => (s ?? "").Replace("_", "_\u200B");
        private static string N(int n) => HighlightSet.N(n);
    }
}
