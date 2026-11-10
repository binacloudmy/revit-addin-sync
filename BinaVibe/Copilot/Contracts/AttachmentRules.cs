#nullable enable
// Attachment contract checks (AI-297; owned by W0, contracts). Revit-free.
//
// Violations() returns every way an attachment breaks
// contracts/copilot/protocol3.schema.json#/$defs/attachment that the C# types
// cannot rule out themselves. An empty list means it is valid. The contract
// tests call it; the turn router calls it before sending, so a malformed
// attachment is left out instead of failing the whole turn on the server.

using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Copilot.Contracts
{
    public static class AttachmentLimits
    {
        public const int MaxPages = 2000;
    }

    public static class AttachmentRules
    {
        public static IReadOnlyList<string> Violations(AttachmentDto a)
        {
            var v = new List<string>();
            if (string.IsNullOrEmpty(a.Name)) v.Add("name must not be empty");
            if (!AttachmentKinds.All.Contains(a.Kind))
            {
                v.Add($"kind '{a.Kind}' is not one of {string.Join("|", AttachmentKinds.All)}");
                return v;
            }

            if (a.Kind == AttachmentKinds.Table)
            {
                if (a.Columns == null || a.Rows == null) v.Add("a table attachment needs columns and rows");
                else if (a.Rows.Any(r => r.Count != a.Columns.Count)) v.Add("every table row needs one cell per column");
            }
            else if (a.Kind == AttachmentKinds.Image)
            {
                if (string.IsNullOrEmpty(a.ImageB64)) v.Add("an image attachment needs image_b64");
            }
            else
            {
                if (a.Pages == null || a.Pages.Count == 0) v.Add("a document attachment needs at least one page");
                else
                {
                    if (a.Pages.Count > AttachmentLimits.MaxPages) v.Add($"a document has at most {AttachmentLimits.MaxPages} pages");
                    for (var i = 0; i < a.Pages.Count; i++)
                    {
                        if (a.Pages[i] == null) { v.Add($"pages[{i}] is missing"); continue; }
                        if (a.Pages[i].Page < 1) v.Add($"pages[{i}].page counts from 1");
                        if (a.Pages[i].Text == null) v.Add($"pages[{i}].text is required (empty for a page without text)");
                    }
                }
                if (a.PageCount is < 1) v.Add("page_count is at least 1");
            }
            return v;
        }
    }
}
