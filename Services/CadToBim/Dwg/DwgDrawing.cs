// AI-647: the one DWG read boundary. Every engine hands back an in-memory ACadSharp
// CadDocument (the interchange model the Cad2Bim walker already consumes), so detection,
// units and tessellation are the same code whichever engine read the file. No ODA type
// ever appears here: OdaDwgReader translates and disposes its database inside Read.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;

namespace RevitWebAppSync.Services.CadToBim.Dwg
{
    /// <summary>Opens a drawing. Engine is "oda" or "acadsharp".</summary>
    public interface IDwgReader
    {
        string Engine { get; }
        DwgDrawing Read(string path);
    }

    /// <summary>Structured read warning for the pre-flight model (AI-641).
    /// Kind: xref_missing | xref_unloaded | proxy_entity | image_missing | host_warning.</summary>
    public sealed class DwgWarning
    {
        public string Kind;
        public string Name;
        public string Detail;
        public int Count = 1;

        public override string ToString() => Kind + " " + Name + (Count > 1 ? " ×" + Count : "") + (string.IsNullOrEmpty(Detail) ? "" : ": " + Detail);
    }

    /// <summary>What one read produced.</summary>
    public sealed class DwgDrawing
    {
        public CadDocument Document;
        public string Engine = "acadsharp";

        /// <summary>CadSourceVerdict's input: (DxfName, IsAnEntity, InstanceCount) per class in the file.</summary>
        public List<(string DxfName, bool IsAnEntity, int InstanceCount)> Classes = new List<(string, bool, int)>();

        public List<DwgWarning> Warnings = new List<DwgWarning>();

        /// <summary>User-facing lines about how the file was read (e.g. the ODA fallback). Empty on a normal read.</summary>
        public List<string> Notes = new List<string>();

        /// <summary>Seconds per read phase (ODA: readFile, translate, census, images, classes); empty for ACadSharp.</summary>
        public Dictionary<string, double> Timings = new Dictionary<string, double>();

        /// <summary>An in-memory document (tests, and the ACadSharp path).</summary>
        public static DwgDrawing FromDocument(CadDocument document, string path = null, List<string> notifications = null)
        {
            var drawing = new DwgDrawing
            {
                Document = document,
                Classes = document.Classes.Select(c => (c.DxfName, c.IsAnEntity, c.InstanceCount)).ToList(),
            };
            drawing.Warnings = AcadSharpWarnings(document, path, notifications);
            return drawing;
        }

        /// <summary>
        /// Xrefs by the block record's own flags (AI-624: XRef / XRefOverlay, never the
        /// path alone), unloaded xrefs, missing xref and image files, proxy entities, and the
        /// reader's own warnings.
        /// </summary>
        internal static List<DwgWarning> AcadSharpWarnings(CadDocument document, string path, List<string> notifications)
        {
            var warnings = new List<DwgWarning>();
            string folder = string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);

            foreach (BlockRecord record in document.BlockRecords)
            {
                var flags = record.BlockEntity?.Flags ?? ACadSharp.Blocks.BlockTypeFlags.None;
                if ((flags & (ACadSharp.Blocks.BlockTypeFlags.XRef | ACadSharp.Blocks.BlockTypeFlags.XRefOverlay)) == 0) continue;
                string xrefPath = record.BlockEntity?.XRefPath ?? "";
                if (record.IsUnloaded || (record.BlockEntity?.IsUnloaded ?? false))
                    warnings.Add(new DwgWarning { Kind = "xref_unloaded", Name = record.Name, Detail = Path.GetFileName(xrefPath) });
                else if (!FileFound(xrefPath, folder))
                    warnings.Add(new DwgWarning { Kind = "xref_missing", Name = record.Name, Detail = Path.GetFileName(xrefPath) });
            }

            foreach (var image in (IEnumerable<ACadSharp.Objects.ImageDefinition>)document.ImageDefinitions ?? Enumerable.Empty<ACadSharp.Objects.ImageDefinition>())
            {
                if (!FileFound(image.FileName, folder))
                    warnings.Add(new DwgWarning { Kind = "image_missing", Name = Path.GetFileName(image.FileName ?? ""), Detail = "" });
            }

            var proxies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (BlockRecord record in document.BlockRecords)
            {
                foreach (Entity entity in record.Entities)
                {
                    if (entity is UnknownEntity || entity is ProxyEntity)
                    {
                        string name = entity.ObjectName ?? "PROXY";
                        proxies.TryGetValue(name, out int n);
                        proxies[name] = n + 1;
                    }
                }
            }
            warnings.AddRange(ProxyWarnings(proxies));

            if (notifications != null)
                warnings.AddRange(HostWarnings(notifications));
            return warnings;
        }

        internal static IEnumerable<DwgWarning> ProxyWarnings(Dictionary<string, int> byClass) =>
            byClass.OrderByDescending(kv => kv.Value)
                   .Select(kv => new DwgWarning { Kind = "proxy_entity", Name = kv.Key, Detail = "", Count = kv.Value });

        /// <summary>Repeated host messages collapse into one row with a count.</summary>
        internal static IEnumerable<DwgWarning> HostWarnings(IEnumerable<string> messages) =>
            messages.Where(m => !string.IsNullOrWhiteSpace(m))
                    .GroupBy(m => m.Trim())
                    .Select(g => new DwgWarning { Kind = "host_warning", Name = "", Detail = g.Key, Count = g.Count() });

        /// <summary>As stored, then relative to the drawing's folder, then by file name next to the drawing.</summary>
        internal static bool FileFound(string stored, string folder)
        {
            if (string.IsNullOrWhiteSpace(stored)) return false;
            try
            {
                if (Path.IsPathRooted(stored) && File.Exists(stored)) return true;
                if (folder == null) return false;
                if (File.Exists(Path.GetFullPath(Path.Combine(folder, stored)))) return true;
                return File.Exists(Path.Combine(folder, Path.GetFileName(stored)));
            }
            catch (Exception) { return false; } // a malformed stored path is "not found", never a crash
        }
    }

    /// <summary>Today's reader, unchanged: CadRenderSource's DWG/DXF read plus the warnings above.</summary>
    public sealed class AcadSharpDwgReader : IDwgReader
    {
        public string Engine => "acadsharp";

        public DwgDrawing Read(string path)
        {
            var notes = new List<string>();
            void Notify(object sender, NotificationEventArgs e)
            {
                if (e.NotificationType == NotificationType.Warning || e.NotificationType == NotificationType.Error)
                    notes.Add(e.Message);
            }

            // Disposed: an undisposed reader keeps the file locked until the GC runs.
            CadDocument document;
            if (path.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
                using (var dxf = new DxfReader(path, Notify)) document = dxf.Read();
            else
                using (var dwg = new DwgReader(path, Notify)) document = dwg.Read();
            return DwgDrawing.FromDocument(document, path, notes);
        }
    }
}
