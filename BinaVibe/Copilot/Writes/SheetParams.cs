#nullable enable
// C4.2.2: a sheet's own name and number through set_parameters. Revit-free.
//
// A sheet title ("Sheet Name") and number are INSTANCE properties of the ViewSheet (SHEET_NAME / SHEET_NUMBER), never a type
// rename: a sheet has no type to rename. Round 22b/23: Copilot sent "Name" or "Sheet Name" for a sheet called "Unnamed", the
// plain LookupParameter did not always find it, and the model repeated the same refusal until the loop guard stopped the turn.
// This class holds the rules that need no Revit: which parameter names mean the sheet's name or number, and which values Revit
// refuses. SetParametersTool feeds it and resolves the built-in parameter on the ViewSheet.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Copilot.Writes
{
    public enum SheetParamKind { None, Name, Number }

    public static class SheetParams
    {
        /// <summary>Which sheet property a parameter name means, or None. "Name" counts here only because the caller has a ViewSheet in hand.</summary>
        public static SheetParamKind Classify(string? param)
        {
            var p = (param ?? "").Trim();
            if (p.Equals("Sheet Name", StringComparison.OrdinalIgnoreCase) || p.Equals("Name", StringComparison.OrdinalIgnoreCase)
                || p.Equals("Sheet Title", StringComparison.OrdinalIgnoreCase))
                return SheetParamKind.Name;
            if (p.Equals("Sheet Number", StringComparison.OrdinalIgnoreCase) || p.Equals("Number", StringComparison.OrdinalIgnoreCase))
                return SheetParamKind.Number;
            return SheetParamKind.None;
        }

        /// <summary>Why a sheet name or number cannot be used (null when it can). The 255-character limit holds for both. The characters Revit
        /// refuses in element names apply to the sheet NUMBER only: a sheet NAME such as "Plan: Level 1" may be accepted by Revit (to be
        /// verified live), and if Revit does refuse one, Parameter.Set fails and the batch reports it.</summary>
        public static string? Check(SheetParamKind kind, string? value)
        {
            var what = kind == SheetParamKind.Number ? "A sheet number" : "A sheet name";
            var v = (value ?? "").Trim();
            if (v.Length == 0) return what + " cannot be empty.";
            if (kind == SheetParamKind.Number)
            {
                var bad = v.IndexOfAny(TypeRename.ForbiddenChars);
                if (bad >= 0) return what + " cannot contain '" + v[bad] + "' (Revit refuses \\ : { } [ ] | ; < > ? ` ~).";
            }
            if (v.Length > TypeRename.MaxNameLength) return what + " is at most " + TypeRename.MaxNameLength + " characters; this one has " + v.Length + ".";
            return null;
        }

        /// <summary>Sheet numbers must be unique (names may repeat). Built once from the document's sheets; each planned number is claimed in
        /// turn, so two sheets of one call cannot take the same number.</summary>
        public sealed class NumberLedger
        {
            private readonly Dictionary<string, long> _owner = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            public NumberLedger(IEnumerable<KeyValuePair<long, string>> existing)
            {
                foreach (var kv in existing) _owner[(kv.Value ?? "").Trim()] = kv.Key;
            }

            /// <summary>The refusal when another sheet holds (or was just given) the number, else null and the number is claimed for the sheet.</summary>
            public string? Claim(long sheetId, string? number)
            {
                var v = (number ?? "").Trim();
                if (_owner.TryGetValue(v, out var holder) && holder != sheetId)
                    return "Another sheet already has the number '" + v + "'; sheet numbers must be unique, give a different one.";
                _owner[v] = sheetId;
                return null;
            }
        }

        /// <summary>Why type_param true is refused on a sheet: it has no type; the name and number belong to the sheet itself.</summary>
        public static string TypeParamRefusal(string param) =>
            "'" + param + "' is a property of the sheet itself, and a sheet has no type to change. Resend with type_param false; nothing was changed.";
    }
}
