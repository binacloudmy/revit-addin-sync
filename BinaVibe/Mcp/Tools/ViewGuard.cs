// ViewGuard — satisfy Revit's view preconditions before a call needs them,
// in ONE place, so the two callers cannot drift apart again.
//
// Why this file exists: on 2026-08-06 a drafter asked for a bungalow and got no
// roof. Three create_roof calls were refused —
//
//   footprint at level elevation -> ArgumentNullException: Value cannot be null
//   footprint at Z=0             -> ArgumentNullException: Value cannot be null
//   extrusion roof               -> InvalidOperationException: Invalid profile
//   Context: ... active view '{3D}' (View3D)
//
// — because NewFootPrintRoof wants a plan view active and the session was in
// 3D. BuildDesign already knew this and switched views first. CreateRoof, which
// takes the same uidoc, did not. The knowledge existed in one caller and not
// the other, which is the same way the roof STRATEGIES diverged before
// RoofBuilder collected them.
//
// A precondition is ours to satisfy, never the model's to reason about and
// never the drafter's to hear about. Nothing here is surfaced: it switches the
// view, does the work, and puts the view back.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BinaVibe.Mcp.Tools
{
    /// <summary>Restores the drafter's view on dispose. Use with `using`.</summary>
    internal sealed class ViewSwitch : IDisposable
    {
        private readonly UIDocument? _uidoc;
        private readonly View? _restore;

        internal ViewSwitch(UIDocument? uidoc, View? restore)
        {
            _uidoc = uidoc;
            _restore = restore;
        }

        /// <summary>True when a switch actually happened — worth reporting in a
        /// result so a surprising view change is never silent to us, even
        /// though it stays silent to the drafter.</summary>
        public bool Switched => _restore != null;

        public void Dispose()
        {
            if (_uidoc == null || _restore == null) return;
            try { _uidoc.ActiveView = _restore; } catch { /* best-effort */ }
        }
    }

    internal static class ViewGuard
    {
        /// <summary>Ensure a plan view is active for APIs that demand one.
        ///
        /// MUST be called BEFORE opening a Transaction — Revit rejects a view
        /// change while one is open, so a switch attempted inside the
        /// transaction throws instead of fixing anything.
        ///
        /// A plan view with no GenLevel is not associated with a level and does
        /// not satisfy the roof APIs, so those are skipped rather than picked
        /// and hoped for.</summary>
        public static ViewSwitch EnsurePlanView(Document doc, UIDocument? uidoc)
        {
            if (uidoc == null || uidoc.ActiveView is ViewPlan)
                return new ViewSwitch(null, null);

            var plan = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .FirstOrDefault(v => !v.IsTemplate && v.GenLevel != null);
            if (plan == null)
                return new ViewSwitch(null, null);   // nothing better available

            var previous = uidoc.ActiveView;
            try { uidoc.ActiveView = plan; }
            catch { return new ViewSwitch(null, null); }
            return new ViewSwitch(uidoc, previous);
        }
    }
}
