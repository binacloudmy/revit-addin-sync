#!/usr/bin/env python3
"""AI-216: turn the add-in's `turn_phase_timing` log lines into a Markdown table.

The add-in writes one line per finished Copilot turn to
%LOCALAPPDATA%\\Bina\\RevitSync\\session.log:

    10:42:07 [turn_phase_timing] {"turn_id":"...","wall_ms":...,...}

Usage:
    python scripts/turn_timing_table.py                       # default log, all turns
    python scripts/turn_timing_table.py --last 10             # newest 10 turns
    python scripts/turn_timing_table.py --since 2026-11-02T09:00
    python scripts/turn_timing_table.py --labels A-06,A-06,A-09,A-09,G-01,G-01 --last 6
    python scripts/turn_timing_table.py path\\to\\session.log

Phases are printed in seconds. `active` = wall minus time a card/question
waited for the drafter; `other` = wall minus every measured phase.
Rows are sorted by turn start (`start_ts`): a turn's line is written when it
ENDS, and an auto-declined card can end after the next turn. `idle grace` is
only idle-watchdog fast-fails; `unstarted` is jobs that never ran for any
other reason (Stop, timeout behind other jobs, update gate).
"""
import argparse
import json
import os
import sys

MARKER = "[turn_phase_timing]"

COLUMNS = [
    ("model", "model_wait_ms"),
    ("1st reply", "first_reply_ms"),
    ("tool queue", "tool_queue_ms"),
    ("tool exec", "tool_exec_ms"),
    ("idle grace", "pump_idle_grace_ms"),
    ("unstarted", "unstarted_ms"),
    ("receipt", "receipt_ms"),
    ("view export", "view_export_ms"),
    ("approval wait", "approval_wait_ms"),
    ("clarify wait", "clarify_wait_ms"),
    ("other", "other_ms"),
    ("active", "active_ms"),
    ("wall", "wall_ms"),
]


def default_log():
    base = os.environ.get("LOCALAPPDATA") or os.path.expanduser("~")
    return os.path.join(base, "Bina", "RevitSync", "session.log")


def read_rows(path):
    rows = []
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            i = line.find(MARKER)
            if i < 0:
                continue
            payload = line[i + len(MARKER):].strip()
            try:
                rows.append(json.loads(payload))
            except ValueError:
                continue  # half-written / foreign line: skip, never die
    return rows


def secs(ms, key=""):
    if ms is None:
        return ""
    if key == "first_reply_ms" and ms < 0:
        return "-"  # no reply text streamed this turn
    return f"{ms / 1000.0:.1f}"


def md_escape(s):
    return (s or "").replace("|", "\\|")


def prompt_cell(r: dict) -> str:
    """The log keeps no prompt text: show the slash command (if any), the hash and
    the length. Old lines that still carry "prompt" show its first 40 chars."""
    if r.get("prompt"):
        return str(r["prompt"])[:40]
    parts = []
    if r.get("command_id"):
        parts.append("/" + str(r["command_id"]))
    if r.get("prompt_hash"):
        parts.append("#" + str(r["prompt_hash"]))
    if r.get("prompt_len") is not None:
        parts.append(f'{r["prompt_len"]} ch')
    return " ".join(parts)


def main(argv=None):
    try:  # redirected stdout on Windows is cp1252; prompts can hold any char
        sys.stdout.reconfigure(encoding="utf-8")
    except (AttributeError, ValueError):
        pass
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("log", nargs="?", default=default_log(), help="session.log path (default: %(default)s)")
    ap.add_argument("--last", type=int, default=0, help="only the newest N turns")
    ap.add_argument("--since", default="", help="only turns with ts >= this ISO prefix, e.g. 2026-11-02T09:00")
    ap.add_argument("--labels", default="", help="comma-separated row names (e.g. A-06,A-06,G-01), in order")
    ap.add_argument("--writes-only", action="store_true", help="only write turns")
    args = ap.parse_args(argv)

    if not os.path.exists(args.log):
        print(f"log not found: {args.log}", file=sys.stderr)
        return 1
    rows = read_rows(args.log)
    # Order by when the turn STARTED (lines are written when it ends).
    rows.sort(key=lambda r: str(r.get("start_ts") or r.get("ts") or ""))
    if args.since:
        rows = [r for r in rows if str(r.get("start_ts") or r.get("ts", "")) >= args.since]
    if args.writes_only:
        rows = [r for r in rows if r.get("write")]
    if args.last > 0:
        rows = rows[-args.last:]
    if not rows:
        print("no turn_phase_timing lines found", file=sys.stderr)
        return 1
    labels = [s.strip() for s in args.labels.split(",")] if args.labels else []

    head = ["row", "time", "prompt", "status", "write"] + [c for c, _ in COLUMNS] + ["legs", "tools", "cards"]
    print("| " + " | ".join(head) + " |")
    print("|" + "|".join(["---"] * 5 + ["---:"] * (len(COLUMNS) + 3)) + "|")
    for n, r in enumerate(rows):
        label = labels[n] if n < len(labels) and labels[n] else str(n + 1)
        ts = str(r.get("start_ts") or r.get("ts", ""))
        hhmmss = ts[11:19] if len(ts) >= 19 else ts
        status = str(r.get("status", ""))
        if r.get("resumed_orphan"):
            status += " (partial)"
        cells = [
            md_escape(label), hhmmss, md_escape(prompt_cell(r)), status,
            "W" if r.get("write") else "R",
        ]
        cells += [secs(r.get(key), key) for _, key in COLUMNS]
        cells += [str(r.get("model_legs", "")), str(r.get("tool_jobs", "")), str(r.get("confirm_cards", ""))]
        print("| " + " | ".join(cells) + " |")
    return 0


if __name__ == "__main__":
    sys.exit(main())
