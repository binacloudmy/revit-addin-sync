#nullable enable
// r23 (AI-459, 0.0.170): "If the library cannot load, the message states the real cause." The Commands tab said only "Couldn't load the
// prompt library. Check your connection and sign-in…" because the fetch swallowed every status and exception. Each base the library is
// asked from now gives a reason (unreachable, timeout, sign-in expired, a server error with its HTTP code, an empty library), the
// tab names the most useful one in plain words and offers Retry. Success is unchanged. WPF-free so it is unit-tested.

using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public enum LibraryFailure { None, Unreachable, Timeout, SignIn, Server, Empty }

    /// <summary>One attempt (or the outcome of all of them): the sections on success, else why not.</summary>
    public sealed class LibraryLoad
    {
        public List<LibrarySection>? Sections;
        public LibraryFailure Failure;
        /// <summary>The HTTP status of a SignIn or Server failure.</summary>
        public int? Status;

        public bool Ok => Failure == LibraryFailure.None && Sections != null && Sections.Count > 0;

        public static LibraryLoad Loaded(List<LibrarySection> sections) => new LibraryLoad { Sections = sections };

        /// <summary>An answer with a status: 2xx with sections is a success, 2xx with none is an empty library, a body that is not the
        /// library (unreadable JSON) is a server error, 401/403 is the sign-in, any other code a server error with that code.</summary>
        public static LibraryLoad FromResponse(int status, List<LibrarySection>? sections, bool unreadable = false)
        {
            if (status >= 200 && status < 300)
            {
                if (unreadable) return new LibraryLoad { Failure = LibraryFailure.Server, Status = status };
                return sections != null && sections.Count > 0 ? Loaded(sections) : new LibraryLoad { Failure = LibraryFailure.Empty, Status = status };
            }
            return new LibraryLoad { Failure = status == 401 || status == 403 ? LibraryFailure.SignIn : LibraryFailure.Server, Status = status };
        }

        /// <summary>No answer: a timeout (the client gave up; not the pane closing) or the host could not be reached.</summary>
        public static LibraryLoad FromException(Exception ex) =>
            new LibraryLoad { Failure = ex is OperationCanceledException || ex is TimeoutException ? LibraryFailure.Timeout : LibraryFailure.Unreachable };

        // The reason worth telling when every base failed: the one the drafter can act on first.
        private static int Rank(LibraryFailure f) => f switch
        {
            LibraryFailure.SignIn => 0,
            LibraryFailure.Server => 1,
            LibraryFailure.Empty => 2,
            LibraryFailure.Timeout => 3,
            LibraryFailure.Unreachable => 4,
            _ => 5,
        };

        /// <summary>The outcome of all attempts: the first success, else the most useful failure (unreachable when none was made).</summary>
        public static LibraryLoad Pick(IEnumerable<LibraryLoad>? attempts)
        {
            var all = (attempts ?? Enumerable.Empty<LibraryLoad>()).Where(a => a != null).ToList();
            var ok = all.FirstOrDefault(a => a.Ok);
            if (ok != null) return ok;
            return all.OrderBy(a => Rank(a.Failure)).FirstOrDefault() ?? new LibraryLoad { Failure = LibraryFailure.Unreachable };
        }

        /// <summary>The Commands tab's line for a failure, naming its cause.</summary>
        public static string Message(LibraryLoad load)
        {
            var code = load.Status is int s ? " (HTTP " + s + ")" : "";
            return load.Failure switch
            {
                LibraryFailure.Unreachable => "Couldn't load the prompt library: the server can't be reached. Check your internet connection, then retry.",
                LibraryFailure.Timeout => "Couldn't load the prompt library: the server took too long to answer. Retry in a moment.",
                LibraryFailure.SignIn => "Couldn't load the prompt library: your sign-in has expired" + code + ". Sign in again from the Bina tab, then retry.",
                LibraryFailure.Server => "Couldn't load the prompt library: the server answered with an error" + code + ". Retry later; if it keeps failing, report it.",
                LibraryFailure.Empty => "The prompt library is empty on the server: there are no prompts to show yet.",
                _ => "",
            };
        }
    }
}
