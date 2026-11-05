#nullable enable
// Where the model's text stops for a tool and starts again (AI-269 item 3).
// "I'll count the doors in the host model." and "62 doors in the host model"
// arrive as two streams of reply_delta with the tool round between them; glued
// they read "...host model.62 doors". The gap is a blank line, added only when
// the text has not got one already. Revit-free.

using System;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class ReplySeam
    {
        /// <summary>The newlines to put between the text so far and the next delta so that a
        /// tool round never glues two sentences: none when either side already breaks the line.</summary>
        public static string Gap(string existing, string next)
        {
            if (string.IsNullOrWhiteSpace(existing) || string.IsNullOrEmpty(next)) return "";
            var before = 0;
            for (var i = existing.Length - 1; i >= 0 && (existing[i] == '\n' || existing[i] == '\r'); i--)
                if (existing[i] == '\n') before++;
            var after = 0;
            for (var i = 0; i < next.Length && (next[i] == '\n' || next[i] == '\r'); i++)
                if (next[i] == '\n') after++;
            var need = 2 - before - after;
            return need <= 0 ? "" : new string('\n', need);
        }
    }
}
