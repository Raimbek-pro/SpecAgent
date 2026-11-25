using System.Text.RegularExpressions;
using SpecAgent.Core.Documents;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Graphics;

namespace SpecAgent.Ingestion.Parsing;

/// <summary>
/// Layout-aware PDF parser built on PdfPig.
/// Extracts:
///   1. Ruled (bordered) tables via PDF vector paths/lines/thin rectangles → Markdown pipe tables.
///   2. Borderless / whitespace-aligned tables via consistent multi-column X-intervals → Markdown pipe tables.
///   3. Visual headings via document-relative font size / bold heuristics + SectionBuilder numbering/IDs.
///   4. Page number headers/footers filtered out so quotes don't break across pages.
/// </summary>
public sealed partial class PdfDocumentParser : IDocumentParser
{
    [GeneratedRegex(@"^(?:[-–—]\s*)?(?:(?:стр\.?|страница|page)\s*)?\d+(?:\s*(?:из|of|/)\s*\d+)?(?:\s*[-–—])?$", RegexOptions.IgnoreCase)]
    private static partial Regex PageNumberPattern();

    private sealed record Segment(double X1, double Y1, double X2, double Y2);
    private sealed record CellSpan(double Left, double Right, string Text);
    private sealed record VisualRow(double Bottom, double Top, double FontSize, bool IsBold, List<Word> Words);
    private sealed record PageBlock(double TopY, IReadOnlyList<(string Line, int? HeadingLevel)> Lines);

    public bool CanParse(string fileName) => fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    public TzDocument Parse(Stream content, string fileName)
    {
        using var buffer = EnsureSeekable(content);
        using var pdf = PdfDocument.Open(buffer);
        var pages = pdf.GetPages().ToList();

        var pageWords = pages.Select(p => p.GetWords().ToList()).ToList();
        var bodyFontSize = ComputeMedianFontSize(pageWords.SelectMany(w => w));

        var builder = new SectionBuilder();
        for (int i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            var words = pageWords[i];
            if (words.Count == 0)
            {
                var fallback = ContentOrderTextExtractor.GetText(page);
                foreach (var line in fallback.Split('\n')) builder.AddLine(line);
                continue;
            }

            foreach (var block in ExtractPageBlocks(page, words, bodyFontSize))
            {
                foreach (var (line, headingLevel) in block.Lines)
                    builder.AddLine(line, headingLevel);
            }
        }

        return builder.Build(fileName);
    }

    private static MemoryStream EnsureSeekable(Stream content)
    {
        var ms = new MemoryStream();
        content.CopyTo(ms);
        ms.Position = 0;
        return ms;
    }

    private static List<PageBlock> ExtractPageBlocks(Page page, List<Word> words, double bodyFontSize)
    {
        var blocks = new List<PageBlock>();
        var consumed = new HashSet<Word>();

        // 1. Detect ruled tables from PDF vector paths (lines + thin filled border rectangles).
        foreach (var table in ExtractRuledTables(page, words, consumed))
            blocks.Add(table);

        // 2. Group remaining words into visual rows sorted top-to-bottom.
        var remaining = words.Where(w => !consumed.Contains(w)).ToList();
        var rows = GroupIntoVisualRows(remaining);

        // Filter isolated page-number headers/footers near page margins.
        var filteredRows = rows.Where(r => !IsMarginPageNumber(r, page.Height)).ToList();

        // 3. Scan visual rows for borderless / whitespace-aligned tables vs normal paragraphs & headings.
        int idx = 0;
        while (idx < filteredRows.Count)
        {
            if (TryConsumeWhitespaceTable(filteredRows, idx, out var tableBlock, out var nextIdx))
            {
                blocks.Add(tableBlock);
                idx = nextIdx;
                continue;
            }

            var row = filteredRows[idx];
            var text = JoinWords(row.Words);
            if (!string.IsNullOrWhiteSpace(text))
            {
                var headingLevel = InferHeadingLevel(row, text, bodyFontSize);
                blocks.Add(new PageBlock(row.Top, [(text, headingLevel)]));
            }
            idx++;
        }

        // Preserve exact reading order top-to-bottom on the page.
        return blocks.OrderByDescending(b => b.TopY).ToList();
    }

