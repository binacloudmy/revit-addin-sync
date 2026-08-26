using System;
using System.Collections.Generic;

namespace RevitWebAppSync.Models
{
    /// <summary>
    /// State a rule can be in. The audit decisions map each rule id to one of these.
    /// </summary>
    public enum CellDecision
    {
        Open = 0,
        Comply,     // manual rule signed off
        NotComply,  // manual rule signed off as not complying
        Defer,      // manual rule deferred
        Ignored,    // cleared from the working list (stays "not comply" in the Borang)
        Resolved    // auto-fix applied (ai rule fixed)
    }

    public sealed class JkrCopilotRule
    {
        public string Id { get; set; }
        public string Sec { get; set; }        // section id (A..E)
        public string Item { get; set; }       // group key (A1, A5, B2, ...)
        public string Cat { get; set; }        // element category (Levels, Walls, ...)
        public string Title { get; set; }
        public string Sev { get; set; }        // High / Med / Low
        public string Kind { get; set; }       // "ai" | "manual"
        public int Cells { get; set; }         // failing cells for this rule
        public int Rows { get; set; }          // Borang rows this rule touches
        public bool Crit { get; set; }         // critical finding (may block sign-off)
        public string Checked { get; set; }    // check description
        public string Req { get; set; }        // requirement
        public string Act { get; set; }        // actual
        public string From { get; set; }       // fix source (null => not fixable)
        public string To { get; set; }         // fix target
        public string Reason { get; set; }     // manual-only: why AI is not allowed to judge
        public string Cite { get; set; }       // citation
    }

    public sealed class JkrCopilotSection
    {
        public string Id { get; set; }         // A..E
        public string Name { get; set; }       // "Penamaan & susunan"
        public string Short { get; set; }      // "nam"
        public int AiCells { get; set; }       // section total cell budget (1480/1120/940/720/352)
    }

    public sealed class JkrCopilotLod
    {
        public int Level { get; set; }         // 100..500
        public string Title { get; set; }      // "Massing only", ...
        public string Desc { get; set; }
        public int Checks { get; set; }        // number of checks at this LOD
    }

    public sealed class JkrCopilotPhase
    {
        public string Value { get; set; }      // "Rekabentuk"
        public string En { get; set; }         // "Design"
        public string Note { get; set; }
        public int Lod { get; set; }           // 300 / 400 / 500
    }

    public sealed class JkrCopilotProject
    {
        public string ProjectName { get; set; }
        public string Model { get; set; }
        public string File { get; set; }
        public string Date { get; set; }
    }

    /// <summary>Everything the copilot needs to render S1..S6 for one run.</summary>
    public sealed class JkrCopilotRunData
    {
        public List<JkrCopilotRule> Rules { get; set; } = new List<JkrCopilotRule>();
        public List<JkrCopilotSection> Sections { get; set; } = new List<JkrCopilotSection>();
        public List<JkrCopilotPhase> Phases { get; set; } = new List<JkrCopilotPhase>();
        public IReadOnlyDictionary<int, JkrCopilotLod> Lods { get; set; } =
            new Dictionary<int, JkrCopilotLod>();
        public IReadOnlyDictionary<string, string> RowNames { get; set; } =
            new Dictionary<string, string>();
        public int TotalAi { get; set; }
        public JkrCopilotProject Project { get; set; } = new JkrCopilotProject();
    }

    /// <summary>Parameters captured on the S1 start screen that drive the run.</summary>
    public sealed class PanelRunRequest
    {
        public int? LodLevel { get; set; } = null;
        public string Discipline { get; set; } = "AR";
        public string ReportLanguage { get; set; } = "BM";
        public bool IsDensityComfortable { get; set; }
    }

    public sealed class ScoreSummary
    {
        public int Pct { get; set; }
        public int Verified { get; set; }
        public int Failed { get; set; }
        public int Manual { get; set; }
        public int TotalAi { get; set; }
    }

    public sealed class SectionScore
    {
        public string Id { get; set; }
        public string Short { get; set; }
        public string Name { get; set; }
        public int Pct { get; set; }
        public string Color { get; set; }
        public string ColorB { get; set; }
        public int OpenCells { get; set; }
        public string OpenCellsLabel { get; set; }
    }

    /// <summary>One section's row in the S2 scanning list (per-section indeterminate state).</summary>
    public sealed class RunSectionProgress
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Stat { get; set; }     // "done" | "scanning" | "queued"
        public int Pct { get; set; }         // populated once done
    }

    /// <summary>Walk model state for the S2 screen (steps 0..4 over the five sections).</summary>
    public sealed class RunProgress
    {
        public int ActiveStep { get; set; }
        public IReadOnlyList<RunSectionProgress> Sections { get; set; } =
            Array.Empty<RunSectionProgress>();
        public string ManualCellsLabel { get; set; } = "";
    }

    public sealed class RuleGroup
    {
        public string Item { get; set; }
        public string Name { get; set; }
        public string Sec { get; set; }
        public string SecName { get; set; }
        public int Cells { get; set; }
        public int Rows { get; set; }
        public bool Crit { get; set; }       // any critical rule in the group
        public bool IsOpen { get; set; } = true;
        public List<JkrCopilotRule> Rules { get; set; } = new List<JkrCopilotRule>();
    }

    /// <summary>A pending destructive action — surfaced as a confirm sheet to the user.</summary>
    public sealed class ConfirmRequest
    {
        public string Kind { get; set; }     // "one" | "all" | "top" | "ignoreAll"
        public string RuleId { get; set; }   // set for Kind == "one"
        public string Title { get; set; }
        public string Body { get; set; }
        public string Note { get; set; }
        public string Cta { get; set; }
    }
}