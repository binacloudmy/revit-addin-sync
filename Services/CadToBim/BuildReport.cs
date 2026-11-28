// BuildReport — what one Cad2BimBuilder.Build run produced. Deliberately free of
// Revit types: the Tests project's RevitAPI reference is metadata-only at runtime,
// so a Dictionary<Wall, ElementId> here would make CadToBimSession untestable.
// Wall ids travel as long (ElementIdCompat.ToLong / ToElementId on the Revit side).

using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.Services.CadToBim
{
    public sealed class BuildReport
    {
        public int Walls;
        public int Doors;
        public int Windows;
        public int Rooms;
        public int SkippedOpenings;
        public int SkippedWalls;

        /// <summary>Rooms the classifier found but that could not be placed because their level
        /// has no ViewPlan yet — true of every "CAD Level N" this build creates (room separation
        /// lines are view-hosted). Not a failure: the walls and openings on that level are built.</summary>
        public int SkippedRooms;

        /// <summary>NewFile target only: the .rvt that was written.</summary>
        public string OutputPath;

        public TimeSpan Elapsed;

        /// <summary>null = success. Innermost exception message otherwise; the
        /// TransactionGroup was rolled back and nothing below is in the model.</summary>
        public string Error;

        /// <summary>CAD wall -> Revit wall id (ElementId value), for every wall the
        /// build created and that survived Assimilate(). Keyed by reference.</summary>
        public Dictionary<Cad2Bim.Wall, long> WallIds = new();

        /// <summary>AI-642: door / window instances the build placed (ElementId values). Doors and
        /// Windows above are recounted from these after Assimilate, like Walls from WallIds.</summary>
        public List<long> DoorIds = new();
        public List<long> WindowIds = new();

        /// <summary>AI-642: every warning the failure handler deleted and every error it resolved.</summary>
        public FailureLog Failures = new();

        public bool Ok => Error == null;
    }

    /// <summary>One kind of Revit failure from a build: same definition, same severity.</summary>
    public sealed class FailureGroup
    {
        public Guid DefinitionId;
        public string Text = "";
        /// <summary>A join failure (JoinElements / AutoJoin / WallJoin definitions).</summary>
        public bool Join;
        /// <summary>An error, not a warning.</summary>
        public bool Error;
        /// <summary>An error Revit resolved with its default resolution (which may delete elements).</summary>
        public bool Resolved;
        public int Count;
        public List<long> ElementIds = new();
    }

    /// <summary>What SilenceJoinFailures removed from Revit's dialogs, grouped by FailureDefinitionId.
    /// Revit-free so the grouping is unit-tested. AI-642: warnings go to Review, not to nowhere.
    /// Add records into the open transaction's list; Keep moves it into Groups when that transaction
    /// committed, Drop discards it when it rolled back (its failures are not in the model).</summary>
    public sealed class FailureLog
    {
        public List<FailureGroup> Groups = new();
        private readonly List<(Guid Id, string Text, bool Join, bool Error, bool Resolved, long[] Ids)> _open = new();

        public void Add(Guid definitionId, string text, bool join, bool error, bool resolved, IEnumerable<long> elementIds) =>
            _open.Add((definitionId, text ?? "", join, error, error && resolved, (elementIds ?? Array.Empty<long>()).ToArray()));

        public void Keep()
        {
            foreach (var f in _open)
            {
                FailureGroup g = Groups.Find(x => x.DefinitionId == f.Id && x.Error == f.Error && x.Resolved == f.Resolved);
                if (g == null) Groups.Add(g = new FailureGroup { DefinitionId = f.Id, Text = f.Text, Join = f.Join, Error = f.Error, Resolved = f.Resolved });
                g.Count++;
                foreach (long id in f.Ids) if (!g.ElementIds.Contains(id)) g.ElementIds.Add(id);
            }
            _open.Clear();
        }

        public void Drop() => _open.Clear();

        public int JoinWarnings => Groups.Where(g => g.Join && !g.Error).Sum(g => g.Count);
        public int OtherWarnings => Groups.Where(g => !g.Join && !g.Error).Sum(g => g.Count);
        public int ResolvedErrors => Groups.Where(g => g.Error && g.Resolved).Sum(g => g.Count);
        /// <summary>Errors Revit had no resolution for (the transaction still committed).</summary>
        public int OtherErrors => Groups.Where(g => g.Error && !g.Resolved).Sum(g => g.Count);
        public IEnumerable<long> ResolvedErrorIds => Groups.Where(g => g.Error && g.Resolved).SelectMany(g => g.ElementIds).Distinct();
    }
}
