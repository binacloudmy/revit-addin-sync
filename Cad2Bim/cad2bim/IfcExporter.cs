using System.Globalization;
using System.Text;

namespace Cad2Bim {
    /// <summary>
    /// Writes what the classifier found as an IFC file.
    ///
    /// This is the step that turns a reader into a converter. Everything before it produces
    /// numbers about a drawing; this produces a model another program can open — Revit,
    /// Navisworks, Solibri, anything that speaks IFC — which is what "CAD in, BIM out"
    /// actually means.
    ///
    /// IFC is a STEP file: numbered instances referring to each other by line number. The
    /// subset needed here is small and well defined — a project, a site, a building, one
    /// storey, walls and spaces — so it is written directly rather than through a toolkit that
    /// would have to be right about the whole schema.
    ///
    /// Lengths are metres in the file: IFC keeps SI units while everything upstream is
    /// millimetres, so the conversion happens once, at this boundary.
    /// </summary>
    public sealed class IfcExporter {
        private const double MmPerMetre = 1000.0;

        /// <summary>Storey height when the drawing does not say. A plan cannot: height comes
        /// from a section, and no section has been read.</summary>
        public double WallHeightMm { get; set; } = 3000.0;

        public string ProjectName { get; set; } = "Cad2Bim conversion";
        public string StoreyName { get; set; } = "Level 1";

        /// <summary>Stamped into the header. Passed in rather than read from the clock, so the
        /// same drawing exported twice gives the same file.</summary>
        public DateTimeOffset Timestamp { get; set; }

        private readonly StringBuilder _body = new();
        private int _next = 1;

        public void Write(string path, IReadOnlyList<Wall> walls, IReadOnlyList<Space> spaces) {
            _body.Clear();
            _next = 1;

            int person = Add("IFCPERSON($,$,'',$,$,$,$,$)");
            int organisation = Add("IFCORGANIZATION($,'BINA',$,$,$)");
            int personAndOrg = Add($"IFCPERSONANDORGANIZATION(#{person},#{organisation},$)");
            int application = Add($"IFCAPPLICATION(#{organisation},'1.0','Cad2Bim','CAD2BIM')");
            int ownerHistory = Add(
                $"IFCOWNERHISTORY(#{personAndOrg},#{application},$,.ADDED.,$,$,$,{Timestamp.ToUnixTimeSeconds()})");

            int axis = Add("IFCDIRECTION((0.,0.,1.))");
            int refDirection = Add("IFCDIRECTION((1.,0.,0.))");
            int origin = Add("IFCCARTESIANPOINT((0.,0.,0.))");
            int placement3d = Add($"IFCAXIS2PLACEMENT3D(#{origin},#{axis},#{refDirection})");
            int worldPlacement = Add($"IFCLOCALPLACEMENT($,#{placement3d})");

            int lengthUnit = Add("IFCSIUNIT(*,.LENGTHUNIT.,$,.METRE.)");
            int areaUnit = Add("IFCSIUNIT(*,.AREAUNIT.,$,.SQUARE_METRE.)");
            int volumeUnit = Add("IFCSIUNIT(*,.VOLUMEUNIT.,$,.CUBIC_METRE.)");
            int units = Add($"IFCUNITASSIGNMENT((#{lengthUnit},#{areaUnit},#{volumeUnit}))");

            int context = Add(
                $"IFCGEOMETRICREPRESENTATIONCONTEXT($,'Model',3,1.E-05,#{placement3d},$)");
            int project = Add(
                $"IFCPROJECT('{Guid()}',#{ownerHistory},'{Escape(ProjectName)}',$,$,$,$,(#{context}),#{units})");

            int site = Add($"IFCSITE('{Guid()}',#{ownerHistory},'Site',$,$,#{worldPlacement},$,$,.ELEMENT.,$,$,$,$,$)");
            int building = Add($"IFCBUILDING('{Guid()}',#{ownerHistory},'Building',$,$,#{worldPlacement},$,$,.ELEMENT.,$,$,$)");
            int storey = Add(
                $"IFCBUILDINGSTOREY('{Guid()}',#{ownerHistory},'{Escape(StoreyName)}',$,$,#{worldPlacement},$,$,.ELEMENT.,0.)");

            Add($"IFCRELAGGREGATES('{Guid()}',#{ownerHistory},$,$,#{project},(#{site}))");
            Add($"IFCRELAGGREGATES('{Guid()}',#{ownerHistory},$,$,#{site},(#{building}))");
            Add($"IFCRELAGGREGATES('{Guid()}',#{ownerHistory},$,$,#{building},(#{storey}))");

            var contained = new List<int>();
            var spaceIds = new List<int>();

            foreach (Wall wall in walls) {
                if (WriteWall(wall, ownerHistory, worldPlacement, context) is int id) contained.Add(id);
            }

            foreach (Space space in spaces) {
                if (WriteSpace(space, ownerHistory, worldPlacement, context) is int id) spaceIds.Add(id);
            }

            if (contained.Count > 0) {
                Add($"IFCRELCONTAINEDINSPATIALSTRUCTURE('{Guid()}',#{ownerHistory},$,$,({Refs(contained)}),#{storey})");
            }

            // Spaces belong to the storey by aggregation, not containment.
            if (spaceIds.Count > 0) {
                Add($"IFCRELAGGREGATES('{Guid()}',#{ownerHistory},$,$,#{storey},({Refs(spaceIds)}))");
            }

            File.WriteAllText(path, Compose(path), new UTF8Encoding(false));
        }

