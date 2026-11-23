#nullable enable
// r24 privacy: DialogRedaction, moved out of Writes/WriteSeams.cs into its own Revit-free file. It needs nothing but the BCL, so the
// backend's playbook policy check (scripts/check-playbook-policy.sh: Code/*.cs, Contracts/*.cs, Envelopes/EnvelopeBuilder.cs,
// Guard/FailureLog.cs) compiles it alongside FailureLog and the Code files that call it. Namespace and API unchanged.

using System;

namespace BinaVibe.Copilot.Writes
{
    /// <summary>r24 privacy: what a Revit dialog or window title says before it goes into an envelope (the backend DB and the model). Windows
    /// paths (drive letter, UNC, anything with \ or / segments) become &lt;path&gt;, file names of model and data files &lt;file&gt;, email
    /// addresses and the Windows user name or profile folder &lt;user&gt;; at most <see cref="MaxChars"/> characters. A plain title ("Options")
    /// is kept as it is. Never throws.</summary>
    public static class DialogRedaction
    {
        public const int MaxChars = 80;

        /// <summary>The status-bar prompt's limit in the in-command text: WatchdogPolicy.PromptMaxChars (kept equal by a test; this file stays
        /// Revit-free and free of Writes types, so the backend's playbook policy check can compile it with Code/*.cs).</summary>
        public const int PromptMaxChars = 60;

        private static readonly System.Text.RegularExpressions.RegexOptions Opt =
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant;
        private static readonly System.Text.RegularExpressions.Regex Email = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(\.[A-Za-z0-9\-]+)+", Opt);
        private static readonly System.Text.RegularExpressions.Regex Unc = new(@"\\\\[^\s""'<>|]+", Opt);
        private static readonly System.Text.RegularExpressions.Regex Drive = new(@"\b[A-Za-z]:[\\/][^\s""'<>|]*", Opt);
        // A token with a slash or backslash. It is a path when it has a backslash, or two or more '/' and a drive, dotted (file-like) or ~
        // segment (Documents/BINA/out.ifc, http://host.com/x). "and/or", "floor/roof" and "Wall/Floor/Roof" in a Revit warning stay text.
        private static readonly System.Text.RegularExpressions.Regex Segments = new(@"[^\s""'<>|]*[\\/][^\s""'<>|]*", Opt);
        private static readonly System.Text.RegularExpressions.Regex PathSegment = new(@"^([A-Za-z]:|~|.*\..*)$", Opt);
        // Worksharing: Revit names the user who owns, borrows or edits an element ("owned by aminah", "until 'aminah' saves ...", "user 'aminah'").
        // r25: "by user aminah.b" too, and a full name ("owned by Aminah Binti Ali"): the capitalised words after the first, up to punctuation.
        private const string NameEnd = @"(?=[\s,;:()]|\.(?:\s|$)|$)";
        private static readonly System.Text.RegularExpressions.Regex ByUser = new(@"\b(owned|borrowed|used|edited|requested|held|checked out|reserved|locked) by\s+(?:user\s+)?(?:'[^']{1,64}'|""[^""]{1,64}""|‘[^’]{1,64}’|[^\s'""‘’,;:()]+?" + NameEnd
            + @"(?:\s+(?-i:(?!(?:The|This|That|It|You)\b)\p{Lu}[\p{L}\-]*)" + NameEnd + @"){0,4})", Opt);
        private static readonly System.Text.RegularExpressions.Regex QuotedUser = new(@"\b(until|user|by user)\s+(?:'[^']{1,64}'|""[^""]{1,64}""|‘[^’]{1,64}’)", Opt);
        // r25: Revit's own "until aminah.b resaves ...": one unquoted token, not an ordinary word ("until you Reload Latest").
        private static readonly System.Text.RegularExpressions.Regex UntilUser = new(@"\buntil\s+(?!(?:the|you|it|a|an|this|that|all|they|we|he|she|its|your|their|now|then|next|after|before)\b)[^\s'""‘’,;:()<>]+?" + NameEnd, Opt);
        // r25: a cloud model path ("BIM 360://Project/Model.rvt", or the cloud docs scheme) holds the project name: everything after
        // "://" up to a quote or the end of the line. Runs after Segments, which already took a web address with a dotted host.
        private static readonly System.Text.RegularExpressions.Regex SchemeRest = new(@"://[^""'‘’\r\n]*", Opt);

