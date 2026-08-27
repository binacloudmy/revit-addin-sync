// AUTO-GENERATED from "JKR Audit Copilot.dc.html" (Component constants). Do not edit by hand.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RevitWebAppSync.Models;

namespace RevitWebAppSync.Services
{
    /// <summary>Offline fixture mirroring the JKR Audit Copilot design constants byte-for-byte.</summary>
    public sealed class FixtureCopilotSource : IJkrCopilotSource
    {
        public Task<JkrCopilotRunData> LoadRunAsync(PanelRunRequest request)
        {
            return Task.FromResult(Build());
        }

        public static IReadOnlyList<JkrCopilotSection> DesignSections { get; } = new List<JkrCopilotSection>
        {
            new JkrCopilotSection { Id="A", Name="Penamaan & susunan", Short="nam", AiCells=1480 },
            new JkrCopilotSection { Id="B", Name="Parameter & maklumat", Short="par", AiCells=1120 },
            new JkrCopilotSection { Id="C", Name="Kualiti & integriti", Short="qua", AiCells=940 },
            new JkrCopilotSection { Id="D", Name="Geometri rekabentuk", Short="geo", AiCells=720 },
            new JkrCopilotSection { Id="E", Name="Dokumentasi", Short="doc", AiCells=352 },
        };

        public static IReadOnlyList<JkrCopilotPhase> DesignPhases { get; } = new List<JkrCopilotPhase>
        {
            new JkrCopilotPhase { Value="Rekabentuk", En="Design", Note="Drawings going out for review, not yet built.", Lod=300 },
            new JkrCopilotPhase { Value="Pembinaan", En="Construction", Note="Issued for construction on site.", Lod=400 },
            new JkrCopilotPhase { Value="Serahan", En="Handover", Note="Final as-built set at handover.", Lod=500 },
        };

        public static IReadOnlyDictionary<int, JkrCopilotLod> DesignLods { get; } = new Dictionary<int, JkrCopilotLod>
        {
            { 100, new JkrCopilotLod { Level=100, Title="Massing only", Desc="Blocks and volumes. Nothing named yet.", Checks=210 } },
            { 200, new JkrCopilotLod { Level=200, Title="Generic elements", Desc="Walls, floors and doors in place, sizes approximate.", Checks=1180 } },
            { 300, new JkrCopilotLod { Level=300, Title="Specific assemblies", Desc="Real types, real sizes, real materials.", Checks=1842 } },
            { 400, new JkrCopilotLod { Level=400, Title="Fabrication detail", Desc="Enough detail to build from directly.", Checks=2260 } },
            { 500, new JkrCopilotLod { Level=500, Title="As-built", Desc="Checked against what was actually constructed.", Checks=2410 } },
        };

        public static IReadOnlyDictionary<string, string> DesignRowNames { get; } = new Dictionary<string, string>
        {
            { "A1", "Penamaan elemen & aras" },
            { "A3", "Pembahagian model" },
            { "A5", "Penamaan grid" },
            { "B2", "Parameter projek wajib" },
            { "B4", "Parameter kebakaran" },
            { "B7", "Catatan rekabentuk" },
            { "C1", "Integriti geometri" },
            { "C4.2", "Blok tajuk helaian" },
            { "C5.1", "Fail pautan disiplin" },
            { "D-a", "Penamaan jenis pintu" },
            { "D-b", "Anjakan bangunan" },
            { "D-c", "Jenis dinding" },
            { "E2", "Penomboran helaian" },
        };

        public static JkrCopilotRunData Build()
        {
            var data = new JkrCopilotRunData
            {
                TotalAi = 4612,
                Sections = new List<JkrCopilotSection>(DesignSections),
                Phases = new List<JkrCopilotPhase>(DesignPhases),
                RowNames = DesignRowNames,
                Lods = DesignLods,
                Project = new JkrCopilotProject
                {
                    ProjectName = "Klinik Kesihatan Tapah",
                    Model = "Architecture \u00b7 AR \u00b7 LOD 300",
                    File = "jkrAR24_5a_(BEde1A_p14-001)_A1",
                    Date = "25.08.2026"
                }
            };
            data.Rules = new List<JkrCopilotRule>();
            foreach (var r in RawRules()) data.Rules.Add(r);
            return data;
        }

