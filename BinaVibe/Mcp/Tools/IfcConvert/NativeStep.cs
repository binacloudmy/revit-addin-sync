// BinaVibe/Mcp/Tools/IfcConvert/NativeStep.cs
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools.IfcConvert
{
    /// <summary>One step in an execute_revit_batch plan: a tool name + its args dict.</summary>
    public sealed record NativeStep(string Tool, Dictionary<string, object?> Args)
    {
        public Dictionary<string, object?> ToStep() => new() { ["tool"] = Tool, ["args"] = Args };

        /// <summary>Wrap steps into the { steps: [...] } shape BatchExecutor.Run expects.</summary>
        public static object BatchArgs(IEnumerable<NativeStep> steps) =>
            new Dictionary<string, object?> { ["steps"] = steps.Select(s => s.ToStep()).ToList() };
    }
}
