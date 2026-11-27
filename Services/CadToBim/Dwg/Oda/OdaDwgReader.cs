#if HAS_ODA
// AI-647: ODA Drawings.NET 27.10 behind IDwgReader. The ODA database is translated into an
// in-memory ACadSharp CadDocument, covering exactly the entity cases CadRenderSource.Walk
// handles (no more), then disposed. No ODA type leaves this folder.
//
// Coordinates are handed over the way ACadSharp itself would read them from the file, so
// Walk's tessellation and transforms produce the same polylines: ODA returns WCS for arc
// and circle centres, text, insert and solid points, while ACadSharp keeps the file's OCS
// values, so those are converted back with the arbitrary-axis algorithm when the normal is
// not +Z. LWPOLYLINE, 2D polyline vertices and hatch loops are already OCS on both sides.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using ODA.Drawings.TD_DbCoreIntegrated;
using ODA.Kernel.TD_RootIntegrated;

namespace RevitWebAppSync.Services.CadToBim.Dwg
{
    public sealed class OdaDwgReader : IDwgReader
    {
        private readonly string _binDir;
        private readonly string _keyFile;

        public OdaDwgReader(string binDir, string keyFile)
        {
            _binDir = binDir;
            _keyFile = keyFile;
        }

        public string Engine => "oda";

        public DwgDrawing Read(string path) => OdaRuntime.Run(_binDir, _keyFile, () => OdaTranslator.Read(path));
    }

    /// <summary>Runs on the ODA thread only (OdaRuntime), inside one memory transaction per read.</summary>
    internal sealed class OdaTranslator
    {
        private const int MaxDepth = 16;   // same guard as CadRenderSource.MaxDepth

        private readonly OdDbDatabase _db;
        private readonly string _path;
        private readonly CadDocument _doc = new CadDocument();
        private readonly Dictionary<string, Layer> _layers = new Dictionary<string, Layer>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, BlockRecord> _blocks = new Dictionary<string, BlockRecord>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _proxies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _census = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // block records the walk already counted
        private readonly Dictionary<string, double> _timings = new Dictionary<string, double>();
        private int _anonymous;

        private OdaTranslator(OdDbDatabase db, string path)
        {
            _db = db;
            _path = path;
        }

        public static DwgDrawing Read(string path)
        {
            MemoryManager mm = MemoryManager.GetMemoryManager();
            MemoryTransaction tx = mm.StartTransaction();
            try
            {
                OdaSession.HostWarnings.Clear();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                OdDbDatabase db = OdaSession.Host.readFile(path, true, false, Oda_FileShareMode.kShareDenyNo, "");
                var t = new OdaTranslator(db, path);
                t._timings["readFile"] = clock.Elapsed.TotalSeconds;
                DwgDrawing drawing = t.Translate();
                return drawing;
            }
            finally
            {
                mm.StopTransaction(tx);   // frees every wrapper the read created, the database included
            }
        }

        private DwgDrawing Translate()
        {
            _doc.Header.InsUnits = (ACadSharp.Types.Units.UnitsType)(int)_db.getINSUNITS();   // both use the DXF INSUNITS codes

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var modelSpace = (OdDbBlockTableRecord)_db.getModelSpaceId().safeOpenObject();
            _visited.Add(_db.getModelSpaceId().getHandle().ascii());
            CopyEntities(modelSpace, e => _doc.Entities.Add(e), 0);
            Lap("translate", clock);

            var warnings = new List<DwgWarning>();
            CensusAndXrefs(warnings);
            Lap("census", clock);
            Images(warnings);
            Lap("images", clock);
            var classes = Classes();
            Lap("classes", clock);
            warnings.AddRange(DwgDrawing.ProxyWarnings(_proxies));
            warnings.AddRange(DwgDrawing.HostWarnings(OdaSession.HostWarnings));

            return new DwgDrawing
            {
                Document = _doc,
                Engine = "oda",
                Classes = classes,
                Warnings = warnings,
                Timings = _timings,
            };
        }

        // ───────── entities ─────────

        private void CopyEntities(OdDbBlockTableRecord record, Action<Entity> add, int depth)
        {
            for (OdDbObjectIterator it = record.newIterator(); !it.done(); it.step())
            {
                OdDbEntity entity = OdDbEntity.cast(it.objectId().openObject());
                if (entity == null) continue;
                try
                {
                    Count(entity);   // one pass: the census rides on the walk for every block record it visits
                    foreach (Entity copy in Translate(entity, depth))
                        add(copy);
                }
                finally
                {
                    entity.Dispose();   // close it now, not at StopTransaction: 84k open wrappers otherwise (SWIG Dispose is idempotent)
                }
            }
        }

