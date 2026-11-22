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

## AI-513: English copies, Malay exports kept

`spec_rows.json`, `jkr_spec_rules.tsv`, `jkr_spec_items.tsv` and `jkr_spec_ns_clauses.tsv` are **hand-translated English**
copies of the workbook export, not generated output any more (the generator above writes the Malay text). The Malay exports
as of 22 Nov 2026 are kept beside them: `spec_rows_ms.json`, `jkr_spec_*_ms.tsv`. `SpecTallyTests` prove the code matches the
English copy; `SpecDataEnglishCopyTests` prove the English copy has the same ids, classes, numbers and clause / page
references as the Malay export. When the workbook changes: regenerate the Malay files (`*_ms`), re-translate the English ones,
run both tests. A raw export dropped over the English files fails every row.
