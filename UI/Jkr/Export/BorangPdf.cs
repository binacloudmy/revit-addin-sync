using System;
using System.Collections.Generic;
using System.Linq;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace RevitWebAppSync.UI.Jkr.Export
{
    /// <summary>
    /// Renders <see cref="BorangDoc"/> with QuestPDF, 1:1 with
    /// borang-export-template.html: A4, the KOD DOKUMEN / PINDAAN / TARIKH /
    /// MUKA SURAT block on every page, Arial (Courier New for the file name),
    /// 1 px black borders, the same columns, wording and page breaks (A · B+C
    /// views/legends · C schedules/sheets · C link files + D · D + E + summary),
    /// "-- n of N --" at the foot. Sizes are the template's px × 595.28/720
    /// (its pages are 720 px wide). A long remark can push a table onto another
    /// page; MUKA SURAT and the footer count the real pages.
    /// </summary>
    public static class BorangPdf
    {
        /// <summary>The header's KOD DOKUMEN value: the form this export prints.</summary>
        public const string DocumentCode = "BIM010";

        private const float S = 595.28f / 720f;
        private static float Px(float px) => px * S;
        private const float Line = 0.75f;   // 1 px border

        public static byte[] Pdf(BorangDoc doc)
        {
            RevitWebAppSync.Services.ReportExporter.EnsureQuestPdfReady();
            return Create(doc).GeneratePdf();
        }

        /// <summary>The pages as PNG images (for the preview, same engine as the PDF).</summary>
        public static List<byte[]> Pages(BorangDoc doc, int dpi = 110)
        {
            RevitWebAppSync.Services.ReportExporter.EnsureQuestPdfReady();
            return Create(doc).GenerateImages(new ImageGenerationSettings
            {
                ImageFormat = ImageFormat.Png,
                RasterDpi = dpi,
            }).ToList();
        }

        private static Document Create(BorangDoc d) => Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.MarginVertical(Px(26));
                page.MarginHorizontal(Px(28));
                page.PageColor(Colors.White);
                page.DefaultTextStyle(t => t.FontFamily("Arial").FontSize(Px(9)).FontColor("#000000"));

                page.Header().AlignRight().Width(Px(250)).Table(t =>
                {
                    t.ColumnsDefinition(c => { c.RelativeColumn(); c.RelativeColumn(); });
                    void Kv(string k, Action<TextDescriptor> v)
                    {
                        t.Cell().Border(Line).PaddingVertical(Px(2)).PaddingHorizontal(Px(5)).Text(k).FontSize(Px(8.5f));
                        t.Cell().Border(Line).PaddingVertical(Px(2)).PaddingHorizontal(Px(5)).Text(x => { x.DefaultTextStyle(s => s.FontSize(Px(8.5f))); v(x); });
                    }
                    Kv("KOD DOKUMEN", x => x.Span(": " + DocumentCode));
                    Kv("PINDAAN", x => x.Span(": 00"));
                    Kv("TARIKH", x => x.Span(": 1 DIS 2022"));
                    Kv("MUKA SURAT", x => { x.Span(": "); x.CurrentPageNumber(); x.Span(" / "); x.TotalPages(); });
                });

                page.Footer().PaddingTop(Px(16)).AlignCenter().Text(x =>
                {
                    x.DefaultTextStyle(s => s.FontSize(Px(9)));
                    x.Span("-- "); x.CurrentPageNumber(); x.Span(" of "); x.TotalPages(); x.Span(" --");
                });

                page.Content().Column(col =>
                {
                    // ── page 1: title, project block, A ──
                    col.Item().PaddingTop(Px(16)).AlignCenter().Text("MODEL REQUIREMENT CHECKLIST ARCHITECT")
                        .FontSize(Px(13)).Bold().LetterSpacing(0.02f);
                    col.Item().PaddingTop(Px(14)).Table(t =>
                    {
                        t.ColumnsDefinition(c => { c.ConstantColumn(Px(132)); c.RelativeColumn(); });
                        void Kv(string k, string v, bool mono = false)
                        {
                            t.Cell().Border(Line).PaddingVertical(Px(5)).PaddingHorizontal(Px(7)).Text(k).FontSize(Px(9.5f)).Bold();
                            var txt = t.Cell().Border(Line).PaddingVertical(Px(5)).PaddingHorizontal(Px(7)).Text(v ?? "").FontSize(Px(9.5f));
                            if (mono) txt.FontFamily("Courier New");
                        }
                        Kv("PROJECT", d.Header.Project);
                        Kv("PHASE", "REKABENTUK / PEMBINAAN / SERAHAN");
                        Kv("MODEL/SYSTEM", d.Header.Model);
                        Kv("FILE NAME", d.Header.File, mono: true);
                        Kv("DATE", d.Header.Date);
                    });
                    Title(col, "A. MODEL INTEGRITY AND QUALITY", 18);
                    col.Item().PaddingTop(Px(6)).Table(t => { StandardHead(t); foreach (var r in d.SecA) StdRow(t, r); });

                    // ── page 2: B, C views + legends (a) ──
                    col.Item().PageBreak();
                    Title(col, "B. PROJECT INFORMATION", 18);
                    col.Item().PaddingTop(Px(6)).Table(t => { StandardHead(t); foreach (var r in d.SecB) StdRow(t, r); });
                    Title(col, "C. PROJECT BROWSER ORGANIZATION", 18);
                    col.Item().PaddingTop(Px(6)).Table(t =>
                    {
                        StandardHead(t);
                        Group(t, "1.0", "Views (Architectural)");
                        foreach (var r in d.CViews) StdRow(t, r);
                        Group(t, "2.0", "Legends");
                        StdRow(t, d.CLegendsHead);
                        if (d.CLegends.Count > 0) StdRow(t, d.CLegends[0]);
                    });

                    // ── page 3: legends (b), schedules, sheets, 4.2 ──
                    col.Item().PageBreak();
                    Title(col, "C. PROJECT BROWSER ORGANIZATION", 18);
                    col.Item().PaddingTop(Px(6)).Table(t =>
                    {
                        StandardHead(t);
                        foreach (var r in d.CLegends.Skip(1)) StdRow(t, r);
                        Group(t, "3.0", "Schedules/Quantities");
                        StdRow(t, d.CSchedHead);
                        foreach (var r in d.CSched) StdRow(t, r);
                        Group(t, "4.0", "Sheets");
                        StdRow(t, d.CSheetsHead);
                        foreach (var r in d.CSheets) StdRow(t, r);
                        StdRow(t, d.C42);
                    });

                    // ── page 4: link files, D (first rows) ──
                    col.Item().PageBreak();
                    Title(col, "C. PROJECT BROWSER ORGANIZATION", 18);
                    col.Item().PaddingTop(Px(6)).Table(t =>
                    {
                        StandardColumns(t);
                        Group(t, "5.0", "Link Files");
                        StdRow(t, d.C51);
                    });
                    Title(col, "D. ARCHITECTURAL FAMILIES", 20);
                    col.Item().PaddingTop(Px(6)).Table(t => { DHead(t); foreach (var r in d.SecD.Take(BorangDoc.DFirstPage)) DRow(t, r); });

                    // ── page 5: D (rest), E, summary ──
                    col.Item().PageBreak();
                    Title(col, "D. ARCHITECTURAL FAMILIES", 18);
                    col.Item().PaddingTop(Px(6)).Table(t => { DHead(t); foreach (var r in d.SecD.Skip(BorangDoc.DFirstPage)) DRow(t, r); });
                    Title(col, "E.  PROPERTIES AND VISIBILITY GRAPHIC", 20);
                    col.Item().PaddingTop(Px(6)).Table(t =>
                    {
                        StandardHead(t);
                        Group(t, "1.0", "View Template are Correctly Applied");
                        foreach (var r in d.SecE) StdRow(t, r);
                    });
                    Summary(col, d);
                });
            });
        });

        private static void Title(ColumnDescriptor col, string text, float topPx) =>
            col.Item().PaddingTop(Px(topPx)).Text(text).FontSize(Px(10.5f)).Bold();

        // ── standard table: No. 34 · Description · √ 42 · X 42 · Remarks 186 (px) ──
        private static void StandardColumns(TableDescriptor t) => t.ColumnsDefinition(c =>
        {
            c.ConstantColumn(Px(34));
            c.RelativeColumn();
            c.ConstantColumn(Px(42));
            c.ConstantColumn(Px(42));
            c.ConstantColumn(Px(186));
        });

        private static void StandardHead(TableDescriptor t)
        {
            StandardColumns(t);
            t.Header(h =>
            {
                h.Cell().RowSpan(2).Border(Line).Padding(Px(4)).AlignCenter().AlignMiddle().Text("No.").FontSize(Px(9)).Bold();
                h.Cell().RowSpan(2).Border(Line).PaddingVertical(Px(4)).PaddingHorizontal(Px(6)).AlignMiddle().Text("Description").FontSize(Px(9)).Bold();
                h.Cell().ColumnSpan(2).Border(Line).Padding(Px(4)).AlignCenter().Text("Compliance").FontSize(Px(9)).Bold();
                h.Cell().RowSpan(2).Border(Line).PaddingVertical(Px(4)).PaddingHorizontal(Px(6)).AlignMiddle().Text("Remarks").FontSize(Px(9)).Bold();
                h.Cell().Border(Line).Padding(Px(3)).AlignCenter().Text(x =>
                {
                    x.DefaultTextStyle(s => s.FontSize(Px(9)).Bold());
                    x.AlignCenter(); x.Line(BorangDoc.Tick); x.Span("(Yes)");
                });
                h.Cell().Border(Line).Padding(Px(3)).AlignCenter().Text(x =>
                {
                    x.DefaultTextStyle(s => s.FontSize(Px(9)).Bold());
                    x.AlignCenter(); x.Line("X"); x.Span("(No)");
                });
            });
        }

        /// <summary>Sub-heading row across the table ("1.0  Views (Architectural)"), grey.</summary>
        private static void Group(TableDescriptor t, string no, string text) =>
            t.Cell().ColumnSpan(5).Border(Line).Background("#F2F2F2").PaddingVertical(Px(5)).PaddingHorizontal(Px(6))
             .Text(no + "  " + text).FontSize(Px(9)).Bold();

        private static void StdRow(TableDescriptor t, BorangRow r)
        {
            if (r == null) return;
            t.Cell().Border(Line).Padding(Px(5)).AlignCenter().Text(r.No).FontSize(Px(9));
            t.Cell().Border(Line).PaddingVertical(Px(5)).PaddingLeft(Px(r.Indent ? 22 : 6)).PaddingRight(Px(6)).Text(x =>
            {
                x.DefaultTextStyle(s => s.FontSize(Px(9)).LineHeight(1.4f));
                if (string.IsNullOrEmpty(r.TextItalic)) x.Span(r.Text);
                else { x.Line(r.Text); x.Span(r.TextItalic).Italic(); }
            });
            t.Cell().Border(Line).Padding(Px(5)).AlignCenter().Text(r.Yes).FontSize(Px(12)).Bold().FontColor(BorangDoc.Green);
            t.Cell().Border(Line).Padding(Px(5)).AlignCenter().Text(r.No2).FontSize(Px(12)).Bold().FontColor(BorangDoc.Red);
            t.Cell().Border(Line).PaddingVertical(Px(5)).PaddingHorizontal(Px(6))
             .Text(r.Remark ?? "").FontSize(Px(8.5f)).LineHeight(1.4f).FontColor(r.RemarkColor);
        }

        // ── Section D: No. 26 · Category 112 · Naming 88 · Quality 66 · Information 56 · Geometry 60 · Remarks 150 (px) ──
        private static void DHead(TableDescriptor t)
        {
            t.ColumnsDefinition(c =>
            {
                c.RelativeColumn(26); c.RelativeColumn(112); c.RelativeColumn(88); c.RelativeColumn(66);
                c.RelativeColumn(56); c.RelativeColumn(60); c.RelativeColumn(150);
            });
            t.Header(h =>
            {
                h.Cell().RowSpan(2).Border(Line).Padding(Px(4)).AlignCenter().AlignMiddle().Text("No.").FontSize(Px(8.5f)).Bold();
                h.Cell().RowSpan(2).Border(Line).PaddingVertical(Px(4)).PaddingHorizontal(Px(5)).AlignMiddle()
                 .Text("Components and System Families Category").FontSize(Px(8.5f)).Bold();
                h.Cell().ColumnSpan(4).Border(Line).Padding(Px(3)).AlignCenter()
                 .Text("Compliance  " + BorangDoc.Tick + " (Yes)  X (No)").FontSize(Px(8.5f)).Bold();
                h.Cell().RowSpan(2).Border(Line).PaddingVertical(Px(4)).PaddingHorizontal(Px(5)).AlignMiddle().Text("Remarks").FontSize(Px(8.5f)).Bold();
                foreach (var label in new[] { "Standard Component File Naming shall be in Accordance with Geometry and Standards",
                                              "Quality and Integrity", "Information", "Design Geometry" })
                    h.Cell().Border(Line).PaddingVertical(Px(4)).PaddingHorizontal(Px(3)).AlignCenter().AlignMiddle()
                     .Text(label).FontSize(Px(7.5f)).Bold().LineHeight(1.3f).AlignCenter();
            });
        }

        private static void DRow(TableDescriptor t, BorangDRow r)
        {
            t.Cell().Border(Line).Padding(Px(4)).AlignCenter().Text(r.No).FontSize(Px(8.5f));
            t.Cell().Border(Line).PaddingVertical(Px(4)).PaddingHorizontal(Px(5)).Text(r.Cat).FontSize(Px(8.5f)).LineHeight(1.3f);
            foreach (var m in new[] { r.N, r.Q, r.I, r.G })
                t.Cell().Border(Line).Padding(Px(4)).AlignCenter().Text(BorangDoc.DGlyph(m)).FontSize(Px(11)).Bold().FontColor(BorangDoc.DColor(m));
            t.Cell().Border(Line).PaddingVertical(Px(4)).PaddingHorizontal(Px(5))
             .Text(r.Remark ?? "").FontSize(Px(8)).LineHeight(1.35f).FontColor(r.RemarkColor);
        }

        // ── AUDIT SUMMARY box (generated, not part of the standard form) ──
        private static void Summary(ColumnDescriptor col, BorangDoc d)
        {
            col.Item().PaddingTop(Px(20)).ShowEntire().Border(Line).PaddingVertical(Px(12)).PaddingHorizontal(Px(14)).Column(box =>
            {
                box.Spacing(Px(9));
                box.Item().Text("AUDIT SUMMARY — generated, not part of the standard form").FontSize(Px(9)).Bold().LetterSpacing(0.08f);
                box.Item().Row(row =>
                {
                    row.AutoItem().Column(c =>
                    {
                        c.Item().Text(d.SumPct + (d.SumPct == BorangDoc.Dash ? "" : "%")).FontSize(Px(20)).Bold().LineHeight(1f);
                        c.Item().PaddingTop(Px(2)).Text("of " + d.SumTotal + " AI-verifiable cells comply").FontSize(Px(8.5f)).FontColor("#444444");
                    });
                    row.ConstantItem(Px(26));
                    // template: border-left 1 px dashed #666, padding-left 26 px
                    row.AutoItem().BorderLeft(Line).BorderColor("#666666").PaddingLeft(Px(26)).Column(c =>
                    {
                        c.Item().Text(d.SumManual.ToString()).FontSize(Px(20)).Bold().LineHeight(1f).FontColor(BorangDoc.ManualBlue);
                        c.Item().PaddingTop(Px(2)).Text("cells marked semak manual — not scored").FontSize(Px(8.5f)).FontColor("#444444");
                    });
                });
                box.Item().Text("Cells marked semak manual carry no " + BorangDoc.Tick + " and no X. Each is listed in the appended issue detail with the reason the checker declined to judge it and space for the reviewer's signature.")
                   .FontSize(Px(8.5f)).LineHeight(1.5f).FontColor("#333333");
                box.Item().BorderTop(Line).BorderColor("#CCCCCC").PaddingTop(Px(8)).Text(x =>
                {
                    x.DefaultTextStyle(s => s.FontSize(Px(8)).LineHeight(1.6f).FontColor("#555555"));
                    x.Span(string.Join("  ", d.RefVersions));
                });
            });
        }
    }
}
