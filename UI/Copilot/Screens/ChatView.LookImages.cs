// ChatView: the `look` tool's view images while the turn is still running
// (row G-03). The reply streams its comments on the picture before turn_end,
// so the picture shows under the growing reply (LookImageStash, fed by
// PaneToolRunner). Once the turn ends the reply's evidence draws the same
// images (TurnEvidenceView), and this live row is gone with the live message.
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.Services;

namespace RevitWebAppSync.UI.Copilot.Screens
{
    public partial class ChatView
    {
        // One decoded thumbnail per image for the turn: stream ticks re-parent
        // it instead of decoding the PNG again (and keep its enlarge state).
        private readonly Dictionary<ImageDto, FrameworkElement> _liveLookEls = new Dictionary<ImageDto, FrameworkElement>();

        /// <summary>The running turn's look images, or null when there are none.</summary>
        private FrameworkElement LiveLookImages()
        {
            if (Vm == null || !Vm.IsSending) return null;
            var images = LookImageStash.Current();
            if (images.Count == 0) return null;
            var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 6) };
            foreach (var img in images)
            {
                if (!_liveLookEls.TryGetValue(img, out var el))
                {
                    el = Controls.ConfirmCard.Thumbnail(img, expandable: true);
                    if (el == null) continue;   // a broken image never breaks the reply
                    _liveLookEls[img] = el;
                }
                else if (el.Parent is Panel old) old.Children.Remove(el);
                panel.Children.Add(el);
            }
            return panel.Children.Count == 0 ? null : panel;
        }
    }
}
