// UI/Copilot/Model/SavedCommandDraft.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RevitWebAppSync.Models;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>Edit model behind SaveCommandSheet. Pure: no WPF, no HTTP.
    /// The template holds {name} holes; Inputs carries one entry per hole with
    /// the ORIGINAL selected text as its label so Unmark can restore it.</summary>
    public sealed class SavedCommandDraft
    {
        public const int MaxInputs = 8;
        private static readonly Regex NameRe = new Regex("^[a-z][a-z0-9_]{0,39}$");
        private static readonly Regex HoleRe = new Regex(@"\{([a-z][a-z0-9_]{0,39})\}");

        public string Name = "";
        public string Template = "";
        public List<SlashInput> Inputs = new List<SlashInput>();
        public List<string> ToolsCalled = new List<string>();
        public string SourceRunId;
        public string EditingId;   // null = creating

        public static SavedCommandDraft FromReply(string userPrompt, IEnumerable<string> toolsCalled, string runId)
        {
            var p = (userPrompt ?? "").Trim();
            return new SavedCommandDraft
            {
                Name = DefaultName(p), Template = p, SourceRunId = runId,
                ToolsCalled = (toolsCalled ?? Enumerable.Empty<string>()).Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList(),
            };
        }

        public static SavedCommandDraft FromTool(SlashTool t) => new SavedCommandDraft
        {
            EditingId = t.Id, Name = t.Name, Template = t.PromptTemplate ?? "",
            Inputs = t.Inputs.Select(i => new SlashInput { Name = i.Name, Type = i.Type, Required = i.Required, Label = i.Label }).ToList(),
        };

        public static string DefaultName(string userPrompt)
        {
            var words = (userPrompt ?? "").Split(new[] { ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", words.Take(6)).TrimEnd(',', '.', ';', ':');
        }

        /// <summary>Client-side twin of the backend kebab slug rule — the sheet's
        /// "Find it later by typing /my-…" helper line.</summary>
        public static string SuggestSlug(string name)
        {
            var k = Regex.Replace((name ?? "").ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            if (k.Length > 40) k = k.Substring(0, 40).TrimEnd('-');
            return "my-" + (k.Length == 0 ? "command" : k);
        }

        public static string SuggestInputName(string selectedText)
        {
            var s = (selectedText ?? "").ToLowerInvariant();
            s = Regex.Replace(s, "[^a-z0-9]+", "_").Trim('_');
            if (s.Length == 0 || !char.IsLetter(s[0])) s = "x" + (s.Length == 0 ? "" : "_" + s);
            return s.Length > 24 ? s.Substring(0, 24).TrimEnd('_') : s;
        }

        /// <summary>A free input name for a selection label ("level", "level_2"…).</summary>
        public string AutoName(string label)
        {
            var b = SuggestInputName(label);
            if (b.Length == 0) b = "input";
            if (Inputs.All(i => i.Name != b)) return b;
            for (int n = 2; n < 40; n++)
                if (Inputs.All(i => i.Name != b + "_" + n)) return b + "_" + n;
            return b + "_x";
        }

        public bool MarkInput(int selStart, int selLength, string name, out string error)
        {
            error = null;
            if (!NameRe.IsMatch(name ?? "")) { error = "Input name must be snake_case (a-z, 0-9, _)."; return false; }
            if (Inputs.Count >= MaxInputs) { error = $"At most {MaxInputs} inputs per command."; return false; }
            if (Inputs.Any(i => i.Name == name)) { error = $"An input named {name} already exists."; return false; }
            if (selStart < 0 || selLength <= 0 || selStart + selLength > Template.Length) { error = "Select some text first."; return false; }
            foreach (Match m in HoleRe.Matches(Template))
                if (selStart < m.Index + m.Length && m.Index < selStart + selLength) { error = "Selection overlaps an existing input."; return false; }
            var label = Template.Substring(selStart, selLength);
            Template = Template.Substring(0, selStart) + "{" + name + "}" + Template.Substring(selStart + selLength);
            Inputs.Add(new SlashInput { Name = name, Type = LooksNumeric(label) ? "number" : "text", Required = true, Label = label });
            return true;
        }

        private static bool LooksNumeric(string s) => Regex.IsMatch((s ?? "").Trim(), @"^[\d.]+$");

        public void UnmarkInput(string name)
        {
            var i = Inputs.FirstOrDefault(x => x.Name == name);
            if (i == null) return;
            Template = Template.Replace("{" + name + "}", i.Label ?? name);
            Inputs.Remove(i);
        }

        public SaveCommandRequestDto ToRequest() => new SaveCommandRequestDto
        {
            NameEn = (Name ?? "").Trim(), PromptTemplate = Template,
            Args = Inputs.Select(i => new CatalogArgDto { Name = i.Name, Type = i.Type, Required = i.Required, LabelEn = i.Label ?? i.Name }).ToList(),
            ToolsCalled = ToolsCalled.ToList(), SourceRunId = SourceRunId,
        };
    }
}