        private static bool IsPath(string token)
        {
            if (token.IndexOf('\\') >= 0) return true;
            var parts = token.Split('/');
            if (parts.Length < 3) return false;
            foreach (var p in parts) if (p.Length > 0 && PathSegment.IsMatch(p.TrimEnd('.', ',', ';', ')'))) return true;
            return false;
        }
        private const string FileExtensions = "rvt|rfa|rte|rft|txt|xlsx|csv|ifc|dwg|nwc|nwd|rcp|rcs|dxf|dgn|sat|skp|pdf|png|jpg|jpeg";
        private static readonly System.Text.RegularExpressions.Regex File = new(@"[^\s""'<>|\\/]+\.(" + FileExtensions + @")\b", Opt);
        // r25: a quoted file name may hold spaces ('ACME Hospital Structure.nwc'): the whole name inside the quotes.
        private static readonly System.Text.RegularExpressions.Regex QuotedFile = new(@"(['""‘])[^'""‘’\r\n<>|\\/]{1,128}?\.(" + FileExtensions + @")(['""’])", Opt);

        public static string Redact(string? text) => Redact(text, SafeUser(), SafeProfile());

        /// <summary>Paths, files, emails and the user redacted, with no length cap: for Revit's failure messages and API exception
        /// messages that reach an envelope's warnings (their own length limits apply).</summary>
        public static string RedactPaths(string? text) => RedactPaths(text, SafeUser(), SafeProfile());

        /// <param name="userName">The Windows user name (Environment.UserName); <paramref name="profile"/> its profile folder.</param>
        public static string Redact(string? text, string? userName, string? profile)
        {
            var t = RedactPaths(text, userName, profile);
            return t.Length <= MaxChars ? t : t.Substring(0, MaxChars - 1) + "…";
        }

        public static string RedactPaths(string? text, string? userName, string? profile)
        {
            if (string.IsNullOrEmpty(text)) return "";
            try
            {
                var t = text!;
                t = Email.Replace(t, "<user>");
                t = ByUser.Replace(t, m => m.Groups[1].Value + " by <user>");
                t = QuotedUser.Replace(t, m => m.Groups[1].Value + " '<user>'");
                t = UntilUser.Replace(t, "until <user>");
                if (!string.IsNullOrWhiteSpace(profile) && profile!.Length >= 3)
                    t = System.Text.RegularExpressions.Regex.Replace(t, System.Text.RegularExpressions.Regex.Escape(profile.TrimEnd('\\', '/')), "<user>", Opt);
                t = Unc.Replace(t, "<path>");
                t = Drive.Replace(t, "<path>");
                t = Segments.Replace(t, m => IsPath(m.Value) ? "<path>" : m.Value);
                t = SchemeRest.Replace(t, "://<path>");
                t = QuotedFile.Replace(t, m => m.Groups[1].Value + "<file>" + m.Groups[3].Value);
                t = File.Replace(t, "<file>");
                if (!string.IsNullOrWhiteSpace(userName) && userName!.Trim().Length >= 2)
                    t = System.Text.RegularExpressions.Regex.Replace(t, @"(?<![A-Za-z0-9])" + System.Text.RegularExpressions.Regex.Escape(userName.Trim()) + @"(?![A-Za-z0-9])", "<user>", Opt);
                return t;
            }
            catch (Exception) { return "<redacted>"; }
        }

        /// <summary>The in-command text (WatchdogPolicy.InCommandText): our own head and ", press Esc in Revit" tail, which the backend's
        /// copilot_busy parses, are kept whole; only the status-bar prompt between them is redacted and cut (at most
        /// <see cref="PromptMaxChars"/>, so the whole text can pass 80). Anything else is redacted as dialog text.</summary>
        public static string RedactCommandText(string? text)
        {
            const string head = "a Revit command or edit mode is still active";
            const string tail = ", press Esc in Revit";
            var t = text ?? "";
            if (!t.StartsWith(head, StringComparison.Ordinal) || !t.EndsWith(tail, StringComparison.Ordinal)) return Redact(t);
            var prompt = t.Substring(head.Length, t.Length - head.Length - tail.Length);
            if (prompt.StartsWith(": ", StringComparison.Ordinal)) prompt = prompt.Substring(2);
            if (prompt.Length == 0) return head + tail;
            var r = Redact(prompt);
            if (r.Length > PromptMaxChars) r = r.Substring(0, PromptMaxChars - 1) + "…";
            return head + ": " + r + tail;
        }

        private static string? SafeUser() { try { return Environment.UserName; } catch (Exception) { return null; } }
        private static string? SafeProfile() { try { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } catch (Exception) { return null; } }
    }
}