        private int? WriteWall(Wall wall, int ownerHistory, int parentPlacement, int context) {
            Segment line = wall.Centerline;
            double length = line.Length;
            if (length <= 0 || wall.Thickness <= 0) return null;

            double dx = (line.P2.x - line.P1.x) / length;
            double dy = (line.P2.y - line.P1.y) / length;

            // The wall sits on its own axis: origin at the start of the centreline, X running
            // along it. The profile is then a plain rectangle in that frame, which is what
            // makes the result a wall rather than an arbitrary solid that happens to be
            // wall-shaped.
            int origin = Add(Point3(line.P1.x, line.P1.y, 0));
            int direction = Add($"IFCDIRECTION(({Num(dx)},{Num(dy)},0.))");
            int axis = Add("IFCDIRECTION((0.,0.,1.))");
            int placement3d = Add($"IFCAXIS2PLACEMENT3D(#{origin},#{axis},#{direction})");
            int placement = Add($"IFCLOCALPLACEMENT(#{parentPlacement},#{placement3d})");

            int profileCentre = Add(Point2(length / 2.0, 0));
            int profilePlacement = Add($"IFCAXIS2PLACEMENT2D(#{profileCentre},$)");
            int profile = Add(
                $"IFCRECTANGLEPROFILEDEF(.AREA.,$,#{profilePlacement},{Metres(length)},{Metres(wall.Thickness)})");

            int extrudeOrigin = Add("IFCCARTESIANPOINT((0.,0.,0.))");
            int extrudeAxis = Add("IFCDIRECTION((0.,0.,1.))");
            int extrudeRef = Add("IFCDIRECTION((1.,0.,0.))");
            int extrudePlacement = Add($"IFCAXIS2PLACEMENT3D(#{extrudeOrigin},#{extrudeAxis},#{extrudeRef})");
            int solid = Add(
                $"IFCEXTRUDEDAREASOLID(#{profile},#{extrudePlacement},#{extrudeAxis},{Metres(WallHeightMm)})");

            int shape = Add($"IFCSHAPEREPRESENTATION(#{context},'Body','SweptSolid',(#{solid}))");
            int product = Add($"IFCPRODUCTDEFINITIONSHAPE($,$,(#{shape}))");

            string name = wall.IsOutdoor ? "External wall" : "Internal wall";
            return Add($"IFCWALLSTANDARDCASE('{Guid()}',#{ownerHistory},'{name}',$,$,#{placement},#{product},$,$)");
        }

