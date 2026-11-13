// The pane -> builder contract for CAD to BIM (spec 2026-09-08 §3 "Build").
//
// BuildRequest is everything Cad2BimBuilder needs to make Revit elements from
// the classifier's output. Plain data, and deliberately free of Revit types, so
// the pane can be unit-tested and a request can cross the ExternalEvent boundary
// as one object. The level is an ElementId VALUE (ElementIdCompat.ToLong on the
// pane side, ElementIdCompat.ToElementId in the builder).

using System.Collections.Generic;

namespace RevitWebAppSync.Services.CadToBim
{
    /// <summary>Where the built elements go.</summary>
    public enum BuildTarget
    {
        /// <summary>Into the active document, on a level the drafter picked.</summary>
        AddToProject,
        /// <summary>Into a new project created from a template and saved beside the drawing.</summary>
        NewFile,
    }

    /// <summary>How the floor plans a drawing holds side by side are placed.</summary>
    public enum StoreyMode
    {
        /// <summary>One level per plan cluster, each shifted so its own corner meets the
        /// origin - the building the way it stands.</summary>
        Stack,
        /// <summary>Everything on one level, exactly where the drawing puts it, so it lands on
        /// top of a linked CAD and can be compared against it.</summary>
        KeepPosition,
    }

    /// <summary>What a TYPES row sets the type of.</summary>
    public enum TypeSlot { Wall, Door, Window }

    /// <summary>One drafter choice from the Build step: walls of WidthMm (rounded by
    /// BuildSizing.WallWidthMm), or doors/windows of WidthMm x HeightMm (rounded by
    /// BuildSizing.OpeningSize), get the type whose ElementId value is TypeId.</summary>
    public sealed class TypeChoice
    {
        public TypeSlot Slot { get; set; }
        public double WidthMm { get; set; }
        /// <summary>NaN (or ignored) for walls.</summary>
        public double HeightMm { get; set; } = double.NaN;
        public long TypeId { get; set; }

        /// <summary>The chosen type id for a size, or 0 when the drafter left it to the builder.
        /// Sizes match within half a millimetre (both sides come from the same rounding).</summary>
        public static long Find(IEnumerable<TypeChoice> choices, TypeSlot slot, double widthMm, double heightMm)
        {
            if (choices == null) return 0;
            foreach (TypeChoice choice in choices)
            {
                if (choice == null || choice.Slot != slot || choice.TypeId <= 0) continue;
                if (System.Math.Abs(choice.WidthMm - widthMm) > 0.5) continue;
                if (slot != TypeSlot.Wall && System.Math.Abs(choice.HeightMm - heightMm) > 0.5) continue;
                return choice.TypeId;
            }
            return 0;
        }
    }

    public sealed class BuildRequest
    {
        /// <summary>What the handler does with this request. Build unless set.</summary>
        public BuildJobKind Kind { get; set; } = BuildJobKind.Build;

        /// <summary>CheckBuilt / Undo / Select: the element ids (ElementId values) to act on.</summary>
        public List<long> ElementIds { get; set; }

        /// <summary>Undo / Select: the document the ids belong to (BuildReport.DocumentKey).
        /// null = whatever is active.</summary>
        public string DocumentKey { get; set; }

        /// <summary>Build, AddToProject: ids of walls the pane believes an earlier build made.
        /// Checked first on the Revit thread; any that are gone stop the build (BuildReport.Stale)
        /// so the pane can put those walls back in its pending list before building.</summary>
        public List<long> BuiltWallIds { get; set; }

        public BuildTarget Target { get; set; }
        public string DrawingPath { get; set; }

        /// <summary>Pending walls only (session.Pending()): a later Confirm appends, never
        /// rebuilds what is already in the model. The builder CREATES exactly these walls.</summary>
        public List<Cad2Bim.Wall> Walls { get; set; }

        /// <summary>The full layout the plan is clustered and placed against — session.Active(),
        /// pending and already-built together. null means "same as Walls" (a first build, where
        /// the two are identical). Passing the full layout keeps plan origins and level indices
        /// stable across Confirms: without it, a second Confirm sees only the pending subset,
        /// re-clusters from that subset's own bounding box, and appended walls land shifted to
        /// the model origin instead of where the first build put them (spec 2026-09-08 §3).
        /// Openings and rooms are still filtered to the walls actually created (Walls).</summary>
        public List<Cad2Bim.Wall> LayoutWalls { get; set; }

        public List<Cad2Bim.Opening> Openings { get; set; }
        public List<Cad2Bim.Space> Spaces { get; set; }

        /// <summary>Millimetres. A plan carries no height, so this is an assumption until a
        /// section is read; 3 m is the ordinary storey. Also the storey step in Stack mode.</summary>
        public double HeightMm { get; set; } = 3000;
        public StoreyMode Storeys { get; set; } = StoreyMode.Stack;

        /// <summary>Millimetres above the level a window sits at. From CadToBimSettings.WindowSillMm;
        /// 900 mm (an ordinary sill) until the drafter changes it.</summary>
        public double WindowSillMm { get; set; } = 900;

        /// <summary>Millimetres. Door and window types are sized to the drawn width and these
        /// heights (CadToBimSettings.DoorHeightMm / WindowHeightMm).</summary>
        public double DoorHeightMm { get; set; } = 2100;
        public double WindowHeightMm { get; set; } = 1200;

        /// <summary>Let Revit join the built walls at corners and tees (CadToBimSettings.JoinWalls).</summary>
        public bool JoinWalls { get; set; } = true;

        /// <summary>The Build step's TYPES choices: an existing type to use, as it is, for one
        /// wall width or one door/window size. A size with no entry here is chosen by the
        /// builder (BuildSizing: a type that size, else a new "CAD …" type). null = none.</summary>
        public List<TypeChoice> TypeChoices { get; set; }

        /// <summary>AddToProject only. The ElementId value of the level the drafter picked
        /// (ElementIdCompat.ToLong); null = the lowest level in the document.</summary>
        public long? LevelId { get; set; }

        /// <summary>NewFile only: &lt;dwg dir&gt;/&lt;dwg name&gt;.rvt. The pane has already asked
        /// about overwriting by the time the builder sees this.</summary>
        public string OutputPath { get; set; }

        /// <summary>NewFile only. null = Application.DefaultProjectTemplate.</summary>
        public string TemplatePath { get; set; }
    }
}