        private IEnumerable<Entity> Translate(OdDbEntity e, int depth)
        {
            Entity result = null;
            switch (e)
            {
                case OdDbLine line:
                    result = new Line { StartPoint = P(line.startPoint()), EndPoint = P(line.endPoint()) };
                    break;

                case OdDbPolyline pl:
                {
                    var lw = new LwPolyline { IsClosed = pl.isClosed(), Normal = V(pl.normal()), Elevation = pl.elevation() };
                    var p2 = new OdGePoint2d();
                    for (uint i = 0; i < pl.numVerts(); i++)
                    {
                        pl.getPointAt(i, p2);
                        lw.Vertices.Add(new LwPolyline.Vertex(p2.x, p2.y) { Bulge = pl.getBulgeAt(i) });
                    }
                    result = lw;
                    break;
                }

                case OdDb2dPolyline p2d:
                {
                    var vertices = new List<Vertex2D>();
                    for (OdDbObjectIterator it = p2d.vertexIterator(); !it.done(); it.step())
                    {
                        if (it.entity() is OdDb2dVertex v)   // every vertex, control vertices too: ACadSharp keeps them all
                            vertices.Add(new Vertex2D { Location = P(v.position()), Bulge = v.bulge() });
                    }
                    result = new Polyline2D(vertices, p2d.isClosed()) { Normal = V(p2d.normal()) };
                    break;
                }

                case OdDb3dPolyline p3d:
                {
                    var vertices = new List<Vertex3D>();
                    for (OdDbObjectIterator it = p3d.vertexIterator(); !it.done(); it.step())
                    {
                        if (it.entity() is OdDb3dPolylineVertex v)
                            vertices.Add(new Vertex3D(P(v.position())));
                    }
                    result = new Polyline3D(vertices, p3d.isClosed());
                    break;
                }

                // OdDbArc derives from OdDbCurve, not OdDbCircle, but keep ACadSharp's order anyway.
                case OdDbArc arc:
                    result = new Arc { Center = Ocs(arc.center(), arc.normal()), Radius = arc.radius(), StartAngle = arc.startAngle(), EndAngle = arc.endAngle(), Normal = V(arc.normal()) };
                    break;

                case OdDbCircle circle:
                    result = new Circle { Center = Ocs(circle.center(), circle.normal()), Radius = circle.radius(), Normal = V(circle.normal()) };
                    break;

                case OdDbEllipse ellipse:
                {
                    ellipse.getStartParam(out double start);
                    ellipse.getEndParam(out double end);
                    result = new Ellipse
                    {
                        Center = P(ellipse.center()),
                        MajorAxisEndPoint = V(ellipse.majorAxis()),
                        RadiusRatio = ellipse.radiusRatio(),
                        StartParameter = start,
                        EndParameter = end,
                        Normal = V(ellipse.normal()),
                    };
                    break;
                }

                case OdDbSpline spline:
                    result = ToSpline(spline);
                    break;

                case OdDbSolid solid:
                {
                    OdGeVector3d n = solid.normal();
                    XYZ Corner(int i) { var p = new OdGePoint3d(); solid.getPointAt(i, p); return Ocs(p, n); }
                    result = new Solid { FirstCorner = Corner(0), SecondCorner = Corner(1), ThirdCorner = Corner(2), FourthCorner = Corner(3), Normal = V(n) };
                    break;
                }

                case OdDbLeader leader:
                {
                    var l = new Leader { Normal = V(leader.normal()) };
                    for (int i = 0; i < leader.numVertices(); i++) l.Vertices.Add(P(leader.vertexAt(i)));
                    result = l;
                    break;
                }

                case OdDbBlockReference reference:
                    result = ToInsert(reference, depth);
                    break;

                case OdDbHatch hatch:
                    result = ToHatch(hatch);
                    break;

                case OdDbDimension dimension:
                {
                    BlockRecord block = BlockOf(dimension.dimBlockId(), depth);
                    if (block != null) result = new DimensionAligned { Block = block };
                    break;
                }

                // OdDbAttributeDefinition derives from OdDbText: a block's tag reaches Walk as text,
                // exactly as ACadSharp's AttributeDefinition (a TextEntity) does.
                case OdDbText text:
                    result = new TextEntity { InsertPoint = Ocs(text.position(), text.normal()), Height = text.height(), Value = text.textString(), Normal = V(text.normal()) };
                    break;

                case OdDbMText mtext:
                    result = new MText { InsertPoint = P(mtext.location()), Height = mtext.textHeight(), Value = mtext.contents(), Normal = V(mtext.normal()) };
                    break;

                // ACadSharp's default case: AEC and other custom objects arrive as proxies whose
                // cached graphics are the only linework. ODA explodes those graphics into plain
                // entities; downstream, CadSource.Proxy is filtered exactly like Geometry.
                case OdDbProxyEntity proxy:
                {
                    string name = proxy.originalDxfName();
                    _proxies.TryGetValue(name, out int n);
                    _proxies[name] = n + 1;
                    var parts = new OdRxObjectPtrArray();
                    if (proxy.explodeGeometry(parts) != OdResult.eOk) yield break;
                    for (int i = 0; i < parts.Count; i++)
                    {
                        OdDbEntity part = OdDbEntity.cast(parts[i]);
                        if (part == null || depth >= MaxDepth) continue;
                        foreach (Entity copy in Translate(part, depth + 1)) yield return copy;
                    }
                    yield break;
                }
            }

            if (result == null) yield break;
            result.Layer = LayerOf(e.layer());
            result.IsInvisible = e.visibility() == OdDb_Visibility.kInvisible;
            yield return result;
        }

