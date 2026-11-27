using System.Text;
using System.Text.RegularExpressions;
using SpecAgent.Core.Documents;

namespace SpecAgent.Ingestion.Parsing;

/// <summary>
/// Turns a flat stream of lines (with optional heading hints) into numbered/named sections.
/// Shared by every parser so all formats produce the same AST shape.
/// Supports numeric headings ("3", "3.2", "4.3"), named structural sections ("Часть A", "Раздел 1",
/// "Приложение 1"), and requirement IDs ("Ф-1", "НФ-2", "FR-1", "NFR-3").
/// </summary>
public sealed partial class SectionBuilder
{
    // "3", "3.2", "3.2.1." followed by a title. Requires a letter after the number so
    // "100 000 тенге" or "2. 5" are not mistaken for headings.
    [GeneratedRegex(@"^\s*(\d{1,2}(?:\.\d{1,2}){0,4})\.?\s+(\p{L}.{0,600})$")]
    private static partial Regex NumberedHeading();

    // "Часть A", "Часть B. Требования", "Раздел 1 — Общие положения", "Приложение A: Схема"
    [GeneratedRegex(@"^\s*((?:Часть|Part|Раздел|Глава|Приложение|Appendix)\s+[A-Za-zА-Яа-яЁё0-9]+)(?:\s*[:.\-—–]\s*(.*)|\s+(\p{L}.*))?$", RegexOptions.IgnoreCase)]
    private static partial Regex NamedSectionHeading();

    // "Ф-1: Лимит по карте", "* **Ф-1.** Держатель карты...", "НФ-1 Время отклика...", "FR-12 ..."
    [GeneratedRegex(@"^\s*(?:[\*\-•]\s+)?(?:\*\*)?((?:ФТ|НФТ|БТ|ИБ|Ф|НФ|FR|NFR|BR|REQ|UC|US)-\d+(?:\.\d+)*)(?:\*\*)?(?:\s*[:.\-—–]\s*(?:\*\*)?|\s+)(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex RequirementIdHeading();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpace();

    private readonly List<TzSection> _sections = [];
    private readonly StringBuilder _body = new();
    private string _id = "0";
    private string _title = "Преамбула";
    private int _level = 0;
    private int _auto = 0;
    private string? _docTitle;

    public void AddLine(string line, int? headingLevel = null)
    {
        var text = line.TrimEnd();

        // Markdown table rows must never be split as headings.
        if (text.TrimStart().StartsWith('|'))
        {
            _body.AppendLine(text);
            return;
        }

        if (headingLevel is { } lvl && text.Length > 0)
        {
            if (TryMatchNamedHeading(text, isExplicitHeading: true, out var namedId, out var namedTitle))
            {
                StartSection(namedId, namedTitle, lvl);
                return;
            }
            if (TryMatchRequirementHeading(text, out var reqId, out var reqRest))
            {
                AddRequirementSection(reqId, reqRest, lvl);
                return;
            }
            var m = NumberedHeading().Match(text);
            StartSection(m.Success ? m.Groups[1].Value : null, m.Success ? m.Groups[2].Value.Trim() : text.Trim(), lvl);
            return;
        }

        if (TryMatchNamedHeading(text, isExplicitHeading: false, out var nId, out var nTitle))
        {
            StartSection(nId, nTitle, 1);
            return;
        }

        if (TryMatchRequirementHeading(text, out var rId, out var rRest))
        {
            AddRequirementSection(rId, rRest, 2);
            return;
        }

        var numbered = NumberedHeading().Match(text);
        if (numbered.Success)
        {
            var id = numbered.Groups[1].Value;
            var rest = numbered.Groups[2].Value.Trim();
            var level = id.Count(c => c == '.') + 1;
            // "4.3 Вызов процессинга выполняется синхронно." is a numbered requirement, not a heading:
            // it still becomes its own section so findings can point at "4.3" exactly.
            if (!LooksLikeItem(rest))
            {
                StartSection(id, rest, level);
                return;
            }
            if (level > 1)
            {
                StartSection(id, "", level);
                _body.AppendLine(rest);
                return;
            }
            // "5 рабочих дней требуется на …" — single number + sentence is ordinary text.
        }

        if (_docTitle is null && text.Trim().Length > 0) _docTitle = text.Trim();
        _body.AppendLine(text);
    }

    private void AddRequirementSection(string id, string rest, int level)
    {
        if (!LooksLikeRequirementBody(rest))
        {
            StartSection(id, rest, level);
        }
        else
        {
            StartSection(id, "", level);
            _body.AppendLine(rest);
        }
    }

    private static bool LooksLikeItem(string rest) =>
        rest.EndsWith('.') || rest.EndsWith('?') || rest.Contains(';') || rest.Length > 90;

    // Wrapped PDF requirement lines (e.g. "Ф-4 После подтверждения сервис отправляет запрос GET /api/...")
    // must stay in section body rather than Title so patches rewrite them cleanly.
    private static bool LooksLikeRequirementBody(string rest) =>
        LooksLikeItem(rest) ||
        rest.Contains(". ") ||
        rest.Contains(',') ||
        rest.Contains('/') ||
        rest.EndsWith('!') ||
        rest.Length > 55;

    private static bool TryMatchNamedHeading(string text, bool isExplicitHeading, out string id, out string title)
    {
        var m = NamedSectionHeading().Match(text);
        if (!m.Success) { id = ""; title = ""; return false; }

        var rawTitle = m.Groups[2].Success ? m.Groups[2].Value.Trim()
                     : m.Groups[3].Success ? m.Groups[3].Value.Trim()
                     : "";
        // Avoid false positives on prose sentences like "Часть B — дочерняя спецификация, на которую ссылается..."
        if (!isExplicitHeading && (rawTitle.EndsWith('.') || rawTitle.Length > 90))
        {
            id = ""; title = ""; return false;
        }

        id = MultiSpace().Replace(m.Groups[1].Value.Trim(), " ");
        title = rawTitle;
        return true;
    }

    private static bool TryMatchRequirementHeading(string text, out string id, out string rest)
    {
        var m = RequirementIdHeading().Match(text);
        if (!m.Success) { id = ""; rest = ""; return false; }

        id = m.Groups[1].Value.Trim().ToUpperInvariant();
        rest = m.Groups[2].Value.Trim();
        return rest.Length > 0;
    }

    private void StartSection(string? id, string title, int level)
    {
        Flush();
        _id = id ?? $"s-{++_auto}";
        _title = title;
        _level = level;
        if (_docTitle is null && title.Length > 0) _docTitle = title;
    }

    private void Flush()
    {
        var text = _body.ToString().Trim();
        if (text.Length > 0 || _level > 0)
        {
            var id = _id;
            // Duplicate numbers happen in real ТЗ (copy-paste). Keep ids unique.
            if (_sections.Any(s => s.Id == id)) id = $"{id}~{_sections.Count(s => s.Id.StartsWith(id))}";
            _sections.Add(new TzSection(id, _title, _level, text));
        }
        _body.Clear();
    }

    public TzDocument Build(string fileName)
    {
        Flush();
        return new TzDocument(_docTitle ?? Path.GetFileNameWithoutExtension(fileName), fileName, _sections.ToList());
    }
}
