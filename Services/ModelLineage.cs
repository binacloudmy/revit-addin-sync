using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Stable identity for a Revit model, stored inside the document itself
    /// (ClickUp 86d3x42mz).
    ///
    /// Without this, lineage is the filename: rename Block-A.rvt to
    /// BlockA-Rev2.rvt and the server starts a fresh chain at v1 while the old
    /// row stays active. A GUID stamped into the model survives renames and
    /// moves.
    ///
    /// The catch is that ExtensibleStorage travels with the document, so
    /// SaveAs-ing a copy carries the GUID into what is now a different building.
    /// Syncing that copy would file it as the next version of the original and
    /// deactivate it. To catch that we also record the path the GUID was stamped
    /// at: when the current path differs AND the filename differs, the user is
    /// asked whether this is a new model or a new version of the old one.
    /// A pure folder move changes the path but not the name, and is not worth a
    /// question.
    /// </summary>
    public static class ModelLineage
    {
        // Schema GUID must never change once shipped — it is how the field is
        // found in models stamped by earlier builds.
        private static readonly Guid SchemaGuid = new Guid("6f2b1c84-9d37-4f5a-9b21-0c7f2a5d81e3");
        private const string SchemaName = "BinaSync";
        private const string FieldLineageId = "LineageId";
        private const string FieldOriginPath = "OriginPath";

        private static Schema GetOrCreateSchema()
        {
            var existing = Schema.Lookup(SchemaGuid);
            if (existing != null) return existing;

            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName(SchemaName);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(FieldLineageId, typeof(string));
            builder.AddSimpleField(FieldOriginPath, typeof(string));
            return builder.Finish();
        }

        /// <summary>The element the schema entity is attached to: the ProjectInfo singleton.</summary>
        private static Element GetHost(Document doc) => doc?.ProjectInformation;

        public sealed class LineageStamp
        {
            public string LineageId { get; set; }
            public string OriginPath { get; set; }
        }

        /// <summary>
        /// Reads the stamp, distinguishing "this model carries none" from "the
        /// stamp could not be read".
        ///
        /// The difference decides whether a caller may mint a fresh id. Minting
        /// on a failed read gives the SAME document a second GUID over its life,
        /// which forks `lineageKey` server-side and scatters one model's history
        /// across chains. A read that failed must therefore send no GUID at all,
        /// not a new one — the server then inherits the head's.
        /// </summary>
        /// <returns>False when the read itself failed.</returns>
        public static bool TryRead(Document doc, out LineageStamp stamp)
        {
            try
            {
                var host = GetHost(doc);
                if (host == null) { stamp = null; return false; }

                var schema = Schema.Lookup(SchemaGuid);
                if (schema == null) { stamp = null; return true; }   // never stamped

                var entity = host.GetEntity(schema);
                if (entity == null || !entity.IsValid()) { stamp = null; return true; }

                stamp = new LineageStamp
                {
                    LineageId = entity.Get<string>(schema.GetField(FieldLineageId)),
                    OriginPath = entity.Get<string>(schema.GetField(FieldOriginPath))
                };
                return true;
            }
            catch
            {
                stamp = null;
                return false;
            }
        }

        /// <summary>Reads the stamp, or null when this model has never been synced.</summary>
        public static LineageStamp Read(Document doc)
        {
            try
            {
                var host = GetHost(doc);
                if (host == null) return null;

                var schema = Schema.Lookup(SchemaGuid);
                if (schema == null) return null;

                var entity = host.GetEntity(schema);
                if (entity == null || !entity.IsValid()) return null;

                return new LineageStamp
                {
                    LineageId = entity.Get<string>(schema.GetField(FieldLineageId)),
                    OriginPath = entity.Get<string>(schema.GetField(FieldOriginPath))
                };
            }
            catch
            {
                // A model that cannot carry the stamp still syncs — it just falls
                // back to filename lineage server-side.
                return null;
            }
        }

        /// <summary>
        /// Writes the stamp. MUST be called inside a Revit transaction, on the UI
        /// thread — the caller owns both.
        /// </summary>
        public static void Write(Document doc, string lineageId, string originPath)
        {
            var host = GetHost(doc);
            if (host == null) throw new InvalidOperationException("Document has no ProjectInformation element.");

            var schema = GetOrCreateSchema();
            var entity = new Entity(schema);
            entity.Set(schema.GetField(FieldLineageId), lineageId ?? "");
            entity.Set(schema.GetField(FieldOriginPath), originPath ?? "");
            host.SetEntity(entity);
        }

        public static string NewLineageId() => Guid.NewGuid().ToString("N");

        /// <summary>
        /// True when the stamp looks inherited from a SaveAs copy rather than
        /// belonging to this model: the file now lives at a different path AND
        /// under a different name. A folder move alone does not qualify.
        /// </summary>
        public static bool LooksLikeCopy(LineageStamp stamp, string currentPath)
        {
            if (stamp == null || string.IsNullOrEmpty(stamp.OriginPath)) return false;
            if (string.IsNullOrEmpty(currentPath)) return false;
            if (string.Equals(stamp.OriginPath, currentPath, StringComparison.OrdinalIgnoreCase)) return false;

            string originName = System.IO.Path.GetFileName(stamp.OriginPath);
            string currentName = System.IO.Path.GetFileName(currentPath);
            return !string.Equals(originName, currentName, StringComparison.OrdinalIgnoreCase);
        }
    }
}
