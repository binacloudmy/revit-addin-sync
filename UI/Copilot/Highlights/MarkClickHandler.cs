#nullable enable
// A click on a result mark in the model (result marks design, rule 7; build step 2). Revit draws the marks as
// TemporaryGraphicsManager in-canvas controls; a click on one goes to the server registered with Revit's
// temporary-graphics external service, with the control's index. This is that server, registered once per session
// (in Revit's API context, on the first marks drawn). It only passes the document and the index on: InModelResult
// maps the index to its mark (element and group) and does the rest through Revit's API context.
// ITemporaryGraphicsHandler and TemporaryGraphicsCommandData are in RevitAPIUI from Revit 2022 on (2023 to 2027 here).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExternalService;
using Autodesk.Revit.UI;

namespace RevitWebAppSync.UI.Copilot.Highlights
{
    internal sealed class MarkClickHandler : ITemporaryGraphicsHandler
    {
        private static readonly Guid ServerId = new Guid("6f1c2b9e-4a57-4d0e-9b8f-3c2e7a1d5b64");
        private static bool _registered;

        /// <summary>A mark (control index) was clicked in this document. Raised on Revit's thread.</summary>
        public static event Action<Document, int>? Clicked;

        /// <summary>Register the handler with Revit's temporary-graphics service, once. Call in Revit's API context. False when
        /// Revit has no such service or refused; the marks still draw, a click on one then does nothing.</summary>
        public static bool EnsureRegistered()
        {
            if (_registered) return true;
            try
            {
                var service = ExternalServiceRegistry.GetService(ExternalServices.BuiltInExternalServices.TemporaryGraphicsHandlerService);
                if (service == null) return false;
                var handler = new MarkClickHandler();
                if (!service.IsRegisteredServerId(ServerId)) service.AddServer(handler);
                if (service is SingleServerService single) single.SetActiveServer(ServerId);
                else if (service is MultiServerService multi)
                {
                    // Another add-in's handler stays active beside ours: each one only answers its own controls.
                    var active = new List<Guid>(multi.GetActiveServerIds());
                    if (!active.Contains(ServerId)) { active.Add(ServerId); multi.SetActiveServers(active); }
                }
                _registered = true;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[BINA] mark click handler: " + ex.Message);
                return false;
            }
        }

        public void OnClick(TemporaryGraphicsCommandData data)
        {
            try { if (data != null) Clicked?.Invoke(data.Document, data.Index); }
            catch (Exception ex) { Debug.WriteLine("[BINA] mark click: " + ex.Message); }
        }

        public string GetName() => "BINA Copilot result marks";
        public string GetDescription() => "Selects the element under a Copilot result mark and shows its row in the Copilot pane.";
        public string GetVendorId() => "BINA";
        public Guid GetServerId() => ServerId;
        public ExternalServiceId GetServiceId() => ExternalServices.BuiltInExternalServices.TemporaryGraphicsHandlerService;
    }
}
