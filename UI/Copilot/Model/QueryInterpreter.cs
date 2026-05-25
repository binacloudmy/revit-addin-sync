using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>Result of interpreting a chat query — either a direct tool, or a clarification.</summary>
    public class InterpretResult
    {
        public bool IsClarify;
        public string ToolId;                  // direct
        public string Question;                // clarify
        public List<ClarifyOption> Options = new List<ClarifyOption>();  // clarify
    }

    /// <summary>
    /// Deterministic offline interpreter — a port of chat.jsx interpretQuery / pickResponseTool /
    /// CLARIFICATIONS. Used as the fast-path / fallback when the backend /route is unreachable.
    /// </summary>
    public static class QueryInterpreter
    {
        private static readonly string[] Verbs =
        {
            "count","rename","find","show","list","tag","set","place","export","import",
            "purge","create","make","check","fix","select","open","color","hide","delete",
        };

        // vague noun (raw) → clarification topic key
        private static readonly Dictionary<string, string> VagueNouns = new Dictionary<string, string>
        {
            ["fire"] = "fire-rating", ["frr"] = "fire-rating", ["fire rating"] = "fire-rating",
            ["door"] = "doors", ["doors"] = "doors",
            ["wall"] = "walls", ["walls"] = "walls",
            ["room"] = "rooms", ["rooms"] = "rooms",
            ["schedule"] = "schedules", ["schedules"] = "schedules",
            ["level"] = "levels", ["levels"] = "levels",
            ["family"] = "families", ["families"] = "families",
            ["sheet"] = "sheets", ["sheets"] = "sheets",
        };

        public static InterpretResult Interpret(string text)
        {
            var raw = (text ?? "").ToLowerInvariant().Trim();
            var words = raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            string matched = VagueNouns.Keys.FirstOrDefault(n => raw == n || raw.EndsWith(" " + n) || raw == n + "s");
            bool hasVerb = words.Any(w => Verbs.Contains(w));

            if (matched != null && !hasVerb)
            {
                var topic = VagueNouns[matched];
                if (Clarifications.TryGetValue(topic, out var clar))
                    return new InterpretResult { IsClarify = true, Question = clar.Question, Options = clar.Options };
            }

            return new InterpretResult { IsClarify = false, ToolId = PickResponseTool(text).Id };
        }

        private static readonly string[] Categories = { "Walls", "Doors", "Windows", "Floors", "Rooms", "Furniture" };

        private static string MatchCategory(string q)
        {
            foreach (var c in Categories)
            {
                var lc = c.ToLowerInvariant();
                if (q.Contains(lc) || q.Contains(lc.TrimEnd('s'))) return c;  // wall(s), door(s)…
            }
            return null;
        }

        // ─── PRD V2 chat-path router ────────────────────────────────────────────
        // QueryInterpreter.Decide is the SOLE router under PRD revit_copilot_v2: a discriminated
        // RouteResult tells the caller whether to synthesize a vetted action locally or to call
        // bina-ai for codegen. The 5 vetted patterns mirror the regexes in the PRD §4.1; param
        // extractors are deliberately conservative — if a required param can't be pulled
        // confidently from the prompt we fall through to NeedsAI rather than guess.

        private static readonly Regex OpenViewRe = new Regex(
            @"\b(open|show|switch\s+to|go\s+to|display)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex OpenViewCueRe = new Regex(
            @"\b(view|3d|three[-\s]?d|plan|section|elevation|sheet)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ExportScheduleRe = new Regex(
            @"\b(export|save|download)\b.*\b(schedule|table)\b" +
            @"|\bschedule\b.*\b(to|as)\b.*\b(excel|csv|xlsx|file)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex RenameRe = new Regex(
            @"\b(rename|change\s+name|replace)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // "rename <cat> from <find> to <replace>" — the only shape we'll synthesize unattended.
        private static readonly Regex RenameExtractRe = new Regex(
            @"\brename\s+(?<cat>\w+)\s+from\s+(?<find>\S+)\s+to\s+(?<replace>\S+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // "set <param> to <value> on <cat>"  /  "set <cat> <param> to <value>"
        private static readonly Regex SetParamReA = new Regex(
            @"\bset\s+(?<param>.+?)\s+to\s+(?<value>\S+)\s+(?:on|for)\s+(?<cat>\w+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SetParamReB = new Regex(
            @"\bset\s+(?<cat>\w+)\s+(?<param>.+?)\s+to\s+(?<value>\S+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex SelectRe = new Regex(
            @"\b(select|highlight|pick|choose)\b.*\b(all|every)?\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Decide whether a prompt is handled locally (vetted recipe) or punted to AI codegen.
        /// Returns a RouteResult with Kind set to VettedTool or NeedsAI; the caller is
        /// responsible for synthesizing C# from the bound params (Revit assemblies stay out
        /// of this class).
        /// </summary>
        public static RouteResult Decide(string text, string fallbackToolId = null)
        {
            var q = (text ?? "").Trim();
            if (q.Length == 0) return RouteResult.NeedsAI(text, fallbackToolId);

            var lower = q.ToLowerInvariant();

            // 1. open_view ────────────────────────────────────────────────────
            if (OpenViewRe.IsMatch(lower) && OpenViewCueRe.IsMatch(lower))
            {
                var p = new Dictionary<string, object>();
                string viewType = ExtractViewType(lower);
                if (viewType != null) p["view_type"] = viewType;
                string viewName = ExtractViewName(q);
                if (!string.IsNullOrEmpty(viewName)) p["view_name"] = viewName;
                string intent = "Open " + (viewName ?? viewType ?? "view");
                return RouteResult.VettedTool("open_view", p, code: null, intent: intent, toolId: "open-view");
            }

            // 2. rename_elements ─────────────────────────────────────────────
            // Only synthesize when we can pull category+find+replace; otherwise punt to AI.
            var rn = RenameExtractRe.Match(q);
            if (rn.Success)
            {
                var cat = NormalizeCategory(rn.Groups["cat"].Value);
                if (cat != null)
                {
                    var p = new Dictionary<string, object>
                    {
                        ["target_category"] = cat,
                        ["find"] = rn.Groups["find"].Value,
                        ["replace"] = rn.Groups["replace"].Value,
                    };
                    return RouteResult.VettedTool("rename_elements", p, code: null,
                        intent: $"Rename {cat} ({rn.Groups["find"].Value} → {rn.Groups["replace"].Value})",
                        toolId: "rename");
                }
            }
            // 3. set_parameter ────────────────────────────────────────────────
            var sp = SetParamReA.Match(q);
            if (!sp.Success) sp = SetParamReB.Match(q);
            if (sp.Success)
            {
                var cat = NormalizeCategory(sp.Groups["cat"].Value);
                if (cat != null)
                {
                    var p = new Dictionary<string, object>
                    {
                        ["target_category"] = cat,
                        ["parameter_name"] = sp.Groups["param"].Value.Trim(),
                        ["value"] = sp.Groups["value"].Value.Trim(),
                    };
                    return RouteResult.VettedTool("set_parameter", p, code: null,
                        intent: $"Set {sp.Groups["param"].Value.Trim()} on {cat}",
                        toolId: "set-param");
                }
            }

            // 4. export_schedule ──────────────────────────────────────────────
            if (ExportScheduleRe.IsMatch(lower))
            {
                var p = new Dictionary<string, object>();
                string fmt = lower.Contains("csv") ? "csv" : "xlsx";
                p["format"] = fmt;
                string name = ExtractScheduleName(q);
                if (!string.IsNullOrEmpty(name)) p["schedule_name"] = name;
                return RouteResult.VettedTool("export_schedule", p, code: null,
                    intent: name != null ? $"Export {name}" : "Export schedule",
                    toolId: "export-sched");
            }

            // 5. select_elements ──────────────────────────────────────────────
            if (SelectRe.IsMatch(lower))
            {
                var cat = MatchCategory(lower);
                if (cat != null)
                {
                    var p = new Dictionary<string, object> { ["target_category"] = cat };
                    return RouteResult.VettedTool("select_elements", p, code: null,
                        intent: $"Select {cat}", toolId: "select");
                }
            }

            // No vetted match — punt to bina-ai codegen.
            return RouteResult.NeedsAI(text, fallbackToolId);
        }

        private static string ExtractViewType(string lower)
        {
            if (Regex.IsMatch(lower, @"\b(3d|three[-\s]?d)\b")) return "3D";
            if (lower.Contains("section")) return "Section";
            if (lower.Contains("elevation")) return "Elevation";
            if (lower.Contains("drafting")) return "Drafting";
            if (lower.Contains("floor plan") || lower.Contains("floor") || lower.Contains("plan"))
                return "Floor Plan";
            if (lower.Contains("sheet")) return "Sheet";
            return null;
        }

        // Pull a quoted name, or the tail after the last view-type/cue word.
        private static readonly Regex QuotedNameRe = new Regex(
            "[\"'“](?<name>[^\"'”]+)[\"'”]", RegexOptions.Compiled);
        private static readonly Regex AfterCueRe = new Regex(
            @"\b(view|3d|plan|section|elevation|sheet|schedule)\s+(?<name>[A-Za-z0-9 _\-\.]+?)\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static string ExtractViewName(string original)
        {
            var qm = QuotedNameRe.Match(original);
            if (qm.Success) return qm.Groups["name"].Value.Trim();
            var am = AfterCueRe.Match(original);
            if (am.Success)
            {
                var name = am.Groups["name"].Value.Trim();
                if (!string.IsNullOrEmpty(name)
                    && !name.Equals("view", StringComparison.OrdinalIgnoreCase))
                    return name;
            }
            return null;
        }

        private static readonly Regex ScheduleNameRe = new Regex(
            @"\b(export|save|download)\s+(?<name>[A-Za-z0-9 _\-]+?)\s+(schedule|table)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static string ExtractScheduleName(string original)
        {
            var qm = QuotedNameRe.Match(original);
            if (qm.Success) return qm.Groups["name"].Value.Trim();
            var sn = ScheduleNameRe.Match(original);
            if (sn.Success)
            {
                var name = sn.Groups["name"].Value.Trim();
                // Capitalize first letter — addin matches case-insensitive anyway.
                if (!string.IsNullOrEmpty(name))
                    return char.ToUpperInvariant(name[0]) + name.Substring(1) + " Schedule";
            }
            return null;
        }

        // Map "wall" / "walls" → canonical Revit category name.
        private static string NormalizeCategory(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var w = raw.Trim().ToLowerInvariant();
            foreach (var c in Categories)
            {
                var lc = c.ToLowerInvariant();
                if (w == lc || w == lc.TrimEnd('s') || w + "s" == lc) return c;
            }
            return null;
        }

        public static ToolDef PickResponseTool(string query)
        {
            var q = (query ?? "").ToLowerInvariant();
            if (q.Contains("rename") || q.Contains("prefix")) return CopilotCatalog.Find("rename-level-prefix");
            if (q.Contains("fire rating") || q.Contains("frr"))
                return (q.Contains("set") || q.Contains("apply")) ? CopilotCatalog.Find("set-frr-corridor") : CopilotCatalog.Find("walls-missing-frr");
            if (q.Contains("count") || q.Contains("how many") || q.Contains("total")) return CopilotCatalog.Find("count-doors");
            if (q.Contains("tag") || q.Contains("annotate")) return CopilotCatalog.Find("tag-walls");
            if (q.Contains("schedule") || q.Contains("export")) return CopilotCatalog.Find("export-sched");
            if (q.Contains("purge") || q.Contains("unused") || q.Contains("clean")) return CopilotCatalog.Find("purge-unused");
            if (q.Contains("sheet")) return CopilotCatalog.Find("place-views-sheet");
            if (q.Contains("excel") || q.Contains("import")) return CopilotCatalog.Find("import-doors-excel");
            if (q.Contains("ubbl") || q.Contains("compliance") || q.Contains("room")) return CopilotCatalog.Find("ubbl-rooms");
            if (q.Contains("corridor") || q.Contains("set")) return CopilotCatalog.Find("set-frr-corridor");
            return CopilotCatalog.Find("count-doors");
        }

        private class Clar { public string Question; public List<ClarifyOption> Options; }

        private static ClarifyOption O(string toolId, string label, string prompt, string hint)
            => new ClarifyOption { ToolId = toolId, Label = label, Prompt = prompt, Hint = hint };

        private static readonly Dictionary<string, Clar> Clarifications = new Dictionary<string, Clar>
        {
            ["fire-rating"] = new Clar
            {
                Question = "A few things I can do about fire ratings — which one?",
                Options = new List<ClarifyOption>
                {
                    O("walls-missing-frr", "Find walls missing fire rating", "Find walls missing fire rating", "QA scan — read-only"),
                    O("set-frr-corridor", "Set FRR-60 on corridor doors", "Set FRR-60 on corridor doors", "Modifies the model"),
                    O("ubbl-rooms", "Check UBBL room minimums", "Check UBBL room minimums", "Compliance scan"),
                },
            },
            ["doors"] = new Clar
            {
                Question = "Got it — what should I do with the doors?",
                Options = new List<ClarifyOption>
                {
                    O("count-doors", "Count doors by level", "count doors by level", "Read-only · grouped breakdown"),
                    O("export-sched", "Export a door schedule to Excel", "Export door schedule to Excel", "Vetted tool · one click"),
                    O("set-frr-corridor", "Set fire rating on corridor doors", "Set FRR-60 on corridor doors", "Modifies the model"),
                    O("select", "Select all doors in the model", "select all doors", "Vetted tool · zoom to selection"),
                },
            },
            ["walls"] = new Clar
            {
                Question = "Walls — pick what you'd like me to do:",
                Options = new List<ClarifyOption>
                {
                    O("walls-missing-frr", "Find walls missing fire rating", "Find walls missing fire rating", "QA scan"),
                    O("tag-walls", "Tag all walls in active view", "Tag all walls in this view", "Adds wall tags"),
                    O("set-param", "Set a parameter on all walls", "set parameter on walls", "Vetted tool · batch modify"),
                    O("select", "Select all walls in the model", "select all walls", "Vetted tool"),
                },
            },
            ["rooms"] = new Clar
            {
                Question = "Rooms — what should I check?",
                Options = new List<ClarifyOption>
                {
                    O("ubbl-rooms", "Check UBBL room minimums", "Check UBBL room minimums", "Malaysian standard compliance"),
                    O("select", "Select all rooms", "select all rooms", "Vetted tool"),
                },
            },
            ["schedules"] = new Clar
            {
                Question = "Schedules — what would you like?",
                Options = new List<ClarifyOption>
                {
                    O("export-sched", "Export an existing schedule", "Export door schedule to Excel", "Vetted tool"),
                    O("place-views-sheet", "Place schedule views on a sheet", "Place 4 views on sheet A101", "Auto-layouts on title block"),
                },
            },
            ["levels"] = new Clar
            {
                Question = "Levels — what would you like to do?",
                Options = new List<ClarifyOption>
                {
                    O("rename-level-prefix", "Rename levels with L-prefix", "Rename levels Level to L", "Level 1 → L1, etc."),
                    O("count-doors", "Count elements per level", "count doors by level", "Read-only · grouped breakdown"),
                    O("open-view", "Open a specific level's floor plan", "Open Level 1 floor plan", "Vetted · jumps to the view"),
                },
            },
            ["families"] = new Clar
            {
                Question = "Families — what should I do?",
                Options = new List<ClarifyOption>
                {
                    O("purge-unused", "Find unused families to purge", "Purge unused families", "Preview first · then confirm"),
                    O("rename", "Bulk rename family types", "rename family types", "Vetted tool · find / replace"),
                },
            },
            ["sheets"] = new Clar
            {
                Question = "Sheets — what would you like?",
                Options = new List<ClarifyOption>
                {
                    O("place-views-sheet", "Place views on a new sheet", "Place 4 views on sheet A101", "Auto-layout 2×2 grid"),
                    O("export-sched", "Export schedule from a sheet", "Export schedule to Excel", "Vetted tool"),
                },
            },
        };
    }
}