    // ── Ruled table extraction (PdfPig vector paths) ─────────────────────────

    private static List<PageBlock> ExtractRuledTables(Page page, List<Word> words, HashSet<Word> consumed)
    {
        var horizontals = new List<Segment>();
        var verticals = new List<Segment>();

        foreach (var path in page.Paths)
        {
            if (!path.IsStroked && !path.IsFilled) continue;
            CollectSegments(path, horizontals, verticals);
        }

        if (horizontals.Count < 3 || verticals.Count < 3) return [];

        // Build connected components of intersecting/touching horizontal & vertical lines.
        var all = horizontals.Select(s => (seg: s, isH: true))
            .Concat(verticals.Select(s => (seg: s, isH: false)))
            .ToList();

        var visited = new bool[all.Count];
        var result = new List<PageBlock>();

        for (int i = 0; i < all.Count; i++)
        {
            if (visited[i]) continue;
            var compH = new List<Segment>();
            var compV = new List<Segment>();
            var queue = new Queue<int>();
            queue.Enqueue(i);
            visited[i] = true;

            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                var (seg, isH) = all[cur];
                if (isH) compH.Add(seg); else compV.Add(seg);

                for (int j = 0; j < all.Count; j++)
                {
                    if (visited[j]) continue;
                    if (SegmentsTouch(seg, all[j].seg, tolerance: 4.0))
                    {
                        visited[j] = true;
                        queue.Enqueue(j);
                    }
                }
            }

            var ys = ClusterCoords(compH.Select(s => (s.Y1 + s.Y2) / 2.0), tolerance: 3.5)
                .OrderByDescending(y => y).ToList();
            var xs = ClusterCoords(compV.Select(s => (s.X1 + s.X2) / 2.0), tolerance: 3.5)
                .OrderBy(x => x).ToList();

            if (ys.Count < 3 || xs.Count < 3) continue; // Need at least 2 rows x 2 columns.

            double minX = xs[0] - 2, maxX = xs[^1] + 2;
            double minY = ys[^1] - 2, maxY = ys[0] + 2;

            var tableWords = words.Where(w =>
            {
                var c = w.BoundingBox.Centroid;
                return c.X >= minX && c.X <= maxX && c.Y >= minY && c.Y <= maxY;
            }).ToList();

            if (tableWords.Count == 0) continue;

            var matrix = new List<List<string>>();
            for (int r = 0; r < ys.Count - 1; r++)
            {
                double top = ys[r], bottom = ys[r + 1];
                var rowCells = new List<string>(xs.Count - 1);
                for (int c = 0; c < xs.Count - 1; c++)
                {
                    double left = xs[c], right = xs[c + 1];
                    var cellWords = tableWords.Where(w =>
                    {
                        var pt = w.BoundingBox.Centroid;
                        return pt.X >= left - 1 && pt.X <= right + 1 && pt.Y <= top + 1 && pt.Y >= bottom - 1;
                    }).ToList();

                    rowCells.Add(FormatCellText(cellWords));
                }
                if (rowCells.Any(cell => cell.Length > 0))
                    matrix.Add(rowCells);
            }

            if (matrix.Count < 2) continue;

            foreach (var w in tableWords) consumed.Add(w);
            var mdLines = ToMarkdownTable(matrix).Select(l => (l, (int?)null)).ToList();
            result.Add(new PageBlock(ys[0], mdLines));
        }

