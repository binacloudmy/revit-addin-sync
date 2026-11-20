using System;

namespace RevitWebAppSync.Services.JkrSpec
{
    // ─────────────────────────────────────────────────────────────────────────
    // What the JKR Model Audit reads from a Revit document, decided without Revit.
    // SpecModelReader (the only Revit-dependent file) turns an element into the plain
    // facts below and asks these rules; the rules are unit-tested on the Mac harness.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Where an element sits relative to Revit design options.</summary>
    public enum SpecDesignOptionKind
    {
        /// <summary>In the main model (no design option).</summary>
        None,
        /// <summary>In the primary option of an option set: shown in the main model.</summary>
        Primary,
        /// <summary>In a secondary option: an alternative that is not part of the built model.</summary>
        NonPrimary,
    }

    /// <summary>The element's status in the project's final phase (Revit ElementOnPhaseStatus).</summary>
    public enum SpecPhaseState
    {
        /// <summary>No phase information (levels, grids, most annotation) or not read: kept.</summary>
        Unknown,
        Existing,
        New,
        Temporary,
        /// <summary>Demolished in the final phase itself (still drawn there, but not built).</summary>
        Demolished,
        /// <summary>Demolished in an earlier phase: gone by the final phase.</summary>
        Past,
        /// <summary>Created after the final phase.</summary>
        Future,
    }

    /// <summary>Whether a parameter the check needs is on the element at all, and filled.</summary>
    public enum SpecParamState
    {
        /// <summary>The element does not carry the parameter (not loaded / not bound / renamed).</summary>
        Missing,
        /// <summary>The parameter exists but has no value (or only white space).</summary>
        Empty,
        Filled,
    }

    /// <summary>A parameter as shown, with whether it was there.</summary>
    public readonly struct SpecParamValue
    {
        public SpecParamState State { get; }
        /// <summary>The value as Revit shows it; "" when missing or empty.</summary>
        public string Text { get; }

        public SpecParamValue(SpecParamState state, string text)
        {
            State = state;
            Text = text ?? "";
        }
    }

    public static class SpecExtractionRules
    {
        /// <summary>Tally keys in <see cref="SpecModel.Excluded"/>.</summary>
        public const string ExcludedDesignOption = "design-option";
        public const string ExcludedDemolished = "demolished";
        public const string ExcludedLinked = "linked";

        /// <summary>
        /// The model audit judges the file under audit only. Linked models (ST / ME / EL
        /// and AR parts) are other deliverables with their own audit; the spec (A3, C5) only
        /// asks that the links EXIST, never that their content counts as this model's. Kept
        /// as a constant so the decision is stated in one place and pinned by a test; changing
        /// it is a product decision (see the AI-471 PR, question 1).
        /// </summary>
        public const bool CountLinkedContent = false;

        /// <summary>
        /// Null = read the element; otherwise the <c>Excluded*</c> key that says why it is not
        /// part of the built model under audit.
        /// • linked: element lives in a linked document (never counted, see
        ///   <see cref="CountLinkedContent"/>);
        /// • non-primary design option: an alternative, not built;
        /// • demolished / past / future in the final phase: not part of the built model.
        /// </summary>
        public static string ExcludeReason(bool fromLink, SpecDesignOptionKind option, SpecPhaseState phase)
        {
            if (fromLink && !CountLinkedContent) return ExcludedLinked;
            if (option == SpecDesignOptionKind.NonPrimary) return ExcludedDesignOption;
            if (phase == SpecPhaseState.Demolished || phase == SpecPhaseState.Past || phase == SpecPhaseState.Future)
                return ExcludedDemolished;
            return null;
        }

        /// <summary>Maps the Revit enum names (ElementOnPhaseStatus.ToString()) to <see cref="SpecPhaseState"/>.</summary>
        public static SpecPhaseState PhaseOf(string revitStatusName)
        {
            switch ((revitStatusName ?? "").Trim())
            {
                case "Existing": return SpecPhaseState.Existing;
                case "New": return SpecPhaseState.New;
                case "Temporary": return SpecPhaseState.Temporary;
                case "Demolished": return SpecPhaseState.Demolished;
                case "Past": return SpecPhaseState.Past;
                case "Future": return SpecPhaseState.Future;
                default: return SpecPhaseState.Unknown;   // "None" and anything new
            }
        }

        /// <summary>
        /// Missing vs empty. <paramref name="carried"/> = the element has the parameter;
        /// <paramref name="value"/> = what it shows. Never confuses "no such parameter"
        /// with "parameter left blank": the spec treats both as not filled where a value is
        /// mandatory (B1.1, Db.1) but names them differently, and a check that could not read
        /// the parameter at all is not a check that found it blank.
        /// </summary>
        public static SpecParamValue Classify(bool carried, string value)
        {
            if (!carried) return new SpecParamValue(SpecParamState.Missing, "");
            var v = value ?? "";
            return string.IsNullOrWhiteSpace(v)
                ? new SpecParamValue(SpecParamState.Empty, "")
                : new SpecParamValue(SpecParamState.Filled, v);
        }

        /// <summary>
        /// Db.1 verdict for one mandatory text parameter on a type, from the snapshot.
        /// <see cref="SpecType.ParamNamesRead"/> false (old snapshot / fixture): a parameter
        /// the type's texts do not hold is unknown (null), not missing.
        /// </summary>
        public static SpecParamState? MandatoryState(SpecType t, string name)
        {
            if (t.Texts.TryGetValue(name, out var text))
                return Classify(true, text).State;
            if (!t.ParamNamesRead) return null;
            // Carried but not text-typed: the snapshot holds no text to judge.
            if (t.ParamNames.Contains(name)) return null;
            return SpecParamState.Missing;
        }
    }
}
