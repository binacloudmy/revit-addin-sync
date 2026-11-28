#nullable enable
// UX review Q6 (18 Nov): the canvas bar read "None of the 1 is visible in this view" for one changed element: it reads as an error. One element is "Not
// visible in this view" / "Not on screen"; the counts above one keep their words.
using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Copilot.Highlights;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class BarOneElement0138Tests
    {
        private static KeyText KeyOf(int n, bool inView)
        {
            var set = new HighlightSet { Kinds = { new MarkGroup { Title = "Doors", Category = "Doors", Outcome = MarkOutcome.Changed, Ids = Enumerable.Range(1, n).Select(i => (long)i).ToList(), Count = n } } };
            var places = set.Kinds[0].Ids.Select(id => new ElementPlace { Id = id, Group = 0, InActiveView = inView }).ToList();
            return MarkKey.Build(set, places, "Aras 01");
        }

        [Fact]
        public void OneElementNotInTheView_IsNotVisibleHere()
        {
            Assert.Equal("Not visible in this view", KeyOf(1, inView: false).State);
        }

        [Fact]
        public void SeveralElementsNotInTheView_KeepTheirWords()
        {
            Assert.Equal("None of the 3 is visible in this view", KeyOf(3, inView: false).State);
        }

        [Fact]
        public void OneElementOffScreen_IsNotOnScreen()
        {
            var key = KeyOf(1, inView: true);
            MarkKey.ApplyOnScreen(key, drawn: 1, onScreen: 0);
            Assert.Equal("Not on screen", key.State);
            Assert.Equal("Zoom to them", key.ZoomLabel);
        }
    }
}
