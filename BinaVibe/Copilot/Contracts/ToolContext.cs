#nullable enable
// Copilot tool-host contracts, Revit-bound part (plan §4 Task 0.2; owned by W0).
// The Revit-free rest of the interface block is in ToolContracts.cs.
//
// Signatures are normative for W1-W6: fill bodies elsewhere, never change these.

using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BinaVibe.Copilot.Contracts
{
    /// <summary>Everything a tool call needs; created per call on Revit's thread.</summary>
    public sealed class ToolContext
    {
        public UIApplication App = null!;
        public Document Doc = null!;
        public UIDocument UiDoc = null!;
        public string TurnId = "";
        public string CallId = "";
        public string IdempotencyKey = "";
        public IHandleStore Handles = null!;
        public SnapshotModel TurnSnapshot = new();
    }

    public interface ICopilotTool
    {
        /// <summary>One of <see cref="ToolNames.AddIn"/>.</summary>
        string Name { get; }
        ToolKind Kind { get; }
        Envelope Run(ToolContext ctx, JsonElement args);
    }

    /// <summary>Write and Code tools. The batch runner (W3) owns transactions.</summary>
    public interface IWriteTool : ICopilotTool
    {
        /// <summary>Resolve targets BEFORE any change.</summary>
        WritePlan Plan(ToolContext ctx, JsonElement args);
        Envelope Execute(ToolContext ctx, JsonElement args, WritePlan plan, WritePhase phase);
    }
}
