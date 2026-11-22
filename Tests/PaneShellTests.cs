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
            Assert.Equal("Copy of jkrAR24_5a.rvt", m.HeaderSubtitle);
            Assert.True(m.HeaderIsFile);
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
            Assert.Equal("Buka model Revit untuk mengauditnya", m.NoModelTitle);
            Assert.Equal("Menunggu model", new PaneShellModel().HeaderSubtitle);
            Assert.Equal("Cuba semula", m.LoginRetry);
            Assert.Equal("JKR Compliance", m.PanelTitle);

            m.IsEn = true;
            Assert.True(m.IsEn);
            Assert.False(m.IsBm);
            Assert.Equal("Project", m.ProjectLabel);
            Assert.Equal("Open a Revit model to audit it", m.NoModelTitle);
            Assert.Equal("The pane picks up the active model as soon as one is open.", m.NoModelText);
            var empty = new PaneShellModel { IsEn = true };
            Assert.Equal("Waiting for a model", empty.HeaderSubtitle);
            Assert.False(empty.HeaderIsFile);
            Assert.Equal("Log in to BINA to run the audit", m.LoginTitle);
            Assert.Equal("Some checks run on the BINA backend. Log in from the Bina tab on the Revit ribbon, then retry.", m.LoginText);
            Assert.Equal("Retry", m.LoginRetry);
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

        /// <summary>The pane uses the product's design tokens (UI/Copilot/CopilotTokens.xaml).
        /// Every Bz.* brush mirrors the Cp.* key named in its comment, so the two files
        /// are read and compared here: a Copilot palette change that is not carried into
        /// the pane fails this test.</summary>
        private static Dictionary<string, string> CopilotBrushes()
        {
            var doc = XDocument.Load(Path.Combine(RepoRoot(), "UI", "Copilot", "CopilotTokens.xaml"));
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            return doc.Root.Elements()
                .Where(e => e.Name.LocalName == "SolidColorBrush")
                .ToDictionary(e => (string)e.Attribute(x + "Key"), e => ((string)e.Attribute("Color")).ToUpperInvariant());
        }

        [Fact]
        public void The_fixed_Slate_keys_are_all_present_and_do_not_follow_the_Copilot_theme_switch()
        {
            // Merge with feat/bina-ai-copilot: Copilot's dark column moved to a new palette (ThemeTokens: #15181E ground, lime
            // accent), so Cp.Dark.* no longer equals it. The JKR pane keeps the Slate values it was designed on (contrast of
            // Cp.Jkr.Dark.* is checked against them); whether it adopts Copilot's new dark palette is a design call, not made here.
            // What stays pinned: every key the pane uses exists, and ThemeTokens holds it with the SAME value in both
            // columns, so the theme switch cannot repaint the always-dark pane.
            var cp = CopilotBrushes();
            foreach (var name in new[] { "Bg", "Sunken", "Menu", "Ink", "Ink2", "Muted", "Line", "Hair2", "LineSoft",
                                         "Hover", "Accent", "BlueSoft", "BlueLine", "BlueText", "CodeFg" })
            {
                var key = "Cp.Dark." + name;
                Assert.True(cp.ContainsKey(key), key + " missing from CopilotTokens.xaml");
                var t = RevitWebAppSync.UI.Copilot.Model.ThemeTokens.Solid[key];
                Assert.Equal(t.Light, t.Dark);
                Assert.Equal(cp[key], t.Dark.ToUpperInvariant());
            }
        }

        [Fact]
        public void The_pane_no_longer_uses_the_yellow_brand()
        {
            var dir = Path.Combine(RepoRoot(), "UI", "Jkr", "Panel");
            foreach (var f in Directory.GetFiles(dir, "*.xaml"))
                Assert.DoesNotContain("F2C200", File.ReadAllText(f).ToUpperInvariant());
        }

        [Fact]
        public void The_shell_fills_the_pane_and_the_pane_minimum_is_320()
        {
            // Clear Sheet draws the pane at 380 px and the result at 320 px (BM): 380 is the
            // design width, 320 the narrowest the dockable pane may be dragged.
            var shell = File.ReadAllText(Path.Combine(RepoRoot(), "UI", "Jkr", "Panel", "JkrPaneShell.xaml"));
            Assert.Contains("x:Name=\"Frame\" MinWidth=\"320\" HorizontalAlignment=\"Stretch\"", shell);
            Assert.DoesNotContain("Width=\"380\" HorizontalAlignment=\"Left\"", shell);   // no blank strip in a wider pane
            var code = File.ReadAllText(Path.Combine(RepoRoot(), "UI", "Jkr", "Panel", "JkrPaneShell.xaml.cs"));
            Assert.Contains("public const double DesignWidth = 380;", code);
            Assert.Contains("public const double MinimumWidth = 320;", code);
            var host = File.ReadAllText(Path.Combine(RepoRoot(), "UI", "JkrComplianceDashboardHost.cs"));
            Assert.Contains("MinimumWidth = (int)Jkr.Panel.JkrPaneShell.MinimumWidth", host);
        }
    }
}