        private Entity ToSpline(OdDbSpline spline)
        {
            var controlPoints = new OdGePoint3dArray();
            var knots = new OdDoubleArray();
            var weights = new OdDoubleArray();
            spline.getNurbsData(out int degree, out bool rational, out bool closed, out bool periodic,
                                controlPoints, knots, weights, out _, out _);
            var copy = new Spline { Degree = degree };
            if (closed) copy.Flags |= SplineFlags.Closed;
            if (periodic) copy.Flags |= SplineFlags.Periodic;
            if (rational) copy.Flags |= SplineFlags.Rational;
            foreach (OdGePoint3d p in controlPoints) copy.ControlPoints.Add(P(p));
            foreach (double k in knots) copy.Knots.Add(k);
            foreach (double w in weights) copy.Weights.Add(w);
            for (int i = 0; i < spline.numFitPoints(); i++)
            {
                var p = new OdGePoint3d();
                if (spline.getFitPointAt(i, p) == OdResult.eOk) copy.FitPoints.Add(P(p));
            }
            return copy;
        }

        private Entity ToInsert(OdDbBlockReference reference, int depth)
        {
            BlockRecord block = BlockOf(reference.blockTableRecord(), depth);
            if (block == null) return null;

            OdGeVector3d n = reference.normal();
            OdGeScale3d scale = reference.scaleFactors();
            var insert = new Insert(block)
            {
                InsertPoint = Ocs(reference.position(), n),
                Rotation = reference.rotation(),
                XScale = scale.sx,
                YScale = scale.sy,
                Normal = V(n),
            };
            if (reference is OdDbMInsertBlock array)
            {
                insert.RowCount = array.rows();
                insert.ColumnCount = array.columns();
                insert.RowSpacing = array.rowSpacing();
                insert.ColumnSpacing = array.columnSpacing();
            }

            for (OdDbObjectIterator it = reference.attributeIterator(); !it.done(); it.step())
            {
                OdDbAttribute attribute = OdDbAttribute.cast(it.entity());
                if (attribute == null) continue;
                insert.Attributes.Add(new AttributeEntity
                {
                    InsertPoint = Ocs(attribute.position(), attribute.normal()),
                    Height = attribute.height(),
                    Value = attribute.textString(),
                    Normal = V(attribute.normal()),
                    Layer = LayerOf(attribute.layer()),
                });
            }
            return insert;
        }

        /// <summary>A block definition, translated once and shared by every insert of it.</summary>
        private BlockRecord BlockOf(OdDbObjectId id, int depth)
        {
            if (id == null || id.isNull() || depth >= MaxDepth) return null;
            OdDbBlockTableRecord record = OdDbBlockTableRecord.cast(id.openObject());
            if (record == null) return null;

            string key = id.getHandle().ascii();
            if (_blocks.TryGetValue(key, out BlockRecord done)) return done;

            string name = record.getName();
            if (string.IsNullOrEmpty(name) || _doc.BlockRecords.Contains(name)) name = "*ODA" + (++_anonymous);
            var copy = new BlockRecord(name);
            copy.BlockEntity.BasePoint = P(record.origin());
            _doc.BlockRecords.Add(copy);
            _visited.Add(key);
            _blocks[key] = copy;   // before the contents: a self-referencing block stops at MaxDepth, not here
            CopyEntities(record, e => copy.Entities.Add(e), depth + 1);
            return copy;
        }

