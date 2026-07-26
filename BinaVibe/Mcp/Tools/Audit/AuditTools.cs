// fill_audit / draft_export executors.
//
// fill_audit: parse the attached audit form (pdf_ref) → match each row to a
// deterministic checker → evaluate against the LIVE document → template a
// remark from that row's evidence only. Rows with no confident checker match
// come back not_verifiable — never a guessed pass/fail. The result is the
// single source of truth for compliance; draft_export renders it verbatim.
//
// draft_export: pure templating of a cached fill_audit result to
// xlsx | csv | docx | pdf. No new findings, no re-worded remarks.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
// Extension methods for the fluent PDF builder; Document stays qualified
// (QuestPDF.Fluent.Document) to avoid clashing with Revit's Document.
using QuestPDF.Fluent;
using Document = Autodesk.Revit.DB.Document;

namespace BinaVibe.Mcp.Tools.Audit
{
    public static class AuditTools
    {
        // ─── fill_audit ─────────────────────────────────────────────────

        public static Dictionary<string, object?> FillAudit(Document doc, JsonElement args)
        {
            var pdfRef = ArgsHelp.GetString(args, "pdf_ref") ?? "";
            if (string.IsNullOrWhiteSpace(pdfRef))
                throw new InvalidOperationException(
                    "pdf_ref is required — use the ref from the [Attached PDF] block");

            var (formName, formPath) = PdfAttachmentCache.Use(pdfRef, d => (d.Name, d.Path));
            var rows = AuditFormParser.Parse(formPath);

            // One context for the whole run: shared model inventories, so two
            // rows about the same thing cite the same numbers (and each
            // collector runs once, not once per checker).
            var ctx = new AuditContext(doc);

            var records = new List<AuditRecord>();
            foreach (var row in rows)
            {
                var rec = new AuditRecord { Row = row };
                var match = AuditCheckers.Match(row);
                if (match == null)
                {
                    // No checker — still surface whatever inventory the row's own
                    // wording points at, so "semak manual" comes with the facts a
                    // human would look up first. Context, never a verdict.
                    var (evidence, note) = AuditCheckers.UnmatchedContext(ctx, row);
                    rec.Evidence = evidence;
                    rec.Remark = note.Length > 0
                        ? "Tiada semakan automatik untuk baris ini. " + note + " Semak manual."
                        : "Tiada semakan automatik untuk baris ini — semak manual.";
                }
                else
                {
                    var (checker, category) = match.Value;
                    rec.CheckerMatched = true;
                    rec.CheckerId = checker.Id;
                    try
                    {
                        var outcome = category != null
                            ? AuditCheckers.EvaluateCategory(ctx, category)
                            : checker.Evaluate(ctx, row);
                        rec.Compliance = outcome.Compliance;
                        rec.RulePattern = outcome.RulePattern;
                        rec.Severity = AuditCheckers.SeverityOf(checker, outcome);
                        rec.Evidence = outcome.Evidence;
                        rec.ElementIds = outcome.ElementIds;
                        rec.Remark = outcome.Remark;
                    }
                    catch (Exception ex)
                    {
                        // A checker crash must not sink the whole audit — and must
                        // not fake a verdict either.
                        rec.Compliance = "not_verifiable";
                        rec.Evidence = new Dictionary<string, object?> { ["checker_error"] = ex.Message };
                        rec.Remark = "Semakan automatik gagal (" + checker.Id + ") — semak manual.";
                    }
                }
                records.Add(rec);
            }

            // A row whose Reference cell the form left blank inherits the matched
            // checker's citation (or one a sibling row of the same checker
            // printed). Runs after all rows are evaluated so the sibling scan sees
            // the whole form. Clause text is never synthesised.
            BackfillCheckerRefs(records);

            var result = new AuditResult
            {
                FormName = formName,
                PdfRef = pdfRef,
                ModelTitle = doc.Title ?? "",
                CreatedUtc = DateTime.UtcNow,
                Records = records,
            };
            var auditId = AuditResultCache.Store(result);

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["audit_id"] = auditId,
                ["form"] = formName,
                ["model"] = result.ModelTitle,
                ["summary"] = new Dictionary<string, object?>
                {
                    ["rows"] = records.Count,
                    ["yes"] = records.Count(r => r.Compliance == "yes"),
                    ["no"] = records.Count(r => r.Compliance == "no"),
                    ["not_verifiable"] = records.Count(r => r.Compliance == "not_verifiable"),
                    ["checker_matched"] = records.Count(r => r.CheckerMatched),
                    // Severity of the FAILED rows only — triage order for the
                    // Isu list. It never changes a verdict.
                    ["failed_by_severity"] = new Dictionary<string, object?>
                    {
                        ["critical"] = records.Count(r => r.Compliance == "no" && r.Severity == Severities.Critical),
                        ["major"] = records.Count(r => r.Compliance == "no" && r.Severity == Severities.Major),
                        ["minor"] = records.Count(r => r.Compliance == "no" && r.Severity == Severities.Minor),
                    },
                },
                ["rows"] = records.Select(r => (object)r.ToDict()).ToList(),
            };
        }

