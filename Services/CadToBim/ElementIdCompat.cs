// ElementIdCompat — the one place the ElementId value/IntegerValue drift lives.
// net48 builds against Revit 2023 (IntegerValue : int, no Value); net8/net10 build
// against 2025/2027 (Value : long, IntegerValue obsolete). Services/Net48Shims.cs
// also gives net48 an `.Value` extension (int) for the rest of the codebase — ToLong
// reads via that shim (implicit widening). ToElementId must use #if because the
// constructor signature differs: int ctor on net48, long ctor on net8+.

using Autodesk.Revit.DB;

namespace RevitWebAppSync.Services.CadToBim
{
    internal static class ElementIdCompat
    {
        public static long ToLong(ElementId id)
        {
            if (id == null) return -1;
            return id.Value;  // Net48Shims provides .Value as int extension; widens to long implicitly
        }

        public static ElementId ToElementId(long value)
        {
#if NETFRAMEWORK
            return new ElementId(checked((int)value));
#else
            return new ElementId(value);
#endif
        }
    }
}
