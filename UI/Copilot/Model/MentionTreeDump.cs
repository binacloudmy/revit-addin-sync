using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>
    /// Test aid: when the environment variable BINA_MENTION_DUMP names a file, the picker's tree is written there as JSON each time it is
    /// built, so a script can compare what the drafter is offered with the Project Browser without a screenshot. Off when the variable
    /// is unset (the normal case); never throws.
    /// </summary>
    public static class MentionTreeDump
    {
        public const string Variable = "BINA_MENTION_DUMP";

        public static JArray ToJson(IEnumerable<MentionNode> nodes) =>
            new JArray((nodes ?? Enumerable.Empty<MentionNode>()).Select(n =>
            {
                var o = new JObject { ["kind"] = n.Kind, ["name"] = n.Name };
                if (n.ElementId.HasValue) o["id"] = n.ElementId.Value;
                if (n.IsActive) o["active"] = true;
                if (!string.IsNullOrEmpty(n.ClassName)) o["class"] = n.ClassName;
                if (!string.IsNullOrEmpty(n.CategoryKey)) o["categoryKey"] = n.CategoryKey;
                if (n.Children.Count > 0) o["children"] = ToJson(n.Children);
                return (JToken)o;
            }));

        public static bool Enabled => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable));

        /// <summary>A second file beside the dump (path + suffix) for what the picker left out; never throws.</summary>
        public static void WriteExtra(string suffix, JToken content)
        {
            try
            {
                var path = Environment.GetEnvironmentVariable(Variable);
                if (string.IsNullOrWhiteSpace(path)) return;
                File.WriteAllText(path + suffix, content.ToString());
            }
            catch { /* a test aid never fails the picker */ }
        }

        public static void Write(IEnumerable<MentionNode> tree)
        {
            try
            {
                var path = Environment.GetEnvironmentVariable(Variable);
                if (string.IsNullOrWhiteSpace(path)) return;
                File.WriteAllText(path, ToJson(tree).ToString());
            }
            catch { /* a test aid never fails the picker */ }
        }
    }
}
