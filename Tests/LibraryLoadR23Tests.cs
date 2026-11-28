#nullable enable
// r23 (AI-459, 0.0.170): "If the library cannot load, the message states the real cause." Each reason, its plain message, which one is
// told when both bases failed, Retry, and the success path unchanged.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class LibraryLoadR23Tests
    {
        private static List<LibrarySection> OneSection() => new List<LibrarySection> { new LibrarySection { Section = "SETUP" } };

        [Fact]
        public void ALibraryWithSections_Loads_AsBefore()
        {
            var ok = LibraryLoad.FromResponse(200, OneSection());
            Assert.True(ok.Ok);
            Assert.Equal(LibraryFailure.None, ok.Failure);
            Assert.Same(ok, LibraryLoad.Pick(new[] { LibraryLoad.FromException(new HttpRequestException("down")), ok }));   // the cloud answered
        }

        [Fact]
        public void EachReason_IsNamed_InItsOwnMessage()
        {
            var unreachable = LibraryLoad.FromException(new HttpRequestException("No such host is known."));
            Assert.Equal(LibraryFailure.Unreachable, unreachable.Failure);
            Assert.Equal("Couldn't load the prompt library: the server can't be reached. Check your internet connection, then retry.", LibraryLoad.Message(unreachable));

            var timeout = LibraryLoad.FromException(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));
            Assert.Equal(LibraryFailure.Timeout, timeout.Failure);
            Assert.Equal("Couldn't load the prompt library: the server took too long to answer. Retry in a moment.", LibraryLoad.Message(timeout));

            var expired = LibraryLoad.FromResponse(401, null);
            Assert.Equal(LibraryFailure.SignIn, expired.Failure);
            Assert.Equal("Couldn't load the prompt library: your sign-in has expired (HTTP 401). Sign in again from the Bina tab, then retry.", LibraryLoad.Message(expired));
            Assert.Equal(LibraryFailure.SignIn, LibraryLoad.FromResponse(403, null).Failure);

            var notFound = LibraryLoad.FromResponse(404, null);
            Assert.Equal(LibraryFailure.Server, notFound.Failure);
            Assert.Equal("Couldn't load the prompt library: the server answered with an error (HTTP 404). Retry later; if it keeps failing, report it.", LibraryLoad.Message(notFound));
            Assert.Contains("(HTTP 503)", LibraryLoad.Message(LibraryLoad.FromResponse(503, null)));
            Assert.Contains("(HTTP 200)", LibraryLoad.Message(LibraryLoad.FromResponse(200, null, unreadable: true)));   // not the library's JSON

            var empty = LibraryLoad.FromResponse(200, new List<LibrarySection>());
            Assert.Equal(LibraryFailure.Empty, empty.Failure);
            Assert.Equal("The prompt library is empty on the server: there are no prompts to show yet.", LibraryLoad.Message(empty));
            Assert.Equal(LibraryFailure.Empty, LibraryLoad.FromResponse(204, null).Failure);
        }

        [Fact]
        public void WhenBothBasesFail_TheReasonTheDrafterCanActOnIsTold()
        {
            var down = LibraryLoad.FromException(new HttpRequestException("refused"));
            var slow = LibraryLoad.FromException(new TimeoutException());
            Assert.Equal(LibraryFailure.SignIn, LibraryLoad.Pick(new[] { down, LibraryLoad.FromResponse(401, null) }).Failure);
            Assert.Equal(503, LibraryLoad.Pick(new[] { slow, LibraryLoad.FromResponse(503, null) }).Status);
            Assert.Equal(LibraryFailure.Empty, LibraryLoad.Pick(new[] { down, LibraryLoad.FromResponse(200, null) }).Failure);
            Assert.Equal(LibraryFailure.Timeout, LibraryLoad.Pick(new[] { down, slow }).Failure);
            Assert.Equal(LibraryFailure.Unreachable, LibraryLoad.Pick(new LibraryLoad[0]).Failure);
        }

        [Fact]
        public void TheTab_ShowsTheCause_AndRetry_AndTheOldFixedLineIsGone()
        {
            var view = Src("UI/Copilot/Screens/LibraryView.xaml.cs");
            Assert.Contains(".FetchPromptLibraryAsync(BinaConfig.Load().AccessToken, token);", view);
            Assert.Contains("ListHost.Children.Add(Note(LibraryLoad.Message(failure ?? new LibraryLoad { Failure = LibraryFailure.Unreachable })));", view);
            Assert.Contains("Controls.CardKit.Secondary(\"Retry\", () => { _ = BuildAsync(); }", view);
            Assert.DoesNotContain("Check your connection and sign-in, then reopen this tab.", view);
            Assert.Contains("if (fetched.Ok) CopilotPromptLibrary.Cached = sections = fetched.Sections;", view);   // success: cached and drawn as before
            var ai = Src("Services/AIService.cs");
            Assert.Contains("attempt = UI.Copilot.Model.LibraryLoad.FromResponse((int)resp.StatusCode, sections, unreadable);", ai);
            Assert.Contains("catch (Exception ex) { attempt = UI.Copilot.Model.LibraryLoad.FromException(ex); }", ai);
            Assert.Contains("return UI.Copilot.Model.LibraryLoad.Pick(attempts);", ai);
        }

        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel)).Replace("\r\n", "\n");
        }
    }
}
