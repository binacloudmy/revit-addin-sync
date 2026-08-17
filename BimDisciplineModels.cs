using System;
using System.Collections.Generic;

namespace RevitWebAppSync
{
    public class BimDisciplineFile
    {
        public string DisciplineType { get; set; }
        public string FileUrl { get; set; }
        public string FileName { get; set; }
    }

    /// <summary>
    /// A project discipline as returned by the API. Disciplines used to be a
    /// hardcoded enum with one fixed property per discipline (Structure,
    /// Architecture, HVAC, Electrical) on <c>BimDisciplineResponse</c>; they are
    /// now per-project user-defined data fetched as a list from
    /// projectDisciplines(projectId) (see BinaApiService.GetProjectDisciplinesAsync).
    ///
    /// Code is the immutable identity — it is what the server stores in every
    /// bim_* record's disciplineType column and what is embedded in OBS storage
    /// keys. Name is display-only and can change at any time (renames); never
    /// key off Name, never persist it as an identifier.
    /// </summary>
    public class BimDiscipline
    {
        public int Id { get; set; }
        public string Code { get; set; }
        public string Name { get; set; }
        public string ShortCode { get; set; }
        public string Color { get; set; }
        public string Icon { get; set; }
        public int SortOrder { get; set; }
        public bool IsSystem { get; set; }

        /// <summary>
        /// True for the "MainFile" system row. The registry seeds MainFile like
        /// any other system discipline (project-discipline.entity.ts:
        /// SYSTEM_DISCIPLINE_SEED), but it represents the federation output, not
        /// a user-selectable discipline — callers must exclude it from pickers
        /// and iteration over "disciplines to download/link/upload", while still
        /// using it (via this flag or a direct Code == "MainFile" check) for the
        /// federated-file path.
        /// </summary>
        public bool IsMainFile => string.Equals(Code, "MainFile", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The registry-driven replacement for the old fixed-property
    /// BimDisciplineResponse. Disciplines is the project's discipline list
    /// (from projectDisciplines(projectId), including the MainFile row).
    /// FilesByCode is a best-effort map of the latest downloadable file per
    /// discipline Code, populated from BinaApiService.GetBimDisciplineModelsAsync
    /// — see the caveat on that method: the REST endpoint it calls
    /// (latest-urls) was renamed server-side to latest-shared-urls with an
    /// unrelated response shape well before this change, so FilesByCode is
    /// expected to come back empty until that endpoint is fixed independently.
    /// </summary>
    public class BimDisciplineModels
    {
        public List<BimDiscipline> Disciplines { get; set; } = new List<BimDiscipline>();
        public Dictionary<string, BimDisciplineFile> FilesByCode { get; set; } =
            new Dictionary<string, BimDisciplineFile>(StringComparer.OrdinalIgnoreCase);
    }
}
