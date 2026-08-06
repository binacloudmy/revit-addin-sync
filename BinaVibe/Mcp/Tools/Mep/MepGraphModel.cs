// MEP connector-graph DTOs — pure, Revit-free, MILLIMETRES ONLY.
//
// Split from MepGraphTools.cs precisely so it can be linked into
// Tests/Tests.csproj (that project lists addin sources one by one; anything
// touching Autodesk.Revit.DB is untestable there). Same reason SocketLayout.cs
// was split out of SocketCandidates.cs.
//
// THE ONE DESIGN DECISION WORTH READING: GraphSystem carries
// RequiresPhysicalConnection as DATA. That is how the checks stay
// discipline-blind while still applying the electrical/mechanical asymmetry —
// an electrical circuit is a logical label over devices that need not touch,
// a duct system is a claim about a physically connected network. The driver
// stamps the flag; MepGraphChecks never asks what discipline it is looking at.
using System;
using System.Collections.Generic;

namespace BinaVibe.Mcp.Tools.Mep
{
    /// <summary>One element in the graph.</summary>
    public sealed class GraphNode
    {
        /// <summary>Revit element id.</summary>
        public long Id;
        /// <summary>curve | fitting | terminal | equipment | device | unknown.</summary>
        public string Kind = "unknown";
        public string? Category;
        public string? TypeName;
        /// <summary>Wire domain word, see MepDomains.ToWire.</summary>
        public string Domain = "unknown";
        /// <summary>Systems that list this element as a member.</summary>
        public List<long> ClaimedBySystemIds = new();
    }

    /// <summary>One connector on one element.</summary>
    public sealed class GraphPort
    {
        public long OwnerId;
        /// <summary>Index within the owner's connector manager.</summary>
        public int Index;
        public string Domain = "unknown";
        public bool IsConnected;
        /// <summary>Null when the connector has no readable origin — a panel's
        /// LOGICAL connector is the common case, and it is not a defect.</summary>
        public double? XMm, YMm, ZMm;
        /// <summary>Owners on the far side of this connector's PHYSICAL refs.</summary>
        public List<long> LinkedOwnerIds = new();
    }

    /// <summary>An undirected physical connection. Normalised so A &lt; B —
    /// two elements joined at two points still produce one edge.</summary>
    public readonly struct GraphEdge : IEquatable<GraphEdge>
    {
        public readonly long A;
        public readonly long B;

        public GraphEdge(long a, long b)
        {
            A = a < b ? a : b;
            B = a < b ? b : a;
        }

        public bool Equals(GraphEdge other) => A == other.A && B == other.B;
        public override bool Equals(object? obj) => obj is GraphEdge e && Equals(e);
        public override int GetHashCode() => (A, B).GetHashCode();
        public override string ToString() => $"{A}-{B}";
    }

    /// <summary>A system as the model reports it, plus the one capability flag
    /// the checks need.</summary>
    public sealed class GraphSystem
    {
        public long Id;
        public string Name = "";
        public string Domain = "unknown";
        /// <summary>True when membership IMPLIES a physical connection (duct,
        /// pipe). False for electrical circuits, where membership is logical.</summary>
        public bool RequiresPhysicalConnection;
        public long? BaseEquipmentId;
        public List<long> MemberIds = new();
        /// <summary>MEPSystem.GetPhysicalNetworksNumber, or null when the model
        /// would not answer. Used as a free oracle on ConnectedComponents.</summary>
        public int? RevitPhysicalNetworkCount;
    }

    /// <summary>Everything the checks operate on.</summary>
    public sealed class MepGraph
    {
        public List<GraphNode> Nodes = new();
        public List<GraphPort> Ports = new();
        public List<GraphEdge> Edges = new();
        public List<GraphSystem> Systems = new();
        /// <summary>The walk hit its node cap — findings are about a partial
        /// graph and must be reported as such.</summary>
        public bool Truncated;
        public int NodeCap;

        public GraphNode? Node(long id)
        {
            foreach (var n in Nodes) if (n.Id == id) return n;
            return null;
        }
    }

    /// <summary>One problem, or one thing worth saying.</summary>
    public sealed class GraphFinding
    {
        /// <summary>Stable machine code — the agent branches on this, not on
        /// the message. See MepGraphChecks for the full set.</summary>
        public string Code = "";
        /// <summary>error | warning | info. Only `error` clears GraphReport.Ok.</summary>
        public string Severity = "warning";
        public string Message = "";
        public long? SystemId;
        public List<long> ElementIds = new();
        /// <summary>Where to zoom, when the finding has a location.</summary>
        public double? XMm, YMm, ZMm;
    }

    public sealed class GraphReport
    {
        /// <summary>No error-severity findings. NOT the tool's ``ok`` — a tool
        /// that successfully found problems still returns ok:true.</summary>
        public bool Ok;
        public List<GraphFinding> Findings = new();
        /// <summary>Number of connected components across the whole graph.</summary>
        public int ComponentCount;
        public Dictionary<long, int> ComponentOfNode = new();
        public bool Truncated;
        /// <summary>Findings were dropped at MaxFindings.</summary>
        public bool FindingsTruncated;
    }

    public sealed class GraphCheckOptions
    {
        public bool ReportOpenConnectors = true;

        /// <summary>Node kinds whose open connectors are normal, not defects:
        /// a diffuser has one duct connector and nothing downstream, a panel
        /// has spare ways. Reporting those buries the real findings.</summary>
        public HashSet<string> OpenConnectorExemptKinds =
            new(StringComparer.OrdinalIgnoreCase) { "terminal", "equipment", "device" };

        /// <summary>A design in progress legitimately has open ends, so an open
        /// connector is a warning by default. Set true when the caller asserts
        /// the network is meant to be closed.</summary>
        public bool TreatOpenConnectorAsError = false;

        public int MaxFindings = 200;
    }
}
