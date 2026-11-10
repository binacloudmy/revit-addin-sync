// LinkResults — pure verdicts for the link tools (batch_link_models,
// link_file), split out of BatchLink.cs so they are testable without Revit.
// AI-204 (X-07): the tool said "reloaded" without checking what Reload()
// returned. A link now counts as reloaded only when Revit reported LinkLoaded.

#nullable enable

namespace BinaVibe.Mcp.Tools
{
    public static class LinkResults
    {
        /// <summary>Status of an existing link after Reload(): "reloaded" only
        /// when Revit returned LinkLoaded, else "failed" with the result name as
        /// the reason. Takes the LinkLoadResultType name so no Revit type is
        /// needed here.</summary>
        public static (string Status, string? Reason) ReloadStatus(string? loadResult)
        {
            if (loadResult == "LinkLoaded") return ("reloaded", null);
            var why = string.IsNullOrEmpty(loadResult) ? "no result" : loadResult;
            return ("failed", $"reload did not load the link ({why})");
        }

        /// <summary>Overall batch verdict. Nothing succeeded but something failed
        /// → ok:false. Some succeeded and some failed → ok:true, "partial".
        /// Otherwise "completed".</summary>
        public static (bool Ok, string Status) BatchVerdict(int created, int reloaded, int failed)
        {
            if (failed > 0 && created + reloaded == 0) return (false, "failed");
            if (failed > 0) return (true, "partial");
            return (true, "completed");
        }
    }
}
