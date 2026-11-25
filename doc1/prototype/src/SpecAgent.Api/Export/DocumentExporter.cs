using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SpecAgent.Core.Documents;
using SpecAgent.Core.Findings;
using WDocument = DocumentFormat.OpenXml.Wordprocessing.Document;
using WTable = DocumentFormat.OpenXml.Wordprocessing.Table;
using WTableRow = DocumentFormat.OpenXml.Wordprocessing.TableRow;
using WTableCell = DocumentFormat.OpenXml.Wordprocessing.TableCell;

namespace SpecAgent.Api.Export;

/// <summary>
/// Exports a clean, patched (or original) TzDocument to real .pdf (QuestPDF) or .docx (OpenXML),
/// preserving headings, paragraphs, bullet lists, code blocks, and Markdown pipe tables.
/// </summary>
public static partial class DocumentExporter
{
    static DocumentExporter()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        QuestPDF.Settings.CheckIfAllTextGlyphsAreAvailable = false;
        const string fontDir = "/usr/share/fonts/truetype/dejavu";
        if (Directory.Exists(fontDir))
        {
            foreach (var ttf in Directory.EnumerateFiles(fontDir, "*.ttf"))
            {
                using var fs = File.OpenRead(ttf);
                QuestPDF.Drawing.FontManager.RegisterFont(fs);
            }
        }
    }

    [GeneratedRegex(@"^[-*•]\s+")] private static partial Regex BulletPrefix();
    [GeneratedRegex(@"^\d+\.\s+")] private static partial Regex NumberedPrefix();
    [GeneratedRegex(@"\*\*([^*]+)\*\*")] private static partial Regex BoldMd();
    [GeneratedRegex(@"`([^`]+)`")] private static partial Regex CodeMd();

    private static string StripInlineMd(string s) => CodeMd().Replace(BoldMd().Replace(s, "$1"), "$1");

    public static IReadOnlyList<(string Id, string Title, string Body)> BuildCleanSections(TzDocument doc, AuditReport? report, bool includeGherkin = false)
    {
        var patchBySection = report?.Patches
            .GroupBy(p => p.SectionId)
            .ToDictionary(g => g.Key, g => g.Last())
            ?? new Dictionary<string, Patch>();

        var result = new List<(string, string, string)>();
        foreach (var s in doc.Sections)
        {
            string body;
            if (patchBySection.TryGetValue(s.Id, out var p) && !string.IsNullOrWhiteSpace(p.After))
            {
                body = p.After.Trim();
                if (includeGherkin && !string.IsNullOrWhiteSpace(p.Gherkin))
                    body += $"\n\n### Критерии приёмки (Gherkin)\n```gherkin\n{p.Gherkin.Trim()}\n```";
            }
            else
            {
                body = s.Text.Trim();
            }
            result.Add((s.Id, s.Title, body));
        }
        return result;
    }

    // ── PDF Export (QuestPDF, crisp Cyrillic via DejaVu Sans) ──────────────────
    public static byte[] RenderPdf(TzDocument doc, AuditReport? report, bool includeGherkin = false)
    {
        var sections = BuildCleanSections(doc, report, includeGherkin);
        var isPatched = report is not null;

        return QuestPDF.Fluent.Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.MarginVertical(18, Unit.Millimetre);
                page.MarginHorizontal(16, Unit.Millimetre);
                page.DefaultTextStyle(x => x.FontFamily("DejaVu Sans").FontSize(10).FontColor("#1F2328").LineHeight(1.4f));

                page.Content().Column(col =>
                {
                    col.Spacing(6);

                    col.Item().PaddingBottom(6).BorderBottom(1.2f).BorderColor("#1F2328")
                        .Text(doc.Title).FontSize(15).Bold().FontColor("#111827");

                    if (isPatched)
                    {
                        col.Item().PaddingBottom(4)
                            .Text($"Исправленная версия · SpecAgent ({report!.Patches.Count} исправленных разделов)")
                            .FontSize(8.5f).FontColor("#57606A");
                    }

                    foreach (var (id, title, body) in sections)
                    {
                        if (!string.IsNullOrWhiteSpace(title))
                        {
                            var headingText = id.StartsWith("s-", StringComparison.OrdinalIgnoreCase)
                                ? title
                                : $"{id}  {title}";
                            col.Item().PaddingTop(8).Text(headingText).FontSize(12).Bold().FontColor("#111827");
                        }
                        else if (!id.StartsWith("s-", StringComparison.OrdinalIgnoreCase) && id != "0")
                        {
                            col.Item().PaddingTop(6).Text(id).FontSize(10.5f).Bold().FontColor("#24292F");
                        }

                        RenderMarkdownBlocksToPdf(col, body);
                    }
                });

                page.Footer().AlignRight().Text(x =>
                {
                    x.DefaultTextStyle(s => s.FontSize(8.5f).FontColor("#8C959F"));
                    x.Span("Стр. ");
                    x.CurrentPageNumber();
                    x.Span(" / ");
                    x.TotalPages();
                });
            });
        }).GeneratePdf();
    }

    private static void RenderMarkdownBlocksToPdf(ColumnDescriptor col, string markdown)
    {
        var lines = (markdown ?? "").Replace("\r\n", "\n").Split('\n');
        var tableLines = new List<string>();
        var inCode = false;
        var codeLines = new List<string>();

        void FlushTable()
        {
            if (tableLines.Count == 0) return;
            var rows = tableLines
                .Where(l => !Regex.IsMatch(l.Trim(), @"^\|[\s:|\-]+\|$"))
                .Select(l => l.Trim().Trim('|').Split('|').Select(c => StripInlineMd(c.Trim())).ToList())
                .Where(r => r.Count > 0)
                .ToList();
            tableLines.Clear();
            if (rows.Count == 0) return;

            var colCount = rows.Max(r => r.Count);
            col.Item().PaddingVertical(4).Table(table =>
            {
                table.ColumnsDefinition(cols =>
                {
                    for (int i = 0; i < colCount; i++) cols.RelativeColumn();
                });
                for (int r = 0; r < rows.Count; r++)
                {
                    var isHeader = r == 0;
                    for (int c = 0; c < colCount; c++)
                    {
                        var val = c < rows[r].Count ? rows[r][c] : "";
                        var cell = table.Cell().Border(0.6f).BorderColor("#D0D7DE")
                            .Background(isHeader ? "#F6F8FA" : "#FFFFFF")
                            .PaddingVertical(4).PaddingHorizontal(6);
                        if (isHeader) cell.Text(val).FontSize(9).Bold();
                        else cell.Text(val).FontSize(9);
                    }
                }
            });
        }

        void FlushCode()
        {
            if (codeLines.Count == 0) return;
            var text = string.Join('\n', codeLines);
            codeLines.Clear();
            col.Item().PaddingVertical(3).Background("#F6F8FA").Border(0.5f).BorderColor("#D0D7DE")
                .Padding(6).Text(text).FontFamily("DejaVu Sans Mono").FontSize(8.5f);
        }

        foreach (var raw in lines)
        {
            var tr = raw.Trim();
            if (tr.StartsWith("```"))
            {
                FlushTable();
                if (!inCode) inCode = true;
                else { inCode = false; FlushCode(); }
                continue;
            }
            if (inCode) { codeLines.Add(raw); continue; }

            if (tr.StartsWith('|') && tr.EndsWith('|'))
            {
                tableLines.Add(tr);
                continue;
            }
            FlushTable();

            if (string.IsNullOrWhiteSpace(tr)) continue;
            if (tr.StartsWith("### "))
            {
                col.Item().PaddingTop(4).Text(StripInlineMd(tr[4..])).FontSize(10.5f).Bold();
            }
            else if (tr.StartsWith("## "))
            {
                col.Item().PaddingTop(6).Text(StripInlineMd(tr[3..])).FontSize(11.5f).Bold();
            }
            else if (tr.StartsWith("# "))
            {
                col.Item().PaddingTop(6).Text(StripInlineMd(tr[2..])).FontSize(12.5f).Bold();
            }
            else if (BulletPrefix().IsMatch(tr))
            {
                var itemText = StripInlineMd(BulletPrefix().Replace(tr, ""));
                col.Item().PaddingLeft(10).Row(row =>
                {
                    row.ConstantItem(10).Text("•").Bold();
                    row.RelativeItem().Text(itemText);
                });
            }
            else if (NumberedPrefix().IsMatch(tr))
            {
                col.Item().PaddingLeft(6).Text(StripInlineMd(tr));
            }
            else if (tr.StartsWith("> "))
            {
                col.Item().PaddingVertical(2).BorderLeft(2).BorderColor("#D0D7DE").PaddingLeft(8)
                    .Text(StripInlineMd(tr[2..])).FontColor("#4B5563");
            }
            else
            {
                col.Item().Text(StripInlineMd(tr));
            }
        }
        FlushTable();
        FlushCode();
    }

    // ── DOCX Export (DocumentFormat.OpenXml) ───────────────────────────────────
    public static byte[] RenderDocx(TzDocument doc, AuditReport? report, bool includeGherkin = false)
    {
        var sections = BuildCleanSections(doc, report, includeGherkin);
        using var ms = new MemoryStream();
        using (var wordDoc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, true))
        {
            var mainPart = wordDoc.AddMainDocumentPart();
            mainPart.Document = new WDocument(new Body());
            var body = mainPart.Document.Body!;

            body.AppendChild(MakeParagraph(doc.Title, bold: true, fontSizeHalfPt: "32", spaceAfter: "160"));
            if (report is not null)
                body.AppendChild(MakeParagraph($"Исправленная версия · SpecAgent ({report.Patches.Count} исправленных разделов)", bold: false, fontSizeHalfPt: "18", spaceAfter: "200", colorHex: "57606A"));

            foreach (var (id, title, text) in sections)
            {
                if (!string.IsNullOrWhiteSpace(title))
                {
                    var h = id.StartsWith("s-", StringComparison.OrdinalIgnoreCase) ? title : $"{id} {title}";
                    body.AppendChild(MakeParagraph(h, bold: true, fontSizeHalfPt: "26", spaceBefore: "200", spaceAfter: "100"));
                }
                else if (!id.StartsWith("s-", StringComparison.OrdinalIgnoreCase) && id != "0")
                {
                    body.AppendChild(MakeParagraph(id, bold: true, fontSizeHalfPt: "22", spaceBefore: "140", spaceAfter: "60"));
                }

                AppendMarkdownToDocxBody(body, text);
            }

            mainPart.Document.Save();
        }
        return ms.ToArray();
    }

    private static void AppendMarkdownToDocxBody(Body body, string markdown)
    {
        var lines = (markdown ?? "").Replace("\r\n", "\n").Split('\n');
        var tableLines = new List<string>();

        void FlushTable()
        {
            if (tableLines.Count == 0) return;
            var rows = tableLines
                .Where(l => !Regex.IsMatch(l.Trim(), @"^\|[\s:|\-]+\|$"))
                .Select(l => l.Trim().Trim('|').Split('|').Select(c => StripInlineMd(c.Trim())).ToList())
                .Where(r => r.Count > 0)
                .ToList();
            tableLines.Clear();
            if (rows.Count == 0) return;

            var tbl = new WTable(new TableProperties(
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 4, Color = "D0D7DE" },
                    new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "D0D7DE" },
                    new LeftBorder { Val = BorderValues.Single, Size = 4, Color = "D0D7DE" },
                    new RightBorder { Val = BorderValues.Single, Size = 4, Color = "D0D7DE" },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "D0D7DE" },
                    new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = "D0D7DE" }
                ),
                new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct }
            ));

            var colCount = rows.Max(r => r.Count);
            for (int r = 0; r < rows.Count; r++)
            {
                var tr = new WTableRow();
                for (int c = 0; c < colCount; c++)
                {
                    var val = c < rows[r].Count ? rows[r][c] : "";
                    var tc = new WTableCell(MakeParagraph(val, bold: r == 0, fontSizeHalfPt: "20", spaceAfter: "40"));
                    if (r == 0)
                        tc.AppendChild(new TableCellProperties(new Shading { Val = ShadingPatternValues.Clear, Fill = "F6F8FA" }));
                    tr.AppendChild(tc);
                }
                tbl.AppendChild(tr);
            }
            body.AppendChild(tbl);
            body.AppendChild(MakeParagraph("", false, "10", spaceAfter: "80"));
        }

        foreach (var raw in lines)
        {
            var tr = raw.Trim();
            if (tr.StartsWith("```")) { FlushTable(); continue; }
            if (tr.StartsWith('|') && tr.EndsWith('|')) { tableLines.Add(tr); continue; }
            FlushTable();
            if (string.IsNullOrWhiteSpace(tr)) continue;

            if (tr.StartsWith("### "))
                body.AppendChild(MakeParagraph(StripInlineMd(tr[4..]), bold: true, fontSizeHalfPt: "22", spaceBefore: "100", spaceAfter: "60"));
            else if (tr.StartsWith("## "))
                body.AppendChild(MakeParagraph(StripInlineMd(tr[3..]), bold: true, fontSizeHalfPt: "24", spaceBefore: "140", spaceAfter: "80"));
            else if (BulletPrefix().IsMatch(tr))
                body.AppendChild(MakeParagraph("• " + StripInlineMd(BulletPrefix().Replace(tr, "")), bold: false, fontSizeHalfPt: "21", spaceAfter: "60", indentLeft: "360"));
            else
                body.AppendChild(MakeParagraph(StripInlineMd(tr), bold: false, fontSizeHalfPt: "21", spaceAfter: "80"));
        }
        FlushTable();
    }

    private static Paragraph MakeParagraph(string text, bool bold, string fontSizeHalfPt, string? spaceAfter = null, string? spaceBefore = null, string? colorHex = null, string? indentLeft = null)
    {
        var pProps = new ParagraphProperties();
        if (spaceAfter is not null || spaceBefore is not null)
            pProps.AppendChild(new SpacingBetweenLines { After = spaceAfter ?? "0", Before = spaceBefore ?? "0" });
        if (indentLeft is not null)
            pProps.AppendChild(new Indentation { Left = indentLeft });

        var rProps = new RunProperties(
            new RunFonts { Ascii = "Arial", HighAnsi = "Arial", ComplexScript = "Arial" },
            new FontSize { Val = fontSizeHalfPt }
        );
        if (bold) rProps.AppendChild(new Bold());
        if (colorHex is not null) rProps.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Color { Val = colorHex });

        var run = new Run(rProps, new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return new Paragraph(pProps, run);
    }
}