        return result;
    }

    private static void CollectSegments(PdfPath path, List<Segment> horizontals, List<Segment> verticals)
    {
        foreach (var subpath in path)
        {
            PdfPoint? current = null;
            PdfPoint? start = null;

            foreach (var cmd in subpath.Commands)
            {
                switch (cmd)
                {
                    case PdfSubpath.Move move:
                        current = move.Location;
                        start = move.Location;
                        break;
                    case PdfSubpath.Line line:
                        AddIfAxisAligned(line.From, line.To, horizontals, verticals);
                        current = line.To;
                        break;
                    case PdfSubpath.Close when current is { } c && start is { } s:
                        AddIfAxisAligned(c, s, horizontals, verticals);
                        current = s;
                        break;
                }
            }
        }

        // Also handle thin filled rectangles (Word/Chrome often draw cell borders as thin filled rects).
        if (path.IsFilled && path.GetBoundingRectangle() is { } rect)
        {
            if (rect.Height <= 3.0 && rect.Width >= 20.0)
            {
                double y = (rect.Bottom + rect.Top) / 2.0;
                horizontals.Add(new Segment(rect.Left, y, rect.Right, y));
            }
            else if (rect.Width <= 3.0 && rect.Height >= 10.0)
            {
                double x = (rect.Left + rect.Right) / 2.0;
                verticals.Add(new Segment(x, rect.Bottom, x, rect.Top));
            }
        }
    }

    private static void AddIfAxisAligned(PdfPoint a, PdfPoint b, List<Segment> horizontals, List<Segment> verticals)
    {
        double dx = Math.Abs(a.X - b.X);
        double dy = Math.Abs(a.Y - b.Y);
        if (dy <= 1.5 && dx >= 20.0)
            horizontals.Add(new Segment(Math.Min(a.X, b.X), (a.Y + b.Y) / 2.0, Math.Max(a.X, b.X), (a.Y + b.Y) / 2.0));
        else if (dx <= 1.5 && dy >= 10.0)
            verticals.Add(new Segment((a.X + b.X) / 2.0, Math.Min(a.Y, b.Y), (a.X + b.X) / 2.0, Math.Max(a.Y, b.Y)));
    }

    private static bool SegmentsTouch(Segment a, Segment b, double tolerance)
    {
        double aMinX = Math.Min(a.X1, a.X2) - tolerance, aMaxX = Math.Max(a.X1, a.X2) + tolerance;
        double aMinY = Math.Min(a.Y1, a.Y2) - tolerance, aMaxY = Math.Max(a.Y1, a.Y2) + tolerance;
        double bMinX = Math.Min(b.X1, b.X2), bMaxX = Math.Max(b.X1, b.X2);
        double bMinY = Math.Min(b.Y1, b.Y2), bMaxY = Math.Max(b.Y1, b.Y2);
        return aMinX <= bMaxX && aMaxX >= bMinX && aMinY <= bMaxY && aMaxY >= bMinY;
    }

    private static List<double> ClusterCoords(IEnumerable<double> values, double tolerance)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var clusters = new List<double>();
        if (sorted.Count == 0) return clusters;

        double sum = sorted[0];
        int count = 1;
        double prev = sorted[0];

        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] - prev <= tolerance)
            {
                sum += sorted[i];
                count++;
            }
            else
            {
                clusters.Add(sum / count);
                sum = sorted[i];
                count = 1;
            }
            prev = sorted[i];
        }
        clusters.Add(sum / count);
        return clusters;
    }

    // ── Borderless / whitespace-aligned table detection ──────────────────────

    private static bool TryConsumeWhitespaceTable(List<VisualRow> rows, int startIdx, out PageBlock block, out int nextIdx)
    {
        block = null!;
        nextIdx = startIdx;

        var firstSpans = SplitRowIntoSpans(rows[startIdx]);
        if (firstSpans.Count < 2) return false;

        var candidateRows = new List<(VisualRow Row, List<CellSpan> Spans)> { (rows[startIdx], firstSpans) };
        int cursor = startIdx + 1;

        while (cursor < rows.Count)
        {
            var prevRow = rows[cursor - 1];
            var curRow = rows[cursor];
            double verticalGap = prevRow.Bottom - curRow.Top;
            if (verticalGap > Math.Max(22.0, prevRow.FontSize * 2.2)) break;

            var spans = SplitRowIntoSpans(curRow);
            if (spans.Count < 2) break;

            candidateRows.Add((curRow, spans));
            cursor++;
        }

        if (candidateRows.Count < 2) return false;

        // Build shared column buckets from all span left/right intervals.
        var columns = BuildColumnBuckets(candidateRows.SelectMany(r => r.Spans));
        if (columns.Count < 2) return false;

        // Verify that rows actually populate multiple aligned columns cleanly (no single span straddling everything).
        var matrix = new List<List<string>>(candidateRows.Count);
        foreach (var (_, spans) in candidateRows)
        {
            var rowCells = Enumerable.Repeat("", columns.Count).ToList();
            foreach (var span in spans)
            {
                int col = FindBestColumn(span, columns);
                rowCells[col] = string.IsNullOrEmpty(rowCells[col])
                    ? EscapePipe(span.Text)
                    : rowCells[col] + " " + EscapePipe(span.Text);
            }
            matrix.Add(rowCells);
        }

        // At least 2 columns must be non-empty across >= 2 rows to confirm a genuine table.
        int activeCols = Enumerable.Range(0, columns.Count)
            .Count(c => matrix.Count(r => !string.IsNullOrEmpty(r[c])) >= 2);
        if (activeCols < 2) return false;

        var mdLines = ToMarkdownTable(matrix).Select(l => (l, (int?)null)).ToList();
        block = new PageBlock(candidateRows[0].Row.Top, mdLines);
        nextIdx = cursor;
        return true;
    }

    private static List<CellSpan> SplitRowIntoSpans(VisualRow row)
    {
        var spans = new List<CellSpan>();
        if (row.Words.Count == 0) return spans;

        double gapThreshold = Math.Max(14.0, row.FontSize * 1.8);
        var curWords = new List<Word> { row.Words[0] };

        for (int i = 1; i < row.Words.Count; i++)
        {
            double gap = row.Words[i].BoundingBox.Left - row.Words[i - 1].BoundingBox.Right;
            if (gap > gapThreshold)
            {
                spans.Add(MakeSpan(curWords));
                curWords = [row.Words[i]];
            }
            else
            {
                curWords.Add(row.Words[i]);
            }
        }
        spans.Add(MakeSpan(curWords));
        return spans;
    }

    private static CellSpan MakeSpan(List<Word> words) =>
        new(words[0].BoundingBox.Left, words[^1].BoundingBox.Right, JoinWords(words));

    private static List<(double Left, double Right)> BuildColumnBuckets(IEnumerable<CellSpan> spans)
    {
        var sorted = spans.OrderBy(s => s.Left).ToList();
        var buckets = new List<(double Left, double Right)>();
        foreach (var s in sorted)
        {
            int match = buckets.FindIndex(b => s.Left <= b.Right + 6.0 && s.Right >= b.Left - 6.0);
            if (match >= 0)
                buckets[match] = (Math.Min(buckets[match].Left, s.Left), Math.Max(buckets[match].Right, s.Right));
            else
                buckets.Add((s.Left, s.Right));
        }
        return buckets.OrderBy(b => b.Left).ToList();
    }

    private static int FindBestColumn(CellSpan span, List<(double Left, double Right)> columns)
    {
        int best = 0;
        double bestScore = double.NegativeInfinity;
        double center = (span.Left + span.Right) / 2.0;

        for (int i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            double overlap = Math.Min(span.Right, col.Right) - Math.Max(span.Left, col.Left);
            double score = overlap > 0 ? overlap + 1000.0 : -Math.Abs(center - (col.Left + col.Right) / 2.0);
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }
        return best;
    }

    // ── Visual row grouping, headings & formatting helpers ───────────────────

    private static List<VisualRow> GroupIntoVisualRows(List<Word> words)
    {
        if (words.Count == 0) return [];

        var sorted = words.OrderByDescending(w => w.BoundingBox.Centroid.Y).ThenBy(w => w.BoundingBox.Left).ToList();
        var buckets = new List<List<Word>>();

        foreach (var w in sorted)
        {
            var target = buckets.FirstOrDefault(b =>
            {
                double avgY = b.Average(x => x.BoundingBox.Centroid.Y);
                double avgH = b.Average(x => x.BoundingBox.Height);
                return Math.Abs(w.BoundingBox.Centroid.Y - avgY) <= Math.Max(3.0, avgH * 0.5);
            });

            if (target is not null) target.Add(w);
            else buckets.Add([w]);
        }

        return buckets
            .Select(b =>
            {
                var ordered = b.OrderBy(w => w.BoundingBox.Left).ToList();
                var letters = ordered.SelectMany(w => w.Letters).ToList();
                double fontSize = letters.Count > 0 ? letters.Average(l => l.PointSize) : 10.0;
                bool isBold = letters.Count > 0 &&
                              letters.Count(l => l.FontDetails.IsBold || (l.FontName?.Contains("Bold", StringComparison.OrdinalIgnoreCase) ?? false)) > letters.Count / 2;
                return new VisualRow(
                    Bottom: ordered.Min(w => w.BoundingBox.Bottom),
                    Top: ordered.Max(w => w.BoundingBox.Top),
                    FontSize: fontSize,
                    IsBold: isBold,
                    Words: ordered);
            })
            .OrderByDescending(r => r.Top)
            .ToList();
    }

    private static double ComputeMedianFontSize(IEnumerable<Word> words)
    {
        var sizes = words
            .SelectMany(w => w.Letters)
            .Select(l => Math.Round(l.PointSize, 1))
            .Where(s => s > 0)
            .OrderBy(s => s)
            .ToList();
        return sizes.Count == 0 ? 11.0 : sizes[sizes.Count / 2];
    }

    private static int? InferHeadingLevel(VisualRow row, string text, double bodyFontSize)
    {
        if (bodyFontSize <= 0 || text.Length > 140) return null;
        if (row.FontSize >= bodyFontSize * 1.38) return 1;
        if (row.FontSize >= bodyFontSize * 1.16) return 2;

        // Standalone short bold title that doesn't look like a sentence or bullet point.
        if (row.IsBold && text.Length <= 90 && !text.EndsWith('.') && !text.EndsWith(';') &&
            !text.StartsWith('-') && !text.StartsWith('•'))
            return 2;

        return null;
    }

    private static bool IsMarginPageNumber(VisualRow row, double pageHeight)
    {
        if (pageHeight <= 0) return false;
        bool nearEdge = row.Bottom < pageHeight * 0.08 || row.Top > pageHeight * 0.93;
        if (!nearEdge) return false;
        var text = JoinWords(row.Words).Trim();
        return PageNumberPattern().IsMatch(text);
    }

    private static string FormatCellText(List<Word> cellWords)
    {
        if (cellWords.Count == 0) return "";
        var subRows = GroupIntoVisualRows(cellWords);
        var joined = string.Join(" ", subRows.Select(r => JoinWords(r.Words)));
        return EscapePipe(joined.Trim());
    }

    private static string JoinWords(IEnumerable<Word> words) =>
        string.Join(" ", words.Select(w => w.Text.Trim()).Where(t => t.Length > 0));

    private static string EscapePipe(string s) => s.Replace("|", "\\|").Trim();

    private static IEnumerable<string> ToMarkdownTable(List<List<string>> rows)
    {
        if (rows.Count == 0) yield break;
        int cols = rows.Max(r => r.Count);
        string RowLine(List<string> r) =>
            "| " + string.Join(" | ", Enumerable.Range(0, cols).Select(i => i < r.Count ? r[i] : "")) + " |";

        yield return RowLine(rows[0]);
        yield return "|" + string.Concat(Enumerable.Repeat(" --- |", cols));
        foreach (var r in rows.Skip(1))
            yield return RowLine(r);
    }
}
