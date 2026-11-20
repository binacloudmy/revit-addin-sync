using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Shrinks a <see cref="JkrAutoFixRequest"/> before it goes to
    /// /v1/compliance/jkr-autofix, keeping everything the backend fixer reads.
    ///
    /// What the backend reads (bina-ai app/services/jkr_autofix.py):
    /// - the prompt (_issue_context, _build_prompt): per issue check_id, rule, status,
    ///   category, actual_value, expected_value, reason; per element element_id,
    ///   category, family_name, type_name, element_name, jkr_code, parameters (plus
    ///   geometry and material_layers, which the add-in does not send); from the
    ///   project only discipline, lod_level, file_name and project_phase. The whole
    ///   element is printed again for every issue on it, so each parameter kept costs
    ///   once per issue.
    /// - the checks on each proposed fix (_validate_rename, _validate_set_parameter,
    ///   _to_fix_action): the element's name fields, category, the value of the
    ///   parameter being set (it becomes old_value, which Reset writes back), whether a
    ///   _jkr_stm parameter is on the element, and Kod_Jenis / Sistem / Sub_Sistem /
    ///   Bidang_Kejuruteraan / Bahan values. issue.actual_value is old_value when the
    ///   element is not sent (levels, grids).
    /// Never read: issue.domain and issue.type_name, element level_name, the rest of
    /// the project, and the whole model block.
    ///
    /// Pure and deterministic: the input is never changed, and the same input always
    /// gives the same output.
    /// </summary>
    public static class JkrAutoFixPayload
    {
        /// <summary>Longest issue reason / expected_value sent. Long expected values
        /// are lists the backend already prints in full in the per-issue reference
        /// block (valid subtypes, Kod_Jenis lists, discipline names).</summary>
        public const int DefaultMaxTextLength = 300;

        // Parameters the fixer's derivation rules use as evidence, and the ones its
        // post-checks read (validate_param_values, cross_validate_element). Kept on
        // every element that has them, whatever the issue.
        private static readonly HashSet<string> ContextParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // classification
            "Kod_Jenis_jkr_stt", "Sistem_jkr_stt", "Sub_Sistem_jkr_stt", "Jenis_jkr_stt",
            "Bidang_Kejuruteraan_jkr_stt", "Nama_Komponen_Utama_jkr_stt",
            "Kod_DAK_Komponen_jkr_stt", "Kod_Komponen_jkr_stt",
            // material
            "Bahan_jkr_stt", "Bahan_jkr_stm", "Bahan_jkr_sit",
            // derivable per the fixer's instructions
            "LOD_jkr_sit", "Komponen_Bomba_jkr_sty", "Komponen_IBS_jkr_sty", "Kaedah_Pemasangan_jkr_sit",
            // size: the add-in sends no geometry, so these are the only dimensions
            "Tinggi_jkr_stl", "Lebar_jkr_stl", "Panjang_jkr_stl", "Diameter_jkr_stl",
        };

        // Door/window feature parameters: the instructions derive one from another
        // (glazed panel -> Kaca, frame material -> Kumai), so an issue on one keeps all.
        private static readonly HashSet<string> FeatureStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Coping", "Kumai", "Kaca", "Panel", "Bingkai",
        };

        private static readonly string[] JkrCodeParameters = { "Kod_Komponen_jkr_stt", "Kod_DAK_Komponen_jkr_stt" };

        // A JKR parameter name inside an issue's text ("Missing Type parameter: Kumai_jkr_stt").
        private static readonly Regex JkrParamToken = new Regex(@"[A-Za-z0-9_]+_jkr[_.][A-Za-z0-9_.]*", RegexOptions.CultureInvariant);

        /// <summary>
        /// A smaller copy of <paramref name="request"/>:
        /// issues without a check_id and repeats of a check_id dropped; only the
        /// elements an issue points at; on each element only the parameters its issues
        /// name (plus same-name _stt/_stm twins and the feature/finish group), the
        /// context parameters, and no blank unnamed ones; element_name and jkr_code
        /// blanked where they repeat type_name or a kept parameter; level_name, issue
        /// type_name, the unread project fields and the model block emptied;
        /// issue reason and expected_value capped at <paramref name="maxTextLength"/>.
        /// Null-safe: a null request gives an empty request, nulls inside are skipped
        /// or become "".
        /// </summary>
        public static JkrAutoFixRequest Trim(JkrAutoFixRequest request, int maxTextLength = DefaultMaxTextLength)
        {
            var result = new JkrAutoFixRequest
            {
                Project = TrimProject(request?.Project),
                Model = new JkrModelMetadata(),
            };
            if (request == null) return result;

            // Issues: the backend drops those without a check_id and keys fixes by
            // check_id; the panel maps a fix onto every candidate with that id, so one
            // copy is enough.
            var seenCheckIds = new HashSet<string>(StringComparer.Ordinal);
            var sourceIssues = new List<JkrAutoFixIssue>();
            foreach (var issue in request.Issues ?? new List<JkrAutoFixIssue>())
            {
                if (issue == null || string.IsNullOrEmpty(issue.CheckId)) continue;
                if (!seenCheckIds.Add(issue.CheckId)) continue;
                sourceIssues.Add(issue);
                result.Issues.Add(new JkrAutoFixIssue
                {
                    CheckId = issue.CheckId,
                    Rule = issue.Rule ?? "",
                    // Null would fail the backend's str fields; use its defaults.
                    Domain = issue.Domain ?? "element",
                    Status = issue.Status ?? "fail",
                    ElementId = issue.ElementId,
                    Category = issue.Category ?? "",
                    TypeName = "",
                    ActualValue = issue.ActualValue ?? "",
                    ExpectedValue = Cap(issue.ExpectedValue, maxTextLength),
                    Reason = Cap(issue.Reason, maxTextLength),
                    // AI-434: the picker's inputs, untouched (a few short codes).
                    ValidOptions = issue.ValidOptions == null ? null : new List<string>(issue.ValidOptions),
                    RequiresUserChoice = issue.RequiresUserChoice,
                });
            }

            // Elements: last one wins on a repeated id, as in the backend's elem_map;
            // output in first-seen order.
            var order = new List<long>();
            var byId = new Dictionary<long, JkrElementData>();
            foreach (var el in request.Elements ?? new List<JkrElementData>())
            {
                if (el == null) continue;
                if (!byId.ContainsKey(el.ElementId)) order.Add(el.ElementId);
                byId[el.ElementId] = el;
            }

            var issuesByElement = sourceIssues
                .GroupBy(i => i.ElementId)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var id in order)
            {
                if (!issuesByElement.TryGetValue(id, out var issues)) continue;
                result.Elements.Add(TrimElement(byId[id], issues));
            }

            return result;
        }

        private static JkrProjectMetadata TrimProject(JkrProjectMetadata project)
        {
            var trimmed = new JkrProjectMetadata();
            if (project == null) return trimmed;
            trimmed.FileName = project.FileName ?? "";
            trimmed.Discipline = project.Discipline ?? "AR";   // the backend's default
            trimmed.LodLevel = project.LodLevel;
            trimmed.ProjectPhase = project.ProjectPhase ?? "";
            return trimmed;
        }

        private static JkrElementData TrimElement(JkrElementData el, List<JkrAutoFixIssue> issues)
        {
            var source = el.Parameters ?? new Dictionary<string, string>();

            // JKR names the issues mention, and their stems (text before "_jkr").
            var issueText = string.Join("\n", issues.SelectMany(i => new[] { i.Rule, i.ExpectedValue, i.ActualValue, i.Reason })
                .Where(s => !string.IsNullOrEmpty(s)));
            var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in JkrParamToken.Matches(issueText))
                stems.Add(Stem(m.Value));
            bool featureGroup = stems.Any(s => FeatureStems.Contains(s));
            bool finishGroup = stems.Any(IsFinishStem);

            var kept = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in source)
            {
                if (kv.Key == null) continue;
                var stem = Stem(kv.Key);
                bool named = issueText.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0
                             || stems.Contains(stem)
                             || (featureGroup && FeatureStems.Contains(stem))
                             || (finishGroup && IsFinishStem(stem));
                if (named)
                {
                    // Kept as is, even blank: its value becomes the fix's old_value,
                    // and a _jkr_stm key on the element is what allows a write to it.
                    kept[kv.Key] = kv.Value;
                }
                else if (ContextParameters.Contains(kv.Key)
                         && (!string.IsNullOrWhiteSpace(kv.Value) || kv.Key.EndsWith("_jkr_stm", StringComparison.OrdinalIgnoreCase)))
                {
                    // Blank ones carry nothing, except a _jkr_stm key: its presence is read.
                    kept[kv.Key] = kv.Value;
                }
            }

            var typeName = el.TypeName ?? "";
            var elementName = el.ElementName ?? "";
            var jkrCode = el.JkrCode ?? "";
            // The backend reads `element_name or type_name`, so a copy of the type name adds nothing.
            if (string.Equals(elementName, typeName, StringComparison.Ordinal)) elementName = "";
            // jkr_code is read from one of these two parameters; drop it when that one is sent.
            if (jkrCode.Length > 0 && JkrCodeParameters.Any(p => kept.TryGetValue(p, out var v) && string.Equals(v, jkrCode, StringComparison.Ordinal)))
                jkrCode = "";

            return new JkrElementData
            {
                ElementId = el.ElementId,
                Category = el.Category ?? "",
                TypeName = typeName,
                ElementName = elementName,
                FamilyName = el.FamilyName ?? "",
                LevelName = "",
                JkrCode = jkrCode,
                Parameters = new Dictionary<string, string>(kept, StringComparer.Ordinal),
            };
        }

        /// <summary>"Kumai_jkr_stt" -> "Kumai"; "Kod_Tinggi_jkr_jkr_stt" -> "Kod_Tinggi".</summary>
        private static string Stem(string name)
        {
            var i = name.IndexOf("_jkr", StringComparison.OrdinalIgnoreCase);
            return i > 0 ? name.Substring(0, i) : name;
        }

        private static bool IsFinishStem(string stem) =>
            stem.StartsWith("Kemasan", StringComparison.OrdinalIgnoreCase);

        private static string Cap(string text, int max)
        {
            text = text ?? "";
            if (max <= 0 || text.Length <= max) return text;
            var cut = max - 1;
            if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
            return text.Substring(0, cut) + "…";
        }
    }
}
