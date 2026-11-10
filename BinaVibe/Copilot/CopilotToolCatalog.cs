#nullable enable
// The 7 tools the add-in runs (plan §4 Task 0.2; owned by W0). lookup runs in
// the cloud and ask_user in the pane, so they are not here. Nothing outside
// this list may run from a protocol-3 tool_request (CopilotToolHost enforces).

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Tools;

namespace BinaVibe.Copilot
{
    public static class CopilotToolCatalog
    {
        /// <summary>One instance per tool, in <see cref="ToolNames.AddIn"/> order.</summary>
        public static IReadOnlyList<ICopilotTool> All { get; } = new ICopilotTool[]
        {
            new ModelOverviewTool(),
            new FindElementsTool(),
            new InspectTool(),
            new CheckTool(),
            new SetParametersTool(),
            new LookTool(),
            new RunRevitCodeTool(),
        };

        /// <summary>The catalogued tool with this exact name, or null.</summary>
        public static ICopilotTool? Find(string? name) =>
            name == null ? null : All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
    }
}
