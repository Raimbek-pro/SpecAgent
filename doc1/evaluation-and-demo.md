# Evaluation, demo and timeline

## Benchmark dataset

```text
dataset/
├─ specs/    5–10 ТЗ: mobile banking, payment gateway, CRM, personal cabinet (.md / .docx / .pdf)
└─ labels/   one JSON per spec with the ground-truth defects
```

- Start from real open specifications, then make copies with **deliberately injected defects**: logic contradictions, security holes, vague wording.
- Label format:

```json
{
  "spec": "p2p-transfer-bad.md",
  "defects": [
    { "id": "D1", "section": "4.3", "category": "idempotency", "severity": "Blocker",
      "summary": "Повтор запроса без ключа идемпотентности → двойное списание" }
  ]
}
```

### Demo spec: `p2p-transfer-bad.md` (P2P transfer by phone number)

| Id | Section | Injected defect | Severity |
|---|---|---|---|
| D1 | 4.3 | Retry on error with no idempotency key → double debit on timeout | Blocker |
| D2 | 4.3 | No timeouts, no handling of UNKNOWN outcome | Critical |
| D3 | 5.1 | PAN and CVV written to the application log | Blocker |
| D4 | 4.1 | `amount (float)` | Critical |
| D5 | 2.5 / 3 | DECLINED in the scenario, REJECTED in the status table | Major |
| D6 | 2.4 | No compensation if credit fails after debit | Critical |
| D7 | 2.1 | No amount/count limits (AML) | Major |
| D8 | 2.3 | No limit on SMS-code attempts | Major |
| D9 | 6.1 | «быстро», «удобно», «при необходимости» | Minor |
| D10 | 2.2 | Recipient's full name shown by phone number (PII exposure) | Major |

It also contains a valid test ИИН and a phone number, to show masking live.

## Metrics

| Metric | Definition | Source |
|---|---|---|
| Precision | matched findings / all reported findings | report vs labels |
| Recall | labelled defects found / all labelled defects | report vs labels |
| Recall@Blocker | Blocker defects found / Blocker defects | report vs labels |
| Hallucination rate | ungrounded raw findings / all raw findings | `GROUNDED` event |
| Time per document | `RUN_STARTED` → `REPORT_READY` | event timestamps |
| Cost per document | Σ tokens × price per agent | per-call usage logs |

Matching rule: same section (or the section the label names) and the same defect. Judge by hand for the pitch table; an LLM grader can be added later, with its own agreement check against the hand labels.

Report three configurations on the same specs to show what each layer adds:

| Configuration | Precision | Recall | Time | Cost |
|---|---|---|---|---|
| Rules critic only (no LLM) | | | | $0 |
| Single prompt, single model | | | | |
| Full SpecAgent (routing + critics + judge) | | | | |

Comparison point for the pitch: ~15–20 s per document versus ~4 hours of an analyst's review.

## Demo script (pitch, 4 December)

1. **Upload** `p2p-transfer-bad.md`. Point out the trace line `PII values masked` and that the ИИН/phone are tokens in the log.
2. **Watch the agents**: Supervisor detects a payment module and adds the compliance and distributed-systems critics; critics report in parallel.
3. **Headline**: the Blocker card "Нет защиты от двойного списания" on section 4.3 — click it, the text highlights on the left.
4. **One-click fix**: open the patch — before/after with an `Idempotency-Key` requirement and Gherkin scenarios; show "verified, 1 round".
5. **Trust**: open the debate view — which critics raised it, the judge's rationale, and one hallucinated finding that grounding rejected.
6. **Human in the loop**: `@compliance проверь 4.1 на IDOR` on a card, answer arrives live.
7. **Resilience (optional)**: kill the API container mid-audit, restart, watch `RESUMED` and the report finish without re-running critics.
8. Close with the metrics table and the architecture diagram (PII perimeter + deterministic checks highlighted).

## Timeline

| When | What |
|---|---|
| Before 25 Nov | Repo skeleton in C#: parsers, PII masker, event log, Claude client with structured outputs, split-screen UI |
| Before 25 Nov | Knowledge base in Markdown: ТЗ writing standard, FinTech security checklist (idempotency, 3DS, PCI DSS, timeouts, retries), НБРК/АРРФР infosec notes |
| Before 25 Nov | Dataset: 5–10 specs, at least 3 with injected defects, labels |
| 25 Nov (kick-off) | Read the official case from БЦК; adapt prompts, routing rules, severity criteria and knowledge base |
| 72 h hackathon | Day 1: end-to-end pipeline on the demo spec. Day 2: quality on the full dataset, patch loop, debate view. Day 3: metrics table, Docker Compose, Telegram bot, rehearsal |
| 4 Dec | Pitch: live demo, metrics, architecture |

## Open questions for kick-off

- Which document formats and size does БЦК actually use? (Affects parser priority.)
- Is there an internal ТЗ template or regulation to add to the knowledge base?
- Where must the model run (direct API vs a cloud platform in a specific region)?
- Should patches be exported back into the original .docx, or is a report enough?
