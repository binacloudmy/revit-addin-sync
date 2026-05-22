using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>
    /// Maps a snippet's real structured return (JSON) to a ResultModel — no mock data.
    /// Honors an explicit envelope (an object with a "kind" field) for exact cards, and
    /// otherwise infers the shape from a projection (array of {label,count} → breakdown,
    /// array of {id,sub} → issues, array of {from,to} → diff list, a number → count).
    /// </summary>
    public static class CopilotResultMapper
    {
        private static readonly string[] BarColors = { "#1d4ed8", "#7c3aed", "#0891b2", "#16a34a", "#a16207", "#b91c1c" };

        public static ResultModel Map(string json, ToolDef tool, string message)
        {
            if (string.IsNullOrWhiteSpace(json)) return Plain(message);

            JToken tok;
            try { tok = JToken.Parse(json); }
            catch { return Plain(message); }

            if (tok is JObject o && o["kind"] != null) return FromEnvelope(o, message);
            if (tok.Type == JTokenType.Integer || tok.Type == JTokenType.Float)
                return new ResultModel { Kind = CpResultKind.Count, Headline = tok.ToString(), Unit = "" };
            if (tok is JArray arr) return FromArray(arr, message);
            if (tok is JObject obj) return FromObject(obj, message);
            return Plain(message);
        }

        private static ResultModel FromEnvelope(JObject o, string message)
        {
            var kind = (o["kind"]?.ToString() ?? "plain").ToLowerInvariant();
            var r = new ResultModel
            {
                Headline = o["headline"]?.ToString(),
                Unit = o["unit"]?.ToString(),
                Sub = o["sub"]?.ToString(),
                Path = o["path"]?.ToString(),
            };
            switch (kind)
            {
                case "count": r.Kind = CpResultKind.Count; break;
                case "issues": r.Kind = CpResultKind.Issues; break;
                case "list": r.Kind = CpResultKind.List; break;
                case "file": r.Kind = CpResultKind.File; break;
                default: r.Kind = CpResultKind.Plain; break;
            }
            if (string.IsNullOrEmpty(r.Headline)) r.Headline = message ?? "Done";
            r.GroupedSkipped = Int(o, "grouped");

            if (o["bars"] is JArray bars)
            {
                int ci = 0;
                foreach (var b in bars.OfType<JObject>())
                    r.Bars.Add(new BarItem(
                        Str(b, "label", "name", "level") ?? "",
                        Int(b, "value", "count"),
                        b["color"]?.ToString() ?? BarColors[ci++ % BarColors.Length]));
            }
            if (o["items"] is JArray items)
                foreach (var it in items.OfType<JObject>())
                    r.Items.Add(new IssueItem(Str(it, "id", "name", "mark") ?? "", Str(it, "sub", "description", "detail") ?? ""));
            if (o["diffs"] is JArray diffs)
                foreach (var d in diffs.OfType<JObject>())
                    r.Diffs.Add(new DiffItem(Str(d, "from", "old", "before") ?? "", Str(d, "to", "new", "after") ?? ""));
            return r;
        }

        private static ResultModel FromArray(JArray arr, string message)
        {
            var objs = arr.OfType<JObject>().ToList();
            if (arr.Count == 0) return Plain(message ?? "No results.");

            if (objs.Count == arr.Count && objs.Count > 0)
            {
                var props = objs[0].Properties().Select(p => p.Name).ToList();

                if (HasAny(props, "from", "old", "before") && HasAny(props, "to", "new", "after"))
                {
                    var r = new ResultModel { Kind = CpResultKind.List, Headline = $"{objs.Count} item{(objs.Count == 1 ? "" : "s")}" };
                    foreach (var d in objs)
                        r.Diffs.Add(new DiffItem(Str(d, "from", "old", "before") ?? "", Str(d, "to", "new", "after") ?? ""));
                    return r;
                }

                string numProp = props.FirstOrDefault(p => objs.All(x => IsNum(x[p])));
                string strProp = props.FirstOrDefault(p => objs.Any(x => x[p]?.Type == JTokenType.String));
                if (numProp != null && strProp != null && numProp != strProp)
                {
                    var r = new ResultModel { Kind = CpResultKind.Count, Unit = "" };
                    int total = 0, ci = 0;
                    foreach (var x in objs)
                    {
                        int v = ToInt(x[numProp]);
                        total += v;
                        r.Bars.Add(new BarItem(x[strProp]?.ToString() ?? "", v, BarColors[ci++ % BarColors.Length]));
                    }
                    r.Headline = total.ToString();
                    r.Sub = $"across {objs.Count} group{(objs.Count == 1 ? "" : "s")}";
                    return r;
                }

                // id-ish + a description → issues
                string idProp = props.FirstOrDefault(p => Match(p, "id", "name", "mark", "element", "tag")) ?? strProp ?? props.FirstOrDefault();
                var iss = new ResultModel { Kind = CpResultKind.Issues, Headline = objs.Count.ToString(), Unit = "results" };
                foreach (var x in objs)
                {
                    string id = idProp != null ? x[idProp]?.ToString() : x.ToString();
                    string sub = string.Join(" · ", x.Properties().Where(p => p.Name != idProp).Select(p => p.Value?.ToString()).Where(s => !string.IsNullOrEmpty(s)));
                    iss.Items.Add(new IssueItem(id ?? "", sub));
                }
                return iss;
            }

            // Array of scalars → list of flagged items.
            var scalar = new ResultModel { Kind = CpResultKind.Issues, Headline = arr.Count.ToString(), Unit = "results" };
            foreach (var t in arr.Take(50))
                scalar.Items.Add(new IssueItem(t.ToString(), ""));
            return scalar;
        }

        private static ResultModel FromObject(JObject obj, string message)
        {
            var numProp = obj.Properties().FirstOrDefault(p => IsNum(p.Value));
            if (numProp != null && obj.Properties().Count() <= 2)
                return new ResultModel { Kind = CpResultKind.Count, Headline = numProp.Value.ToString(), Unit = numProp.Name };

            var lines = string.Join(" · ", obj.Properties().Select(p => $"{p.Name}: {p.Value}"));
            return new ResultModel { Kind = CpResultKind.Plain, Headline = message ?? "Done", Sub = lines };
        }

        private static ResultModel Plain(string message)
            => new ResultModel { Kind = CpResultKind.Plain, Headline = string.IsNullOrEmpty(message) ? "Done" : message };

        // ─── helpers ─────────────────────────────────────────────────────────
        private static bool IsNum(JToken t) => t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float);
        private static int ToInt(JToken t) { try { return t == null ? 0 : (int)Math.Round(t.Value<double>()); } catch { return 0; } }
        private static bool Match(string name, params string[] keys) => keys.Any(k => string.Equals(name, k, StringComparison.OrdinalIgnoreCase));
        private static bool HasAny(IEnumerable<string> props, params string[] keys) => props.Any(p => Match(p, keys));

        private static string Str(JObject o, params string[] keys)
        {
            foreach (var k in keys)
            {
                var prop = o.Properties().FirstOrDefault(p => string.Equals(p.Name, k, StringComparison.OrdinalIgnoreCase));
                if (prop != null && prop.Value != null && prop.Value.Type != JTokenType.Null) return prop.Value.ToString();
            }
            return null;
        }

        private static int Int(JObject o, params string[] keys)
        {
            foreach (var k in keys)
            {
                var prop = o.Properties().FirstOrDefault(p => string.Equals(p.Name, k, StringComparison.OrdinalIgnoreCase));
                if (prop != null && IsNum(prop.Value)) return ToInt(prop.Value);
            }
            return 0;
        }
    }
}
