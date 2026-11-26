using Autodesk.Revit.UI;

namespace RevitWebAppSync.Services.CadToBim
{
    /// <summary>Production IBuildEventRaiser over the ExternalEvent App.OnStartup created.</summary>
    public sealed class ExternalEventRaiser : IBuildEventRaiser
    {
        private readonly ExternalEvent _event;

        public ExternalEventRaiser(ExternalEvent externalEvent)
        {
            _event = externalEvent;
        }

        public bool Raise()
        {
            // A null event means OnStartup never created it (pane opened in a host without
            // Revit); "busy, try again" is the honest surface for that too.
            if (_event == null) return false;
            try
            {
                // Pending = already raised and not run yet. The handler has one request slot and
                // reads it when it runs, so the request just set is the one that runs (a queued
                // background CheckBuilt replaced by a build, AI-344).
                ExternalEventRequest result = _event.Raise();
                return result == ExternalEventRequest.Accepted || result == ExternalEventRequest.Pending;
            }
            catch
            {
                return false;
            }
        }
    }
}
