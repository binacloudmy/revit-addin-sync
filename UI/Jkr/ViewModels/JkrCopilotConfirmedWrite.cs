using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Jkr.ViewModels
{
    /// <summary>
    /// AI-490: builds the <c>confirmed_write</c> field of the turn request (bina-ai PR #402) from the same facts the prompt and the
    /// confirm dialog are built from, so the three cannot disagree. WPF- and Revit-free.
    /// The field says: the person said Ya in our dialog to exactly this scope, so the server need not ask again.
    /// <list type="bullet">
    /// <item>Instance-level family (Db.2, Db.1, C1.6.3, C4.2.2, A5.2): <c>ids</c> = the ids of THIS prompt part only (a part confirms
    /// only the ids its own prompt lists, at most <see cref="JkrCopilotPrompt.MaxIdsPerPrompt"/>). Strict: a write on any other id is asked.</item>
    /// <item>Type-level family (Da.1, Dc.1, Dd.1): <c>ids</c> = the one INSTANCE id per type the prompt lists, <c>type_level</c> true (the server
    /// resolves the types itself) and <c>instances</c> = the total instances the dialog showed for those types.</item>
    /// <item><c>params</c> = <see cref="CopilotPromptVariant.Params"/>, the names the prompt tells Copilot to set. A family whose parameters
    /// are only known at run time (Db.1, C1.6.3) has none: no field, the server asks as before.</item>
    /// </list>
    /// A <c>Either</c> family (Db.2 Material, Db.1) is sent as instance-level: a type-parameter write through it is asked by the server
    /// (the dialog warns "may change the type and every instance"; instance ids never confirm a type parameter).
    /// </summary>
    public static class JkrCopilotConfirmedWrite
    {
        /// <summary>Type-level families: the total instances behind the listed types, as the dialog shows them (the row's distinct ids).</summary>
        public static int InstanceCount(CopilotPromptFacts facts) =>
            (facts?.ElementIds ?? new List<long>()).Where(i => i > 0).Distinct().Count();

        /// <summary>The spec reader keeps at most this many instance ids per type (SpecModelReader.InstanceIdCap).</summary>
        public const int ReaderInstanceCap = 2000;

        /// <summary>True when some type of the row has as many ids as the reader cap, so its real instance count may be larger.</summary>
        public static bool InstanceCountCapped(CopilotPromptFacts facts)
        {
            if (facts == null) return false;
            var perType = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var id in (facts.ElementIds ?? new List<long>()).Where(i => i > 0).Distinct())
            {
                string label = null;
                if (facts.ElementLabels != null) facts.ElementLabels.TryGetValue(id, out label);
                label = JkrCopilotPrompt.CleanLabel(label);
                var key = label.Length == 0 ? "#" + id : label;
                perType[key] = perType.TryGetValue(key, out var n) ? n + 1 : 1;
            }
            return perType.Values.Any(n => n >= ReaderInstanceCap);
        }

        /// <summary>The field for prompt part <paramref name="partIndex"/> (0-based), or null when none can be sent (no declared
        /// parameter, no ids, or over a contract cap: the normal gate then applies).</summary>
        public static ConfirmedWriteDto ForPart(CopilotPromptFacts facts, int partIndex)
        {
            if (facts == null) return null;
            var checkId = (facts.CheckId ?? "").Trim();
            if (checkId.Length == 0 || checkId.Length > ConfirmedWriteDto.MaxCheckIdLength) return null;
            var variant = JkrCopilotPrompt.VariantFor(checkId);
            var names = variant.Params.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct().ToList();
            if (names.Count == 0 || names.Count > ConfirmedWriteDto.MaxParams) return null;
            var ids = JkrCopilotPrompt.PartIds(facts, partIndex);
            if (ids.Count == 0 || ids.Count > ConfirmedWriteDto.MaxIds) return null;
            var dto = new ConfirmedWriteDto { CheckId = checkId, Ids = ids, Params = names };
            if (variant.Level == CopilotWriteLevel.Type)
            {
                dto.TypeLevel = true;
                dto.Instances = InstanceCount(facts);
            }
            return dto;
        }
    }
}
