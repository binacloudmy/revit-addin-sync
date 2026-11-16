#nullable enable
// The bar in the corner of the view (the "Gabung" result view, G5): one line that says
// what was done ("52 elements changed · 2 need a look") and the truth about where the marks
// are ("14 of 52 on screen", "in Site Plan"), with its buttons; or, hidden, the small pill.
// Also the corner it sits in, the size of a frame and the dot's size. Everything here is
// decided from plain values: the mark groups, where Revit found each element
// (ElementPlace), pixel points. Revit-free and WPF-free. Every word is English.

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

        /// <summary>An undo of the turn this result is of takes the result out of the view, whoever ran it: the card's
        /// button, the key's, or Revit's own Ctrl+Z (live round 13: after Ctrl+Z the key still read "60 doors changed" and
        /// offered Undo). A redo, or an undo of another turn, leaves it.</summary>
        public bool GoneWith(BinaVibe.Copilot.Undo.UndoChange? change) =>
            change != null && change.Kind == BinaVibe.Copilot.Undo.UndoChangeKind.Undone && StateOf(change.TurnId) != MarksState.None;

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

    /// <summary>The bar's words (the "Gabung" result view, G5): one line in the corner of the view.</summary>
    public sealed class KeyText
    {
        /// <summary>The bar's dot: the first made or changed kind's outcome; amber only when a look is all there is.</summary>
        public MarkOutcome HeadOutcome = MarkOutcome.Made;
        /// <summary>"52 elements changed", "58 door tags made" (bold).</summary>
        public string Title = "";
        /// <summary>" · 2 need a look" when something needs a look beside what was made or changed; "" otherwise.</summary>
        public string Look = "";
        /// <summary>How many elements need a look (the stepper's count).</summary>
        public int Looks;
        /// <summary>"in Level 1, this view": where the result is, for the card's header.</summary>
        public string Where = "";
        /// <summary>The truth about where the marks are, short, on the bar's line: "None of the 52 is on screen", "14 of 52 on
        /// screen", "in Site Plan"; "" when every mark is in this view and on screen.</summary>
        public string State = "";
        /// <summary>Some or all of the marks are off screen: the zoom button ("Zoom to all", "Zoom to them"), or null.</summary>
        public string? ZoomLabel;
        /// <summary>No mark is on screen: the zoom is the bar's one prominent action.</summary>
        public bool ZoomProminent;
        /// <summary>The view "Go to view" opens (the elements are all in other views), or null for no button.</summary>
        public long? OpenViewId;
        public string? OpenViewName;
        /// <summary>"Copilot result hidden · 58 door tags made".</summary>
        public string Pill = "";
        /// <summary>Elements marked in the active view, and all of them.</summary>
        public int Here;
        public int Total;
        /// <summary>The stepper through what needs a look ("1 of 2"), or "" when nothing needs a look.</summary>
        public string Step = "";
    }

    public static class MarkKey
    {
        public const double Margin = 12;
        public const string GoToView = "Go to view";

        /// <summary>The result card's marks button: "Hide marks" while the marks show, else "Show in model".</summary>
        public static string ButtonLabel(MarksState state) => state == MarksState.Shown ? "Hide marks" : "Show in model";

        /// <summary>The bar's words and counts. <paramref name="places"/> is where Revit found the elements (null: not
        /// resolved, everything counts as in this view).</summary>
        public static KeyText Build(HighlightSet set, IReadOnlyCollection<ElementPlace>? places, string activeViewName)
        {
            var key = new KeyText();
            if (set == null) return key;
            var kinds = set.Kinds.Where(k => k.Count > 0 || k.Ids.Count > 0).ToList();
            key.HeadOutcome = kinds.FirstOrDefault(k => k.Outcome != MarkOutcome.NeedsLook)?.Outcome
                              ?? (kinds.Count > 0 ? MarkOutcome.NeedsLook : MarkOutcome.Made);
            key.Title = Title(kinds);
            key.Pill = "Copilot result hidden · " + key.Title;
            key.Looks = kinds.Where(k => k.Outcome == MarkOutcome.NeedsLook).Sum(k => k.Count);
            // The title already says it when a look is all there is.
            if (key.Looks > 0 && kinds.Any(k => k.Outcome == MarkOutcome.Made || k.Outcome == MarkOutcome.Changed))
                key.Look = " · " + N(key.Looks) + (key.Looks == 1 ? " needs a look" : " need a look");

            // Where each element is: here, in a named other view, or not visible here. The numbers count ELEMENTS, each once:
            // an element listed twice (in two kinds, or twice in one) is one element. Live 13 Nov, F-t04b: "Showing 52 here ·
            // 52 not in this view" for 52 elements.
            var byGroup = (places ?? Array.Empty<ElementPlace>()).GroupBy(p => p.Group).ToDictionary(g => g.Key, g => g.ToList());
            var resolved = places != null;
            var hereIds = new HashSet<long>(); var seen = new HashSet<long>();
            var viewIds = new Dictionary<long, HashSet<long>>();
            var viewNames = new Dictionary<long, (string Name, bool Plan)>();
            int sum = 0, unresolved = 0;
            for (var i = 0; i < set.Kinds.Count; i++)
            {
                var k = set.Kinds[i];
                if (!kinds.Contains(k)) continue;
                if (!resolved) { sum += k.Count; continue; }
                var list = byGroup.TryGetValue(i, out var l) ? l : new List<ElementPlace>();
                // A type's instances are counted as Revit found them; an element group keeps its true count.
                var distinct = list.Select(p => p.Id).Distinct().Count();
                var count = k.Kind == MarkKind.TypeInstances ? distinct : Math.Max(k.Count, distinct);
                if (k.Kind == MarkKind.TypeInstances && list.Count == 0) count = k.Count;
                foreach (var p in list)
                {
                    seen.Add(p.Id);
                    if (p.InActiveView) { hereIds.Add(p.Id); continue; }
                    if (p.ViewId is long vid)
                    {
                        if (!viewIds.TryGetValue(vid, out var ids)) viewIds[vid] = ids = new HashSet<long>();
                        ids.Add(p.Id);
                        viewNames[vid] = (p.ViewName ?? "another view", p.IsPlan);
                    }
                }
                // Elements the kind counts that Revit did not place (more than its ids, or ids it did not find).
                unresolved += Math.Max(0, count - distinct);
            }
            int total, here;
            if (resolved) { here = hereIds.Count; total = seen.Count + unresolved; }
            else { here = sum; total = sum; }
            key.Total = total; key.Here = here;
            var otherViews = viewIds.ToDictionary(kv => kv.Key, kv => (viewNames[kv.Key].Name, viewNames[kv.Key].Plan, Count: kv.Value.Count(id => !hereIds.Contains(id))))
                .Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
            var viewWord = otherViews.Count > 0 && otherViews.Values.All(v => v.Plan) ? "plan" : "view";
            var view = string.IsNullOrWhiteSpace(activeViewName) ? "this view" : activeViewName.Trim();

            // Where (the card's header), and the bar's state when not every element is in this view.
            if (here >= total)
                key.Where = "in " + view + ", this view";
            else if (here == 0 && otherViews.Count > 0)
            {
                var target = otherViews.OrderByDescending(v => v.Value.Count).ThenBy(v => v.Value.Name, StringComparer.Ordinal).First();
                key.OpenViewId = target.Key; key.OpenViewName = target.Value.Name;
                key.Where = otherViews.Count == 1 ? "in " + target.Value.Name : "in " + N(otherViews.Count) + " " + viewWord + "s";
                key.State = key.Where;
            }
            else if (here == 0)
            {
                key.Where = "None of the " + N(total) + " is visible in this view";
                key.State = key.Where;
            }
            else
            {
                key.Where = N(here) + " of " + N(total) + " in " + view + ", this view";
                key.State = N(here) + " of " + N(total) + " in this view";
            }
            return key;
        }

        /// <summary>How many marks lie inside the view's visible rectangle: each mark's model point projected with the view's
        /// zoom corners and its right and up directions (<see cref="ViewProjection"/>). A degenerate rectangle shows none.</summary>
        public static int OnScreen(IEnumerable<Vec3> marks, Vec3 corner0, Vec3 corner1, Vec3 right, Vec3 up) =>
            (marks ?? Array.Empty<Vec3>()).Count(p => ViewProjection.InView(ViewProjection.ToPercent(p, corner0, corner1, right, up)));

        /// <summary>A window's DPI as the pixels-per-DIP scale (96 is 1.0, 144 is 1.5, 192 is 2.0); 0 for a value no display
        /// has (the call failed).</summary>
        public static double ScaleOf(uint dpi) => dpi >= 72 && dpi <= 960 ? dpi / 96.0 : 0;

        /// <summary>What a mark says when the pointer rests on it: its kind as the key words it, and what a click does.</summary>
        public static string Tooltip(MarkGroup k) => LineLabel(k).TrimEnd('.') + " · click to select";

        /// <summary>What a count badge says when the pointer rests on it.</summary>
        public static string BadgeTooltip(int count) => N(count) + " marks here · click to select them";

        /// <summary>How far the repaint moves each zoom corner along the view's diagonal: a tenth of a percent. Enough for
        /// Revit to draw the view again, too little to see, and the next one goes the other way.</summary>
        public const double RepaintNudge = 0.001;

        /// <summary>The scale the mark bitmaps are made at: always 100 %. Revit scales an in-canvas image itself on a scaled
        /// display. Measured on the 200 % test display: a 22 px ring drew 36 px, a 44 px ring 75 px, a 24 px dot 40 px
        /// (about 1.67 times each), so bitmaps made at the display's scale came out 1.67 times too large (live rounds 14
        /// to 16).</summary>
        public const double ImageScale = 1.0;

        /// <summary>The zoom corners moved no further than the repaint's own nudge does (with room for rounding): not a zoom
        /// of the drafter's or of a row's. <paramref name="moved"/>: how far the two corners went, added up;
        /// <paramref name="diagonal"/>: the distance between the corners before.</summary>
        public static bool IsNudge(double moved, double diagonal) => diagonal > 0 && moved <= 4 * RepaintNudge * diagonal;

        /// <summary>Revit drew none of the marks (it refused every in-canvas control): the bar never claims marks then.
        /// <paramref name="places"/> elements are in this view, <paramref name="drawn"/> controls went up.</summary>
        public static void ApplyNotDrawn(KeyText key, int places, int drawn)
        {
            if (key == null || places <= 0 || drawn > 0) return;
            key.State = "the marks could not be drawn in this view";
            key.ZoomLabel = null; key.ZoomProminent = false;
        }

        /// <summary>The bar speaks about what is on screen (live 14 Nov: "Showing all 52" over a view zoomed away from the
        /// model). <paramref name="drawn"/> marks are in this view and <paramref name="onScreen"/> of them lie inside its
        /// visible rectangle (null: not known, as in a perspective view; the state stays). All on screen: as it was. Some:
        /// "14 of 52 on screen" with Zoom to all. None: "None of the 52 is on screen" with Zoom to them, the bar's prominent
        /// action. The view is never zoomed by itself: the bar offers it.</summary>
        public static void ApplyOnScreen(KeyText key, int drawn, int? onScreen)
        {
            if (key == null || !onScreen.HasValue || drawn <= 0 || key.Here <= 0 || onScreen.Value >= drawn) return;
            var seen = onScreen.Value;
            // Past the marks' cap the drawn marks stand for every element here: count those, not the cap.
            var n = key.Here;
            if (seen <= 0)
            {
                key.State = "None of the " + N(n) + " is on screen";
                key.ZoomLabel = "Zoom to them";
                key.ZoomProminent = true;
            }
            else
            {
                var shown = drawn >= n ? seen : (int)Math.Round(seen * (double)n / drawn);
                key.State = N(shown) + " of " + N(n) + " on screen";
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

        /// <summary>A dot's diameter in DIP: one size for every element, whatever the zoom (the display scale multiplies it).</summary>
        public const double DotDip = 10;

        /// <summary>A badge's height in DIP (a count or a number); numbered badges that come closer than this go together.</summary>
        public const double BadgeDip = 18;

        /// <summary>The most marks that get a number; past it what needs a look is a plain amber dot.</summary>
        public const int MaxNumbers = 99;

        /// <summary>What needs a look, in the card's order (the result's look groups in turn, each element once): the
        /// numbered badges count 1, 2, 3 along it and the bar's stepper walks it.</summary>
        public static List<long> LookOrder(HighlightSet set)
        {
            var seen = new HashSet<long>();
            var list = new List<long>();
            foreach (var k in set?.Kinds ?? new List<MarkGroup>())
                if (k.Outcome == MarkOutcome.NeedsLook)
                    foreach (var id in k.Ids) if (seen.Add(id)) list.Add(id);
            return list;
        }

        /// <summary>The number a mark of <paramref name="id"/> shows (1-based), 0 when it has none: not in the look order, or
        /// past <see cref="MaxNumbers"/>.</summary>
        public static int NumberOf(long id, IReadOnlyDictionary<long, int> places) =>
            places != null && places.TryGetValue(id, out var at) && at < MaxNumbers ? at + 1 : 0;

        /// <summary>Where the stepper goes from <paramref name="at"/> (-1: nothing stepped to yet) by <paramref name="delta"/>
        /// among <paramref name="count"/>: round at both ends; from nothing, › is the first and ‹ the last; 0 is where it is
        /// (the first when nothing yet). -1 when there is nothing to step through.</summary>
        public static int StepTo(int at, int delta, int count)
        {
            if (count <= 0) return -1;
            if (at < 0 || at >= count) return delta < 0 ? count - 1 : 0;
            return ((at + delta) % count + count) % count;
        }

        /// <summary>The stepper's words: "1 of 2" (the first before any step).</summary>
        public static string StepLabel(int at, int count) => count <= 0 ? "" : N(Math.Max(0, Math.Min(at, count - 1)) + 1) + " of " + N(count);

        /// <summary>Frame sizes in pixels (DIP): a few steps, so the images are cached and reused. Frames only: a dot has one size.</summary>
        public static readonly int[] Sizes = { 22, 30, 40, 52 };

        /// <summary>A frame's size for an annotation whose box is this big on screen: a little bigger than the box,
        /// so the frame goes round the tag and does not cover it; never smaller than a fingertip of 22 px, never more
        /// than 52 px (a long dimension string still gets one frame at its middle).</summary>
        public static int SizeFor(double boxWpx, double boxHpx)
        {
            var side = Math.Max(double.IsNaN(boxWpx) ? 0 : boxWpx, double.IsNaN(boxHpx) ? 0 : boxHpx) + 8;
            foreach (var s in Sizes) if (side <= s) return s;
            return Sizes[Sizes.Length - 1];
        }

        /// <summary>The frame (rounded square) is for an annotation or a dimension; an element gets a dot.</summary>
        // A tag gets the hollow frame (a dot would hide its text). A dimension does not: it is a long string, and a square
        // frame at its middle covered the numbers and marked nothing (live D1, 14 November); it gets a dot like an element.
        public static bool IsFrame(MarkKind k) => k == MarkKind.Annotation;

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

        /// <summary>The most characters of a mark's tooltip label.</summary>
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

        private static string N(int n) => HighlightSet.N(n);
    }
}
