// Saved Commands J1 (2026-08-30) — wire DTOs for the /revit-copilot/commands
// catalog + CRUD. Contract: bina-ai docs/superpowers/plans/
// 2026-08-30-saved-commands-j1-backend.md Task 6.
using System.Collections.Generic;
using Newtonsoft.Json;

namespace RevitWebAppSync.Models
{
    public class CatalogArgDto
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("type")] public string Type { get; set; } = "text";
        [JsonProperty("source")] public string Source { get; set; }
        [JsonProperty("required")] public bool Required { get; set; }
        [JsonProperty("label_en")] public string LabelEn { get; set; } = "";
    }

    public class CatalogCommandDto
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("group")] public string Group { get; set; }
        [JsonProperty("engine")] public string Engine { get; set; }
        [JsonProperty("name_en")] public string NameEn { get; set; }
        [JsonProperty("name_ms")] public string NameMs { get; set; }
        [JsonProperty("description_en")] public string DescriptionEn { get; set; } = "";
        [JsonProperty("icon")] public string Icon { get; set; }
        [JsonProperty("keywords")] public List<string> Keywords { get; set; } = new List<string>();
        [JsonProperty("args")] public List<CatalogArgDto> Args { get; set; } = new List<CatalogArgDto>();
        [JsonProperty("tools")] public List<string> Tools { get; set; } = new List<string>();
        [JsonProperty("status")] public string Status { get; set; } = "live";
    }

    public class CatalogResponseDto
    {
        [JsonProperty("version")] public string Version { get; set; }
        [JsonProperty("commands")] public List<CatalogCommandDto> Commands { get; set; } = new List<CatalogCommandDto>();
    }

    public class SaveCommandRequestDto
    {
        [JsonProperty("name_en")] public string NameEn { get; set; }
        [JsonProperty("name_ms")] public string NameMs { get; set; } = "";
        [JsonProperty("prompt_template")] public string PromptTemplate { get; set; }
        [JsonProperty("args")] public List<CatalogArgDto> Args { get; set; } = new List<CatalogArgDto>();
        [JsonProperty("tools_called")] public List<string> ToolsCalled { get; set; } = new List<string>();
        [JsonProperty("source_run_id")] public string SourceRunId { get; set; }
    }

    public class SaveCommandResponseDto
    {
        [JsonProperty("command")] public CatalogCommandDto Command { get; set; }
        [JsonProperty("prompt_template")] public string PromptTemplate { get; set; }
        [JsonProperty("run_count")] public int RunCount { get; set; }
    }
}
