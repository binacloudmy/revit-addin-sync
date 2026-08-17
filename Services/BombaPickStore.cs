using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Persists the drafter's building-type decision in the model itself via
    /// Extensible Storage: survives Revit restarts and syncs to teammates
    /// with the .rvt. The provenance tag ("auto" vs "your pick") is stored
    /// beside the value and must never be silently lost — a later scan may
    /// re-read room names but must not overwrite a human's assertion.
    /// Reads are safe anywhere; WRITES need API context, so they go through
    /// the ExternalEvent handler below (JkrRenameHandler pattern).
    /// </summary>
    public static class BombaPickStore
    {
        private static readonly Guid SchemaGuid = new Guid("7b1f4a2e-0c3d-4e5f-9a6b-8d7c6b5a4f30");

        private const string FieldPath = "PurposeGroupPath";
        private const string FieldLabel = "PurposeGroupLabel";
        private const string FieldTag = "ProvenanceTag";   // "auto" | "your pick"

        public class Pick
        {
            public string Path;
            public string Label;
            public string Tag;
        }

        private static Schema GetOrCreateSchema()
        {
            var existing = Schema.Lookup(SchemaGuid);
            if (existing != null) return existing;
            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName("BinaBombaPurposeGroup");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(FieldPath, typeof(string));
            builder.AddSimpleField(FieldLabel, typeof(string));
            builder.AddSimpleField(FieldTag, typeof(string));
            return builder.Finish();
        }

        /// Read the stored pick from the document, or null when never stored.
        /// Safe outside API context (no transaction).
        public static Pick Read(Document doc)
        {
            if (doc == null) return null;
            try
            {
                var schema = Schema.Lookup(SchemaGuid);
                if (schema == null) return null;
                var entity = doc.ProjectInformation.GetEntity(schema);
                if (entity == null || !entity.IsValid()) return null;
                var path = entity.Get<string>(FieldPath);
                if (string.IsNullOrEmpty(path)) return null;
                return new Pick
                {
                    Path = path,
                    Label = entity.Get<string>(FieldLabel),
                    Tag = entity.Get<string>(FieldTag),
                };
            }
            catch { return null; }
        }

        /// Write inside an already-open API context (the ExternalEvent
        /// handler calls this). Never call from a pane click directly.
        internal static void WriteInContext(Document doc, Pick pick)
        {
            var schema = GetOrCreateSchema();
            using (var tx = new Transaction(doc, "BINA: store building type"))
            {
                tx.Start();
                var entity = new Entity(schema);
                entity.Set(FieldPath, pick.Path ?? "");
                entity.Set(FieldLabel, pick.Label ?? "");
                entity.Set(FieldTag, pick.Tag ?? "");
                doc.ProjectInformation.SetEntity(entity);
                tx.Commit();
            }
        }
    }

    /// <summary>
    /// ExternalEvent handler carrying pending building-type writes into API
    /// context. Registered in App alongside JkrRenameHandler; the pane sets
    /// Pending and raises the event — failure is logged, never fatal (the
    /// in-session dictionaries still hold the pick; only persistence is lost).
    /// </summary>
    public class BombaPickWriteHandler : IExternalEventHandler
    {
        public BombaPickStore.Pick Pending;

        public void Execute(UIApplication app)
        {
            var pick = Pending;
            Pending = null;
            var doc = app.ActiveUIDocument != null ? app.ActiveUIDocument.Document : null;
            if (pick == null || doc == null) return;
            try { BombaPickStore.WriteInContext(doc, pick); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[BINA] bomba pick persist failed: " + ex.Message);
            }
        }

        public string GetName() { return "BINA Bomba pick store"; }
    }
}
