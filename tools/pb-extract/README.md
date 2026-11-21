# Buku Parameter extractor (Linear AI-503)

Regenerates `Services/JkrSpec/data/jkr_pb_ar_parameters.csv` (the AR per-category x LOi tables that Db.1 reads)
from JKR's "BIM - Spesifikasi Parameter" PDF.

    python3 -I tools/pb-extract/extract.py "<09_BIM - SPESIFIKASI PARAMETER JKR.pdf>"

Needs `pdftotext` (poppler). Stdlib python 3 only. The run fails and writes nothing if:

* the Project Information tables (PB p.30-34) no longer match `SpecPbData.ProjectInfoAr` 101/101
  (the hand-checked set the parser is held to), or
* an AR row has no ticks / a gap in its tick run, or the AR categories are not 20.

Data fixes live in `extract.py` (see its docstring), never in the CSV by hand. Layout: PDF page = printed page + 11.
After a new JKR edition: re-run, review `git diff` of the CSV, then spot-check pages against the PDF images.
ST / ME / EL tables are out of scope (AR only).
