# Multi-agent orchestration

Topology: **Supervisor → parallel specialist critics → Judge (consensus) → Writer ⇄ Verifier (critic-refiner loop)**. Subagents are only worth their cost if each one adds independent signal; this layout is designed so that they do.

```mermaid
flowchart LR
    IN([masked ТЗ]) --> SUP{{Supervisor}}
    SUP -->|sections 2, 4| L[logic]
    SUP -->|all| Q[qa]
    SUP -->|4, 5| C[compliance]
    SUP -->|2, 4| D[distributed]
    IN --> R[rules<br/>deterministic]
    L & Q & C & D & R --> G[/Ground quotes<br/>deterministic/]
    G --> J{{Meta-Critic}}
    J --> S[/Score<br/>deterministic/]
    S --> W[Patch Writer]
    W --> V{Verifier ok?}
    V -- no, round < 2 --> W
    V -- yes / round = 2 --> OUT([report])
```

## Why these levels

| Level | Why it exists | What it saves or protects |
|---|---|---|
| Supervisor | A UI redesign doesn't need a PCI DSS auditor; a payment gateway always does | Tokens and time on simple documents |
| Parallel critics | Narrow prompts find more than one big prompt; parallel runs keep latency at the slowest critic, not the sum | Recall; wall-clock time |
| Deterministic rules critic | Some defects are pattern-matchable and must never be missed | Floor on recall; offline demo |
| Grounding | Models sometimes quote text that isn't there | Hallucinated findings never reach the analyst |
| Judge | 5 critics produce duplicates and conflicts | Precision; one severity scale |
| Writer ⇄ Verifier | A fix can break the neighbouring section | Patches that are safe to paste |

## Execution model

The orchestrator is a single method, `RunAsync(sessionId)`, that is both **start** and **resume**:

```text
state = AuditState.From(log.Read(session))        // rebuild everything from events
if state.Finished: return
append RESUMED if state.Started else RUN_STARTED

routing  = state.Routing  ?? Supervisor.Route()          → append ROUTED
rules    = state.Findings[rules] ?? RulesCritic.Review()  → append FINDINGS
pending  = routing.critics without a FINDINGS event
parallel for each pending critic:                         → append CRITIC_STARTED, FINDINGS
judged   = state.Judged   ?? Ground() + Judge() + Score()  → append GROUNDED, JUDGED
patches  = state.Patches  ?? PatchLoop()                  → append PATCHES
append REPORT_READY(report)
```

- **Fan-out:** `Task.WhenAll` over the pending critics. Each critic gets its own `CriticAgent` instance and context.
- **Fan-in through the log:** each critic writes its own `FINDINGS` event as soon as it finishes. There is no in-memory merge step that could be lost.
- **Idempotent stages:** every stage first checks whether its event exists. Re-running `RunAsync` on a finished stage is a no-op.

## Crash and resume

```mermaid
sequenceDiagram
    participant W1 as Worker 1
    participant LOG as Event log
    participant W2 as Worker 2 (fresh)
    W1->>LOG: RUN_STARTED, ROUTED
    W1->>LOG: FINDINGS ×5 (critics done)
    W1--xW1: crash during Meta-Critic
    Note over LOG: no REPORT_READY → session is "open"
    W2->>LOG: ListOpenSessions() on startup
    W2->>LOG: Read(s-42)
    W2->>LOG: RESUMED (from event 9)
    Note over W2: critics already have FINDINGS → skipped
    W2->>LOG: GROUNDED, JUDGED, PATCHES, REPORT_READY
```

- On startup the worker calls `ListOpenSessionsAsync()` and re-enqueues every session without a terminal event.
- `RUN_FAILED` (e.g. the API was down) is not "finished": `POST /api/audits/{id}/resume` retries it.
- Shutdown (`OperationCanceledException`) deliberately writes nothing, so the session stays open and is picked up again.

## Failure policy per agent

| Agent fails | Behaviour |
|---|---|
| Supervisor | All critics on all sections |
| One critic | `CRITIC_FAILED` + empty `FINDINGS`; others continue |
| Meta-Critic | Deterministic merge by section + quote |
| Patch writer/verifier | That patch is skipped; report still ships |
| Model refusal | Server-side fallback model answers in the same call (Opus 5.5 / Sonnet 5.5); otherwise treated as a failure above |

## Model routing and cost

Strong reasoning where decisions are made (Supervisor, Meta-Critic, Verifier: Claude Opus 5.5), mid-tier for detailed reading (logic, compliance, distributed, writer: Claude Sonnet 5.5), cheapest for pattern work (QA checker: Claude Haiku 5.5). On the pitch this is the **cost-per-document** argument. Everything is configurable per agent:

```json
"ModelRouting": {
  "Default": { "Model": "claude-opus-5-5", "Effort": "medium" },
  "Agents": {
    "supervisor":  { "Model": "claude-opus-5-5",   "Effort": "medium" },
    "qa":          { "Model": "claude-haiku-5-5",  "Effort": "low" },
    "distributed": { "Model": "claude-sonnet-5-5", "Effort": "high" },
    "meta-critic": { "Model": "claude-opus-5-5",   "Effort": "high" }
  }
}
```

Before committing to a multi-model cascade, measure the simpler option too: Claude Opus 5.5 at lower effort for every agent. Pick by cost per *correct* finding on the benchmark, not per request.

## Making the agents visible (demo)

- **Live trace** — every event streams to the UI over SSE: `supervisor · ROUTED — P2P-переводы (high) → logic, qa, compliance, distributed`, `meta-critic · JUDGED — 14 → 9 findings (2 Blocker, 4 Critical, 3 Minor)`.
- **Agent logs / debate tab** — click a finding to see which critics raised it, their raw wording, and the judge's rationale (`GET /api/audits/{id}/findings/{fid}/trace`).
- **@mentions** — ask one critic about one section live.
