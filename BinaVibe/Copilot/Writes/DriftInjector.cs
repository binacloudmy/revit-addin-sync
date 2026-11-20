#if DEBUG
#nullable enable
// TEST ONLY, Debug builds only (the whole file is inside #if DEBUG: a Release or Staging build has no trace of it).
//
// r21: the Force card (a commit that drifted from its preview in connected elements only, drift.connected_only=true) cannot
// be reached live: after the r18 BornIds fix a real resize commits exactly as previewed. To prove the card, Cancel and
// "Force the change" end to end, this injector makes ONE commit comparison report ONE real element as drifted:
//   - armed only by the file %LOCALAPPDATA%\Bina\RevitSync\inject-connected-drift.txt (any content), at a commit that is
//     not forced and whose real comparison matched; the file is deleted when it fires (one-shot) and it fires only if the
//     delete succeeded;
//   - the element is a real fitting the preview changed (preview Modified) that is in the batch's connected set read before
//     the commit, and not a target; with no such element nothing fires and the file stays (logged);
//   - once fired, the same turn and the same calls get the same element again at every later commit of that turn: the
//     runner's retry commit (a first drift is re-previewed and committed once more before the card) and the forced commit
//     after "Force the change", which then runs the real forced path (ForceApproval.Covers, the forced warning). The forced
//     commit clears it. A new arming or another turn replaces it.
// Revit itself is never altered: only the comparison result is. Revit-free (compiles in Tests.csproj).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace BinaVibe.Copilot.Writes
{
    public static class DriftInjector
    {
        public const string FlagFileName = "inject-connected-drift.txt";
        /// <summary>The comparison reason of an injected drift (the commit's diff_drift Reason).</summary>
        public const string Reason = "Debug drift injector (test only): one connected element was reported as drifted.";
        /// <summary>Commits of one armed turn that may be injected (attempt 0, its retry, the forced commit, one spare).</summary>
        public const int MaxFires = 4;

        /// <summary>Where the flag file is; a test points it elsewhere.</summary>
        public static Func<string> FlagPath = () =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bina", "RevitSync", FlagFileName);

        private sealed class Armed
        {
            public string TurnId = "";
            public List<string> Signatures = new();
            public long Id;
            public string Category = "";
            public int Fires;
        }

        private static readonly object Gate = new();
        private static Armed? _armed;

        /// <summary>Forgets the armed turn (tests).</summary>
        public static void Reset() { lock (Gate) _armed = null; }

        /// <summary>The element injected for this turn, or null.</summary>
        public static long? ArmedId(string turnId) { lock (Gate) return _armed != null && _armed.TurnId == (turnId ?? "") ? _armed.Id : (long?)null; }

        /// <summary>Called with the real comparison of a commit. Returns the element to report as drifted (and a line for the
        /// clicks log), or null to leave the real result alone. <paramref name="log"/> gets one line whenever the flag is seen
        /// or the injector fires.</summary>
        public static DriftExtra? At(string turnId, IReadOnlyList<string> signatures, bool forced, bool realMatch, bool measured,
                                     BatchShape? preview, IReadOnlyDictionary<long, string> connectedBefore, IReadOnlyCollection<long> targets,
                                     out string? log)
        {
            log = null;
            turnId ??= "";
            lock (Gate)
            {
                if (_armed != null && (_armed.TurnId != turnId || !_armed.Signatures.SequenceEqual(signatures ?? Array.Empty<string>())))
                {
                    if (_armed.TurnId != turnId) _armed = null;   // another turn: the old arming is gone
                    else return null;                              // other calls in the same turn: not this write
                }

                if (_armed != null)
                {
                    // A real drift is judged as real; a cut-short connected set could not be connected-only either.
                    if (!realMatch || !measured || !Eligible(_armed.Id, preview, connectedBefore, targets) || _armed.Fires >= MaxFires)
                    {
                        log = "inject connected drift: armed id " + Id(_armed.Id) + " not injected (" +
                              (!realMatch ? "real drift" : !measured ? "connected set not measured" : _armed.Fires >= MaxFires ? "used up" : "no longer eligible") + ")";
                        if (forced) _armed = null;
                        return null;
                    }
                    _armed.Fires++;
                    log = "inject connected drift: fired again on id " + Id(_armed.Id) + " (" + _armed.Category + ")" + (forced ? ", forced commit" : ", retry commit");
                    var again = new DriftExtra { Id = _armed.Id, Category = _armed.Category, Kind = ConnectedDrift.Changed };
                    if (forced) _armed = null;   // the forced commit is the last one of the turn
                    return again;
                }

                if (forced) return null;   // never armed by a forced request
                var path = FlagPath();
                bool present;
                try { present = File.Exists(path); } catch (Exception) { return null; }
                if (!present) return null;
                if (!realMatch || !measured)
                {
                    log = "inject connected drift: flag seen, not fired (" + (!realMatch ? "real drift" : "connected set not measured") + "); flag kept";
                    return null;
                }
                var pick = Candidate(preview, connectedBefore, targets);
                if (pick == null)
                {
                    log = "inject connected drift: flag seen, not fired (no connected fitting the preview changed); flag kept";
                    return null;
                }
                try { File.Delete(path); } catch (Exception) { /* checked below */ }
                bool stillThere;
                try { stillThere = File.Exists(path); } catch (Exception) { stillThere = true; }
                if (stillThere)
                {
                    log = "inject connected drift: flag could not be deleted, not fired";
                    return null;
                }
                _armed = new Armed { TurnId = turnId, Signatures = (signatures ?? Array.Empty<string>()).ToList(), Id = pick.Value.Key, Category = pick.Value.Value, Fires = 1 };
                log = "inject connected drift: flag deleted, fired on id " + Id(pick.Value.Key) + " (" + pick.Value.Value + ")";
                return new DriftExtra { Id = pick.Value.Key, Category = pick.Value.Value, Kind = ConnectedDrift.Changed };
            }
        }

        /// <summary>The lowest-id connected fitting (category ending in "Fittings") the preview changed that is not a target; null when none.</summary>
        public static KeyValuePair<long, string>? Candidate(BatchShape? preview, IReadOnlyDictionary<long, string> connectedBefore, IReadOnlyCollection<long> targets)
        {
            if (preview == null || connectedBefore == null) return null;
            foreach (var id in preview.Modified.OrderBy(i => i))
                if (Eligible(id, preview, connectedBefore, targets)) return new KeyValuePair<long, string>(id, connectedBefore[id]);
            return null;
        }

        private static bool Eligible(long id, BatchShape? preview, IReadOnlyDictionary<long, string> connectedBefore, IReadOnlyCollection<long> targets) =>
            preview != null && preview.Modified.Contains(id) && connectedBefore != null && connectedBefore.TryGetValue(id, out var category) &&
            (category ?? "").EndsWith("Fittings", StringComparison.Ordinal) && (targets == null || !targets.Contains(id));

        /// <summary>The real connected verdict with the injected element added (the verdict's own rule: connected-only when nothing
        /// lies outside and something differs).</summary>
        public static ConnectedVerdict Apply(ConnectedVerdict real, DriftExtra extra)
        {
            if (!real.Extras.Any(e => e.Id == extra.Id)) real.Extras.Add(extra);
            real.ConnectedOnly = real.Outside == null && real.Extras.Count > 0;
            return real;
        }

        private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
    }
}
#endif
