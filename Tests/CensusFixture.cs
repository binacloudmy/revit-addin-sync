// Builds small CadDocuments in memory for the symbol-sink and electrical census tests, so no
// drawing ever has to be checked in. Everything is in millimetres unless a test says otherwise.

using System;
using System.IO;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CSMath;

namespace Tests
{
    internal static class CensusFixture
    {
        /// <summary>The document's layer of that name, added on first use.</summary>
        public static Layer Layer(CadDocument doc, string name)
        {
            if (doc.Layers.TryGetValue(name, out Layer existing)) return existing;
            var layer = new Layer(name);
            doc.Layers.Add(layer);
            return layer;
        }

        /// <summary>A block definition holding these entities, registered in the document.</summary>
        public static BlockRecord Block(CadDocument doc, string name, params Entity[] entities)
        {
            var record = new BlockRecord(name);
            foreach (Entity entity in entities) record.Entities.Add(entity);
            doc.BlockRecords.Add(record);
            return record;
        }

        /// <summary>A square symbol of the given size, centred on its base point.</summary>
        public static BlockRecord SquareBlock(CadDocument doc, string name, double size)
        {
            double h = size / 2;
            return Block(doc, name,
                new Line(new XYZ(-h, -h, 0), new XYZ(h, -h, 0)),
                new Line(new XYZ(h, -h, 0), new XYZ(h, h, 0)),
                new Line(new XYZ(h, h, 0), new XYZ(-h, h, 0)),
                new Line(new XYZ(-h, h, 0), new XYZ(-h, -h, 0)));
        }

        /// <summary>An insert not yet placed anywhere: add it to a block or to model space.</summary>
        public static Insert MakeInsert(CadDocument doc, BlockRecord block, double x, double y,
                                        string layer = "0", double rotationDeg = 0, double sx = 1, double sy = 1)
        {
            return new Insert(block)
            {
                InsertPoint = new XYZ(x, y, 0),
                Rotation = rotationDeg * Math.PI / 180.0,
                XScale = sx,
                YScale = sy,
                Layer = Layer(doc, layer),
            };
        }

        /// <summary>An insert in model space.</summary>
        public static Insert Insert(CadDocument doc, BlockRecord block, double x, double y,
                                    string layer = "0", double rotationDeg = 0, double sx = 1, double sy = 1)
        {
            Insert insert = MakeInsert(doc, block, x, y, layer, rotationDeg, sx, sy);
            doc.Entities.Add(insert);
            return insert;
        }

        public static Line Line(CadDocument doc, string layer, double x1, double y1, double x2, double y2)
        {
            var line = new Line(new XYZ(x1, y1, 0), new XYZ(x2, y2, 0)) { Layer = Layer(doc, layer) };
            doc.Entities.Add(line);
            return line;
        }

        /// <summary>Two parallel faces <paramref name="thickness"/> apart, the first along the
        /// given line and the second offset to its left.</summary>
        public static void WallPair(CadDocument doc, string layer, double x1, double y1, double x2, double y2,
                                    double thickness = 150)
        {
            double dx = x2 - x1, dy = y2 - y1;
            double length = Math.Sqrt((dx * dx) + (dy * dy));
            double nx = -dy / length * thickness, ny = dx / length * thickness;
            Line(doc, layer, x1, y1, x2, y2);
            Line(doc, layer, x1 + nx, y1 + ny, x2 + nx, y2 + ny);
        }

        /// <summary>A rectangular room of double-line walls, outer box from (x, y).</summary>
        public static void Room(CadDocument doc, string layer, double x, double y, double width, double height,
                                double thickness = 150)
        {
            double t = thickness;
            WallPair(doc, layer, x, y + t, x + width, y + t, -t);              // bottom
            WallPair(doc, layer, x + width, y + height - t, x, y + height - t, -t); // top
            WallPair(doc, layer, x + t, y + height, x + t, y, -t);             // left
            WallPair(doc, layer, x + width - t, y, x + width - t, y + height, -t); // right
        }

        public static TextEntity Text(CadDocument doc, string layer, double x, double y, string value,
                                      double height = 250)
        {
            var text = new TextEntity
            {
                InsertPoint = new XYZ(x, y, 0),
                Height = height,
                Value = value,
                Layer = Layer(doc, layer),
            };
            doc.Entities.Add(text);
            return text;
        }

        /// <summary>Writes the document as DXF to a temp path. The caller deletes it.</summary>
        public static string WriteDxf(CadDocument doc)
        {
            string path = Path.Combine(Path.GetTempPath(), "census-" + Guid.NewGuid().ToString("N") + ".dxf");
            using (var writer = new DxfWriter(path, doc, false))
            {
                writer.Write();
            }
            return path;
        }
    }
}
