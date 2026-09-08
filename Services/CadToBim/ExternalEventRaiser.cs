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
                return _event.Raise() == ExternalEventRequest.Accepted;
            }
            catch
            {
                return false;
            }
        }
    }
}
