using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Remembers that the open model is a restored version, until the sync that
    /// publishes it (ClickUp 86d3ut47q).
    ///
    /// Rollback is append-only: restoring v3 while the cloud is at v7 does not
    /// touch v4-v7. It publishes a NEW v8 whose content is v3's, and the server
    /// labels v8 with `rolledBackFromDesignId`. But the server only hears about
    /// the rollback on that next sync — the restore itself is local, and the user
    /// may edit for a week first, or abandon the model entirely.
    ///
    /// So the marker has to survive being closed and reopened, which rules out
    /// holding it in memory. It lives in the .rvt because that is what the fact
    /// is about: these bytes came from v3. Copy the file to another machine and
    /// the statement is still true.
    ///
    /// SEPARATE SCHEMA, NOT A FIELD ON BinaSync
    /// ----------------------------------------
    /// ExtensibleStorage schemas are immutable once a document carries one:
    /// Schema.Lookup returns what the model was stamped with, and re-declaring
    /// the same GUID with an extra field throws. Adding a third field to
    /// ModelLineage's BinaSync schema would therefore break every model any
    /// shipped build has already stamped. A second schema with its own GUID is
    /// the pattern the repo already uses for this — see BombaPickStore.
    /// </summary>
    public static class RollbackMarkerStore
    {
        // Never change once shipped: it is how the marker is found in models
        // stamped by earlier builds.
        private static readonly Guid SchemaGuid = new Guid("c4a7e916-3b28-4d5f-8e70-1f9a6c3b5d24");
        private const string SchemaName = "BinaRollback";
        private const string FieldFromDesignId = "RolledBackFromDesignId";
        private const string FieldFromVersion = "RolledBackFromVersion";

        private static Schema GetOrCreateSchema()
        {
            var existing = Schema.Lookup(SchemaGuid);
            if (existing != null) return existing;

            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName(SchemaName);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(FieldFromDesignId, typeof(int));
            // The version number is display only — "restored from V3" in the sync
            // dialog. The server is told the design id and resolves the number
            // itself, so a stale value here can mislead a human but cannot
            // mislabel a version.
            builder.AddSimpleField(FieldFromVersion, typeof(int));
            return builder.Finish();
        }

        private static Element GetHost(Document doc)
        {
            return doc != null ? doc.ProjectInformation : null;
        }

        public sealed class Marker
        {
            public int FromDesignId { get; set; }
            public int FromVersion { get; set; }
        }

        /// <summary>
        /// The pending rollback, or null when this model is not a restore — which
        /// is the normal case for every model that has never been rolled back.
        /// </summary>
        public static Marker Read(Document doc)
        {
            try
            {
                var host = GetHost(doc);
                if (host == null) return null;

                var schema = Schema.Lookup(SchemaGuid);
                if (schema == null) return null;

                var entity = host.GetEntity(schema);
                if (entity == null || !entity.IsValid()) return null;

                int fromDesignId = entity.Get<int>(schema.GetField(FieldFromDesignId));

                // Cleared markers are written as 0 rather than deleted — see Clear.
                if (fromDesignId <= 0) return null;

                return new Marker
                {
                    FromDesignId = fromDesignId,
                    FromVersion = entity.Get<int>(schema.GetField(FieldFromVersion))
                };
            }
            catch
            {
                // A model that cannot carry the marker still syncs; it just syncs
                // as an ordinary version rather than a labelled rollback. Losing
                // a label is not worth failing a sync over.
                return null;
            }
        }

        /// <summary>
        /// Records that this model's bytes came from <paramref name="fromDesignId"/>.
        /// Opens its own transaction, so it MUST run on the Revit API thread —
        /// the rollback handler calls it while swapping documents.
        /// </summary>
        public static void Write(Document doc, int fromDesignId, int fromVersion)
        {
            var host = GetHost(doc);
            if (host == null)
                throw new InvalidOperationException("Document has no ProjectInformation element.");

            var schema = GetOrCreateSchema();

            using (var tx = new Transaction(doc, "BINA: mark restored version"))
            {
                tx.Start();
                var entity = new Entity(schema);
                entity.Set(schema.GetField(FieldFromDesignId), fromDesignId);
                entity.Set(schema.GetField(FieldFromVersion), fromVersion);
                host.SetEntity(entity);
                tx.Commit();
            }
        }

        /// <summary>
        /// Drops the marker once the rollback has been published, so the version
        /// after v8 is an ordinary version rather than a second labelled rollback.
        ///
        /// Writes zeroes instead of deleting the entity: DeleteEntity on a schema
        /// the model may not carry is another failure path, and Read already
        /// treats a non-positive id as absent.
        /// </summary>
        public static void Clear(Document doc)
        {
            try
            {
                var host = GetHost(doc);
                if (host == null) return;

                var schema = Schema.Lookup(SchemaGuid);
                if (schema == null) return;

                using (var tx = new Transaction(doc, "BINA: clear restored marker"))
                {
                    tx.Start();
                    var entity = new Entity(schema);
                    entity.Set(schema.GetField(FieldFromDesignId), 0);
                    entity.Set(schema.GetField(FieldFromVersion), 0);
                    host.SetEntity(entity);
                    tx.Commit();
                }
            }
            catch
            {
                // A marker we failed to clear costs one mislabelled version in the
                // trail. Failing the sync that just succeeded costs the user their
                // upload. Swallow it.
            }
        }
    }
}