        private int? WriteSpace(Space space, int ownerHistory, int parentPlacement, int context) {
            if (space.Boundary.Count < 3) return null;

            var points = new List<int>(space.Boundary.Count + 1);
            foreach (Point point in space.Boundary) {
                points.Add(Add(Point2(point.x, point.y)));
            }

            // A closed profile repeats its first point at the end.
            points.Add(points[0]);

            int polyline = Add($"IFCPOLYLINE(({Refs(points)}))");
            int profile = Add($"IFCARBITRARYCLOSEDPROFILEDEF(.AREA.,$,#{polyline})");

            int origin = Add("IFCCARTESIANPOINT((0.,0.,0.))");
            int axis = Add("IFCDIRECTION((0.,0.,1.))");
            int refDirection = Add("IFCDIRECTION((1.,0.,0.))");
            int placement3d = Add($"IFCAXIS2PLACEMENT3D(#{origin},#{axis},#{refDirection})");
            int placement = Add($"IFCLOCALPLACEMENT(#{parentPlacement},#{placement3d})");

            int solid = Add($"IFCEXTRUDEDAREASOLID(#{profile},#{placement3d},#{axis},{Metres(WallHeightMm)})");
            int shape = Add($"IFCSHAPEREPRESENTATION(#{context},'Body','SweptSolid',(#{solid}))");
            int product = Add($"IFCPRODUCTDEFINITIONSHAPE($,$,(#{shape}))");

            string name = Escape(space.Name ?? "Space");
            return Add(
                $"IFCSPACE('{Guid()}',#{ownerHistory},'{name}',$,$,#{placement},#{product},$,.ELEMENT.,.INTERNAL.,$)");
        }

        private int Add(string instance) {
            _body.Append('#').Append(_next).Append('=').Append(instance).Append(";\n");
            return _next++;
        }

        private string Compose(string path) {
            var file = new StringBuilder();
            string stamp = Timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

            file.Append("ISO-10303-21;\n");
            file.Append("HEADER;\n");
            file.Append("FILE_DESCRIPTION(('ViewDefinition [CoordinationView]'),'2;1');\n");
            file.Append($"FILE_NAME('{Escape(Path.GetFileName(path))}','{stamp}',(''),(''),'Cad2Bim','Cad2Bim','');\n");
            file.Append("FILE_SCHEMA(('IFC4'));\n");
            file.Append("ENDSEC;\n");
            file.Append("DATA;\n");
            file.Append(_body);
            file.Append("ENDSEC;\n");
            file.Append("END-ISO-10303-21;\n");

            return file.ToString();
        }

        private string Point3(double x, double y, double z) =>
            $"IFCCARTESIANPOINT(({Metres(x)},{Metres(y)},{Metres(z)}))";

        private string Point2(double x, double y) =>
            $"IFCCARTESIANPOINT(({Metres(x)},{Metres(y)}))";

        private static string Metres(double millimetres) => Num(millimetres / MmPerMetre);

        /// <summary>STEP wants a decimal point on every real, so 3 has to be written "3.".</summary>
        private static string Num(double value) {
            string text = value.ToString("0.######", CultureInfo.InvariantCulture);
            return text.Contains('.') ? text : text + ".";
        }

        private static string Refs(IEnumerable<int> ids) => string.Join(",", ids.Select(id => "#" + id));

        private static string Escape(string text) => text.Replace("'", "''");

        /// <summary>
        /// IFC identifies every object by a 22-character id of its own, and the schema means
        /// globally unique: two objects sharing one is a malformed file, and importers are
        /// entitled to drop or merge them.
        ///
        /// It is derived from the instance number and the drawing's own timestamp rather than
        /// drawn at random, so exporting the same drawing twice produces the same file and two
        /// exports can be compared. The hash spreads those few input bytes across all sixteen,
        /// which a straight copy of the counter did not: it left most of the id constant and
        /// collisions a real possibility across a thousand walls.
        /// </summary>
        private string Guid() {
            byte[] seed = Encoding.ASCII.GetBytes($"cad2bim:{_next}:{Timestamp.ToUnixTimeSeconds()}");
            byte[] bytes = System.Security.Cryptography.MD5.HashData(seed);

            const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_$";
            var text = new StringBuilder(22);

            // 128 bits as one leading 2-bit character then 21 six-bit characters.
            text.Append(alphabet[bytes[0] >> 6]);

            ulong window = 0;
            int bits = 2;
            int index = 0;

            while (text.Length < 22) {
                while (bits < 6) {
                    index++;
                    window = (window << 8) | (index < bytes.Length ? bytes[index] : (byte)0);
                    bits += 8;
                }

                bits -= 6;
                text.Append(alphabet[(int)((window >> bits) & 0x3F)]);
            }

            return text.ToString();
        }
    }
}
