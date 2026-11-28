#nullable enable
// r24 (slow schedule 616d168b: one run_revit_code round trip took 255.8 s, normally ~10 s, and the add-in reported only the total). Revit-free.
//
// One RevitTiming per tool request (CopilotToolHost.RouteAsync opens it). Every RevitThread job queued from that request's async flow adds
// how long it waited for Revit's turn (queued: from RunAsync to the Idling / ExternalEvent callback starting it) and how long it ran on
// Revit's thread. The request's envelopes then carry the totals in their free-form data object, under "revit_timing", so the cloud's stored
// results split "waiting for Revit to go idle" from "running inside Revit". No contract change: data is the envelope's tool-specific object.
// The totals are the request's, so only its FIRST envelope with an object (or null) data carries them: a multi-call result never looks as if
// each call waited. An envelope whose data is an array is left as it is; a request that queued no Revit job (fakes, refusals) is left as it is.
// Privacy (audit of 13ff5913): revit_timing.busy is only a fixed category (BusyCategory), never free text. Dialog messages, window titles and
// status-bar text can hold file paths and user names (C:\Users\<name>\...\x.rvt); those words go to the local log line only (SlowWaitLine).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Writes
{
    public sealed class RevitTiming
    {
        /// <summary>The data key the totals go under.</summary>
        public const string Key = "revit_timing";

        /// <summary>A wait longer than this gets one line in the add-in log naming what Revit was busy with (RevitThread's watchdog).</summary>
        public static readonly TimeSpan SlowWait = TimeSpan.FromSeconds(30);

        private static readonly AsyncLocal<RevitTiming?> Slot = new AsyncLocal<RevitTiming?>();

        private readonly object _gate = new object();
        private long _queuedMs;
        private long _runMs;
        private long _maxQueuedMs;
        private int _jobs;
        private int _notRun;
        private string? _busy;

        /// <summary>The request in this async flow; null outside one.</summary>
        public static RevitTiming? Current => Slot.Value;

        /// <summary>Starts a request's timing for this async flow (and the flows it starts). An async caller's own flow keeps it until it returns.</summary>
        public static RevitTiming Begin()
        {
            var t = new RevitTiming();
            Slot.Value = t;
            return t;
        }

        public int Jobs { get { lock (_gate) return _jobs + _notRun; } }
        public long QueuedMs { get { lock (_gate) return _queuedMs; } }
        public long RunMs { get { lock (_gate) return _runMs; } }
        public string? Busy { get { lock (_gate) return _busy; } }

        /// <summary>A job that started on Revit's thread after <paramref name="queuedMs"/> and ran <paramref name="runMs"/>.</summary>
        public void Ran(long queuedMs, long runMs)
        {
            lock (_gate)
            {
                _jobs++;
                _queuedMs += Math.Max(0, queuedMs);
                _runMs += Math.Max(0, runMs);
                _maxQueuedMs = Math.Max(_maxQueuedMs, queuedMs);
            }
        }

        /// <summary>A job that never started (the watchdog failed it, the update gate, cancelled before its turn).</summary>
        public void NotRun(long queuedMs)
        {
            lock (_gate)
            {
                _notRun++;
                _queuedMs += Math.Max(0, queuedMs);
                _maxQueuedMs = Math.Max(_maxQueuedMs, queuedMs);
            }
        }

        /// <summary>What Revit was busy with while a job of this request waited past <see cref="SlowWait"/> (the first one seen). Only a
        /// category from <see cref="BusyCategory"/> is kept; anything else is dropped, so no free text can reach the envelope.</summary>
        public void NoteBusy(string? category)
        {
            if (!IsCategory(category)) return;
            lock (_gate) if (_busy == null) _busy = category;
        }

        public const string ModalDialog = "modal_dialog";
        public const string TaskDialog = "task_dialog";
        public const string StatusBarBusy = "status_bar_busy";
        public const string NoIdle = "no_idle";
        public const string CopilotJobPrefix = "copilot_job:";

        /// <summary>True for the fixed categories and copilot_job:&lt;our job type name&gt; (letters, digits, _ and [] only, at most 40).</summary>
        public static bool IsCategory(string? s)
        {
            if (s == ModalDialog || s == TaskDialog || s == StatusBarBusy || s == NoIdle) return true;
            if (s == null || !s.StartsWith(CopilotJobPrefix, StringComparison.Ordinal)) return false;
            var name = s.Substring(CopilotJobPrefix.Length);
            return name.Length > 0 && name.Length <= 40 && JobName(name) == name;
        }

        /// <summary>One of our job type names (typeof(T).Name: "Envelope[]", "CommitOutcome"), reduced to letters, digits, _ and [].</summary>
        public static string JobName(string? name)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var c in name ?? "")
                if (sb.Length < 40 && ((c < 128 && char.IsLetterOrDigit(c)) || c == '_' || c == '[' || c == ']')) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>The envelope's busy category, best first, never the words themselves: an open modal window, a job of ours still running,
        /// a dialog Revit showed while the job waited, the status bar showing something (an active command's prompt, a progress text),
        /// otherwise no idle turn.</summary>
        public static string BusyCategory(string? modal, string? status, string? dialog, string? running)
        {
            if (!string.IsNullOrWhiteSpace(modal)) return ModalDialog;
            if (!string.IsNullOrWhiteSpace(running) && JobName(running).Length > 0) return CopilotJobPrefix + JobName(running);
            if (!string.IsNullOrWhiteSpace(dialog)) return TaskDialog;
            if (!string.IsNullOrWhiteSpace(status)) return StatusBarBusy;
            return NoIdle;
        }

        private static double Seconds(long ms) => Math.Round(ms / 1000.0, 2);

        /// <summary>The "revit_timing" object: queued_s, run_s, max_queued_s, jobs, plus not_run and busy when there are any.</summary>
        public JsonObject ToJson()
        {
            lock (_gate)
            {
                var o = new JsonObject
                {
                    ["queued_s"] = Seconds(_queuedMs),
                    ["run_s"] = Seconds(_runMs),
                    ["max_queued_s"] = Seconds(_maxQueuedMs),
                    ["jobs"] = _jobs,
                };
                if (_notRun > 0) o["not_run"] = _notRun;
                if (_busy != null) o["busy"] = _busy;
                return o;
            }
        }

        /// <summary>Adds (or replaces) "revit_timing" in the FIRST envelope whose data is an object or null (null becomes an object holding
        /// only it): the totals are the request's, not each call's. Arrays are left alone. Nothing changes when the request queued no Revit
        /// job. Never throws: timing is a courtesy.</summary>
        public void Stamp(IReadOnlyList<Envelope?> envelopes)
        {
            if (envelopes == null || Jobs == 0) return;
            foreach (var e in envelopes)
            {
                if (e == null) continue;
                if (e.Data is { } d && d.ValueKind != JsonValueKind.Object && d.ValueKind != JsonValueKind.Null && d.ValueKind != JsonValueKind.Undefined) continue;
                try { e.Data = With(e.Data, ToJson()); }
                catch (Exception) { /* leave the envelope as it was */ }
                return;
            }
        }

        /// <summary>data with "revit_timing" set; data unchanged when it is an array or another non-object.</summary>
        public static JsonElement? With(JsonElement? data, JsonObject timing)
        {
            JsonObject obj;
            if (data == null || data.Value.ValueKind == JsonValueKind.Null || data.Value.ValueKind == JsonValueKind.Undefined) obj = new JsonObject();
            else if (data.Value.ValueKind == JsonValueKind.Object) obj = JsonNode.Parse(data.Value.GetRawText()) as JsonObject ?? new JsonObject();
            else return data;
            obj[Key] = timing;
            using var doc = JsonDocument.Parse(obj.ToJsonString());
            return doc.RootElement.Clone();
        }

        /// <summary>The one LOCAL add-in log line (copilot-clicks.log, never sent) for a job that waited past <see cref="SlowWait"/>: what Revit
        /// was busy with, in its own words when they could be read.
        /// <paramref name="modal"/> is an open modal window's title, <paramref name="status"/> the status bar text (an active command's prompt,
        /// a calculation's progress), <paramref name="dialog"/> a dialog Revit showed since the job was queued, <paramref name="running"/> a job
        /// of ours still running on Revit's thread.</summary>
        public static string SlowWaitLine(string job, double waitedSeconds, bool idleSinceQueued, string? modal, string? status, string? dialog, string? running)
            => "copilot request waiting " + ((int)waitedSeconds).ToString(CultureInfo.InvariantCulture) + " s for Revit (job " + job + "): "
               + BusyWith(idleSinceQueued, modal, status, dialog, running);

        /// <summary>For the local log only (may hold paths and user names): what Revit is busy with, in words, best first: a modal window, a job of ours, the status bar, a dialog, or not detectable.</summary>
        public static string BusyWith(bool idleSinceQueued, string? modal, string? status, string? dialog, string? running)
        {
            if (!string.IsNullOrWhiteSpace(modal)) return "modal dialog open '" + modal!.Trim() + "'";
            if (!string.IsNullOrWhiteSpace(running)) return "a copilot job still running (" + running!.Trim() + ")";
            if (!string.IsNullOrWhiteSpace(status)) return "status bar '" + status!.Trim() + "' (active command or calculation)";
            if (!string.IsNullOrWhiteSpace(dialog)) return "dialog shown while waiting " + dialog!.Trim();
            return idleSinceQueued
                ? "Revit gave idle turns but other work ran first; cause not detectable"
                : "no idle turn from Revit (busy UI thread: command, regeneration or analysis); cause not detectable";
        }
    }
}
