// BuildReport — what one Cad2BimBuilder.Build run produced. Deliberately free of
// Revit types: the Tests project's RevitAPI reference is metadata-only at runtime,
// so a Dictionary<Wall, ElementId> here would make CadToBimSession untestable.
// Wall ids travel as long (ElementIdCompat.ToLong / ToElementId on the Revit side).

using System;
using System.Collections.Generic;

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

        public bool Ok => Error == null;
    }
}
