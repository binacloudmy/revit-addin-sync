// Driver registry. One dictionary keyed by discipline, plus an alias index so
// create_mep_system can resolve from ``system_type`` when ``domain`` is
// omitted.
//
// Registration is via [ModuleInitializer] in each driver file (see
// Electrical\CircuitDriver.cs), so adding Mechanical later touches no file in
// this folder. Module initializers run before any other code in the assembly
// and the registry is an unordered dictionary, so initializer ORDER does not
// matter — but a driver constructor must make no Revit API calls, because it
// runs at assembly load, potentially before Revit hands us a Document.
//
// If the module-initializer route ever proves fragile inside the Revit addin
// loader, the fallback is Bootstrap(): one Register call per discipline,
// invoked from ToolRegistry.Invoke. Still no change to the core, one line per
// new discipline instead of zero.
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools.Mep
{
    internal static class MepSystemDrivers
    {
        private static readonly Dictionary<MepDomainKind, IMepSystemDriver> _byKind = new();
        private static readonly Dictionary<string, IMepSystemDriver> _byAlias =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Idempotent — re-registering a discipline replaces it, so a
        /// double module-initializer run cannot throw at assembly load.</summary>
        public static void Register(IMepSystemDriver driver)
        {
            if (driver == null) return;
            _byKind[driver.Kind] = driver;
            foreach (var alias in driver.SystemTypeAliases) _byAlias[alias] = driver;
        }

        public static IReadOnlyCollection<IMepSystemDriver> All => _byKind.Values;

        public static IReadOnlyCollection<MepDomainKind> RegisteredKinds => _byKind.Keys;

        public static IMepSystemDriver? TryResolve(MepDomainKind kind)
            => _byKind.TryGetValue(kind, out var d) ? d : null;

        /// <summary>Throws with the list of what IS available — an agent that
        /// asked for "plumbing" before that driver ships needs to be told it is
        /// not implemented yet, not handed a null reference.</summary>
        public static IMepSystemDriver Resolve(MepDomainKind kind)
            => TryResolve(kind) ?? throw new InvalidOperationException(
                $"no MEP system driver for '{MepDomains.ToWire(kind)}' — "
                + $"available: {string.Join(", ", _byKind.Keys.Select(MepDomains.ToWire).OrderBy(x => x))}");

        public static IMepSystemDriver? ResolveBySystemType(string? alias)
            => string.IsNullOrWhiteSpace(alias) ? null
             : _byAlias.TryGetValue(alias!.Trim(), out var d) ? d : null;

        public static IMepSystemDriver ForSystem(MEPSystem system)
            => Resolve(MepDomains.KindOf(system));

        /// <summary>Fallback registration path, kept wired so the module
        /// initializers are not the only route. Safe to call repeatedly.</summary>
        public static void Bootstrap()
        {
            // Drivers self-register via [ModuleInitializer]; this exists so a
            // caller can force the assembly's initializers to have run before
            // asking the registry anything.
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
                typeof(MepSystemDrivers).TypeHandle);
        }
    }
}
