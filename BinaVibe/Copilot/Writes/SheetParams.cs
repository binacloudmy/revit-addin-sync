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

        /// <summary>Why a sheet name or number cannot be used (null when it can). Same characters Revit refuses in any element name.</summary>
        public static string? Check(SheetParamKind kind, string? value)
        {
            var what = kind == SheetParamKind.Number ? "A sheet number" : "A sheet name";
            var v = (value ?? "").Trim();
            if (v.Length == 0) return what + " cannot be empty.";
            var bad = v.IndexOfAny(TypeRename.ForbiddenChars);
            if (bad >= 0) return what + " cannot contain '" + v[bad] + "' (Revit refuses \\ : { } [ ] | ; < > ? ` ~).";
            if (v.Length > TypeRename.MaxNameLength) return what + " is at most " + TypeRename.MaxNameLength + " characters; this one has " + v.Length + ".";
            return null;
        }

        /// <summary>A sheet number must be unique. Sheet NAMES may repeat. Returns the refusal, or null.</summary>
        public static string? DuplicateNumber(string? value, IEnumerable<string> otherNumbers)
        {
            var v = (value ?? "").Trim();
            var clash = otherNumbers.FirstOrDefault(n => string.Equals(n, v, StringComparison.OrdinalIgnoreCase));
            return clash == null ? null : "Another sheet already has the number '" + clash + "'; sheet numbers must be unique, give a different one.";
        }

        /// <summary>Why type_param true is refused on a sheet: it has no type; the name and number belong to the sheet itself.</summary>
        public static string TypeParamRefusal(string param) =>
            "'" + param + "' is a property of the sheet itself, and a sheet has no type to change. Resend with type_param false; nothing was changed.";
    }
}
