using System.Collections.Generic;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>One curated Library prompt. Tapping a row drops <see cref="Prompt"/>
    /// into the Chat composer (does not auto-send). Mirrors the design file's
    /// libraryData entries (docs/design/copilot-panel-slate.dc.html). Kept as pure
    /// data — like the slash <see cref="SlashTool"/> catalogue — so the list can
    /// later be served from the backend instead of hard-coded here.</summary>
    public sealed class LibraryPrompt
    {
        public string Title;
        public string Desc;
        public string IconKey;   // ti-* (resolved via ToolCatalog.Icon)
        public string Prompt;    // natural-language text inserted into the composer
    }

    /// <summary>One titled section of the Library (uppercase muted header + rows).</summary>
    public sealed class LibrarySection
    {
        public string Section;
        public IReadOnlyList<LibraryPrompt> Items;
    }

    /// <summary>Curated Library catalogue — verbatim from the design file's
    /// libraryData. Grouped by section in display order. Pure data (no Revit /
    /// WPF dependency) so it can be unit-tested and, later, replaced by a
    /// backend-configured list.</summary>
    public static class CopilotPromptLibrary
    {
        public static readonly IReadOnlyList<LibrarySection> Sections = new List<LibrarySection>
        {
            new LibrarySection
            {
                Section = "Modeling",
                Items = new List<LibraryPrompt>
                {
                    new LibraryPrompt { Title = "Create exterior walls", Desc = "Generate walls along a grid range", IconKey = "ti-wall",
                        Prompt = "Create exterior walls along grid A–F on Level 2 using a 200mm generic wall type." },
                    new LibraryPrompt { Title = "Place windows on facade", Desc = "Even spacing along a building face", IconKey = "ti-window",
                        Prompt = "Place windows on the south facade at 2400mm spacing using a Fixed 1200x1500 family." },
                    new LibraryPrompt { Title = "Generate sloped floor", Desc = "Convert a flat floor into a ramp", IconKey = "ti-stairs",
                        Prompt = "Convert the selected flat floor into a sloped floor with a 5% fall to the north." },
                },
            },
            new LibrarySection
            {
                Section = "Documentation",
                Items = new List<LibraryPrompt>
                {
                    new LibraryPrompt { Title = "Door schedule", Desc = "Auto-build a graphical door schedule", IconKey = "ti-door",
                        Prompt = "Generate a graphical door schedule for Block A, sorted by level, with Mark, Level and Width fields." },
                    new LibraryPrompt { Title = "Room views (plan + 3D)", Desc = "Batch-create views per room", IconKey = "ti-box-multiple",
                        Prompt = "Create a plan and 3D view for every room on Level 1 using the standard room view template." },
                    new LibraryPrompt { Title = "Tag all rooms", Desc = "Name + number tags in one pass", IconKey = "ti-tag",
                        Prompt = "Tag all rooms on Level 1 with name and number, placed horizontally." },
                },
            },
            new LibrarySection
            {
                Section = "QA & Coordination",
                Items = new List<LibraryPrompt>
                {
                    new LibraryPrompt { Title = "Compare levels vs. linked model", Desc = "Export mismatches to .csv", IconKey = "ti-git-compare",
                        Prompt = "Compare levels between the host and linked structural model and export any mismatches to a .csv." },
                    new LibraryPrompt { Title = "Batch link / reload models", Desc = "Reload or relink in bulk", IconKey = "ti-link",
                        Prompt = "Reload all linked consultant models and report any that failed to load." },
                },
            },
        };
    }
}
