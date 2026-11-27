using SpecAgent.Agents.Agents;
using SpecAgent.Core.Findings;
using SpecAgent.Ingestion.Parsing;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace SpecAgent.Tests;

public class PdfParserTests
{
    [Fact]
    public void Pdf_with_ruled_table_extracts_markdown_table_and_preserves_sections()
    {
        var pdfBytes = BuildRuledTablePdf();
        using var stream = new MemoryStream(pdfBytes);

        var doc = new PdfDocumentParser().Parse(stream, "spec-ruled.pdf");

        Assert.Contains(doc.Sections, s => s.Id == "1" && s.Title.Contains("Overview"));
        var statusSection = Assert.Single(doc.Sections, s => s.Id == "2");
        Assert.Contains("| Status | Code | Description |", statusSection.Text);
        Assert.Contains("| --- | --- | --- |", statusSection.Text);
        Assert.Contains("| NEW | 100 | Created |", statusSection.Text);
        Assert.Contains("| REJECTED | 400 | Declined |", statusSection.Text);

        var apiSection = Assert.Single(doc.Sections, s => s.Id == "3.1");
        Assert.Contains("Retry executes synchronously on timeout.", apiSection.Text);
        // Bottom margin page number must not leak into the last section.
        Assert.DoesNotContain("\n1", apiSection.Text);

        // Verify quote grounding works on extracted PDF table and requirement text.
        var raw = new[]
        {
            new RawFinding
            {
                Id = "r-1", Agent = "logic", SectionId = "2",
                Quote = "| REJECTED | 400 | Declined |",
                Title = "Status mismatch", Description = "d", Category = "consistency",
                SuggestedSeverity = Severity.Major,
            },
            new RawFinding
            {
                Id = "r-2", Agent = "distributed", SectionId = "3.1",
                Quote = "Retry executes synchronously on timeout.",
                Title = "Missing idempotency", Description = "d", Category = "idempotency",
                SuggestedSeverity = Severity.Blocker,
            },
        };
        var grounded = MetaCriticAgent.Ground(doc, raw);
        Assert.Empty(grounded.Ungrounded);
        Assert.Equal(2, grounded.Grounded.Count);
    }

    [Fact]
    public void Pdf_with_borderless_table_extracts_aligned_columns()
    {
        var pdfBytes = BuildBorderlessTablePdf();
        using var stream = new MemoryStream(pdfBytes);

        var doc = new PdfDocumentParser().Parse(stream, "spec-borderless.pdf");

        var limitsSection = Assert.Single(doc.Sections, s => s.Id == "Part A");
        Assert.Contains("| Tier | DailyLimit | Currency |", limitsSection.Text);
        Assert.Contains("| Standard | 500000 | KZT |", limitsSection.Text);
        Assert.Contains("| Premium | 2000000 | KZT |", limitsSection.Text);

        var reqSection = Assert.Single(doc.Sections, s => s.Id == "FR-1");
        Assert.Contains("System must validate daily card spending limits.", reqSection.Text);
    }

    [Fact]
    public void SectionBuilder_splits_complex_russian_headings_and_requirement_ids()
    {
        var lines = new[]
        {
            "ТЗ: Расходы по картам БЦК",
            "Часть A. Функциональные требования",
            "Ф-1: Отображение истории операций",
            "Пользователь видит список операций по карте за выбранный период.",
            "Ф-2. Выгрузка выписки выполняется синхронно; при таймауте выполняется повторный запрос.",
            "| Поле | Тип | Обязательность |",
            "| --- | --- | --- |",
            "| amount | float | Да |",
            "Часть B — Нефункциональные требования",
            "НФ-1: Время отклика API",
            "Сервис должен отвечать быстро.",
            "Приложение 1: Коды ошибок",
            "400 — некорректный запрос.",
        };

        var builder = new SectionBuilder();
        foreach (var l in lines) builder.AddLine(l);
        var doc = builder.Build("bcc-card-expenses.md");

        Assert.Contains(doc.Sections, s => s.Id == "Часть A" && s.Title == "Функциональные требования");
        Assert.Contains(doc.Sections, s => s.Id == "Ф-1" && s.Title == "Отображение истории операций"
                                           && s.Text.Contains("список операций по карте"));
        Assert.Contains(doc.Sections, s => s.Id == "Ф-2"
                                           && s.Text.Contains("Выгрузка выписки выполняется синхронно")
                                           && s.Text.Contains("| amount | float | Да |"));
        Assert.Contains(doc.Sections, s => s.Id == "Часть B" && s.Title == "Нефункциональные требования");
        Assert.Contains(doc.Sections, s => s.Id == "НФ-1" && s.Title == "Время отклика API");
        Assert.Contains(doc.Sections, s => s.Id == "Приложение 1" && s.Title == "Коды ошибок");
    }

