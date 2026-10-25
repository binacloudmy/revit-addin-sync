#nullable enable
// The models a check reads: the host document and each Revit link instance,
// with the link's transform and, when it cannot be read, why. Coverage gaps
// (an unloaded link, a closed workset) become not_verified outcomes in
// CheckRunner: missing coverage is never a pass. Revit-bound.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Check
{
    internal sealed class CheckSource
    {
        /// <summary>"host" or "link: STR_v3", as listed in the envelope's searched block.</summary>
        public string Label = "host";

        /// <summary>The link type name Revit shows ("STR_v3.rvt"); empty for the host.</summary>
        public string Name = "";

        /// <summary>Null when the link is not loaded.</summary>
        public Document? Doc;

        /// <summary>The RevitLinkInstance id; null for the host.</summary>
        public long? LinkId;

        /// <summary>Link coordinates to host coordinates.</summary>
        public Transform Transform = Transform.Identity;

        /// <summary>Why the source cannot be read, e.g. "link STR_v3 is not loaded".</summary>
        public string? UnavailableReason;

        public bool IsHost => LinkId == null;

        public ElementRef RefOf(Element e) => new ElementRef(e.Id.Value, LinkId);

        /// <summary>The ref of the link instance itself (for "this link is unloaded" outcomes).</summary>
        public ElementRef InstanceRef => new ElementRef(LinkId ?? Evaluated.NoElement, null);
    }

    internal static class RevitCheckSources
    {
        public static CheckSource Host(Document doc) => new CheckSource { Doc = doc };

        /// <summary>Every Revit link instance in the host, loaded or not.</summary>
        public static List<CheckSource> Links(Document host)
        {
            var list = new List<CheckSource>();
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var inst in new FilteredElementCollector(host).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var type = host.GetElement(inst.GetTypeId());
                var name = type?.Name ?? inst.Name;
                var clean = LinkNames.Clean(name);
                seen[clean] = seen.TryGetValue(clean, out var n) ? n + 1 : 1;
                var label = "link: " + clean + (seen[clean] > 1 ? $" ({seen[clean]})" : "");

                Document? linkDoc = null;
                string? why = null;
                try
                {
                    linkDoc = inst.GetLinkDocument();
                }
                catch (Exception ex)
                {
                    why = $"link {clean} could not be opened: {ex.Message}";
                }
                var transform = Transform.Identity;
                try
                {
                    transform = inst.GetTotalTransform();
                }
                catch
                {
                    // An unloaded link may refuse its transform; nothing is read from it then.
                }
                if (linkDoc == null) why ??= $"link {clean} is not loaded{Status(type)}";

                list.Add(new CheckSource
                {
                    Label = label,
                    Name = name,
                    Doc = linkDoc,
                    LinkId = inst.Id.Value,
                    Transform = transform,
                    UnavailableReason = why,
                });
            }
            return list;
        }

        private static string Status(Element? type)
        {
            try
            {
                if (type is RevitLinkType linkType)
                {
                    var status = linkType.GetLinkedFileStatus();
                    if (status != LinkedFileStatus.Loaded) return $" ({status})";
                }
            }
            catch
            {
                // The status is only decoration on the reason.
            }
            return "";
        }

        /// <summary>"workset 'Furniture' is closed" for each closed user workset: its
        /// elements are not in memory, so no collector can see them.</summary>
        public static List<string> ClosedWorksets(Document doc)
        {
            var gaps = new List<string>();
            if (!doc.IsWorkshared) return gaps;
            try
            {
                foreach (var ws in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset))
                    if (!ws.IsOpen) gaps.Add($"workset '{ws.Name}' is closed");
            }
            catch (Exception ex)
            {
                gaps.Add("worksets could not be listed: " + ex.Message);
            }
            return gaps;
        }
    }
}
