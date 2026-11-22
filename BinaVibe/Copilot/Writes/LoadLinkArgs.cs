#nullable enable
// load_link (AI-529, WP-7): arguments and decision rules, Revit-free so the tests link them.
// The Revit-bound tool is Tools/LoadLinkTool.cs.
//
//   links  names or ids of Revit link types in the open model (model_overview "links")
//   path   a full .rvt path: with one link it is the file to load; with no links it adds the file as a new link
//
// Rules (every problem is invalid_args; nothing has run when one is raised):
//   - give links or path; at most MaxLinks links per call; no blank entry; no duplicate
//   - path is a full Windows path (drive or UNC) to a .rvt file
//   - path names one file, so it goes with at most one link
// What happens to each link is decided by LinkLoadRules.Decide from the link's own state.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Writes
{
    public sealed class LoadLinkArgs
    {
        /// <summary>Hard limit per call (the model also sees it in tools.json).</summary>
        public const int MaxLinks = 10;

        public IReadOnlyList<string> Links { get; private set; } = Array.Empty<string>();

        /// <summary>The file the drafter named, or null.</summary>
        public string? Path { get; private set; }

        /// <summary>Parses and validates; throws <see cref="WriteRefusal"/> (invalid_args).</summary>
        public static LoadLinkArgs Parse(JsonElement args)
        {
            if (args.ValueKind != JsonValueKind.Object)
                throw Invalid("load_link arguments must be a JSON object with links and/or path.");
            var links = new List<string>();
            string? path = null;
            foreach (var p in args.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "links":
                        if (p.Value.ValueKind == JsonValueKind.Null) break;
                        if (p.Value.ValueKind != JsonValueKind.Array)
                            throw Invalid("links must be a list of link names or ids.");
                        foreach (var item in p.Value.EnumerateArray())
                        {
                            var text = item.ValueKind == JsonValueKind.String ? item.GetString()
                                : item.ValueKind == JsonValueKind.Number ? item.GetRawText() : null;
                            if (string.IsNullOrWhiteSpace(text))
                                throw Invalid("links holds a blank or non-text entry; give each link by name or id.");
                            if (!links.Contains(text!.Trim(), StringComparer.OrdinalIgnoreCase)) links.Add(text.Trim());
                        }
                        break;
                    case "path":
                        if (p.Value.ValueKind == JsonValueKind.Null) break;
                        if (p.Value.ValueKind != JsonValueKind.String)
                            throw Invalid("path must be text.");
                        path = string.IsNullOrWhiteSpace(p.Value.GetString()) ? null : p.Value.GetString()!.Trim();
                        break;
                    default:
                        throw Invalid("Unknown argument '" + p.Name + "'; load_link takes links and path.");
                }
            }

            if (links.Count == 0 && path == null)
                throw Invalid("Give links (link names or ids from model_overview) or path (a .rvt file).");
            if (links.Count > MaxLinks)
                throw Invalid("At most " + MaxLinks.ToString(CultureInfo.InvariantCulture) + " links per call; got " + links.Count.ToString(CultureInfo.InvariantCulture) + ". Send the rest in a second call.");
            if (path != null)
            {
                var problem = PathProblem(path);
                if (problem != null) throw Invalid(problem);
                if (links.Count > 1)
                    throw Invalid("path names one file, so give it with at most one link.");
            }
            return new LoadLinkArgs { Links = links, Path = path };
        }

        /// <summary>Why a path cannot be loaded as a link, or null: it must be a full Windows path to a .rvt file.</summary>
        public static string? PathProblem(string path)
        {
            if (!LinkLoadRules.IsLocalPath(path))
                return "path must be a full Windows path such as C:\\Models\\ST.rvt (a drive or a \\\\server\\share path).";
            if (!path.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
                return "path must be a .rvt file.";
            return null;
        }

        private static WriteRefusal Invalid(string message) => new WriteRefusal(ErrorCodes.InvalidArgs, message);
    }

    /// <summary>A link type's loaded state, reduced to what the decision needs (Revit's LinkedFileStatus maps onto it).</summary>
    public enum LinkStatusKind { Loaded, Unloaded, NotFound, ClosedWorkset, Other }

    /// <summary>What load_link does with one link.</summary>
    public enum LinkAction
    {
        /// <summary>Nothing: the link is loaded.</summary>
        None,
        /// <summary>Load from the stored path (RevitLinkType.Load).</summary>
        Reload,
        /// <summary>Load from a path the call or the file picker gave (RevitLinkType.LoadFrom).</summary>
        LoadFrom,
        /// <summary>The stored file is missing: ask the drafter for the file (the host file picker), then LoadFrom.</summary>
        Pick,
        /// <summary>load_link cannot load it; the reason is in <see cref="LinkDecision.Reason"/>.</summary>
        Refuse,
    }

    public readonly struct LinkDecision
    {
        public LinkDecision(LinkAction action, string? reason = null) { Action = action; Reason = reason; }
        public LinkAction Action { get; }
        public string? Reason { get; }
    }

    public static class LinkLoadRules
    {
        private static readonly Regex Local = new Regex(@"^(?:[A-Za-z]:[\\/]|\\\\[^\\/]+[\\/])", RegexOptions.Compiled);

        /// <summary>A drive or UNC path: a file the add-in can look for. A Revit Server or cloud path is not (Revit alone can open it).</summary>
        public static bool IsLocalPath(string? path) => !string.IsNullOrWhiteSpace(path) && Local.IsMatch(path!.Trim());

        /// <summary>What to do with one existing link type.</summary>
        /// <param name="status">its loaded state</param>
        /// <param name="storedFileExists">false only when the stored path is a local path and no file is there</param>
        /// <param name="overridePath">the path of the call, or null</param>
        public static LinkDecision Decide(LinkStatusKind status, bool storedFileExists, string? overridePath, string linkName)
        {
            var name = "'" + linkName + "'";
            switch (status)
            {
                case LinkStatusKind.Loaded:
                    return new LinkDecision(LinkAction.None);
                case LinkStatusKind.ClosedWorkset:
                    return new LinkDecision(LinkAction.Refuse, "Link " + name + " sits in a closed workset; open that workset in Revit's Worksets dialog first. Nothing was loaded.");
                case LinkStatusKind.Other:
                    return new LinkDecision(LinkAction.Refuse, "Link " + name + " is in a state load_link cannot load (not an unloaded Revit link). Nothing was loaded.");
                case LinkStatusKind.Unloaded:
                case LinkStatusKind.NotFound:
                default:
                    if (overridePath != null) return new LinkDecision(LinkAction.LoadFrom);
                    return status == LinkStatusKind.Unloaded && storedFileExists
                        ? new LinkDecision(LinkAction.Reload)
                        : new LinkDecision(LinkAction.Pick);
            }
        }

        /// <summary>The refusal for a batch that mixes load_link with another change, or null. Revit's own Load clears the whole Undo list and
        /// cannot run inside a transaction, so it cannot share a batch (one Undo entry) with other writes.</summary>
        public static string? MixedBatchRefusal(IReadOnlyList<string> writeTools)
        {
            if (writeTools == null || writeTools.Count < 2) return null;
            if (!writeTools.Any(t => string.Equals(t, ToolNames.LoadLink, StringComparison.Ordinal))) return null;
            return "load_link runs on its own: Revit clears its Undo list when a link loads and cannot do it inside a change group, so it cannot join other changes in one message. Send load_link alone, then the other changes. Nothing was changed.";
        }

        /// <summary>The words the envelope uses for what Revit answered when a load did not work.</summary>
        public static string Explain(string resultType) => resultType switch
        {
            "LinkNotFound" => "Revit could not find the file",
            "LinkNotOpenable" => "Revit could not read the file as a Revit model",
            "SameModelAsHost" or "SameCentralModelAsHost" or "LinkOpenAsHost" => "the file is the host model itself (or its central), which Revit refuses to link",
            "ExternalServerMissing" => "the add-in that serves this link is missing",
            "LinkExists" or "UsedExisting" => "a link of that file already exists",
            "Uninitialized" => "Revit did not try to load it",
            _ => "Revit refused (" + resultType + ")",
        };
    }

    /// <summary>The host file picker, a seam: Windows' open-file dialog in the add-in, a fake in tests.</summary>
    public interface ILinkFilePicker
    {
        /// <summary>Asks the drafter for the file of a link; null when they cancel.</summary>
        string? Pick(string linkName);
    }
}