        /// <summary>Boundary loops only, as Walk reads them: polyline loops, and line and circular-arc edges.</summary>
        private Entity ToHatch(OdDbHatch hatch)
        {
            var copy = new Hatch();
            for (int i = 0; i < hatch.numLoops(); i++)
            {
                var path = new Hatch.BoundaryPath();
                if ((hatch.loopTypeAt(i) & (int)OdDbHatch_HatchLoopType.kPolyline) != 0)
                {
                    var vertices = new OdGePoint2dArray();
                    var bulges = new OdDoubleArray();
                    hatch.getLoopAt(i, vertices, bulges);
                    var polyline = new Hatch.BoundaryPath.Polyline();
                    for (int v = 0; v < vertices.Count; v++)
                        polyline.Vertices.Add(new XYZ(vertices[v].x, vertices[v].y, v < bulges.Count ? bulges[v] : 0));
                    path.Edges.Add(polyline);
                }
                else
                {
                    var edges = new OdArray_OdGeCurve2d__p_OdObjectsAllocator();
                    hatch.getLoopAt(i, edges);
                    foreach (OdGeCurve2d edge in edges)
                    {
                        switch ((OdGe_EntityId)edge.type())
                        {
                            case OdGe_EntityId.kLineSeg2d:
                            {
                                var seg = new OdGeLineSeg2d(OdGeCurve2d.getCPtr(edge).Handle, false);
                                path.Edges.Add(new Hatch.BoundaryPath.Line { Start = XY(seg.startPoint()), End = XY(seg.endPoint()) });
                                break;
                            }
                            case OdGe_EntityId.kCircArc2d:
                            {
                                // Walk takes the two end points from centre + angle; hand over the real
                                // end points' angles so a clockwise edge lands on the same corners.
                                var arc = new OdGeCircArc2d(OdGeCurve2d.getCPtr(edge).Handle, false);
                                OdGePoint2d c = arc.center(), s = arc.startPoint(), t = arc.endPoint();
                                path.Edges.Add(new Hatch.BoundaryPath.Arc
                                {
                                    Center = XY(c),
                                    Radius = arc.radius(),
                                    StartAngle = Math.Atan2(s.y - c.y, s.x - c.x),
                                    EndAngle = Math.Atan2(t.y - c.y, t.x - c.x),
                                    CounterClockWise = true,
                                });
                                break;
                            }
                            // Ellipse and spline edges: Walk ignores them too.
                        }
                    }
                }
                copy.Paths.Add(path);
            }
            return copy;
        }

        // ───────── classes, xrefs, images ─────────

        /// <summary>
        /// Instance census over every block record (model space, layouts, block definitions),
        /// plus xref status from the block records themselves. A proxy counts under its original
        /// DXF name, the name ACadSharp's CLASSES section carries.
        /// </summary>
        private void CensusAndXrefs(List<DwgWarning> warnings)
        {
            string folder = Path.GetDirectoryName(_path);
            var table = (OdDbBlockTable)_db.getBlockTableId().safeOpenObject();
            for (var bt = table.newIterator(); !bt.done(); bt.step())
            {
                var record = (OdDbBlockTableRecord)bt.getRecordId().safeOpenObject();
                if (record.isFromExternalReference())
                {
                    string stored = record.pathName() ?? "";
                    if (record.isUnloaded())
                        warnings.Add(new DwgWarning { Kind = "xref_unloaded", Name = record.getName(), Detail = Path.GetFileName(stored) });
                    else if (!DwgDrawing.FileFound(stored, folder))
                        warnings.Add(new DwgWarning { Kind = "xref_missing", Name = record.getName(), Detail = Path.GetFileName(stored) });
                }
                if (_visited.Contains(bt.getRecordId().getHandle().ascii())) continue;   // counted during the walk
                for (OdDbObjectIterator it = record.newIterator(); !it.done(); it.step())
                {
                    OdDbEntity entity = OdDbEntity.cast(it.objectId().openObject());
                    if (entity == null) continue;
                    Count(entity);
                    entity.Dispose();
                }
            }
        }

        /// <summary>One instance under its DXF name; a proxy under its original DXF name, as ACadSharp's CLASSES section has it.</summary>
        private void Count(OdDbEntity entity)
        {
            string dxf = entity is OdDbProxyEntity proxy ? proxy.originalDxfName() : _db.classDxfName(entity.isA());
            if (string.IsNullOrEmpty(dxf)) return;
            _census.TryGetValue(dxf, out int n);
            _census[dxf] = n + 1;
        }

        private void Lap(string phase, System.Diagnostics.Stopwatch clock)
        {
            _timings[phase] = clock.Elapsed.TotalSeconds;
            clock.Restart();
        }

