using System;
using System.Collections.Generic;

namespace RevitWebAppSync
{
    /// <summary>
    /// Shape of GET /api/cloud-docs/bim-discipline/project/{id}/latest-shared-urls.
    ///
    /// The add-in previously called `latest-urls`, which does not exist — a silent
    /// 404 — and modelled the answer as one file per discipline. The real endpoint
    /// groups by discipline, then by tracking-enabled folder, and returns the
    /// latest version of EVERY filename in each folder so Revit can recreate the
    /// folder structure on disk.
    /// </summary>
    public class BimDisciplineFile
    {
        public int Id { get; set; }
        public string FileName { get; set; }
        public string FileUrl { get; set; }
        public string Version { get; set; }

        /// <summary>
        /// "nwc" when a Navisworks cache is linked (what a coordinator opens),
        /// otherwise the design's own type — normally "rvt", which is what a
        /// Revit user actually wants.
        /// </summary>
        public string FileType { get; set; }

        /// <summary>Populated when this file has nothing downloadable.</summary>
        public string Error { get; set; }
    }

    public class BimDisciplineFolder
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public List<BimDisciplineFile> Files { get; set; } = new List<BimDisciplineFile>();
    }

    public class BimDisciplineGroup
    {
        public List<BimDisciplineFolder> Folders { get; set; } = new List<BimDisciplineFolder>();
    }

    /// <summary>
    /// Keys are lower-case discipline names. Civil is absent by design — the
    /// backend's grouping switch skips it (bim-discipline.service.ts), so a Civil
    /// tracking folder is never returned.
    /// </summary>
    public class BimDisciplineResponse
    {
        public BimDisciplineGroup Architecture { get; set; }
        public BimDisciplineGroup Structure { get; set; }
        public BimDisciplineGroup Mechanical { get; set; }
        public BimDisciplineGroup Electrical { get; set; }

        /// <summary>Disciplines present in the response, with their display labels.</summary>
        public IEnumerable<(string Label, BimDisciplineGroup Group)> Groups()
        {
            yield return ("Architecture", Architecture);
            yield return ("Structure", Structure);
            yield return (Services.DisciplineTypes.MechanicalLabel, Mechanical);
            yield return ("Electrical", Electrical);
        }
    }
}
