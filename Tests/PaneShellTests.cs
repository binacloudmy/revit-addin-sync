using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// Step 7: the pane shell — which body state shows, the BM / EN switch, the
    /// project label, and the Binazone tokens. The XAML itself is checked in Revit.
    /// </summary>
    public class PaneShellTests
    {
        private static readonly ActiveModelInfo Project =
            new ActiveModelInfo("Copy of jkrAR24_5a", @"C:\Users\x\Downloads\Copy of jkrAR24_5a.rvt", false);

        // ── body state ──

        [Fact]
        public void Without_a_model_the_shell_shows_no_model_open()
        {
            var m = new PaneShellModel();
            Assert.Equal(ShellState.NoModel, m.State);
            Assert.True(m.ShowNoModel);
            Assert.False(m.ShowContent);
            Assert.Equal("—", m.FileLabel);
        }

        [Fact]
        public void A_project_shows_the_audit_screens_and_its_file_name()
        {
            var m = new PaneShellModel { Model = Project };
            Assert.Equal(ShellState.Content, m.State);
            Assert.Equal("Copy of jkrAR24_5a.rvt", m.FileLabel);
        }

        [Theory]
        [InlineData("Door - Single", @"C:\lib\Door - Single.rfa", true)]
        [InlineData("JKR AR template", @"C:\lib\JKR AR template.rte", false)]
        public void Family_files_and_templates_cannot_be_audited(string title, string path, bool isFamily)
        {
            var m = new PaneShellModel { Model = new ActiveModelInfo(title, path, isFamily) };
            Assert.Equal(ShellState.NoModel, m.State);
            Assert.Equal("—", m.FileLabel);
        }

        [Fact]
        public void An_unsaved_project_shows_its_title()
        {
            Assert.Equal("Project1", new ActiveModelInfo("Project1", "", false).FileLabel);
            Assert.Equal("Project1", new PaneShellModel { Model = new ActiveModelInfo("Project1", "", false) }.FileLabel);
        }

        [Fact]
        public void Login_required_shows_only_when_a_model_is_open()
        {
            var m = new PaneShellModel { LoginRequired = true };
            Assert.Equal(ShellState.NoModel, m.State);          // nothing to sign in for yet
            m.Model = Project;
            Assert.Equal(ShellState.LoginRequired, m.State);
            Assert.True(m.ShowLogin);
            m.LoginRequired = false;                             // the next scan succeeded
            Assert.Equal(ShellState.Content, m.State);
        }

        // ── BM / EN ──

        [Fact]
        public void Bm_is_the_default_and_en_switches_every_shell_string()
        {
            var m = new PaneShellModel { Model = Project };
            Assert.True(m.IsBm);
            Assert.Equal("Projek", m.ProjectLabel);
            Assert.Equal("Tiada model dibuka", m.NoModelTitle);
            Assert.Equal("JKR Compliance", m.PanelTitle);

            m.IsEn = true;
            Assert.True(m.IsEn);
            Assert.False(m.IsBm);
            Assert.Equal("Project", m.ProjectLabel);
            Assert.Equal("No model open", m.NoModelTitle);
            Assert.Equal("Open an AR model in Revit to audit it. Family files and templates can’t be audited.", m.NoModelText);
            Assert.Equal("Sign-in required", m.LoginTitle);
            Assert.Equal("Close", m.CloseLabel);
        }

        [Fact]
        public void Switching_language_notifies_bindings_and_unchecking_does_nothing()
        {
            var m = new PaneShellModel();
            var raised = 0;
            m.PropertyChanged += (_, __) => raised++;
            m.IsEn = true;
            Assert.Equal(1, raised);
            m.IsBm = false;                                     // the other radio unchecking
            Assert.True(m.IsEn);
            m.SetLang("EN");                                    // same language: no change
            Assert.Equal(1, raised);
        }

        // ── active model source ──

        [Fact]
        public void The_active_model_source_raises_only_on_a_real_change()
        {
            var seen = new List<ActiveModelInfo>();
            void Handler(ActiveModelInfo i) => seen.Add(i);
            ActiveModelSource.Set(ActiveModelInfo.None);
            ActiveModelSource.Changed += Handler;
            try
            {
                ActiveModelSource.Set(Project);
                ActiveModelSource.Set(new ActiveModelInfo(Project.Title, Project.PathName, false));
                ActiveModelSource.Set(null);
                Assert.Equal(2, seen.Count);
                Assert.False(seen[1].IsOpen);
            }
            finally
            {
                ActiveModelSource.Changed -= Handler;
                ActiveModelSource.Set(ActiveModelInfo.None);
            }
        }

        // ── tokens ──

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            Assert.True(dir != null, "repo root not found");
            return dir.FullName;
        }

        private static Dictionary<string, string> Brushes()
        {
            var doc = XDocument.Load(Path.Combine(RepoRoot(), "UI", "Jkr", "Panel", "PanelTokens.xaml"));
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            return doc.Root.Elements()
                .Where(e => e.Name.LocalName == "SolidColorBrush")
                .ToDictionary(e => (string)e.Attribute(x + "Key"), e => ((string)e.Attribute("Color")).ToUpperInvariant());
        }

        [Theory]
        [InlineData("Bz.Brand", "#F2C200")]
        [InlineData("Bz.OnBrand", "#0F0F0F")]
        [InlineData("Bz.Ink", "#0F0F0F")]
        [InlineData("Bz.Chrome", "#0B0F1A")]
        [InlineData("Bz.Chrome2", "#161C2B")]
        [InlineData("Bz.Text", "#161616")]
        [InlineData("Bz.Text2", "#4B4F57")]
        [InlineData("Bz.Text3", "#686C74")]
        [InlineData("Bz.Line", "#E7E6E2")]
        [InlineData("Bz.Line2", "#D6D4CE")]
        [InlineData("Bz.Sunken", "#F5F5F3")]
        [InlineData("Bz.BrandTint", "#FEF8DC")]
        [InlineData("Bz.High", "#C23B3B")]
        [InlineData("Bz.HighText", "#B42F2F")]
        [InlineData("Bz.Med", "#EA580C")]
        [InlineData("Bz.MedText", "#B83D0B")]
        [InlineData("Bz.Low", "#6B7079")]
        [InlineData("Bz.Manual", "#4A4EB0")]
        [InlineData("Bz.ManualBg", "#F2F2FC")]
        [InlineData("Bz.Ok", "#2E7D4F")]
        public void Tokens_are_the_briefs_values(string key, string hex)
        {
            Assert.Equal(hex, Brushes()[key]);
        }

        [Fact]
        public void The_shell_is_380_wide_and_the_pane_minimum_is_380()
        {
            var shell = File.ReadAllText(Path.Combine(RepoRoot(), "UI", "Jkr", "Panel", "JkrPaneShell.xaml"));
            Assert.Contains("x:Name=\"Frame\" Width=\"380\"", shell);
            var host = File.ReadAllText(Path.Combine(RepoRoot(), "UI", "JkrComplianceDashboardHost.cs"));
            Assert.Contains("MinimumWidth = (int)Jkr.Panel.JkrPaneShell.PaneWidth", host);
        }
    }
}
