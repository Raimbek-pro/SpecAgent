using System.Text;
using System.Text.RegularExpressions;
using SpecAgent.Core.Documents;

namespace SpecAgent.Ingestion.Parsing;

/// <summary>
/// Turns a flat stream of lines (with optional heading hints) into numbered sections.
/// Shared by every parser so all formats produce the same AST shape.
/// </summary>
public sealed partial class SectionBuilder
{
    // "3", "3.2", "3.2.1." followed by a title. Requires a letter after the number so
    // "100 000 тенге" or "2. 5" are not mistaken for headings.
    [GeneratedRegex(@"^\s*(\d{1,2}(?:\.\d{1,2}){0,4})\.?\s+(\p{L}.{0,600})$")]
    private static partial Regex NumberedHeading();

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
        if (headingLevel is { } lvl && text.Length > 0)
        {
            var m = NumberedHeading().Match(text);
            StartSection(m.Success ? m.Groups[1].Value : null, m.Success ? m.Groups[2].Value.Trim() : text.Trim(), lvl);
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
            var looksLikeItem = rest.EndsWith('.') || rest.Contains(';') || rest.Length > 90;
            if (!looksLikeItem)
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

    private void StartSection(string? id, string title, int level)
    {
        Flush();
        _id = id ?? $"s-{++_auto}";
        _title = title;
        _level = level;
        _docTitle ??= title;
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
