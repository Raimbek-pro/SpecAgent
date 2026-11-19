# Benchmark dataset

- `specs/` — ТЗ (.md / .docx / .pdf). Real open specs plus copies with **deliberately injected defects**.
- `labels/` — one JSON per spec: the ground-truth defects (section, category, severity).

A predicted finding matches a labelled defect when the section matches and the category/summary is the same defect (judged by hand or by an LLM grader, recorded in the results table). From that:

- **Precision** = matched findings / all findings reported
- **Recall** = labelled defects found / all labelled defects
- **Time** = wall-clock per document (from `RUN_STARTED` to `REPORT_READY` in the event log)

Target before the pitch: 5–10 specs (mobile banking, payment gateway, CRM, personal cabinet), at least 3 with injected defects. See [docs/evaluation.md](../docs/evaluation.md).

`p2p-transfer-bad.md` is the live-demo spec: the double-debit-on-timeout defect (D1) is the headline.