        private static IEnumerable<JkrCopilotRule> RawRules()
        {
            // RULES (12 ai + 5 manual) mirrored verbatim from the design doc.
            yield return new JkrCopilotRule
            {
                Id = "r1", Sec = "A", Item = "A1", Cat = "Levels", Title = "Level naming convention",
                Sev = "High", Kind = "ai", Cells = 311, Rows = 1, Crit = false,
                Checked = "Every level name in the model against the JKR level format.", Req = "L## +#.### (e.g. \"L02 +4.500\")", Act = "Aras Tanah",
                From = "Aras Tanah", To = "L01 +0.000",
                Reason = null, Cite = "Doc 05 — MPK BIM Rekabentuk Awalan §5.4.1"
            };
            yield return new JkrCopilotRule
            {
                Id = "r2", Sec = "A", Item = "A1", Cat = "Rooms", Title = "Room name and number format",
                Sev = "Med", Kind = "ai", Cells = 52, Rows = 1, Crit = false,
                Checked = "Room name and number against the AR room schedule format.", Req = "RM-<aras>-<no> · name in BM", Act = "Bilik Mesyuarat 1",
                From = "Bilik Mesyuarat 1", To = "RM-01-002 Bilik Mesyuarat",
                Reason = null, Cite = "Buku Parameter v1.4 · p.31"
            };
            yield return new JkrCopilotRule
            {
                Id = "r3", Sec = "A", Item = "A1", Cat = "Doors", Title = "Door clear width below minimum",
                Sev = "High", Kind = "ai", Cells = 24, Rows = 1, Crit = true,
                Checked = "Single-leaf door clear opening width against the accessibility minimum.", Req = "≥ 900 mm (NS §5.8.5)", Act = "PT2p600a = 850 mm",
                From = null, To = null,
                Reason = null, Cite = "Need Statement §5.8.5 — pintu single"
            };
            yield return new JkrCopilotRule
            {
                Id = "r4", Sec = "A", Item = "A5", Cat = "Grids", Title = "Grid sequence has a gap",
                Sev = "High", Kind = "ai", Cells = 8, Rows = 1, Crit = false,
                Checked = "Grid labels for a continuous sequence with no missing member.", Req = "Unbroken sequence 1→7", Act = "1, 2, 3 → 11 (gap at 4–10)",
                From = null, To = null,
                Reason = null, Cite = "Doc 05 §4.2.2 — penamaan grid"
            };
            yield return new JkrCopilotRule
            {
                Id = "r5", Sec = "D", Item = "D-a", Cat = "Door types", Title = "Generic door type names",
                Sev = "Med", Kind = "ai", Cells = 96, Rows = 2, Crit = false,
                Checked = "Family type names against the JKR AR template type codes.", Req = "<code>p<width><variant> (e.g. PT2p600a)", Act = "\"Door 1\", \"Door 2\", \"Copy of Door 1\"",
                From = "Door 1", To = "PT2p600a",
                Reason = null, Cite = "JKR AR template v5a · type register"
            };
            yield return new JkrCopilotRule
            {
                Id = "r6", Sec = "C", Item = "C5.1", Cat = "Links", Title = "Electrical link file absent",
                Sev = "High", Kind = "ai", Cells = 1, Rows = 1, Crit = true,
                Checked = "Presence of the EL discipline link named in the BEP.", Req = "jkrEL24_5a_(BEde1A_p14-001)_A1", Act = "tiada — no EL link in the model",
                From = null, To = null,
                Reason = null, Cite = "BPEP v2.0 §6.4 — federated model"
            };
            yield return new JkrCopilotRule
            {
                Id = "r7", Sec = "B", Item = "B2", Cat = "Project info", Title = "JKR_KodProjek empty on elements",
                Sev = "Med", Kind = "ai", Cells = 112, Rows = 3, Crit = false,
                Checked = "The mandatory project-code parameter on every modelled element at LOD 300.", Req = "JKR/<disiplin>/<tahun>/<no>", Act = "(empty)",
                From = "(empty)", To = "JKR/AR/2026/0184",
                Reason = null, Cite = "Buku Parameter v1.4 · p.12 — parameter wajib"
            };
            yield return new JkrCopilotRule
            {
                Id = "r8", Sec = "B", Item = "B4", Cat = "Walls", Title = "Fire rating parameter missing",
                Sev = "Med", Kind = "ai", Cells = 38, Rows = 1, Crit = false,
                Checked = "Fire-rating parameter on walls bounding protected escape routes.", Req = "JKR_KadarKebakaran filled (minutes)", Act = "(empty) on 38 wall instances",
                From = null, To = null,
                Reason = null, Cite = "Buku Parameter v1.4 · p.18"
            };
            yield return new JkrCopilotRule
            {
                Id = "r9", Sec = "C", Item = "C4.2", Cat = "Title block", Title = "Sheet title field blank",
                Sev = "Med", Kind = "ai", Cells = 12, Rows = 1, Crit = false,
                Checked = "Title-block fields present and filled on every issued sheet.", Req = "Project title · sheet name · revision", Act = "Project title blank on 12 sheets",
                From = "(blank)", To = "Klinik Kesihatan Tapah",
                Reason = null, Cite = "Doc 05 §6.1.3 — blok tajuk"
            };
            yield return new JkrCopilotRule
            {
                Id = "r10", Sec = "C", Item = "C1", Cat = "Geometry", Title = "Overlapping wall elements",
                Sev = "High", Kind = "ai", Cells = 31, Rows = 1, Crit = false,
                Checked = "Element intersections that would double-count quantities.", Req = "No coincident wall volumes", Act = "31 wall pairs overlap > 5 mm",
                From = null, To = null,
                Reason = null, Cite = "BPEP v2.0 §5.2 — integriti model"
            };
            yield return new JkrCopilotRule
            {
                Id = "r11", Sec = "D", Item = "D-c", Cat = "Wall types", Title = "Wall type outside the AR template",
                Sev = "Med", Kind = "ai", Cells = 28, Rows = 2, Crit = false,
                Checked = "Every wall type against the types published in the JKR AR template.", Req = "Type from JKR AR template v5a", Act = "\"Generic - 200mm\" × 28",
                From = null, To = null,
                Reason = null, Cite = "JKR AR template v5a · wall register"
            };
            yield return new JkrCopilotRule
            {
                Id = "r12", Sec = "E", Item = "E2", Cat = "Sheets", Title = "Sheet numbering format",
                Sev = "Low", Kind = "ai", Cells = 9, Rows = 1, Crit = false,
                Checked = "Sheet numbers against the submission numbering rule.", Req = "AR-<siri>-<no>", Act = "A101, A102, …",
                From = "A101", To = "AR-01-101",
                Reason = null, Cite = "Doc 05 §6.2 — penomboran helaian"
            };
            yield return new JkrCopilotRule
            {
                Id = "m1", Sec = "A", Item = "A3", Cat = "Model split", Title = "Reason for the model split",
                Sev = "Med", Kind = "manual", Cells = 3, Rows = 1, Crit = false,
                Checked = "Whether the split into WX01 / WX02 / WX03 follows the agreed strategy.", Req = "Split rationale recorded in the BEP", Act = "WX01, WX02, WX03",
                From = null, To = null,
                Reason = "The split is in place, but whether it matches the strategy agreed with JKR is a judgement about intent, not a fact in the model. The modeller confirms it.", Cite = "BPEP v2.0 §4.1 — strategi pembahagian"
            };
            yield return new JkrCopilotRule
            {
                Id = "m2", Sec = "C", Item = "C5.1", Cat = "Links", Title = "Linked file is the latest issue",
                Sev = "High", Kind = "manual", Cells = 6, Rows = 1, Crit = false,
                Checked = "Whether each linked discipline model is the current issued version.", Req = "Latest issued revision at time of submission", Act = "no revision stamp on 6 links",
                From = null, To = null,
                Reason = "The links carry no revision stamp, so there is nothing in the model to compare against. Confirming currency needs the issue register.", Cite = "BPEP v2.0 §6.4"
            };
            yield return new JkrCopilotRule
            {
                Id = "m3", Sec = "C", Item = "C4.2", Cat = "Title block", Title = "Hardcopy timestamp parity",
                Sev = "Low", Kind = "manual", Cells = 9, Rows = 1, Crit = false,
                Checked = "Whether the printed set carries the same date as the model issue.", Req = "Hardcopy date = model issue date", Act = "—",
                From = null, To = null,
                Reason = "Requires comparing against a physical printed set. Nothing in the model can settle it.", Cite = "Doc 05 §6.1.5"
            };
            yield return new JkrCopilotRule
            {
                Id = "m4", Sec = "B", Item = "B7", Cat = "Design notes", Title = "Design note free text",
                Sev = "Med", Kind = "manual", Cells = 44, Rows = 1, Crit = false,
                Checked = "Whether Catatan Rekabentuk says something meaningful for each element group.", Req = "Non-empty, describes design intent", Act = "44 entries, free text",
                From = null, To = null,
                Reason = "The field is filled. Whether the wording satisfies the reviewer is a content judgement the AI is not allowed to make.", Cite = "Buku Parameter v1.4 · p.24"
            };
            yield return new JkrCopilotRule
            {
                Id = "m5", Sec = "D", Item = "D-b", Cat = "Setbacks", Title = "Setback against planning approval",
                Sev = "High", Kind = "manual", Cells = 16, Rows = 1, Crit = false,
                Checked = "Building line offsets against the approved planning drawing.", Req = "As approved (Kebenaran Merancang)", Act = "front 6.02 m, side 3.48 m",
                From = null, To = null,
                Reason = "The approved planning drawing is ingested for reference but is not authoritative for a verdict. A human reads it against the model.", Cite = "Kebenaran Merancang · lampiran B"
            };
        }
    }
}
