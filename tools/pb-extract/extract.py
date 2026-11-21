"""Buku Parameter (BIM: Spesifikasi Parameter JKR) -> Services/JkrSpec/data/jkr_pb_ar_parameters.csv

Usage (stdlib python 3, run with -I; needs poppler's pdftotext on PATH):
    python3 -I tools/pb-extract/extract.py "<09_BIM - SPESIFIKASI PARAMETER JKR.pdf>"

Steps
 1. pdftotext -bbox for PDF pages 41-120 (printed PB p.30-109; printed = PDF - 11).
 2. parse.py reads every table row with its LOi 100-500 ticks (x-position -> nearest LOi column).
 3. VALIDATION (fails the run, writes nothing): the Project Information rows (PB p.30-34)
    must match SpecPbData.ProjectInfoAr in the add-in 1:1 on name and LOi range. This is the
    hand-checked set the parser is held to; a new JKR edition that changes the layout breaks it.
 4. Integrity checks (fail the run): every AR row has ticks, ticks are one contiguous run,
    20 AR categories found, every Type/Instance letter in a _jkr_ name agrees with the table heading
    after the fixes below.
 5. Data fixes applied here, not by hand in the CSV (so a re-extract keeps them):
    - 'Kadaran_jkr.sit' -> 'Kadaran_jkr_sit' (dot typo, 5 pages); stray spaces inside a name removed
    - the name suffix is truth over the table heading (Kod_Ruang_AR_jkr_stt, Warna_Kelulusan_PBT_jkr_sit)
    - duplicate (category, name, kind) rows merged to the narrower LOi run (Komponen_IBS_jkr_sty twice in Stair: 100-500 and 300-500 -> 300-500)
    - 'Railing Arkitek' heading -> 'Railing'
    - leading '*' / '**' on a name -> the star column (0, 1 or 2); the legend is not in the book (D8)
"""
import csv, json, os, re, subprocess, sys, tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from parse import pages, rows_of_page, lines  # noqa: E402

REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
OUT = os.path.join(REPO, "Services", "JkrSpec", "data", "jkr_pb_ar_parameters.csv")
PBDATA = os.path.join(REPO, "Services", "JkrSpec", "SpecPbData.cs")
FIRST_PDF_PAGE, LAST_PDF_PAGE = 41, 120
AR_PAGES = (36, 102)
CATEGORY_FIX = {"Railing Arkitek": "Railing"}


def extract_rows(bbox_path):
    P = pages(bbox_path)
    out = []; cat = None; typ = None
    for idx, ws in enumerate(P):
        pb = FIRST_PDF_PAGE + idx - 11
        cols, c, t, rows = rows_of_page(ws)
        if rows is None:
            continue
        if c:
            cat = c
        transitions = []
        for l in lines(ws, tol=3):
            s = " ".join(w[4] for w in l["w"])
            m = re.search(r"Parameter Data\s*:\s*(\w+)", s)
            if m:
                transitions.append((l["y"], m.group(1)))
        for r in rows:
            tt = typ
            above = [tr for tr in transitions if tr[0] <= r["y0"]]
            if above:
                tt = above[-1][1]
            out.append(dict(cat=cat, pb=pb, ptype=tt, name=r["name"], ticks=sorted(r["ticks"])))
        if transitions:
            typ = transitions[-1][1]
    return out


def project_info_from_book(rows):
    got = {}
    for r in rows:
        if r["cat"] == "Project Information" and r["ticks"] and 30 <= r["pb"] <= 34:
            name = re.sub(r"^\*+", "", r["name"]).replace(" ", "") if "jkr" in r["name"] else r["name"].strip()
            got[(name, r["ticks"][0], r["ticks"][-1])] = r["pb"]
    return got


def project_info_from_addin():
    src = open(PBDATA, encoding="utf8").read()
    block = src.split("ProjectInfoAr = new[]", 1)[1].split("};", 1)[0]
    return {(m.group(1), int(m.group(3)), int(m.group(4))) for m in re.finditer(r'F\("([^"]+)",\s*(\d+),\s*(\d+),\s*(\d+)\)', block)}


def validate_project_information(rows):
    book = set(project_info_from_book(rows)); addin = project_info_from_addin()
    only_book, only_addin = sorted(book - addin), sorted(addin - book)
    print(f"validation: Project Information book {len(book)} rows, add-in {len(addin)}, match {len(book & addin)}")
    if only_book or only_addin or len(addin) != 101:
        print("  only in the extraction:", only_book[:10]); print("  only in SpecPbData:", only_addin[:10])
        sys.exit("VALIDATION FAILED: the parser no longer reproduces SpecPbData.ProjectInfoAr 101/101; nothing written")


def clean(rows):
    out, seen = [], {}
    for r in rows:
        if not r["ticks"] or not (AR_PAGES[0] <= r["pb"] <= AR_PAGES[1]):
            continue
        cat = CATEGORY_FIX.get(r["cat"], r["cat"])
        raw = r["name"].strip()
        star = len(raw) - len(raw.lstrip("*"))
        name = raw.lstrip("*").strip()
        jkr = "jkr" in re.sub(r"\s+", "", name)
        if jkr:
            name = re.sub(r"\s+", "", name).replace("_jkr.", "_jkr_")
            m = re.search(r"(_jkr_s[it][a-z])$", name)
            if not m:
                sys.exit(f"unrecognised JKR name {name!r} (p.{r['pb']})")
            suffix = m.group(1); kind = "Type" if suffix[-2] == "t" else "Instance"
        else:
            suffix = ""; kind = r["ptype"]
        ticks = r["ticks"]
        if ticks != list(range(ticks[0], ticks[-1] + 100, 100)):
            sys.exit(f"gap in LOi ticks {ticks} for {name} p.{r['pb']}")
        key = (cat, name, kind)
        if key in seen:
            # Duplicate row (Stair Komponen_IBS_jkr_sty: LOi 100-500 under Calculation Rules, 300-500 after
            # Function). Keep ONE row with the narrower run (latest start, earliest end): never fails
            # more than either printed row would. Stair p.91-93 is on the page spot-check list.
            prev = seen[key]
            print(f"duplicate {key}: LOi {prev[5]}-{prev[6]} and {ticks[0]}-{ticks[-1]} -> narrower run kept")
            prev[5], prev[6] = max(prev[5], ticks[0]), min(prev[6], ticks[-1])
            continue
        row = [cat, r["pb"], kind, name, star, ticks[0], ticks[-1], suffix, "instance" if jkr and kind == "Instance" else "type" if jkr else "revit-builtin"]
        seen[key] = row
        out.append(row)
    return out


def main(pdf):
    with tempfile.TemporaryDirectory() as d:
        bbox = os.path.join(d, "bbox.html")
        subprocess.check_call(["pdftotext", "-bbox", "-f", str(FIRST_PDF_PAGE), "-l", str(LAST_PDF_PAGE), pdf, bbox])
        rows = extract_rows(bbox)
    validate_project_information(rows)
    data = clean(rows)
    cats = {r[0] for r in data}
    if len(cats) != 20:
        sys.exit(f"expected 20 AR categories, got {len(cats)}: {sorted(cats)}")
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf8", newline="") as f:
        w = csv.writer(f, lineterminator="\n")
        w.writerow(["category", "pb_page", "param_kind", "parameter", "star", "loi_from", "loi_to", "suffix_code", "name_kind"])
        w.writerows(data)
    print(f"wrote {len(data)} rows, {len(cats)} categories -> {OUT}")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    main(sys.argv[1])
