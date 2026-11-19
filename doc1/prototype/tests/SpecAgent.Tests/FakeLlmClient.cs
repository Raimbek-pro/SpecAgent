using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using SpecAgent.Agents.Agents;
using SpecAgent.Agents.Llm;
using SpecAgent.Core.Findings;

namespace SpecAgent.Tests;

/// <summary>Scripted LLM: deterministic answers per agent, call counting, and crash injection.</summary>
public sealed partial class FakeLlmClient : ILlmClient
{
    public ConcurrentDictionary<string, int> Calls { get; } = new();

    /// <summary>Agent name → simulate the worker process dying during that agent's call (once).</summary>
    public string? CrashOnceOn { get; set; }

    [GeneratedRegex("\"id\":\"([^\"]+)\"")] private static partial Regex RawId();

    public Task<T> CompleteJsonAsync<T>(AgentCall call, CancellationToken ct = default) where T : class
    {
        Calls.AddOrUpdate(call.Agent, 1, (_, n) => n + 1);
        if (CrashOnceOn == call.Agent)
        {
            CrashOnceOn = null;
            throw new OperationCanceledException("simulated worker crash");
        }

        object result = (typeof(T).Name, call.Agent) switch
        {
            (nameof(SupervisorOutput), _) => new SupervisorOutput
            {
                Domain = "P2P-переводы", Complexity = "high", Critics = ["logic", "qa"], Rationale = "payments",
            },
            (nameof(CriticOutput), CriticNames.Distributed) => new CriticOutput
            {
                Findings =
                [
                    new() { SectionId = "4.3", Quote = "при ошибке запрос повторяется", Title = "Ретрай без идемпотентности",
                        Description = "Двойное списание при таймауте", Category = "idempotency", Severity = Severity.Blocker },
                    new() { SectionId = "4.1", Quote = "используется протокол ISO 8583", Title = "Галлюцинация",
                        Description = "Цитаты нет в документе", Category = "x", Severity = Severity.Major },
                ],
            },
            (nameof(CriticOutput), CriticNames.Logic) => new CriticOutput
            {
                Findings =
                [
                    new() { SectionId = "3", Quote = "клиент видит статус DECLINED", Title = "DECLINED vs REJECTED",
                        Description = "Статус называется по-разному", Category = "consistency", Severity = Severity.Major },
                ],
            },
            (nameof(CriticOutput), _) => new CriticOutput(),
            (nameof(JudgeOutput), _) => new JudgeOutput
            {
                Verdicts = RawId().Matches(call.User).Select(m => m.Groups[1].Value).Distinct()
                    .Select(id => new JudgeVerdict { RawIds = [id], Accept = true, Severity = Severity.Critical, Rationale = "ok" })
                    .ToList(),
            },
            (nameof(PatchDraft), _) => new PatchDraft { After = "Исправленный текст с Idempotency-Key.", Gherkin = "Given… When… Then…" },
            (nameof(VerifierOutput), _) => new VerifierOutput { Ok = true },
            (nameof(AskOutput), _) => new AskOutput { Answer = "IDOR не обнаружен" },
            _ => throw new NotSupportedException(typeof(T).Name),
        };
        return Task.FromResult((T)result);
    }
}