        /// <summary>Fill a blank Reference from the matched checker: its declared
        /// authoritative clause first, else a clause other rows of the SAME
        /// checker printed on themselves (form_sibling refs are already inherited,
        /// so only "form" rows seed a sibling). Blank stays blank when neither
        /// source has a value — nothing is invented.</summary>
        private static void BackfillCheckerRefs(List<AuditRecord> records)
        {
            // 1. Static: the checker's own authoritative clause.
            foreach (var r in records)
            {
                if (r.Row.GuidelineRef.Length > 0 || !r.CheckerMatched) continue;
                var checker = AuditCheckers.ById(r.CheckerId);
                if (checker != null && checker.GuidelineRef.Length > 0)
                {
                    r.Row.GuidelineRef = checker.GuidelineRef;
                    r.Row.ReferenceSource = "checker";
                }
            }

            // 2. Learned within the form: a clause a sibling row of the same
            // checker printed (ReferenceSource "form"), when it is unambiguous.
            var printedByChecker = records
                .Where(r => r.CheckerMatched && r.Row.ReferenceSource == "form"
                            && r.Row.GuidelineRef.Length > 0)
                .GroupBy(r => r.CheckerId)
                .Where(g => g.Select(r => r.Row.GuidelineRef).Distinct(StringComparer.Ordinal).Count() == 1)
                .ToDictionary(g => g.Key, g => g.First().Row.GuidelineRef, StringComparer.Ordinal);

            foreach (var r in records)
            {
                if (r.Row.GuidelineRef.Length > 0 || !r.CheckerMatched) continue;
                if (printedByChecker.TryGetValue(r.CheckerId, out var clause))
                {
                    r.Row.GuidelineRef = clause;
                    r.Row.ReferenceSource = "checker_sibling";
                }
            }
        }

        // ─── draft_export ───────────────────────────────────────────────

