#nullable enable
// The same read-mode run_revit_code, asked again in one turn, does not run again (AI-327: "never runs the same code twice for one
// request"). Revit-free.
//
// A repeat is the same code and arguments, on the same model (the document's change sequence did not move), in the same view with the
// same selection, and only a run that SUCCEEDED as a read is kept. Anything that changed the model, the view or the selection in
// between gives a different key, so the code runs again on the new state. The answer is a copy of the first envelope with a plain
// note in front of its summary, so the model can see it was not a new run. Only the last turn is kept.
using System;
using System.Collections.Generic;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Read
{
    public sealed class ReadDedupe
    {
        public const string Note = "Same code, same model as the earlier run in this turn: its result again. ";

        private readonly object _gate = new object();
        private readonly Dictionary<string, Envelope> _kept = new Dictionary<string, Envelope>(StringComparer.Ordinal);
        private string? _turn;

        /// <summary>The key of one run: turn, the call's arguments as sent, the document's change sequence, the active view and the selection.</summary>
        public static string Key(string turnId, string argsJson, long changeSeq, long viewId, string selectionKey) =>
            string.Join("\u0001", turnId ?? "", argsJson ?? "", changeSeq.ToString(System.Globalization.CultureInfo.InvariantCulture),
                viewId.ToString(System.Globalization.CultureInfo.InvariantCulture), selectionKey ?? "");

        /// <summary>The kept answer for this key, as a copy with the note; false when the code has not run on this state in this turn.</summary>
        public bool TryGet(string turnId, string key, out Envelope? copy)
        {
            copy = null;
            lock (_gate)
            {
                if (_turn != turnId || !_kept.TryGetValue(key, out var first)) return false;
                copy = Clone(first);
            }
            if (copy == null) return false;
            var summary = Note + (copy.Summary ?? "");
            var limit = EnvelopeLimits.SummaryMaxChars;
            copy.Summary = summary.Length <= limit ? summary : summary.Substring(0, limit - 1) + "…";
            return true;
        }

        /// <summary>Keep an envelope for this key. Only a successful read-mode code run is worth keeping.</summary>
        public void Put(string turnId, string key, Envelope env)
        {
            if (env == null || !Repeatable(env)) return;
            lock (_gate)
            {
                if (_turn != turnId) { _kept.Clear(); _turn = turnId; }
                _kept[key] = env;
            }
        }

        /// <summary>A run that finished ok as a read (not a write, not a failure, not a timeout).</summary>
        public static bool Repeatable(Envelope env) =>
            env.Ok && env.StatusKind() == EnvelopeStatusKind.Done && ToolResults.Mode(env) == EnvelopeCodeMode.Read;

        private static Envelope? Clone(Envelope env)
        {
            try { return JsonSerializer.Deserialize<Envelope>(JsonSerializer.Serialize(env)); }
            catch (Exception) { return null; }
        }
    }
}
