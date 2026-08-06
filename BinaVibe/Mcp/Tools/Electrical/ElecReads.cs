// Guarded Revit reads shared across the electrical tools.
//
// Every read here is wrapped because Revit THROWS rather than returning a
// blank on incomplete data: CircuitNumber / StartSlot / Length / PolesNumber
// all throw on a circuit Revit considers incomplete, and a single-phase
// distribution system's VoltageLineToLine throws rather than returning null.
// A tool that dies on the one broken circuit in the model is useless exactly
// when it is needed, so each read carries its own documented fallback.
//
// Touches the Revit API, so this file is NOT linked into Tests.csproj.

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static class ElecReads
    {
        // ─── circuits ───────────────────────────────────────────────────

        internal static Element? SafeBaseEquipment(ElectricalSystem sys)
        {
            try { return sys.BaseEquipment; }
            catch { return null; }
        }

        internal static ElectricalSystemType SafeSystemType(ElectricalSystem sys)
        {
            try { return sys.SystemType; }
            catch { return ElectricalSystemType.UndefinedSystemType; }
        }

        /// <summary>CircuitNumber throws on a circuit Revit considers
        /// incomplete — an unassigned one, for instance.</summary>
        internal static string SafeCircuitNumber(ElectricalSystem sys)
        {
            try { return sys.CircuitNumber ?? ""; }
            catch { return ""; }
        }

        internal static string SafeName(ElectricalSystem sys)
        {
            try { return sys.Name ?? ""; }
            catch { return ""; }
        }

        /// <summary>Null when unassigned or not applicable.</summary>
        internal static object? SafeStartSlot(ElectricalSystem sys)
        {
            try { return sys.StartSlot; }
            catch { return null; }
        }

        internal static int SafePoles(ElectricalSystem sys)
        {
            try { return sys.PolesNumber; }
            catch { return 1; }
        }

        internal static double SafeLengthMm(ElectricalSystem sys)
        {
            try { return sys.Length * GeomMm.MmPerFoot; }
            catch { return 0.0; }
        }

        internal static ElectricalCircuitPathMode SafePathMode(ElectricalSystem sys)
        {
            try { return sys.CircuitPathMode; }
            catch { return ElectricalCircuitPathMode.FarthestDevice; }
        }

        // ─── distribution systems ───────────────────────────────────────

        /// <summary>A voltage read that survives a distribution system with an
        /// undefined line-to-line voltage (single-phase), where the property
        /// throws rather than returning null.</summary>
        internal static double? SafeVolts(Func<double?> read)
        {
            try { return read(); }
            catch { return null; }
        }

        internal static double SafeActual(VoltageType v)
        {
            try { return v.ActualValue; }
            catch { return double.NaN; }
        }

        internal static int SafePhases(DistributionSysType d)
        {
            try { return d.ElectricalPhase == ElectricalPhase.ThreePhase ? 3 : 1; }
            catch { return 1; }
        }

        internal static int? SafeWires(DistributionSysType d)
        {
            try { return d.NumWires; }
            catch { return null; }
        }

        internal static object? SafeConfig(DistributionSysType d)
        {
            try { return d.ElectricalPhaseConfiguration.ToString(); }
            catch { return null; }
        }

        internal static bool SafeInUse(DistributionSysType d)
        {
            try { return d.IsInUse; }
            catch { return false; }
        }

        // ─── connectors ─────────────────────────────────────────────────

        internal static Domain SafeDomain(ConnectorElement c)
        {
            try { return c.Domain; }
            catch { return Domain.DomainUndefined; }
        }

        /// <summary>A ConnectorElement carries SystemClassification; the
        /// ElectricalSystemType property belongs to the runtime Connector, which
        /// a family document does not hand out.</summary>
        internal static string SafeSystemType(ConnectorElement c)
        {
            try { return c.SystemClassification.ToString(); }
            catch { return "unknown"; }
        }

        // ─── parameters ─────────────────────────────────────────────────

        /// <summary>Instance-only read, unset reported as 0.0. Deliberately
        /// distinct from ReadParam: no symbol fallback, and callers that report
        /// rating_a / apparent_load_va / voltage_v depend on the 0.0 default
        /// rather than a null.</summary>
        internal static double ParamAs(Element el, BuiltInParameter bip, ForgeTypeId unit)
        {
            var p = el.get_Parameter(bip);
            return p != null && p.HasValue
                ? UnitUtils.ConvertFromInternalUnits(p.AsDouble(), unit)
                : 0.0;
        }

        /// <summary>Instance-then-symbol walk, since JKR families set electrical
        /// values on either. Null when neither carries a value.</summary>
        internal static Parameter? ReadParam(FamilyInstance? fi, BuiltInParameter bip)
        {
            if (fi == null) return null;
            var p = fi.get_Parameter(bip);
            if (p == null || !p.HasValue) p = fi.Symbol?.get_Parameter(bip);
            return p != null && p.HasValue ? p : null;
        }

        /// <summary>Unlike VoltageType.ActualValue a PARAMETER is in internal
        /// units, so this one does convert.</summary>
        internal static double? ReadVolts(FamilyInstance? fi, BuiltInParameter bip)
        {
            var p = ReadParam(fi, bip);
            if (p == null) return null;
            try { return UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.Volts); }
            catch { return null; }
        }

        /// <summary>Apparent load in VA off an instance OR a type, or null when
        /// the family carries none. Element rather than FamilyInstance on
        /// purpose: suggest_lighting_points has to know a fixture's wattage
        /// BEFORE anything is placed, so it asks the FamilySymbol.
        ///
        /// An affirmative zero reads as "no value" — a family that declares 0 VA
        /// tells us nothing, and dividing a wattage target by it is how a room
        /// asks for infinite fixtures.</summary>
        internal static double? ApparentLoadVa(Element? el)
        {
            var p = el?.get_Parameter(BuiltInParameter.RBS_ELEC_APPARENT_LOAD);
            if (p == null || !p.HasValue) return null;
            try
            {
                var va = UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.VoltAmperes);
                return va > 1e-9 ? va : (double?)null;
            }
            catch { return null; }
        }

        internal static int? ReadInt(FamilyInstance? fi, BuiltInParameter bip)
        {
            var p = ReadParam(fi, bip);
            if (p == null) return null;
            try { return p.AsInteger(); }
            catch { return null; }
        }

        // ─── rounding for the wire ──────────────────────────────────────

        internal static object? Round1(double? v) =>
            v.HasValue ? Math.Round(v.Value, 1) : (object?)null;

        /// <summary>NaN and infinity become null — they do not survive JSON.</summary>
        internal static object? Round(double v, int digits) =>
            double.IsNaN(v) || double.IsInfinity(v) ? null : (object)Math.Round(v, digits);
    }
}
