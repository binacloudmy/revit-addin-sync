using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Persists which documents the drafter designated as carrying fire
    /// systems (the M&E scope, phase-2 design §A.4): link instance
    /// UniqueIds plus a host-included flag, stored in the model via
    /// Extensible Storage (survives sync; per-model, not per-user).
    /// Never guess which link is the M&E model — ask once, persist.
    /// Reads are safe anywhere; writes go through the ExternalEvent
    /// handler below (BombaPickStore pattern).
    /// </summary>
    public static class BombaScopeStore
    {
        private static readonly Guid SchemaGuid = new Guid("3e9d2c81-5f47-4b0a-b1c9-2a6e8f4d7c15");

        private const string FieldLinkIds = "MneLinkUniqueIds";  // '|'-joined
        private const string FieldHost = "HostIncluded";         // "1" | "0"

        public class Scope
        {
            public List<string> LinkUniqueIds = new List<string>();
            public bool HostIncluded;
        }

        private static Schema GetOrCreateSchema()
        {
            var existing = Schema.Lookup(SchemaGuid);
            if (existing != null) return existing;
            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName("BinaBombaMneScope");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(FieldLinkIds, typeof(string));
            builder.AddSimpleField(FieldHost, typeof(string));
            return builder.Finish();
        }

        /// Stored scope, or null when the drafter never designated one —
        /// which is a real state (scan answers NOT CHECKED), not a default.
        public static Scope Read(Document doc)
        {
            if (doc == null) return null;
            try
            {
                var schema = Schema.Lookup(SchemaGuid);
                if (schema == null) return null;
                var entity = doc.ProjectInformation.GetEntity(schema);
                if (entity == null || !entity.IsValid()) return null;
                var ids = entity.Get<string>(FieldLinkIds) ?? "";
                var host = entity.Get<string>(FieldHost) == "1";
                var scope = new Scope { HostIncluded = host };
                scope.LinkUniqueIds = ids.Split('|').Where(s => !string.IsNullOrEmpty(s)).ToList();
                if (!scope.HostIncluded && scope.LinkUniqueIds.Count == 0) return null;
                return scope;
            }
            catch { return null; }
        }

        internal static void WriteInContext(Document doc, Scope scope)
        {
            var schema = GetOrCreateSchema();
            using (var tx = new Transaction(doc, "BINA: store M&E scope"))
            {
                tx.Start();
                var entity = new Entity(schema);
                entity.Set(FieldLinkIds, string.Join("|", scope.LinkUniqueIds.ToArray()));
                entity.Set(FieldHost, scope.HostIncluded ? "1" : "0");
                doc.ProjectInformation.SetEntity(entity);
                tx.Commit();
            }
        }
    }

    /// <summary>
    /// ExternalEvent handler carrying pending scope writes into API context.
    /// Failure is logged, never fatal — the pane's in-session scope still
    /// drives the current scan; only persistence is lost.
    /// </summary>
    public class BombaScopeWriteHandler : IExternalEventHandler
    {
        public BombaScopeStore.Scope Pending;

        public void Execute(UIApplication app)
        {
            var scope = Pending;
            Pending = null;
            var doc = app.ActiveUIDocument != null ? app.ActiveUIDocument.Document : null;
            if (scope == null || doc == null) return;
            try { BombaScopeStore.WriteInContext(doc, scope); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[BINA] bomba scope persist failed: " + ex.Message);
            }
        }

        public string GetName() { return "BINA Bomba M&E scope store"; }
    }
}
