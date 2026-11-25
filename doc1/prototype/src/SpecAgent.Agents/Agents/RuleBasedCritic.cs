using System.Text.RegularExpressions;
using SpecAgent.Core.Documents;
using SpecAgent.Core.Findings;

namespace SpecAgent.Agents.Agents;

/// <summary>
/// Deterministic critic: no LLM, same input → same output, runs in milliseconds and works offline.
/// It is the floor under the LLM critics (they can only add to it) and the baseline we report
/// Precision/Recall against.
/// </summary>
public static partial class RuleBasedCritic
{
    [GeneratedRegex(@"(?i)\b(быстро|удобно|при необходимости|оперативно|интуитивно|своевременно|по возможности|максимально|корректно|и т\.\s?д\.|и т\.\s?п\.|как правило|достаточно)\b")]
    private static partial Regex Vague();
    [GeneratedRegex(@"(?i)\b(float|double|real)\b")] private static partial Regex FloatType();
    [GeneratedRegex(@"(?i)сумм|amount|баланс|balance|тенге|KZT|цена|price")] private static partial Regex Money();
    [GeneratedRegex(@"(?i)перевод|списан|плат[её]ж|transfer|payment|charge")] private static partial Regex MoneyMove();
    [GeneratedRegex(@"(?i)идемпотент|idempoten")] private static partial Regex Idempotency();
    [GeneratedRegex(@"(?i)повтор|retry|ретра")] private static partial Regex Retry();
    [GeneratedRegex(@"(?i)вызов|запрос|\bPOST\b|процессинг|шлюз|gateway")] private static partial Regex ExternalCall();
    [GeneratedRegex(@"(?i)тайм-?аут|timeout|время ожидания")] private static partial Regex Timeout();
    [GeneratedRegex(@"(?i)\bCVV2?\b|\bCVC2?\b")] private static partial Regex Cvv();
    [GeneratedRegex(@"(?i)лог|журнал|log|сохран|хран")] private static partial Regex StoreOrLog();
    [GeneratedRegex(@"(?i)REST type.*\bGET\b|curl\s+-G.*save")] private static partial Regex GetMutation();
    [GeneratedRegex(@"(?i)""result""\s*:\s*""ERROR""")] private static partial Regex OkWithError();
    [GeneratedRegex(@"(?i)заполня[юе]тся тестировщиками|\bTBD\b|отметки не проставлены")] private static partial Regex StubText();

    public static IReadOnlyList<RawFinding> Review(TzDocument doc)
    {
        var found = new List<RawFinding>();
        int n = 0;
        void Add(TzSection s, string quote, string title, string description, string category, Severity sev,
            string rec, string std = "") => found.Add(new RawFinding
        {
            Id = $"{CriticNames.Rules}-{++n}", Agent = CriticNames.Rules, SectionId = s.Id, Quote = quote,
            Title = title, Description = description, Category = category, SuggestedSeverity = sev,
            Recommendation = rec, StandardRef = std,
        });

        foreach (var s in doc.Sections)
        {
            foreach (var line in Lines(s))
            {
                if (Vague().Match(line) is { Success: true } v)
                    Add(s, line, $"Размытая формулировка «{v.Value}»",
                        $"«{v.Value}» не проверяемо: нет количественного критерия.", "ambiguity", Severity.Minor,
                        "Заменить на измеримое значение (например, p95 ≤ 2 с) и добавить критерий приёмки.");

                if (FloatType().IsMatch(line) && Money().IsMatch(line))
                    Add(s, line, "Денежная сумма в типе с плавающей точкой",
                        "float/double теряют точность на денежных суммах — ошибки округления и расхождения при сверке.",
                        "data-types", Severity.Critical,
                        "Использовать decimal (или целое в минимальных единицах — тиынах) и указать валюту.");

                if (Cvv().IsMatch(line) && StoreOrLog().IsMatch(line))
                    Add(s, line, "CVV/CVC сохраняется или логируется",
                        "Хранение CVV после авторизации запрещено, даже в зашифрованном виде.", "pci-dss", Severity.Blocker,
                        "Запретить хранение и логирование CVV/CVC; маскировать PAN (первые 6 / последние 4).",
                        "PCI DSS v4.0 req. 3.3.1");

                if (GetMutation().IsMatch(line))
                    Add(s, line, "Мутация состояния через HTTP GET",
                        "Изменение настроек/лимитов через GET-запрос нарушает RFC 7231 и приводит к утечке параметров в логи прокси и кэш.",
                        "api-design", Severity.Blocker,
                        "Заменить метод на PUT/POST с передачей параметров в теле application/json.",
                        "RFC 7231 / ISO 29148");

                if (OkWithError().IsMatch(line) && s.Title.Contains("200", StringComparison.OrdinalIgnoreCase))
                    Add(s, line, "HTTP 200 OK возвращается при бизнес-ошибке",
                        "Возврат 200 OK с телом {\"result\":\"ERROR\"} нарушает REST-контракт и ломает мониторинг/шлюзы.",
                        "api-design", Severity.Critical,
                        "Возвращать HTTP 400 Bad Request / 422 Unprocessable Entity с единой структурой ошибки.",
                        "ISO/IEC/IEEE 29148 (Consistency)");

                if (StubText().IsMatch(line))
                    Add(s, line, "Незаполненный раздел (заглушка в ТЗ)",
                        "В спецификации оставлена заглушка без конкретных требований или отметок ревью.",
                        "completeness", Severity.Major,
                        "Заполнить раздел проверяемыми требованиями и критериями приёмки до передачи в разработку.",
                        "ISO/IEC/IEEE 29148 п. 5.2.6 (Completeness)");
            }
        }

        // Document-level absences, pinned to where the fix belongs: the line that calls/retries the
        // payment if there is one, otherwise the first sentence that moves money.
        var allLines = doc.Sections.SelectMany(s => Lines(s).Select(l => (s, l))).ToList();
        var narrative = allLines.Where(x => !x.l.StartsWith('|')).ToList(); // skip table rows
        var moneyLine = narrative.FirstOrDefault(x => Retry().IsMatch(x.l) && ExternalCall().IsMatch(x.l));
        if (moneyLine.s is null) moneyLine = narrative.FirstOrDefault(x => ExternalCall().IsMatch(x.l) && MoneyMove().IsMatch(x.l));
        if (moneyLine.s is null) moneyLine = allLines.FirstOrDefault(x => MoneyMove().IsMatch(x.l));
        if (moneyLine.s is not null)
        {
            var all = doc.ToPromptText();
            if (!Idempotency().IsMatch(all))
                Add(moneyLine.s, moneyLine.l, "Нет защиты от двойного списания (идемпотентность)",
                    "В документе не описан ключ идемпотентности: повтор запроса после сетевого таймаута приведёт к повторному списанию.",
                    "idempotency", Severity.Blocker,
                    "Обязательный заголовок Idempotency-Key (UUID) на всех операциях списания; хранить результат 24 ч и возвращать его на повтор.");
            if (!Timeout().IsMatch(all))
                Add(moneyLine.s, moneyLine.l, "Не заданы таймауты и политика ретраев",
                    "Нет таймаутов на вызовы внешних систем и правила обработки неизвестного результата операции.",
                    "timeouts", Severity.Critical,
                    "Указать таймауты на каждый внешний вызов, число ретраев с backoff и статус-запрос/сверку при UNKNOWN.");
        }

        return found;
    }

    private static IEnumerable<string> Lines(TzSection s) =>
        s.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Select(l => l.Length > 250 ? l[..250] : l);
}
