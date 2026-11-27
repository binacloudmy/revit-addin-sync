#if HAS_ODA
// AI-647: the ODA-typed half of OdaRuntime. Touched only on the ODA thread, after the
// assembly resolver is attached. The licence values exist only as locals passed to odActivate.

using System.Collections.Generic;
using ODA.Drawings.TD_DbCoreIntegrated;
using ODA.Kernel.TD_RootIntegrated;

namespace RevitWebAppSync.Services.CadToBim.Dwg
{
    internal static class OdaSession
    {
        private static SystemServices _services;
        internal static HostServices Host;
        internal static readonly List<string> HostWarnings = new List<string>();
        internal static string Version = "";

        public static void Start(string keyFile)
        {
            var (info, signature, _) = OdaKeyFile.Read(keyFile);
            _services = new SystemServices(info, signature);
            TD_DbCoreIntegrated_Globals.odInitialize(_services);
            Host = new HostServices();
            Version = TD_DbCoreIntegrated_Globals.oddbGetLibraryInfo().getLibVersion();
        }

        public static void Stop()
        {
            Host = null;
            TD_DbCoreIntegrated_Globals.odUninitialize();
            _services = null;
        }

        private sealed class SystemServices : RxSystemServicesImpl
        {
            public SystemServices(string userInfo, string userSignature)
                => TD_RootIntegrated_Globals.odActivate(userInfo, userSignature);
        }

        internal sealed class HostServices : ExHostAppServices
        {
            public override void warning(string msg) => HostWarnings.Add(msg);
            public override string product() => "BINA CAD to BIM";
        }
    }
}
#endif
