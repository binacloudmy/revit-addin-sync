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

    public sealed class BuildRequest
    {
        public BuildTarget Target { get; set; }
        public string DrawingPath { get; set; }

        /// <summary>Pending walls only (session.Pending()): a later Confirm appends, never
        /// rebuilds what is already in the model.</summary>
        public List<Cad2Bim.Wall> Walls { get; set; }
        public List<Cad2Bim.Opening> Openings { get; set; }
        public List<Cad2Bim.Space> Spaces { get; set; }

        /// <summary>Millimetres. A plan carries no height, so this is an assumption until a
        /// section is read; 3 m is the ordinary storey. Also the storey step in Stack mode.</summary>
        public double HeightMm { get; set; } = 3000;
        public StoreyMode Storeys { get; set; } = StoreyMode.Stack;

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
