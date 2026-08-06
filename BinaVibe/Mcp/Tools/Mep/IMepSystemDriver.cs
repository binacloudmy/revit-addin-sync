// The discipline seam. Layer 0's system tools call ONLY this interface, so
// adding Mechanical or Plumbing later is a new file implementing it — not an
// edit to any tool.
//
// WHY A SEAM IS NEEDED AT ALL (the three domains do not share a creation API):
//
//   ElectricalSystem.Create(Document, IList<ElementId>, ElectricalSystemType)
//   Document.Create.NewMechanicalSystem(Connector base, ConnectorSet, DuctSystemType)
//   Document.Create.NewPipingSystem  (Connector base, ConnectorSet, PipeSystemType)
//
// Electrical takes ELEMENT IDS and does not require the devices to be
// physically connected — a circuit is a logical label over a set of loads.
// Mechanical and piping take a BASE CONNECTOR plus a ConnectorSet whose owners
// must already be free, domain-matched and category-restricted; RevitAPI.xml
// on NewMechanicalSystem: "the owner of BaseConnector should be a mechanical
// equipment, and the owner of other connectors should be a mechanical
// equipment or air terminal", and "they should not have been used in another
// system".
//
// So "connect the elements, then wrap them in a system" is true for HVAC and
// piping and FALSE for electrical circuits. That asymmetry is expressed as
// MepDriverCapabilities data, which the Layer 0 tools branch on. No Layer 0
// file asks "is this electrical".
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools.Mep
{
    /// <summary>What Layer 0 may assume about a discipline. Every pre-flight
    /// check reads these instead of testing the domain.</summary>
    internal readonly struct MepDriverCapabilities
    {
        /// <summary>Membership IMPLIES a physical connector path between
        /// members (duct, pipe). False for electrical circuits.</summary>
        public bool RequiresPhysicalConnection { get; init; }

        /// <summary>Every member must expose a FREE, domain-matching connector
        /// at creation time, because the API consumes connectors rather than
        /// ids.</summary>
        public bool RequiresFreeConnector { get; init; }

        /// <summary>MEPSystem.DivideSystem: "This function only works for Hvac
        /// and Piping system." Gate any split remediation on this rather than
        /// assuming it.</summary>
        public bool SupportsDivide { get; init; }

        /// <summary>Categories a member may belong to AT CREATION. Empty means
        /// unrestricted. Pre-checking this is what stops "the agent passed duct
        /// ids to NewMechanicalSystem" from looking like a broken tool.</summary>
        public IReadOnlyCollection<BuiltInCategory> ValidMemberCategories { get; init; }
    }

    /// <summary>Everything Layer 0 knows about a create request. A driver that
    /// needs more reads it from <see cref="RawArgs"/> — Layer 0 never does.</summary>
    internal readonly struct MepSystemCreateRequest
    {
        public IReadOnlyList<ElementId> MemberIds { get; init; }
        public ElementId? BaseEquipmentId { get; init; }
        /// <summary>Which connector on the base equipment to anchor to, when
        /// the discipline needs one. Null = let the driver choose.</summary>
        public int? BaseConnectorIndex { get; init; }
        /// <summary>Discipline-specific system type ("power", "supply_air", a
        /// real PipingSystemType name). Null = the driver's default.</summary>
        public string? SystemTypeName { get; init; }
        /// <summary>Name to stamp on the new system, if the discipline lets it
        /// be set.</summary>
        public string? SystemName { get; init; }
        /// <summary>Driver-only extras. Layer 0 NEVER reads this.</summary>
        public JsonElement RawArgs { get; init; }
    }

    internal readonly struct MepSystemCreateResult
    {
        public MEPSystem System { get; init; }
        public List<string> Warnings { get; init; }
    }

    internal interface IMepSystemDriver
    {
        MepDomainKind Kind { get; }

        /// <summary>System-type words this driver answers to, so
        /// create_mep_system can pick a driver from ``system_type`` alone when
        /// ``domain`` is omitted.</summary>
        IReadOnlyCollection<string> SystemTypeAliases { get; }

        MepDriverCapabilities Capabilities { get; }

        /// <summary>Every system of this discipline in the document.</summary>
        FilteredElementCollector Collect(Document doc);

        /// <summary>Create the system. Runs INSIDE a transaction the caller
        /// owns — throw on failure, never return a half-made system, and never
        /// open a transaction here.</summary>
        MepSystemCreateResult Create(Document doc, in MepSystemCreateRequest req);

        void AddMembers(Document doc, MEPSystem system, IReadOnlyList<ElementId> ids);

        /// <summary>Remove members. Must refuse a request that would empty the
        /// system — RevitAPI.xml on MEPSystem.Remove: "It is forbidden to
        /// remove all terminal elements from system".</summary>
        void RemoveMembers(Document doc, MEPSystem system, IReadOnlyList<ElementId> ids);

        /// <summary>Attach or detach the base equipment. Electrical uses
        /// SelectPanel/DisconnectPanel; mechanical and piping set
        /// BaseEquipmentConnector. There is no settable MEPSystem.BaseEquipment,
        /// which is why this is a driver method and not a Layer 0 assignment.</summary>
        void SetBaseEquipment(Document doc, MEPSystem system, ElementId? equipmentId, int? connectorIndex);

        /// <summary>Delete the system wrapper. Returns the ids Revit actually
        /// deleted — the caller reports them verbatim rather than claiming what
        /// "totally dependent" covered.</summary>
        IReadOnlyList<long> Delete(Document doc, MEPSystem system, bool deleteDependents);

        /// <summary>Merge discipline-specific fields into a result row. Reads
        /// only — this runs on the read path.</summary>
        void Describe(MEPSystem system, IDictionary<string, object?> row);
    }
}
