// MEP domain vocabulary — the ONE place in Layer 0 where a discipline is
// named. Everything else in BinaVibe.Mcp.Tools.Mep branches on capability
// flags (IMepSystemDriver.Capabilities), never on "is this electrical".
//
// Why a kind enum at all when Autodesk.Revit.DB.Domain exists: Domain is a
// CONNECTOR property. A system, a category and a wire string all need the
// same discipline label, and Domain alone cannot express "cable tray and
// conduit are electrical distribution" (DomainCableTrayConduit is its own
// member) nor round-trip to the plain words the agent speaks.
//
// UNITS: none. No geometry crosses this file.
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace BinaVibe.Mcp.Tools.Mep
{
    /// <summary>Discipline label for a system, connector or element.</summary>
    internal enum MepDomainKind
    {
        Unknown = 0,
        Electrical,
        Mechanical,
        Piping,
    }

    internal static class MepDomains
    {
        // ─── wire strings ───────────────────────────────────────────────

        /// <summary>Plain word the agent passes as ``domain``. Returns Unknown
        /// rather than throwing so callers can produce their own error naming
        /// what IS accepted.</summary>
        public static MepDomainKind Parse(string? word)
        {
            if (string.IsNullOrWhiteSpace(word)) return MepDomainKind.Unknown;
            switch (word!.Trim().ToLowerInvariant())
            {
                case "electrical": case "electric": case "power": case "elektrik":
                    return MepDomainKind.Electrical;
                case "mechanical": case "hvac": case "duct": case "air":
                    return MepDomainKind.Mechanical;
                case "piping": case "pipe": case "plumbing": case "paip":
                    return MepDomainKind.Piping;
                default:
                    return MepDomainKind.Unknown;
            }
        }

        /// <summary>Canonical wire string for a kind — what tool results carry.</summary>
        public static string ToWire(MepDomainKind kind) => kind switch
        {
            MepDomainKind.Electrical => "electrical",
            MepDomainKind.Mechanical => "mechanical",
            MepDomainKind.Piping => "piping",
            _ => "unknown",
        };

        /// <summary>Every accepted ``domain`` word, for error messages.</summary>
        public static IReadOnlyList<string> AcceptedWords { get; } =
            new[] { "electrical", "mechanical", "piping" };

        // ─── Revit Domain ───────────────────────────────────────────────

        /// <summary>Connector Domain to kind. DomainCableTrayConduit maps to
        /// Electrical: a tray connector is electrical distribution even though
        /// Revit gives it its own Domain member.</summary>
        public static MepDomainKind FromConnectorDomain(Domain d) => d switch
        {
            Domain.DomainElectrical => MepDomainKind.Electrical,
            Domain.DomainCableTrayConduit => MepDomainKind.Electrical,
            Domain.DomainHvac => MepDomainKind.Mechanical,
            Domain.DomainPiping => MepDomainKind.Piping,
            _ => MepDomainKind.Unknown,
        };

        /// <summary>The Domain a driver of this kind creates systems in.
        /// Electrical answers DomainElectrical, NOT DomainCableTrayConduit —
        /// circuits live on electrical connectors.</summary>
        public static Domain ToConnectorDomain(MepDomainKind kind) => kind switch
        {
            MepDomainKind.Electrical => Domain.DomainElectrical,
            MepDomainKind.Mechanical => Domain.DomainHvac,
            MepDomainKind.Piping => Domain.DomainPiping,
            _ => Domain.DomainUndefined,
        };

        /// <summary>Legacy label kept byte-identical to the routing tools'
        /// original DomainToString so list_connectors' contract does not move
        /// under existing callers ("duct"/"pipe", not "mechanical"/"piping").</summary>
        public static string ConnectorDomainLabel(Domain d) => d switch
        {
            Domain.DomainHvac => "duct",
            Domain.DomainPiping => "pipe",
            Domain.DomainElectrical => "electrical",
            Domain.DomainCableTrayConduit => "cable_tray_conduit",
            _ => "undefined",
        };

        // ─── systems ────────────────────────────────────────────────────

        /// <summary>Discipline of a live MEPSystem, by concrete type. Falls
        /// back to the base equipment connector's Domain for subclasses this
        /// build does not know.</summary>
        public static MepDomainKind KindOf(MEPSystem system)
        {
            if (system == null) return MepDomainKind.Unknown;
            if (system is ElectricalSystem) return MepDomainKind.Electrical;
            if (system is MechanicalSystem) return MepDomainKind.Mechanical;
            if (system is PipingSystem) return MepDomainKind.Piping;
            try
            {
                var c = system.BaseEquipmentConnector;
                if (c != null) return FromConnectorDomain(c.Domain);
            }
            catch { }
            return MepDomainKind.Unknown;
        }

        /// <summary>The Type a FilteredElementCollector should ask for.</summary>
        public static Type SystemClass(MepDomainKind kind) => kind switch
        {
            MepDomainKind.Electrical => typeof(ElectricalSystem),
            MepDomainKind.Mechanical => typeof(MechanicalSystem),
            MepDomainKind.Piping => typeof(PipingSystem),
            _ => typeof(MEPSystem),
        };

        // ─── connector system type name ─────────────────────────────────

        /// <summary>Human name of the system a connector belongs to. Prefers
        /// the real MEPSystem name; falls back to the per-domain enum, each of
        /// which can throw on a connector that has none — hence the try/catch
        /// rather than a null check.</summary>
        public static string? ConnectorSystemTypeName(Connector c)
        {
            string? name = null;
            try { name = c.MEPSystem?.Name; } catch { }
            if (name != null) return name;
            try
            {
                name = c.Domain switch
                {
                    Domain.DomainHvac => c.DuctSystemType.ToString(),
                    Domain.DomainPiping => c.PipeSystemType.ToString(),
                    Domain.DomainElectrical => c.ElectricalSystemType.ToString(),
                    _ => null,
                };
            }
            catch { name = null; }
            return name;
        }
    }
}
