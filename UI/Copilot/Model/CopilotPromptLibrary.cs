using System.Collections.Generic;
using Newtonsoft.Json;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>One curated Library prompt. Tapping a row drops <see cref="Prompt"/>
    /// into the Chat composer (does not auto-send). Wire shape of one item in
    /// GET /agents/revit-ai/library.</summary>
    public sealed class LibraryPrompt
    {
        [JsonProperty("title")] public string Title;
        [JsonProperty("desc")] public string Desc;
        [JsonProperty("icon_key")] public string IconKey;   // ti-* (resolved via ToolCatalog.Icon)
        [JsonProperty("prompt")] public string Prompt;      // template with [placeholders] the user edits
    }

    /// <summary>One titled section of the Library (uppercase muted header + rows).</summary>
    public sealed class LibrarySection
    {
        [JsonProperty("section")] public string Section;
        [JsonProperty("items")] public IReadOnlyList<LibraryPrompt> Items;
    }

    /// <summary>Session cache of the backend-served Library catalogue
    /// (GET /agents/revit-ai/library, Bearer-gated). There is deliberately NO
    /// local fallback list — content is curated centrally (ai.prompt_library,
    /// pushed via seed script) so prompt updates reach every user without an
    /// add-in release. Null until the first successful fetch.</summary>
    public static class CopilotPromptLibrary
    {
        public static IReadOnlyList<LibrarySection> Cached;
    }
}