    [Fact]
    public void SectionBuilder_keeps_wrapped_pdf_requirement_lines_in_body_not_title()
    {
        var builder = new SectionBuilder();
        builder.AddLine("2 Функциональные требования", 1);
        builder.AddLine("Ф-1: Короткий заголовок");
        builder.AddLine("Тело первого требования.");
        builder.AddLine("Ф-4 После подтверждения сервис отправляет запрос GET /api/v1/transfers/execute?");
        builder.AddLine("from=1&to=2&amount=100 и при таймауте повторяет до 3 раз.");
        var doc = builder.Build("p2p-transfers-tz.pdf");

        var f1 = Assert.Single(doc.Sections, s => s.Id == "Ф-1");
        Assert.Equal("Короткий заголовок", f1.Title);

        var f4 = Assert.Single(doc.Sections, s => s.Id == "Ф-4");
        Assert.Equal("", f4.Title);
        Assert.Contains("GET /api/v1/transfers/execute?", f4.Text);
        Assert.Contains("повторяет до 3 раз", f4.Text);
    }

    [Fact]
    public void Pdf_with_cyrillic_complex_headings_and_table_parses_when_truetype_font_available()
    {
        var fontPath = FindCyrillicFont();
        if (fontPath is null) return; // Skip gracefully on minimal Linux images without system TTF fonts.

        var pdfBytes = BuildCyrillicPdf(File.ReadAllBytes(fontPath));
        using var stream = new MemoryStream(pdfBytes);

        var doc = new PdfDocumentParser().Parse(stream, "bcc-card-expenses.pdf");

        Assert.Contains(doc.Sections, s => s.Id == "Часть A");
        Assert.Contains(doc.Sections, s => s.Id == "Ф-1" && s.Text.Contains("лимит"));
        var f2 = Assert.Single(doc.Sections, s => s.Id == "Ф-2");
        Assert.Contains("| Категория | Лимит |", f2.Text);
        Assert.Contains("| Кафе | 100000 |", f2.Text);
        Assert.Contains(doc.Sections, s => s.Id == "Часть B");
    }

    [Fact]
    public void Bcc_official_markdown_spec_splits_parts_and_requirements_cleanly()
    {
        var path = Path.Combine(TestPaths.RepoRoot, "dataset", "specs", "bcc-card-expenses.md");
        if (!File.Exists(path)) return;

        using var f = File.OpenRead(path);
        var doc = new TextDocumentParser().Parse(f, "bcc-card-expenses.md");

        Assert.Contains(doc.Sections, s => s.Id == "Часть A" && s.Title.Contains("Новый функционал"));
        Assert.Contains(doc.Sections, s => s.Id == "Часть B" && s.Title.Contains("REST API"));
        Assert.Contains(doc.Sections, s => s.Id == "Ф-1" && s.Text.Contains("1 000 000"));
        Assert.Contains(doc.Sections, s => s.Id == "Ф-2" && s.Text.Contains("Алматы"));
        Assert.Contains(doc.Sections, s => s.Id == "Ф-3" && s.Text.Contains("push"));
        Assert.Contains(doc.Sections, s => s.Id == "Ф-4" && s.Text.Contains("накопленная сумма"));
    }

