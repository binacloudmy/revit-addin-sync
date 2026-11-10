#nullable enable
// Which elements a turn touched, by state, for the in-canvas rings and the
// overlay callouts (result-pattern spec). Revit-free.

using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Highlights
{
    public enum MarkState { Changed, Created, Selected, Fixable, NeedsYou, Deleted }

    public sealed class HighlightSet
    {
        public static readonly string Green = ThemeTokens.LightHex("Cp.Mark.Green");
        public static readonly string Blue = ThemeTokens.LightHex("Cp.Mark.Blue");
        public static readonly string Red = ThemeTokens.LightHex("Cp.Mark.Red");
        public static readonly string Amber = ThemeTokens.LightHex("Cp.Mark.Amber");

        public Dictionary<MarkState, HashSet<long>> Ids = new Dictionary<MarkState, HashSet<long>>();
        /// <summary>Type ids whose instances carry the state (type parameters): the ring
        /// layer expands them to instances inside Revit.</summary>
        public Dictionary<MarkState, HashSet<long>> TypeIds = new Dictionary<MarkState, HashSet<long>>();
        public List<TypeGroup> Groups = new List<TypeGroup>();
        public ResultCardModel? Card;
        public string Headline = "";
        public string Detail = "";

        public bool IsEmpty => Ids.Values.All(s => s.Count == 0) && TypeIds.Values.All(s => s.Count == 0);

        public static string ColorOf(MarkState s)
        {
            switch (s)
            {
                case MarkState.Changed: return Green;
                case MarkState.Created:
                case MarkState.Selected: return Blue;
                case MarkState.Fixable: return Red;
                default: return Amber;
            }
        }

        public static HighlightSet From(ResultCardModel card, IReadOnlyList<Envelope> envelopes)
        {
            var set = new HighlightSet();
            if (card == null) return set;
            set.Card = card; set.Headline = card.Headline; set.Detail = card.Detail; set.Groups = card.Groups;

            foreach (var g in card.Groups)
                foreach (var id in g.Ids) set.Add(g.IsType ? set.TypeIds : set.Ids, MarkState.Changed, id);
            foreach (var c in card.Created) set.Add(set.Ids, MarkState.Created, c.Id);
            foreach (var f in card.Findings) foreach (var id in f.Ids) set.Add(set.Ids, f.Fixable ? MarkState.Fixable : MarkState.NeedsYou, id);

            if (envelopes != null)
                foreach (var env in envelopes)
                {
                    var t = env?.Changes?.Touched;
                    if (t?.Deleted != null) foreach (var d in t.Deleted) set.Add(set.Ids, MarkState.Deleted, d.Id);
                    if (card.Kind == ResultKind.Selection && env?.Data is System.Text.Json.JsonElement data && data.ValueKind == System.Text.Json.JsonValueKind.Object
                        && data.TryGetProperty("rows", out var rows) && rows.ValueKind == System.Text.Json.JsonValueKind.Array)
                        foreach (var row in rows.EnumerateArray())
                            if (row.ValueKind == System.Text.Json.JsonValueKind.Object && (row.TryGetProperty("ref", out var r) || row.TryGetProperty("id", out r)))
                            {
                                if (r.ValueKind == System.Text.Json.JsonValueKind.Object && r.TryGetProperty("id", out var inner)) r = inner;
                                if (r.ValueKind == System.Text.Json.JsonValueKind.Number && r.TryGetInt64(out var id)) set.Add(set.Ids, MarkState.Selected, id);
                            }
                }
            return set;
        }

        private void Add(Dictionary<MarkState, HashSet<long>> into, MarkState s, long id)
        {
            if (!into.TryGetValue(s, out var ids)) into[s] = ids = new HashSet<long>();
            ids.Add(id);
        }

        public int Count(MarkState s) => (Ids.TryGetValue(s, out var a) ? a.Count : 0) + (TypeIds.TryGetValue(s, out var b) ? b.Count : 0);
    }
}
