#nullable enable
// 25 Nov (ClickUp 14zefhy5mud, 14zefhy5mqv, 14zefhy5ap3), build 0.0.176.
// mud/mqv: picking a tool from the "/" list (click, or arrows then Enter) cleared the "/query"; that text change re-ran DetectToken,
//   which closed the list and set _slashIndex to -1, and the next line set the caret to it: ArgumentOutOfRangeException before
//   ToolPicked. A click left an empty box and no command chip; Enter ended Revit (journal 11:45, SlashBtn then the managed exception).
//   The span to clear is read once, before the edit, and the composer's input handlers log and swallow what they throw.
// ap3: a library base this build may not send the token to read "the server can't be reached"; it now names the real cause.
using System.Collections.Generic;
using System.IO;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class SlashPick25NovTests
    {
        private static string Src(params string[] parts)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, Path.Combine(parts))).Replace("\r\n", "\n");
        }

        [Fact]
        public void APick_ClearsTheSlashQuery_FromTheSlashToTheCaret()
        {
            Assert.Equal((0, 1), SlashTrigger.PickSpan(1, 0, 1));          // the "/" button's seeded "/" (the 25 Nov steps)
            Assert.Equal((0, 4), SlashTrigger.PickSpan(4, 0, 4));          // "/cou"
            Assert.Equal((14, 4), SlashTrigger.PickSpan(18, 14, 18));      // "door schedule /cou"
            Assert.Equal((0, 3), SlashTrigger.PickSpan(3, 0, 9));          // a caret past the end: up to the end
            Assert.Equal((2, 1), SlashTrigger.PickSpan(3, 2, 1));          // a caret before the "/": up to the end
        }

        [Fact]
        public void NoTypedSlash_OrAStaleIndex_ClearsNothing()
        {
            Assert.Null(SlashTrigger.PickSpan(0, -1, 0));    // the index after the clear re-ran detection: the old caret target
            Assert.Null(SlashTrigger.PickSpan(12, -1, 5));   // the list opened over a draft: the draft stays
            Assert.Null(SlashTrigger.PickSpan(0, 0, 0));     // the "/" is already gone
            Assert.Null(SlashTrigger.PickSpan(3, 3, 3));
        }

        [Fact]
        public void ThePick_ReadsTheSpanBeforeTheEdit_AndAlwaysHandsTheToolUp()
        {
            var input = Src("UI", "Copilot", "Controls", "MentionInput.xaml.cs");
            var at = input.IndexOf("private void OnSlashToolPicked(SlashTool tool)", System.StringComparison.Ordinal);
            Assert.True(at >= 0);
            var pick = input.Substring(at, input.IndexOf("\n        });\n", at, System.StringComparison.Ordinal) - at);
            Assert.DoesNotContain("Editor.CaretIndex = _slashIndex;", pick);   // _slashIndex is -1 once the clear ran detection
            var span = pick.IndexOf("SlashTrigger.PickSpan(text.Length, _slashIndex, Editor.CaretIndex);", System.StringComparison.Ordinal);
            var clear = pick.IndexOf("Editor.SelectedText = \"\";", System.StringComparison.Ordinal);
            var caret = pick.IndexOf("Editor.CaretIndex = Math.Min(start, Editor.Text.Length);", System.StringComparison.Ordinal);
            var up = pick.IndexOf("ToolPicked?.Invoke(tool);", System.StringComparison.Ordinal);
            Assert.True(span >= 0 && span < clear && clear < caret && caret < up);
            Assert.Equal(1, pick.Split(new[] { "_slashIndex" }, System.StringSplitOptions.None).Length - 1);
            Assert.Contains("=> Guarded(\"composer_slash_pick\", () =>", pick);
        }

        [Fact]
        public void TheComposersInputHandlers_LogAndSwallow_InsteadOfReachingRevit()
        {
            var input = Src("UI", "Copilot", "Controls", "MentionInput.xaml.cs");
            Assert.Contains("Editor.PreviewKeyDown += (s, e) => Guarded(\"composer_key\", () => OnPreviewKeyDown(s, e), () => e.Handled = true);", input);
            Assert.Contains("Editor.TextChanged += (s, e) => Guarded(\"composer_text\", () => OnTextChanged(s, e));", input);
            Assert.Contains("Editor.SelectionChanged += (_, __) => Guarded(\"composer_caret\", SnapCaretOutOfChip);", input);
            Assert.DoesNotContain("Editor.PreviewKeyDown += OnPreviewKeyDown;", input);
            Assert.Contains("RevitWebAppSync.Services.CrashGuard.Contained(where, ex);", input);
            Assert.Contains("public static void Contained(string where, Exception ex)", Src("Services", "CrashGuard.cs"));
        }

        [Fact]
        public void ABaseThisBuildMayNotSignInTo_IsNamed_NotCalledUnreachable()
        {
            var blocked = new LibraryLoad { Failure = LibraryFailure.Blocked };
            var message = LibraryLoad.Message(blocked);
            Assert.StartsWith("Couldn't load the prompt library: the configured server address is not a BINA server", message);
            Assert.DoesNotContain("internet", message);
            // The other base's answer still wins when it says more; Blocked beats a plain unreachable or timeout.
            var expired = LibraryLoad.FromResponse(401, null);
            Assert.Same(expired, LibraryLoad.Pick(new[] { blocked, expired }));
            var down = LibraryLoad.FromException(new System.Net.Http.HttpRequestException("down"));
            Assert.Same(blocked, LibraryLoad.Pick(new[] { blocked, down }));
            var ok = LibraryLoad.FromResponse(200, new List<LibrarySection> { new LibrarySection { Section = "SETUP" } });
            Assert.Same(ok, LibraryLoad.Pick(new[] { blocked, ok }));
            var fetch = Src("Services", "AIService.cs");
            Assert.Contains("if (!MaySend(root)) { attempts.Add(new UI.Copilot.Model.LibraryLoad { Failure = UI.Copilot.Model.LibraryFailure.Blocked }); continue; }", fetch);
        }
    }
}
