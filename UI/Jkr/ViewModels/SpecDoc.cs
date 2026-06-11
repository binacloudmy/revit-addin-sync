using System.Collections.Generic;

namespace RevitWebAppSync.UI.Jkr.ViewModels
{
    public class SpecDoc
    {
        public string Key { get; set; }
        public string Short { get; set; }
        public string Full { get; set; }
        public int Year { get; set; }

        public static readonly Dictionary<string, SpecDoc> All = new Dictionary<string, SpecDoc>
        {
            ["doc01"] = new SpecDoc { Key = "doc01", Short = "Doc 01 · Project Execution Plan",     Full = "JKR BIM Specification Document 01 — Project Execution Plan",     Year = 2023 },
            ["doc02"] = new SpecDoc { Key = "doc02", Short = "Doc 02 · Model Quality Assurance",    Full = "JKR BIM Specification Document 02 — Model Quality Assurance",   Year = 2023 },
            ["doc03"] = new SpecDoc { Key = "doc03", Short = "Doc 03 · Element Classification",     Full = "JKR BIM Specification Document 03 — Element Classification",    Year = 2023 },
            ["doc04"] = new SpecDoc { Key = "doc04", Short = "Doc 04 · File Naming Convention",     Full = "JKR BIM Specification Document 04 — File Naming Convention",    Year = 2023 },
            ["doc05"] = new SpecDoc { Key = "doc05", Short = "Doc 05 · Modelling Standards",        Full = "JKR BIM Specification Document 05 — Modelling Standards",       Year = 2023 },
            ["doc06"] = new SpecDoc { Key = "doc06", Short = "Doc 06 · Level of Development",       Full = "JKR BIM Specification Document 06 — Level of Development",      Year = 2023 },
            ["doc07"] = new SpecDoc { Key = "doc07", Short = "Doc 07 · Coordinate System",          Full = "JKR BIM Specification Document 07 — Coordinate System",         Year = 2023 },
            ["doc09"] = new SpecDoc { Key = "doc09", Short = "Doc 09 · Parameter Requirements",     Full = "JKR BIM Specification Document 09 — Parameter Requirements",    Year = 2023 },
        };

        public static SpecDoc Get(string key)
        {
            if (!string.IsNullOrEmpty(key) && All.TryGetValue(key, out var d)) return d;
            return new SpecDoc { Key = key ?? "", Short = key ?? "JKR Spec", Full = "JKR BIM Specification", Year = 2023 };
        }
    }
}
