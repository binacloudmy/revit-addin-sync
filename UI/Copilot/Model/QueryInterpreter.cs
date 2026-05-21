using System;
using System.Collections.Generic;
using System.Linq;

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