        public static Dictionary<string, object?> DraftExport(Document doc, JsonElement args)
        {
            var auditId = ArgsHelp.GetString(args, "audit_id") ?? "";
            var format = (ArgsHelp.GetString(args, "format") ?? "xlsx").ToLowerInvariant();
            var result = AuditResultCache.Get(auditId);

            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmm");
            var baseName = "Model_Audit_" + Sanitize(result.ModelTitle) + "_" + stamp;
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string path;
            switch (format)
            {
                case "xlsx": path = Path.Combine(dir, baseName + ".xlsx"); ExportXlsx(result, path); break;
                case "csv":  path = Path.Combine(dir, baseName + ".csv");  ExportCsv(result, path);  break;
                case "docx": path = Path.Combine(dir, baseName + ".docx"); ExportDocx(result, path); break;
                case "pdf":  path = Path.Combine(dir, baseName + ".pdf");  ExportPdf(result, path);  break;
                default:
                    throw new InvalidOperationException(
                        "format must be xlsx | csv | docx | pdf (got '" + format + "')");
            }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["kind"] = "file",
                ["headline"] = Path.GetFileName(path),
                ["path"] = dir,
                ["sub"] = result.Records.Count + " rows · " + result.FormName,
                ["full_path"] = path,
            };
        }

        // ─── xlsx / csv (tracking layout: one row per checklist item) ───

        // Severity and Rule ride along here only. docx/pdf keep the BIM 010
        // six-column form layout, which has no place for them.
        private static readonly string[] FlatHeaders =
        {
            "Section", "No.", "Description", "Reference", "Compliance",
            "Severity", "Checker", "Rule", "Remark", "Element IDs",
        };

        private static IEnumerable<string[]> FlatRows(AuditResult result) =>
            result.Records.Select(r => new[]
            {
                r.Row.Section + (r.Row.SectionTitle.Length > 0 ? " — " + r.Row.SectionTitle : ""),
                r.Row.RowRef,
                r.Row.Description,
                // A ref not printed on this row is marked as such: the form
                // printed it once for the section (form_sibling), or it came from
                // the checker / a sibling row of the same checker.
                r.Row.GuidelineRef + r.Row.ReferenceSource switch
                {
                    "form_sibling" => " (rujukan seksyen)",
                    "checker" or "checker_sibling" => " (rujukan checker)",
                    _ => "",
                },
                r.Compliance,
                r.Compliance == "no" ? r.Severity : "",
                r.CheckerMatched ? r.CheckerId : "(manual)",
                r.RulePattern,
                r.Remark,
                string.Join(" ", r.ElementIds),
            });

        /// <summary>Remark for the six-column form layouts (docx/pdf), which have
        /// no Severity column. A failed critical/major row is prefixed so a reader
        /// can still triage; minor and passing rows are unchanged. Ordering only —
        /// it never alters the verdict already shown in the √/X columns. Keeps the
        /// not_verifiable + empty-remark → "Semak manual" fallback.</summary>
        private static string FormRemark(AuditRecord r)
        {
            var remark = r.Compliance == "not_verifiable" && r.Remark.Length == 0
                ? "Semak manual" : r.Remark;
            if (r.Compliance != "no") return remark;
            return r.Severity switch
            {
                Severities.Critical => "[KRITIKAL] " + remark,
                Severities.Major => "[MAJOR] " + remark,
                _ => remark,
            };
        }

        private static void ExportXlsx(AuditResult result, string path)
        {
            using var wb = new ClosedXML.Excel.XLWorkbook();
            var ws = wb.Worksheets.Add("Audit");
            ws.Cell(1, 1).Value = "Model Audit — " + result.FormName;
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(2, 1).Value = "Model: " + result.ModelTitle + "   Date: "
                + result.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            int headerRow = 4;
            for (int c = 0; c < FlatHeaders.Length; c++)
            {
                var cell = ws.Cell(headerRow, c + 1);
                cell.Value = FlatHeaders[c];
                cell.Style.Font.Bold = true;
            }
            int row = headerRow + 1;
            foreach (var vals in FlatRows(result))
            {
                for (int c = 0; c < vals.Length; c++) ws.Cell(row, c + 1).Value = vals[c];
                var compliance = vals[4];
                if (compliance == "no")
                    ws.Cell(row, 5).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("#fecaca");
                else if (compliance == "yes")
                    ws.Cell(row, 5).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("#bbf7d0");
                ws.Cell(row, 3).Style.Alignment.WrapText = true;
                ws.Cell(row, 8).Style.Alignment.WrapText = true;
                ws.Cell(row, 9).Style.Alignment.WrapText = true;
                row++;
            }
            ws.Column(1).Width = 26; ws.Column(2).Width = 6; ws.Column(3).Width = 55;
            ws.Column(4).Width = 18; ws.Column(5).Width = 13; ws.Column(6).Width = 10;
            ws.Column(7).Width = 18; ws.Column(8).Width = 40; ws.Column(9).Width = 60;
            ws.Column(10).Width = 20;
            ws.SheetView.FreezeRows(headerRow);
            if (row > headerRow + 1)
                ws.Range(headerRow, 1, row - 1, FlatHeaders.Length).SetAutoFilter();
            wb.SaveAs(path);
        }

        private static void ExportCsv(AuditResult result, string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", FlatHeaders.Select(Csv)));
            foreach (var vals in FlatRows(result))
                sb.AppendLine(string.Join(",", vals.Select(Csv)));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        private static string Csv(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

        // ─── docx (BIM 010-style layout via OpenXML) ────────────────────

        private static void ExportDocx(AuditResult result, string path)
        {
            using var docx = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(
                path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
            var main = docx.AddMainDocumentPart();
            var w = new DocxWriter();
            main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(
                new DocumentFormat.OpenXml.Wordprocessing.Body(w.Build(result)));
            main.Document.Save();
        }

        /// <summary>Small helper so the OpenXML noise stays in one place.</summary>
        private sealed class DocxWriter
        {
            public IEnumerable<DocumentFormat.OpenXml.OpenXmlElement> Build(AuditResult result)
            {
                yield return Para("MODEL AUDIT FORM", bold: true, size: 32, center: true);
                yield return Para("Form: " + result.FormName + "    Model: " + result.ModelTitle
                    + "    Date: " + result.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd"), size: 18);
                yield return Para("", size: 12);

                foreach (var group in result.Records.GroupBy(r => r.Row.Section + "|" + r.Row.SectionTitle))
                {
                    var first = group.First().Row;
                    var heading = first.Section.Length > 0
                        ? first.Section + ". " + first.SectionTitle : "CHECKLIST";
                    yield return Para(heading, bold: true, size: 22);
                    yield return SectionTable(group.ToList());
                    yield return Para("", size: 12);
                }
            }

            private DocumentFormat.OpenXml.OpenXmlElement SectionTable(List<AuditRecord> records)
            {
                var table = new DocumentFormat.OpenXml.Wordprocessing.Table();
                table.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.TableProperties(
                    new DocumentFormat.OpenXml.Wordprocessing.TableBorders(
                        new DocumentFormat.OpenXml.Wordprocessing.TopBorder { Val = DocumentFormat.OpenXml.Wordprocessing.BorderValues.Single, Size = 4 },
                        new DocumentFormat.OpenXml.Wordprocessing.BottomBorder { Val = DocumentFormat.OpenXml.Wordprocessing.BorderValues.Single, Size = 4 },
                        new DocumentFormat.OpenXml.Wordprocessing.LeftBorder { Val = DocumentFormat.OpenXml.Wordprocessing.BorderValues.Single, Size = 4 },
                        new DocumentFormat.OpenXml.Wordprocessing.RightBorder { Val = DocumentFormat.OpenXml.Wordprocessing.BorderValues.Single, Size = 4 },
                        new DocumentFormat.OpenXml.Wordprocessing.InsideHorizontalBorder { Val = DocumentFormat.OpenXml.Wordprocessing.BorderValues.Single, Size = 4 },
                        new DocumentFormat.OpenXml.Wordprocessing.InsideVerticalBorder { Val = DocumentFormat.OpenXml.Wordprocessing.BorderValues.Single, Size = 4 }),
                    new DocumentFormat.OpenXml.Wordprocessing.TableWidth
                    {
                        Type = DocumentFormat.OpenXml.Wordprocessing.TableWidthUnitValues.Pct,
                        Width = "5000",
                    }));

                table.AppendChild(Row(true, "No.", "Description", "Reference", "√ (Yes)", "X (No)", "Remarks"));
                foreach (var r in records)
                {
                    table.AppendChild(Row(false,
                        r.Row.RowRef,
                        r.Row.Description,
                        r.Row.GuidelineRef,
                        r.Compliance == "yes" ? "√" : "",
                        r.Compliance == "no" ? "X" : "",
                        FormRemark(r)));
                }
                return table;
            }

            private DocumentFormat.OpenXml.Wordprocessing.TableRow Row(bool header, params string[] cells)
            {
                var row = new DocumentFormat.OpenXml.Wordprocessing.TableRow();
                foreach (var text in cells)
                {
                    var cell = new DocumentFormat.OpenXml.Wordprocessing.TableCell();
                    cell.AppendChild(Para(text, bold: header, size: header ? 18 : 16));
                    row.AppendChild(cell);
                }
                return row;
            }

            private DocumentFormat.OpenXml.Wordprocessing.Paragraph Para(
                string text, bool bold = false, int size = 20, bool center = false)
            {
                var runProps = new DocumentFormat.OpenXml.Wordprocessing.RunProperties();
                if (bold) runProps.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Bold());
                runProps.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.FontSize
                {
                    Val = size.ToString(),   // half-points
                });
                var run = new DocumentFormat.OpenXml.Wordprocessing.Run(runProps,
                    new DocumentFormat.OpenXml.Wordprocessing.Text(text ?? "")
                    {
                        Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve,
                    });
                var para = new DocumentFormat.OpenXml.Wordprocessing.Paragraph();
                if (center)
                    para.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.ParagraphProperties(
                        new DocumentFormat.OpenXml.Wordprocessing.Justification
                        {
                            Val = DocumentFormat.OpenXml.Wordprocessing.JustificationValues.Center,
                        }));
                para.AppendChild(run);
                return para;
            }
        }

        // ─── pdf (QuestPDF — same engine + native-dir init as ReportExporter) ──

        private static void ExportPdf(AuditResult result, string path)
        {
            RevitWebAppSync.Services.ReportExporter.EnsureQuestPdfReady();

            QuestPDF.Fluent.Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Margin(30);
                    page.Size(QuestPDF.Helpers.PageSizes.A4);
                    page.DefaultTextStyle(t => t.FontSize(8.5f).FontColor("#111827"));

                    page.Header().Column(col =>
                    {
                        col.Item().Text("MODEL AUDIT FORM").FontSize(14).Bold();
                        col.Item().PaddingTop(2).Text(
                            "Form: " + result.FormName + "   |   Model: " + result.ModelTitle
                            + "   |   " + result.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
                            .FontSize(8).FontColor("#6b7280");
                        col.Item().PaddingTop(4).LineHorizontal(1).LineColor("#e5e7eb");
                    });

                    page.Content().PaddingVertical(6).Column(col =>
                    {
                        col.Spacing(10);
                        foreach (var group in result.Records.GroupBy(r => r.Row.Section + "|" + r.Row.SectionTitle))
                        {
                            var first = group.First().Row;
                            var records = group.ToList();
                            col.Item().Column(sec =>
                            {
                                sec.Item().Text(first.Section.Length > 0
                                        ? first.Section + ". " + first.SectionTitle : "CHECKLIST")
                                    .FontSize(10).Bold();
                                sec.Item().PaddingTop(3).Table(table =>
                                {
                                    table.ColumnsDefinition(c =>
                                    {
                                        c.ConstantColumn(28);   // No.
                                        c.RelativeColumn(4);    // Description
                                        c.RelativeColumn(1.5f); // Reference
                                        c.ConstantColumn(30);   // Yes
                                        c.ConstantColumn(30);   // No
                                        c.RelativeColumn(4);    // Remarks
                                    });
                                    void Head(string t) => table.Cell()
                                        .Background("#f3f4f6").Border(0.5f).BorderColor("#9ca3af")
                                        .Padding(3).Text(t).Bold().FontSize(8);
                                    Head("No."); Head("Description"); Head("Reference");
                                    Head("√ (Yes)"); Head("X (No)"); Head("Remarks");
                                    foreach (var r in records)
                                    {
                                        void Cell(string t, string? bg = null)
                                        {
                                            var c = table.Cell().Border(0.5f).BorderColor("#9ca3af").Padding(3);
                                            if (bg != null) c = c.Background(bg);
                                            c.Text(t ?? "").FontSize(8);
                                        }
                                        Cell(r.Row.RowRef);
                                        Cell(r.Row.Description);
                                        Cell(r.Row.GuidelineRef);
                                        Cell(r.Compliance == "yes" ? "√" : "",
                                             r.Compliance == "yes" ? "#dcfce7" : null);
                                        Cell(r.Compliance == "no" ? "X" : "",
                                             r.Compliance == "no" ? "#fee2e2" : null);
                                        Cell(FormRemark(r));
                                    }
                                });
                            });
                        }
                    });

                    page.Footer().AlignRight().Text(t =>
                    {
                        t.Span("Generated by BINA AI Copilot · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"))
                            .FontSize(7).FontColor("#9ca3af");
                        t.Span("   ").FontSize(7);
                        t.CurrentPageNumber().FontSize(7).FontColor("#9ca3af");
                        t.Span(" / ").FontSize(7).FontColor("#9ca3af");
                        t.TotalPages().FontSize(7).FontColor("#9ca3af");
                    });
                });
            }).GeneratePdf(path);
        }

        private static string Sanitize(string s)
        {
            foreach (var ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '_');
            return s.Replace(' ', '_');
        }
    }
}
