#nullable enable
// ImportDialogGuard: the one dialog a DWG read answers, and only inside its scope. The dialog
// text is Revit's own; the file name in it is invented.

using System;
using System.Collections.Generic;
using BinaVibe.Mcp.Tools;
using Xunit;

namespace Tests
{
    public class ImportDialogGuardTests
    {
        public ImportDialogGuardTests()
        {
            ImportDialogGuard.WriteLog = false;
        }

        private const string Extents =
            "Geometry in the file C:\\drawings\\Level 1.dwg has extents greater than 1E9. " +
            "Data exceeding that range will be truncated. Press OK to continue or Cancel to abort.";

        [Fact]
        public void The_extents_dialog_is_answered_ok()
        {
            Assert.Equal(1, ImportDialogGuard.AnswerFor("TaskDialog_Import_Extents", Extents));
            Assert.Equal(1, ImportDialogGuard.AnswerFor(null, Extents.ToUpperInvariant()));

            var answered = new List<int>();
            using (ImportDialogGuard guard = ImportDialogGuard.Begin())
            {
                Assert.True(ImportDialogGuard.OnDialog("some_id", Extents, r => { answered.Add(r); return true; }));
                Assert.Single(guard.Seen);
                Assert.StartsWith("some_id | Geometry", guard.Seen[0], StringComparison.Ordinal);
            }
            Assert.Equal(new[] { 1 }, answered);
        }

        [Fact]
        public void Answered_lists_only_the_dialogs_the_scope_answered()
        {
            using (ImportDialogGuard guard = ImportDialogGuard.Begin())
            {
                ImportDialogGuard.OnDialog("id", "Save changes to the project?", r => true);
                ImportDialogGuard.OnDialog("id", Extents, r => true);
                Assert.Equal(2, guard.Seen.Count);
                Assert.Equal(new[] { Extents }, guard.Answered);
            }

            // An answer Revit refused is not reported as answered.
            using (ImportDialogGuard guard = ImportDialogGuard.Begin())
            {
                Assert.False(ImportDialogGuard.OnDialog("id", Extents, r => false));
                Assert.Empty(guard.Answered);
            }
        }

        [Fact]
        public void A_nested_scope_hands_back_to_the_outer_one()
        {
            using (ImportDialogGuard outer = ImportDialogGuard.Begin())
            {
                using (ImportDialogGuard inner = ImportDialogGuard.Begin())
                {
                    ImportDialogGuard.OnDialog("id", Extents, r => true);
                    Assert.Single(inner.Answered);
                }
                Assert.Empty(outer.Answered);
                ImportDialogGuard.OnDialog("id", Extents, r => true);
                Assert.Single(outer.Answered);
            }
        }

        [Fact]
        public void Any_other_dialog_is_left_alone()
        {
            Assert.Null(ImportDialogGuard.AnswerFor("id", "Imported model is out of range."));
            Assert.Null(ImportDialogGuard.AnswerFor("id", "Data will be truncated."));
            Assert.Null(ImportDialogGuard.AnswerFor("id", ""));
            Assert.Null(ImportDialogGuard.AnswerFor("id", null));

            var answered = new List<int>();
            Func<int, bool> answer = r => { answered.Add(r); return true; };
            using (ImportDialogGuard guard = ImportDialogGuard.Begin())
            {
                Assert.False(ImportDialogGuard.OnDialog("id", "Save changes to the project?", answer));
                Assert.Single(guard.Seen);
            }

            // Outside a scope even the extents dialog is the user's.
            Assert.False(ImportDialogGuard.OnDialog("id", Extents, answer));
            Assert.Empty(answered);
        }
    }
}
