using System;

namespace RevitWebAppSync.Services.CadToBim
{
    /// <summary>
    /// Raises the build ExternalEvent. An interface because the view model is unit-tested in a
    /// project that references RevitAPI (DB) but not RevitAPIUI, where ExternalEvent lives.
    /// </summary>
    public interface IBuildEventRaiser
    {
        /// <summary>True when Revit accepted the request (ExternalEventRequest.Accepted).</summary>
        bool Raise();
    }

    /// <summary>What the view model needs from CadToBimBuildHandler: a request slot and a
    /// completion callback. The handler implements this; tests fake it.</summary>
    public interface IBuildRequestSink
    {
        BuildRequest Request { get; set; }
        event Action<BuildReport> Completed;
    }
}
