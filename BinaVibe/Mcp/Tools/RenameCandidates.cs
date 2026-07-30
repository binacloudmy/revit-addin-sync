// The rename planner: one pass that decides every name change, including which
// ones would collide, BEFORE a transaction opens.
//
// Why this is its own file and holds no Revit types:
//
//   1. Mutators.RenameElements derived candidates twice — once to preview, once
//      to apply — with Contains/Replace copy-pasted between them. That is how a
//      preview promising 40 renames lands 12: the apply loop swallows duplicate
//      and read-only names per element, and the preview never knew.
//   2. Tests.csproj links individual source files and cannot construct a Revit
//      Document. Logic that touches Document is untestable by construction, so
//      the planner takes plain DTOs. Same reason Audit/AuditNaming.cs is split.
//
// Mutators projects Revit elements into RenameTarget, calls Build once, then
// either renders the plan (dry_run) or applies it. It never derives a name.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace BinaVibe.Mcp.Tools
{
    public enum RenameField { Name, Number, Both }

    public enum RenameMode { Literal, Regex }

    /// <summary>One renamable thing, flattened out of Revit.</summary>
    public class RenameTarget
    {
        public long Id;
        /// <summary>"family" | "type" | "instance" | "sheet" | "view" | "schedule".</summary>
        public string Kind = "";
        public string CurrentName = "";
        /// <summary>Sheet number. Empty for everything that has no number.</summary>
        public string CurrentNumber = "";
        /// <summary>
        /// The namespace this target's value must be unique within. Revit's rules
        /// are not global: sheet numbers and family names are document-wide, but
        /// type names only need to be unique inside their family. Callers pass
        /// "sheet_number", "family", or "type:&lt;familyName&gt;" so collision
        /// detection matches Revit instead of over-reporting.
        /// </summary>
        public string UniquenessScope = "";
    }

    public class RenameCandidate
    {
        public long Id;
        public string Kind = "";
        /// <summary>"name" or "number" — which field this row rewrites.</summary>
        public string Field = "";
        public string From = "";
        public string To = "";
        public bool Collides;
        /// <summary>Id of the occupant, or 0 when two renames in this plan collide.</summary>
        public long CollidesWith;
    }

    public class RenamePlan
    {
        public List<RenameCandidate> Candidates = new List<RenameCandidate>();
        public int WouldRename;
        public int WouldCollide;
        /// <summary>Non-null means the request was unusable; Candidates is empty.</summary>
        public string Error;
    }

    public static class RenameCandidates
    {
        public static RenamePlan Build(
            IEnumerable<RenameTarget> targets,
            string find,
            string replace,
            RenameField field,
            RenameMode mode)
        {
            var plan = new RenamePlan();
            if (string.IsNullOrEmpty(find))
            {
                plan.Error = "find is required";
                return plan;
            }
            replace = replace ?? "";
            var list = (targets ?? Enumerable.Empty<RenameTarget>()).ToList();

            Regex rx = null;
            if (mode == RenameMode.Regex)
            {
                try { rx = new Regex(find); }
                catch (ArgumentException e)
                {
                    plan.Error = "invalid regex: " + e.Message;
                    return plan;
                }
            }

            bool wantName = field == RenameField.Name || field == RenameField.Both;
            bool wantNumber = field == RenameField.Number || field == RenameField.Both;

            // Occupancy per uniqueness scope, so a collision is judged the way
            // Revit judges it. Values a plan vacates are removed before new ones
            // are claimed — otherwise renaming A to B's name while B moves away
            // reports a phantom collision and blocks a legal sweep.
            var occupied = new Dictionary<string, Dictionary<string, long>>();

            void Seed(string scope, string value, long id)
            {
                if (string.IsNullOrEmpty(value)) return;
                if (!occupied.TryGetValue(scope, out var byValue))
                    occupied[scope] = byValue = new Dictionary<string, long>();
                if (!byValue.ContainsKey(value)) byValue[value] = id;
            }

            foreach (var t in list)
            {
                if (wantName) Seed(t.UniquenessScope, t.CurrentName, t.Id);
                if (wantNumber) Seed(t.UniquenessScope, t.CurrentNumber, t.Id);
            }

            string Apply(string value)
            {
                if (mode == RenameMode.Regex)
                    return rx.IsMatch(value) ? rx.Replace(value, replace) : null;
                return value.Contains(find) ? value.Replace(find, replace) : null;
            }

            // Pass 1: derive every change and vacate the old values.
            var raw = new List<RenameCandidate>();
            foreach (var t in list)
            {
                void Consider(string fieldName, string current)
                {
                    if (string.IsNullOrEmpty(current)) return;
                    var next = Apply(current);
                    // No match, a no-op, or a blank result: not a rename. Revit
                    // refuses a blank name, so emitting one guarantees a skip at
                    // apply time and a preview that overpromised.
                    if (next == null || next == current || string.IsNullOrWhiteSpace(next)) return;

                    raw.Add(new RenameCandidate
                    {
                        Id = t.Id, Kind = t.Kind, Field = fieldName,
                        From = current, To = next,
                    });

                    if (occupied.TryGetValue(t.UniquenessScope, out var byValue)
                        && byValue.TryGetValue(current, out var holder) && holder == t.Id)
                        byValue.Remove(current);
                }

                if (wantName) Consider("name", t.CurrentName);
                if (wantNumber) Consider("number", t.CurrentNumber);
            }

            // Pass 2: claim the new values in order. First writer wins; a later
            // claim on a taken value is a collision, reported rather than dropped.
            foreach (var c in raw)
            {
                var scope = list.First(t => t.Id == c.Id).UniquenessScope;
                if (!occupied.TryGetValue(scope, out var byValue))
                    occupied[scope] = byValue = new Dictionary<string, long>();

                if (byValue.TryGetValue(c.To, out var holder))
                {
                    c.Collides = true;
                    c.CollidesWith = holder == c.Id ? 0 : holder;
                    plan.WouldCollide++;
                }
                else
                {
                    byValue[c.To] = c.Id;
                    plan.WouldRename++;
                }
                plan.Candidates.Add(c);
            }

            return plan;
        }
    }
}
