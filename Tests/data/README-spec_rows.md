# spec_rows.json

One row per check of `JKR-Model-Audit-Spec.xlsx`, sheet "Peraturan comply":
`{id, class (Ai|Human|List), comply_text, not_comply_text}`. GENERATED, committed, never
regenerated at test time. AI-513: `comply_text` / `not_comply_text` are the English text of the cells (the pane is English only; the workbook stays Malay). `SpecCatalogConformanceTests` fails when `SpecCatalog` and this
file disagree.

Regenerate when the Excel / `rules_data.py` changes (the model-audit folder is not a git repo):

    cd ~/Desktop/Projects/model-audit/3-build
    python3 build_spec_xlsx.py --rows-only <revit-addin-sync>/Tests/data/spec_rows.json

`--rows-only` writes only the JSON; it does not rebuild the workbook. Then re-run the tests
and commit the JSON.