        /// <summary>
        /// ODA's own class registry: every runtime class the file uses (db.hasClass), with its DXF
        /// name and whether it is an entity, counted from the census. A proxy class ODA has no
        /// runtime class for still appears, from the census, so an AEC drawing without the AEC
        /// enablers is seen the way ACadSharp's CLASSES section sees it.
        /// </summary>
        private List<(string DxfName, bool IsAnEntity, int InstanceCount)> Classes()
        {
            var classes = new List<(string, bool, int)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            OdRxClass entityClass = OdDbEntity.desc();
            OdRxDictionaryIterator it = TD_RootIntegrated_Globals.odrxClassDictionary().newIterator();
            for (; !it.done(); it.next())
            {
                OdRxClass c = OdRxClass.cast(it.getObject());
                if (c == null || !_db.hasClass(c)) continue;
                string dxf = _db.classDxfName(c);
                if (string.IsNullOrEmpty(dxf) || !seen.Add(dxf)) continue;
                _census.TryGetValue(dxf, out int n);
                classes.Add((dxf, c.isDerivedFrom(entityClass), n));
            }
            foreach (KeyValuePair<string, int> proxy in _proxies)
                if (seen.Add(proxy.Key)) classes.Add((proxy.Key, true, _census.TryGetValue(proxy.Key, out int n) ? n : proxy.Value));
            return classes;
        }

        private void Images(List<DwgWarning> warnings)
        {
            var dictionary = OdDbDictionary.cast(_db.getNamedObjectsDictionaryId().safeOpenObject());
            OdDbObjectId imagesId = dictionary?.getAt("ACAD_IMAGE_DICT");
            if (imagesId == null || imagesId.isNull()) return;
            var images = OdDbDictionary.cast(imagesId.safeOpenObject());
            if (images == null) return;
            string folder = Path.GetDirectoryName(_path);
            for (OdDbDictionaryIterator it = images.newIterator(); !it.done(); it.next())
            {
                OdDbRasterImageDef image = OdDbRasterImageDef.cast(it.objectId().openObject());
                if (image != null && !DwgDrawing.FileFound(image.sourceFileName(), folder))
                    warnings.Add(new DwgWarning { Kind = "image_missing", Name = Path.GetFileName(image.sourceFileName() ?? ""), Detail = "" });
            }
        }

        // ───────── values ─────────

        private Layer LayerOf(string name)
        {
            name = string.IsNullOrEmpty(name) ? "0" : name;
            if (_layers.TryGetValue(name, out Layer layer)) return layer;
            if (!_doc.Layers.TryGetValue(name, out layer))
            {
                layer = new Layer(name);
                _doc.Layers.Add(layer);
            }
            _layers[name] = layer;
            return layer;
        }

        private static XYZ P(OdGePoint3d p) => new XYZ(p.x, p.y, p.z);
        private static XYZ V(OdGeVector3d v) => new XYZ(v.x, v.y, v.z);
        private static XY XY(OdGePoint2d p) => new XY(p.x, p.y);

        /// <summary>WCS point to the entity's OCS (AutoCAD's arbitrary-axis algorithm); identity for a +Z normal.</summary>
        internal static XYZ Ocs(OdGePoint3d p, OdGeVector3d normal) =>
            Ocs(p.x, p.y, p.z, normal.x, normal.y, normal.z);

        internal static XYZ Ocs(double x, double y, double z, double nx, double ny, double nz)
        {
            double length = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
            if (length < 1e-12) return new XYZ(x, y, z);
            nx /= length; ny /= length; nz /= length;
            if (Math.Abs(nx) < 1e-12 && Math.Abs(ny) < 1e-12 && nz > 0) return new XYZ(x, y, z);

            // Ax = (|Nx| < 1/64 and |Ny| < 1/64 ? Wy : Wz) x N
            double ax, ay, az;
            if (Math.Abs(nx) < 1.0 / 64 && Math.Abs(ny) < 1.0 / 64) { ax = nz; ay = 0; az = -nx; }   // (0,1,0) x N
            else { ax = -ny; ay = nx; az = 0; }                                                   // (0,0,1) x N
            double al = Math.Sqrt((ax * ax) + (ay * ay) + (az * az));
            ax /= al; ay /= al; az /= al;
            // Ay = N x Ax
            double bx = (ny * az) - (nz * ay), by = (nz * ax) - (nx * az), bz = (nx * ay) - (ny * ax);
            return new XYZ((x * ax) + (y * ay) + (z * az), (x * bx) + (y * by) + (z * bz), (x * nx) + (y * ny) + (z * nz));
        }
    }
}
#endif
