#!/usr/bin/env python3
"""WP-1 spike results. Tester: fill RESULTS below, run `python3 WP-1-results-template.py`,
paste the printed table into the PR / ticket. Exit code 1 while any row is still PENDING.

status: PASS | FAIL | SKIP | PENDING
error:  the exact error code and text from the result card (policy_rejected ..., compile_error ...), or ""
evidence: screenshot or log file name
"""
import sys

RUN = {  # fill in
    "date": "", "tester": "", "revit": "2027.3", "addin_commit": "", "backend_commit": "",
    "model_copy": "", "template_found": "", "rule24": "", "browser_org": "",
}

OFFLINE = {  # dry-compile verdict from section 7 of the pack
    "Q1": "allowed", "Q1b": "allowed", "Q2": "allowed", "Q3": "allowed", "Q4": "allowed", "Q5": "allowed",
    "Q6": "allowed", "Q7a-c": "rejected (link_load/paths)", "Q7d": "allowed", "Q8a-b": "rejected (application_access/paths)",
    "Q8c": "allowed, mode Read", "Q8d": "n/a (UI)", "Q8e": "n/a (UI)",
}

def r(q, api, status="PENDING", error="", evidence="", notes=""):
    return dict(q=q, api=api, status=status, error=error, evidence=evidence, notes=notes)

RESULTS = [
    r("Q1", "View3D.CreateIsometric per level"),
    r("Q1b", "View3D.SetSectionBox"),
    r("Q2", "set View_jkr_pit and Jenis_Lukisan_jkr_pit"),
    r("Q2b", "checker C1.5.1/C1.5.2 counts the kept views"),
    r("Q3", "ViewTemplateId = jkrAR_07d_Dokumen_(3D)"),
    r("Q4", "Viewport.Create on an empty sheet"),
    r("Q5", "doc.Delete of a sheet (policy)"),
    r("Q6", "ViewTemplateId on a list of views"),
    r("Q7a-c", "RevitLinkType Reload / Create / Unload rejected"),
    r("Q7d", "RevitLinkInstance.Create allowed?"),
    r("Q8a-b", "shared parameter file from code rejected"),
    r("Q8c", "ParameterBindings.ReInsert (S8c)"),
    r("Q8d", "JKR shared parameter file present (rule 24)"),
    r("Q8e", "legacy Db.1 Fix binds"),
]

def esc(s):
    return str(s).replace("|", "\\|").replace("\n", " ")

def main():
    print("Run:", ", ".join("%s=%s" % (k, v or "?") for k, v in RUN.items()))
    print()
    print("| Q | API | Offline | Live | Error | Evidence | Notes |")
    print("|---|---|---|---|---|---|---|")
    for x in RESULTS:
        print("| %s | %s | %s | %s | %s | %s | %s |" % (
            x["q"], esc(x["api"]), OFFLINE.get(x["q"], ""), x["status"], esc(x["error"]), esc(x["evidence"]), esc(x["notes"])))
    pending = [x["q"] for x in RESULTS if x["status"] == "PENDING"]
    if pending:
        print("\nPENDING:", ", ".join(pending))
        return 1
    return 0

if __name__ == "__main__":
    sys.exit(main())
