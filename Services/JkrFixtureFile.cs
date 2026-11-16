using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// TEST-ONLY fixture for the JKR pane (see <see cref="JkrDevMode"/>): a list of
    /// checks against real elements of one model, each with the value it expects.
    /// A scan reads each element's current value and reports an issue only while it
    /// differs from <see cref="JkrFixtureIssue.Expected"/>, so after Fix all the
    /// fixed items drop out, after Undo / Reset and a Re-scan they come back, and a
    /// fix that cannot succeed (read-only parameter, duplicate name, missing
    /// element) keeps failing. Revit-free; JkrFixtureScanner does the Revit side.
    /// </summary>
    public sealed class JkrFixture
    {
        [JsonProperty("note")] public string Note { get; set; } =
            "TEST ONLY. Used by the JKR pane when BINA_DEV_UNLOCK=1 and BINA_JKR_FIXTURE points here. " +
            "Set \"enabled\": false to drop an entry. Delete this file to regenerate it from the open model.";
        [JsonProperty("model")] public string Model { get; set; } = "";
        [JsonProperty("generated")] public string Generated { get; set; } = "";
        [JsonProperty("issues")] public List<JkrFixtureIssue> Issues { get; set; } = new List<JkrFixtureIssue>();
    }

    public sealed class JkrFixtureIssue
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("enabled")] public bool Enabled { get; set; } = true;

        /// <summary>Backend status this entry reports: "fail" (default) or
        /// "cannot_verify" (needs a person: always listed, no fix, reason shown).</summary>
        [JsonProperty("status")] public string Status { get; set; } = "fail";

        /// <summary>JKR tier category; sets High/Medium/Low in the pane
        /// (JkrTierMap: "Levels" High, "Component Naming"/"Component Parameter"
        /// Medium, "LOD 400/500 parameter" Low).</summary>
        [JsonProperty("category")] public string Category { get; set; } = "";
        [JsonProperty("title")] public string Title { get; set; } = "";
        [JsonProperty("reason")] public string Reason { get; set; } = "";

        /// <summary>Revit category and name of the element, for display only.</summary>
        [JsonProperty("revit_category")] public string RevitCategory { get; set; } = "";
        [JsonProperty("element_name")] public string ElementName { get; set; } = "";
        [JsonProperty("element_id")] public long ElementId { get; set; }

        /// <summary>"rename_type" or "set_parameter".</summary>
        [JsonProperty("fix_action")] public string FixAction { get; set; } = "";
        [JsonProperty("fix_parameter")] public string FixParameter { get; set; } = "";
        /// <summary>"instance" or "type".</summary>
        [JsonProperty("fix_target")] public string FixTarget { get; set; } = "instance";

        /// <summary>Value when the fixture was generated (informational; the scan
        /// always reads the live value).</summary>
        [JsonProperty("current")] public string Current { get; set; } = "";
        [JsonProperty("expected")] public string Expected { get; set; } = "";

        /// <summary>Why this entry is expected to fail when fixed, or empty.</summary>
        [JsonProperty("expect_failure")] public string ExpectFailure { get; set; } = "";

        public bool IsRename => string.Equals(FixAction, "rename_type", StringComparison.OrdinalIgnoreCase);

        /// <summary>The backend could not judge it: listed whatever the model holds,
        /// with no fix.</summary>
        public bool IsCannotVerify => string.Equals((Status ?? "").Trim(), "cannot_verify", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What the scanner read for one entry.</summary>
    public readonly struct JkrFixtureObservation
    {
        public bool Found { get; }
        public string Value { get; }
        public JkrFixtureObservation(bool found, string value) { Found = found; Value = value ?? ""; }
        public static JkrFixtureObservation Missing => new JkrFixtureObservation(false, "");
    }

    /// <summary>An entry that does not comply right now.</summary>
    public sealed class JkrFixtureFinding
    {
        public JkrFixtureIssue Issue { get; }
        /// <summary>Live value, or "(element not found)".</summary>
        public string Actual { get; }
        public JkrFixtureFinding(JkrFixtureIssue issue, string actual) { Issue = issue; Actual = actual; }
    }

    public static class JkrFixtureFile
    {
        public const string NotFound = "(element not found)";

        public static JkrFixture Load(string path)
        {
            var json = File.ReadAllText(path);
            var fixture = JsonConvert.DeserializeObject<JkrFixture>(json) ?? new JkrFixture();
            fixture.Issues = fixture.Issues ?? new List<JkrFixtureIssue>();
            return fixture;
        }

        public static void Save(string path, JkrFixture fixture)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonConvert.SerializeObject(fixture, Formatting.Indented));
        }

        /// <summary>Problems that make an entry unusable (shown to the tester).</summary>
        public static List<string> Validate(JkrFixture fixture)
        {
            var errors = new List<string>();
            if (fixture == null) { errors.Add("Fixture is empty."); return errors; }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var i in fixture.Issues ?? new List<JkrFixtureIssue>())
            {
                var label = string.IsNullOrEmpty(i.Id) ? $"element {i.ElementId}" : i.Id;
                if (string.IsNullOrEmpty(i.Id)) errors.Add($"{label}: missing id.");
                else if (!seen.Add(i.Id)) errors.Add($"{label}: duplicate id.");
                if (i.IsCannotVerify)
                {
                    // Needs a person: no fix, may be project-level (element_id 0).
                    if (i.ElementId < 0) errors.Add($"{label}: element_id must be >= 0.");
                    if (string.IsNullOrWhiteSpace(i.Reason)) errors.Add($"{label}: cannot_verify needs a reason.");
                    continue;
                }
                var st = (i.Status ?? "").Trim().ToLowerInvariant();
                if (st != "" && st != "fail" && st != "warning")
                    errors.Add($"{label}: status must be fail, warning or cannot_verify.");
                if (i.ElementId <= 0) errors.Add($"{label}: element_id must be > 0.");
                if (!i.IsRename && !string.Equals(i.FixAction, "set_parameter", StringComparison.OrdinalIgnoreCase))
                    errors.Add($"{label}: fix_action must be rename_type or set_parameter.");
                if (!i.IsRename && string.IsNullOrWhiteSpace(i.FixParameter))
                    errors.Add($"{label}: set_parameter needs fix_parameter.");
                if (string.IsNullOrEmpty(i.Expected)) errors.Add($"{label}: expected is empty.");
            }
            return errors;
        }

        /// <summary>Enabled entries whose element is missing or whose live value is
        /// not the expected one, plus every enabled cannot_verify entry, in fixture
        /// order. Exact (ordinal) comparison.</summary>
        public static List<JkrFixtureFinding> Evaluate(JkrFixture fixture, Func<JkrFixtureIssue, JkrFixtureObservation> read)
        {
            if (read == null) throw new ArgumentNullException(nameof(read));
            var findings = new List<JkrFixtureFinding>();
            foreach (var i in (fixture?.Issues ?? new List<JkrFixtureIssue>()).Where(x => x != null && x.Enabled))
            {
                // Needs a person: always listed; there is no value to compare.
                if (i.IsCannotVerify)
                {
                    findings.Add(new JkrFixtureFinding(i, ""));
                    continue;
                }
                var seen = read(i);
                if (!seen.Found)
                    findings.Add(new JkrFixtureFinding(i, NotFound));
                else if (!string.Equals(seen.Value, i.Expected, StringComparison.Ordinal))
                    findings.Add(new JkrFixtureFinding(i, seen.Value));
            }
            return findings;
        }
    }
}
