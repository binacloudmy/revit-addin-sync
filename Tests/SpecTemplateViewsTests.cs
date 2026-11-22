using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.Services.JkrSpec;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>C1.1.1 / C1.1.2 against the embedded JKR AR template view list (Linear AI-482).</summary>
    public class SpecTemplateViewsTests
    {
        private static List<SpecResult> Run(string id, SpecModel m) => SpecChecks.Rules[id](m.Normalize()).ToList();

        private static SpecModel Project(params string[] templates)
        {
            var m = new SpecModel();
            m.Views.Add(new SpecView { Id = 1, Name = "Aras 01", Kind = "FloorPlan" });
            m.ViewTemplates.AddRange(templates);
            return m;
        }

        private static SpecModel FromJkrTemplate() => Project(SpecTemplateViews.ViewTemplates.Select(r => r.Name).ToArray());

        [Fact]
        public void The_list_is_embedded_with_the_exported_counts()
        {
            Assert.Equal(216, SpecTemplateViews.All.Count);
            Assert.Equal(80, SpecTemplateViews.ViewTemplates.Count());          // is_view_template = true
            Assert.Equal(67, SpecTemplateViews.Judged.Count);                    // "jkrAR_…"
            Assert.Equal(13, SpecTemplateViews.NotJudged.Count);                 // Revit default names
            Assert.Equal(80, SpecTemplateViews.ViewTemplates.Select(r => r.Name).Distinct().Count());
            Assert.Contains("jkrAR_01w_WIP_(Pelan Siling)", SpecTemplateViews.Judged);
            Assert.Contains("Architectural Plan", SpecTemplateViews.NotJudged);
            Assert.DoesNotContain("Aras 01 (01w_WIP)", SpecTemplateViews.Judged);   // an ordinary view, not a template
        }

        [Fact]
        public void The_resource_is_embedded_under_its_logical_name()
        {
            var names = typeof(SpecTemplateViews).Assembly.GetManifestResourceNames();
            Assert.Contains(names, n => n.EndsWith(SpecTemplateViews.ResourceName));
        }

        [Fact]
        public void Parse_reads_quoted_commas_and_quotes()
        {
            var rows = SpecTemplateViews.Parse("\"name\",\"view_type\",\"is_view_template\"\n\"A, \"\"B\"\"\",\"Elevation\",\"true\"\r\n\"C\",\"Legend\",\"false\"\n");
            Assert.Equal(2, rows.Count);
            Assert.Equal("A, \"B\"", rows[0].Name);
            Assert.True(rows[0].IsViewTemplate);
            Assert.False(rows[1].IsViewTemplate);
        }

        [Fact]
        public void C1_1_1_passes_when_every_template_view_is_there()
        {
            var r = Assert.Single(Run("C1.1.1", FromJkrTemplate()));
            Assert.Equal("pass", r.Status);
            Assert.Equal(SpecCoverage.Full, r.Coverage);
            Assert.Contains("Semua 67 view template JKR AR wujud", r.Note);
            Assert.Contains("13 nama default Revit", r.Note);
        }

        [Fact]
        public void C1_1_1_does_not_need_the_revit_default_names()
        {
            Assert.Equal("pass", Assert.Single(Run("C1.1.1", Project(SpecTemplateViews.Judged.ToArray()))).Status);
        }

        [Fact]
        public void C1_1_1_fails_one_per_missing_template_and_lists_unmatched_project_templates()
        {
            var all = SpecTemplateViews.Judged;
            var m = Project(all.Skip(2).Concat(new[] { "Pelan Saya Sendiri" }).ToArray());
            var rs = Run("C1.1.1", m);
            Assert.Equal(2, rs.Count);
            Assert.All(rs, r => Assert.Equal("fail", r.Status));
            Assert.Equal(all.Take(2), rs.Select(r => r.ElementName));
            Assert.Contains("2 daripada 67 tiada", rs[0].Note);
            Assert.Contains("Pelan Saya Sendiri", rs[0].Note);
            Assert.Contains("possibly renamed", rs[0].NoteEn);
            Assert.Equal(2, rs.Select(r => r.IssueId).Distinct().Count());
        }

        [Fact]
        public void C1_1_1_counts_a_cosmetic_rename_as_present()
        {
            var names = SpecTemplateViews.Judged.ToList();
            names[0] = names[0].ToLowerInvariant().Replace("_", " ");
            Assert.Equal("pass", Assert.Single(Run("C1.1.1", Project(names.ToArray()))).Status);
        }

        [Fact]
        public void C1_1_1_unread_or_empty_is_unjudged_never_a_pass()
        {
            var empty = Assert.Single(Run("C1.1.1", new SpecModel()));
            Assert.Equal("cannot_verify", empty.Status);
            var noTemplates = Assert.Single(Run("C1.1.1", Project()));
            Assert.Equal("cannot_verify", noTemplates.Status);
            var err = Project("x");
            err.ReadErrors.Add("Views: access denied");
            var r = Assert.Single(Run("C1.1.1", err));
            Assert.Equal("cannot_verify", r.Status);
            Assert.Contains("access denied", r.Note);
        }

        [Fact]
        public void C1_1_2_passes_when_no_name_changed()
        {
            var r = Assert.Single(Run("C1.1.2", FromJkrTemplate()));
            Assert.Equal("pass", r.Status);
            Assert.Contains("67 view template JKR AR kekal nama asal", r.Note);
        }

        [Fact]
        public void C1_1_2_a_renamed_template_is_a_warning_that_names_both_not_a_fail()
        {
            var names = SpecTemplateViews.Judged.ToList();
            var original = names[0];
            names[0] = original.Replace("jkrAR_", "JKRAR ");   // same name apart from case and punctuation
            var rs = Run("C1.1.2", Project(names.ToArray()));
            var w = Assert.Single(rs);
            Assert.Equal("warning", w.Status);
            Assert.Equal(SpecCoverage.Full, w.Coverage);
            Assert.False(string.IsNullOrEmpty(w.IssueId));
            Assert.Contains(original, w.Note);
            Assert.Contains(names[0], w.Note);
            Assert.Contains("tidak disahkan oleh dokumen JKR", w.Note);
            Assert.Contains("not confirmed by a JKR document", w.NoteEn);
            // C1.1.1 still counts it as present
            Assert.Equal("pass", Assert.Single(Run("C1.1.1", Project(names.ToArray()))).Status);
        }

        [Fact]
        public void C1_1_2_a_warning_scores_as_a_pass_with_an_issue()
        {
            var names = SpecTemplateViews.Judged.ToList();
            names[0] = names[0].ToUpperInvariant();
            var results = Run("C1.1.2", Project(names.ToArray()));
            foreach (var r in results) r.Source = SpecReport.Model;
            var st = SpecReport.Build(results).For("C1.1.2");
            Assert.Equal(0, st.Failed);
            Assert.Single(st.IssueIds);
        }

        [Fact]
        public void C1_1_2_missing_templates_are_not_called_renamed()
        {
            var rs = Run("C1.1.2", Project(SpecTemplateViews.Judged.Skip(3).ToArray()));
            var r = Assert.Single(rs);
            Assert.Equal("pass", r.Status);
            Assert.Contains("3 tiada (lihat C1.1.1)", r.Note);
        }

        [Fact]
        public void C1_1_2_unread_is_unjudged()
        {
            Assert.Equal("cannot_verify", Assert.Single(Run("C1.1.2", new SpecModel())).Status);
            Assert.Equal("cannot_verify", Assert.Single(Run("C1.1.2", Project())).Status);
        }

        [Fact]
        public void The_two_checks_are_no_longer_backend_only()
        {
            Assert.False(SpecChecks.BackendOnly.ContainsKey("C1.1.1"));
            Assert.False(SpecChecks.BackendOnly.ContainsKey("C1.1.2"));
        }
    }
}
