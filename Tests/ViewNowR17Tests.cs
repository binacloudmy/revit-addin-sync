#nullable enable
// r17, tester 20 Nov: "the copilot replies with a PICTURE of the view but does not open it". The backend now claims a view open only
// when a run's result says view_now; view_now therefore names the view asked for only when Revit took the request, and the active
// view otherwise (a switch that threw), and a committed run that asks to open a view says which one too.
using BinaVibe.Copilot.Code;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ViewNowR17Tests
    {
        [Fact]
        public void NowId_TheViewAskedFor_OnlyWhenTheSwitchWasTaken()
        {
            Assert.Equal(20L, UiViewReport.NowId(20, switched: true));
            Assert.Null(UiViewReport.NowId(20, switched: false));   // the switch threw: the active view is named
            Assert.Null(UiViewReport.NowId(null, switched: false)); // nothing asked: the active view is named
        }
    }
}