    private static byte[] BuildRuledTablePdf()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);

        page.AddText("Spec: Card Transfers", 18, new PdfPoint(50, 790), bold);
        page.AddText("1 Overview", 14, new PdfPoint(50, 755), bold);
        page.AddText("Clients transfer funds using a phone number.", 11, new PdfPoint(50, 735), font);

        page.AddText("2 Transfer Statuses", 14, new PdfPoint(50, 700), bold);

        // Draw a 3-row x 3-column ruled table grid: x = [50, 170, 270, 470], y = [680, 655, 630, 605]
        double[] xs = [50, 170, 270, 470];
        double[] ys = [680, 655, 630, 605];
        foreach (var y in ys) page.DrawLine(new PdfPoint(xs[0], y), new PdfPoint(xs[^1], y));
        foreach (var x in xs) page.DrawLine(new PdfPoint(x, ys[^1]), new PdfPoint(x, ys[0]));

        page.AddText("Status", 11, new PdfPoint(58, 663), bold);
        page.AddText("Code", 11, new PdfPoint(178, 663), bold);
        page.AddText("Description", 11, new PdfPoint(278, 663), bold);

        page.AddText("NEW", 11, new PdfPoint(58, 638), font);
        page.AddText("100", 11, new PdfPoint(178, 638), font);
        page.AddText("Created", 11, new PdfPoint(278, 638), font);

        page.AddText("REJECTED", 11, new PdfPoint(58, 613), font);
        page.AddText("400", 11, new PdfPoint(178, 613), font);
        page.AddText("Declined", 11, new PdfPoint(278, 613), font);

        page.AddText("3.1 Retry executes synchronously on timeout.", 11, new PdfPoint(50, 565), font);

        // Bottom margin page number (should be filtered out)
        page.AddText("1", 10, new PdfPoint(295, 25), font);

        return builder.Build();
    }

    private static byte[] BuildBorderlessTablePdf()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);

        page.AddText("Part A: Card Limits", 15, new PdfPoint(50, 780), bold);

        // Borderless whitespace-aligned table
        page.AddText("Tier", 11, new PdfPoint(50, 745), bold);
        page.AddText("DailyLimit", 11, new PdfPoint(200, 745), bold);
        page.AddText("Currency", 11, new PdfPoint(350, 745), bold);

        page.AddText("Standard", 11, new PdfPoint(50, 725), font);
        page.AddText("500000", 11, new PdfPoint(200, 725), font);
        page.AddText("KZT", 11, new PdfPoint(350, 725), font);

        page.AddText("Premium", 11, new PdfPoint(50, 705), font);
        page.AddText("2000000", 11, new PdfPoint(200, 705), font);
        page.AddText("KZT", 11, new PdfPoint(350, 705), font);

        page.AddText("FR-1: System must validate daily card spending limits.", 11, new PdfPoint(50, 650), font);

        return builder.Build();
    }

    private static byte[] BuildCyrillicPdf(byte[] ttfBytes)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddTrueTypeFont(ttfBytes);

        page.AddText("Часть A. Расходы по картам", 15, new PdfPoint(50, 780), font);
        page.AddText("Ф-1: Контроль лимита", 12, new PdfPoint(50, 750), font);
        page.AddText("Система проверяет дневной лимит по карте перед списанием.", 10, new PdfPoint(50, 732), font);
        page.AddText("Ф-2: Категории расходов", 12, new PdfPoint(50, 700), font);

        double[] xs = [50, 220, 380];
        double[] ys = [685, 660, 635];
        foreach (var y in ys) page.DrawLine(new PdfPoint(xs[0], y), new PdfPoint(xs[^1], y));
        foreach (var x in xs) page.DrawLine(new PdfPoint(x, ys[^1]), new PdfPoint(x, ys[0]));

        page.AddText("Категория", 10, new PdfPoint(60, 668), font);
        page.AddText("Лимит", 10, new PdfPoint(230, 668), font);
        page.AddText("Кафе", 10, new PdfPoint(60, 643), font);
        page.AddText("100000", 10, new PdfPoint(230, 643), font);

        page.AddText("Часть B. Требования безопасности", 15, new PdfPoint(50, 590), font);
        page.AddText("Данные карт хранятся в зашифрованном виде.", 10, new PdfPoint(50, 568), font);

        return builder.Build();
    }

    private static string? FindCyrillicFont()
    {
        string[] candidates =
        [
            @"C:\Windows\Fonts\arial.ttf",
            @"C:\Windows\Fonts\segoeui.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
}
