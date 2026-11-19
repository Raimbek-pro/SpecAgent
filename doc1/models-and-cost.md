# Models and cost: Qwen vs Gemini

> **In one minute**
> - SpecAgent now runs on **Qwen** (Alibaba Cloud Model Studio): `qwen4-max` for decisions, `qwen3.7-plus` for critics, `qwen3.7-flash` for routine checks and the checklist pass.
> - On list prices, Qwen comes out roughly **1.5–3× cheaper per audit** than comparable Gemini models: about **$0.19** vs **$0.30–0.57** per document, depending on the Gemini tier and date. Both are cents against hours of analyst time.
> - Qwen supports what our agents need (strict JSON schema output, tool calling, 1M context), **if you pick the right model versions**.
> - Biggest advantage: Qwen also ships **open weights** (Apache 2.0), so the bank can run the same models **on its own servers in Kazakhstan**.

*Prices are list prices from the official pages, fetched **19 Nov 2026**: [Alibaba Model Studio billing](https://www.alibabacloud.com/help/en/model-studio/billing-for-model-studio) (Singapore / International region) and [Google AI Studio pricing](https://aistudio.google.com/docs/pricing) (paid tier, Standard). USD per 1 million tokens. Prices change often; re-check before quoting them on the pitch.*

---

## 1. Price comparison

### Qwen (Alibaba Cloud Model Studio, International)

| Role in SpecAgent | Model | Input | Output | Notes |
|---|---|---|---|---|
| **Decisions** | `qwen4-max` | **$1.50** | **$4.50** | Same price with or without thinking; 1M context; cached input $0.075 |
| Alternative | `qwen3.8-max` | $2.00 | $6.00 | Older Max; more expensive than qwen4-max |
| **Critics, writer** | `qwen3.7-plus` | **$0.40** | **$1.60** | Prompts up to 256K; above that $1.20 / $4.80; temporary 20% promo in the console, not counted here |
| **Routine, checklist** | `qwen3.7-flash` | **$0.03** | **$0.13** | Prompts up to 32K; up to 256K: $0.10 / $0.40; batch at half price |
| Alternative | `qwen3.8-flash` | $0.15 | $0.47 | Newer Flash, single price up to 1M |
| **Embeddings** for Qdrant | `Qwen3-Embedding` (open weights) | self-hosted | — | Apache 2.0; 0.6B / 4B / 8B sizes; 100+ languages |

### Gemini (Google AI Studio, paid tier)

| Comparable tier | Model | Input | Output | Notes |
|---|---|---|---|---|
| Decisions | `gemini-4-hydrogen` | $1.50 | $7.50 | 1M context |
| Decisions | `gemini-4-argon` | $2.00 → **$4.00 from 1 Dec 2026** | $10.00 → **$20.00 from 1 Dec** | Price doubles before the 4 Dec final |
| Decisions | `gemini-4-neon` | $3.00 | $15.00 | |
| Critics | `gemini-3.8-flash` | $0.75 → **$1.50 from 1 Jan 2027** | $3.75 → **$7.50 from 1 Jan** | Introductory price |
| Critics (older) | `gemini-3.1-pro-preview` | $2.00 (≤200K) | $12.00 (≤200K) | Preview; read from a page summary, re-check |
| Routine | `gemini-3.5-flash-lite` | $0.30 | $2.50 | |
| Routine (older) | `gemini-2.5-flash-lite` | $0.10 | $0.40 | Read from a page summary, re-check |

For Gemini, output prices **include thinking tokens**. For `qwen4-max`, thinking costs the same per token. Either way, a model that "thinks" a lot produces more output tokens and costs more than this table suggests.

### Same job, side by side

| Tier | Qwen in / out | Gemini in / out | Qwen is cheaper by |
|---|---|---|---|
| Decisions | 1.50 / 4.50 (`qwen4-max`) | 1.50 / 7.50 (`hydrogen`) · 4.00 / 20.00 (`argon` from 1 Dec) | ~1.7× on output (vs hydrogen) · ~3–4× (vs argon) |
| Critics | 0.40 / 1.60 (`qwen3.7-plus`) | 0.75 / 3.75 (`3.8-flash`) → 1.50 / 7.50 in 2027 | ~2× now, ~4× from January |
| Routine | 0.03 / 0.13 (`qwen3.7-flash`) | 0.30 / 2.50 (`3.5-flash-lite`) · 0.10 / 0.40 (`2.5-flash-lite`) | ~3–20× |

---

## 2. Cost of one audit (estimate)

**Assumptions** (our design; replace them with measured numbers after the first runs):
- a 30-page ТЗ ≈ 20,000 tokens;
- 5 patches, about 1.3 verifier rounds each;
- a 40-rule checklist pass;
- knowledge-base excerpts add about 3,000 tokens to each critic.

| Step | Calls | Tokens in / out (total) | Tier |
|---|---|---|---|
| Supervisor | 1 | 5K / 1K | decisions |
| Logic, Compliance, Distributed critics | 3 | 45K / 6K | critics |
| QA critic | 1 | 15K / 2K | routine |
| Checklist pass | 40 small calls | 60K / 4K | routine |
| Meta-Critic | 1 | 25K / 4K | decisions |
| Patch Writer | ~6.5 | 20K / 10K | critics |
| Patch Verifier | ~6.5 | 33K / 3K | decisions |

| Provider | Decisions | Critics | Routine | **Total per ТЗ** |
|---|---|---|---|---|
| **Qwen** (`qwen4-max` / `qwen3.7-plus` / `qwen3.7-flash`) | $0.13 | $0.05 | <$0.01 | **≈ $0.19** |
| Gemini, now (`hydrogen` / `3.8-flash` / `3.5-flash-lite`) | $0.16 | $0.11 | $0.04 | **≈ $0.30** |
| Gemini, from Jan 2027 (`3.8-flash` doubles) | $0.16 | $0.21 | $0.04 | **≈ $0.41** |
| Gemini with `argon` for decisions (from 1 Dec) | $0.42 | $0.11 | $0.04 | **≈ $0.57** |

**How to read this:**
- Both are **cents per document** against roughly 4 hours of an analyst's time. Price alone won't decide the project.
- Qwen's edge is real (roughly 1.5–3×) and grows when Gemini's introductory prices end.
- What matters more: **quality on Russian-language banking ТЗ** and **where the data goes** (section 4). Measure quality on the dataset before committing.

---

## 3. Can Qwen run our agentic system?

**Yes, with the right model versions.** Our agents need four things:

| Need | Qwen | Notes |
|---|---|---|
| **Strict JSON output** (each agent must return exactly its C# record) | ✅ `response_format: json_schema, strict: true` on **qwen4-max, Qwen3.7-Plus, Qwen3.7/3.8-Flash, Qwen3.7/3.8-Max** | ❌ **Not** on Qwen3.6-Plus, Qwen3.5 or the open-weight models via the API, so don't pick those for agents. Unsupported schema keywords (`multipleOf`, `uniqueItems`, `patternProperties`…) return an error; our schemas don't use them. |
| **Tool calling** (`execute()`) | ✅ all Max, Plus and Flash series; parallel tool calls supported | `tool_choice: "required"` is **not** supported for Qwen. We don't force tools, so this doesn't affect us. |
| **Long context** (the judge reads the whole ТЗ) | ✅ 1M tokens on qwen4-max (max output 131K, or 262K with thinking) | Plenty for any ТЗ |
| **Russian** | ✅ Qwen is strongly multilingual | **Measure** on our Russian dataset: this is the real open question |

**To check in the first hour of the hackathon:**
1. **Thinking + JSON schema together.** The docs don't clearly say whether `json_schema` works with thinking on. Test it. If it doesn't, run the critics in non-thinking mode, or let them think in one call and format in a second.
2. **Region.** One Alibaba page says JSON Schema isn't yet supported in Singapore, while the qwen4-max page lists it as supported there. Test on the region we actually use.
3. **Refusals and errors.** There's no server-side "fallback model" feature like the one we planned with Claude, so we add a simple **client-side fallback**: retry once, then move to the next model in the tier (e.g. `qwen3.7-plus` → `qwen3.8-flash`).

### What changes in the C# backend

Very little, because every model call already goes through `ILlmClient`:
- **Replace** `ClaudeLlmClient` with `QwenLlmClient`.
- **Model Studio** offers an OpenAI-compatible endpoint (`https://dashscope-intl.aliyuncs.com/compatible-mode/v1`), so the client can use the official **OpenAI .NET library** pointed at that endpoint, with `response_format = json_schema` built from the same C# records.
- **Same code, two backends:**
  - **cloud:** Alibaba Model Studio;
  - **on-premise:** open-weight Qwen served by **vLLM**, which exposes the same OpenAI-compatible API and enforces the JSON schema on its side.

  Switching between them is a config change (endpoint + model name).
- The agents, prompts, log, judge and everything else stay as they are.

---

## 4. Data residency: the strongest argument for Qwen

| Option | Where customer text goes | Fit for a Kazakh bank |
|---|---|---|
| Gemini API | Google's cloud | Masked text leaves the country |
| Qwen on Model Studio | Alibaba Cloud (Singapore / International) | Masked text leaves the country |
| **Qwen open weights on the bank's servers** | **Nowhere: stays inside the bank** | **Best**: nothing leaves, and data-localisation rules are easy to meet |

- Our masking already ensures no real ИИН, card numbers or IBANs reach any model. Self-hosting removes the remaining question about the rest of the ТЗ text.
- Open-weight models confirmed on Hugging Face under **Apache 2.0**: e.g. `Qwen/Qwen3.6-35B-A3B` (35B total, 3B active per token, fast) and `Qwen/Qwen3.8-27B`.
- **Plan:** use Model Studio during the hackathon for speed, then present **self-hosted Qwen** as the production path.

---

## 5. Default model routing (`appsettings.json`)

```json
"ModelRouting": {
  "Default": { "Model": "qwen3.7-plus" },
  "Agents": {
    "supervisor":     { "Model": "qwen4-max" },
    "logic":          { "Model": "qwen3.7-plus" },
    "qa":             { "Model": "qwen3.7-flash" },
    "compliance":     { "Model": "qwen3.7-plus" },
    "distributed":    { "Model": "qwen3.7-plus" },
    "checklist":      { "Model": "qwen3.7-flash" },
    "meta-critic":    { "Model": "qwen4-max" },
    "patch-writer":   { "Model": "qwen3.7-plus" },
    "patch-verifier": { "Model": "qwen4-max" }
  },
  "Fallbacks": {
    "qwen4-max":     "qwen3.8-max",
    "qwen3.7-plus":  "qwen3.8-flash",
    "qwen3.7-flash": "qwen3.8-flash"
  }
}
```

As with any routing, also measure the simple option: `qwen4-max` for every agent. Keep the cascade only if it's cheaper **per correctly found defect** at the same quality.
