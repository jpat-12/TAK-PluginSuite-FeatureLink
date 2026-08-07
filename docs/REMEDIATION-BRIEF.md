# FeatureLink Suite — Audit Remediation Brief (shared context for all work packages)

**Branch:** `audit-remediation` (forked from `dev` @ `d5a7ad9`).
**Baseline commit:** `a8f6f4a` — tracks the three previously-untracked auto-symbology sources
(`ATAK5.{6,7}/…/arcgis/AutoSymbology.java`, `CloudTAK/plugin/lib/autoSymbology.ts`) plus the 27
in-flight modifications. This closes **C-29**. Everything you do is diffable against `a8f6f4a`.

**Source of truth:** `FEATURELINK-AUDIT-CHECKLIST.md` at the repo root (3,354 lines, 1,539 checkboxes).
- §0–§5 are the adjudicated executive core and **govern** wherever an appendix disagrees.
- Appendix F §4 (`:3287-3332`) is the **Master Deduplicated Critical List, C-01…C-40** — the
  authoritative work list. Appendix F §1 is the per-claim verification ledger; §2 resolves
  contradictions between auditors; §5 lists coverage gaps no auditor covered.
- Appendices A–E are the raw per-component reports. Read **only your own appendix** — they are large.

| Appendix | Component | Lines |
|---|---|---|
| A | ATAK 5.6 / 5.7 (Java/Android) | 216–826 |
| B | CloudTAK (TypeScript/Vue 3) | 827–1480 |
| C | WinTAK 5.6 / 5.7 (C#/.NET/WPF) | 1481–2026 |
| D | TAK Portal + Infra-TAK (Node/Express/EJS + Python/Flask) | 2027–2481 |
| E | Cross-cutting (docs, git, CI, versioning, licensing) | 2482–2968 |
| F | Verification & adjudication ledger | 2969–3353 |

---

## Ground rules

1. **Verify before you fix.** Every finding cites `file:line`. Open it. Line numbers were taken
   against the working tree at `d5a7ad9`+dirty, which is now `a8f6f4a`, so they should be accurate —
   but §1.1 of the checklist lists three findings that were **struck as false**, and Appendix F §1
   marks 5 more as FALSE POSITIVE and 5 as UNVERIFIABLE. If a finding does not hold at the cited
   line, **do not invent a fix** — record it in your report as `NOT-A-DEFECT` with the evidence.
2. **Appendix severities are as-filed and are superseded by Appendix F.** Rank your work by the
   C-list, not by the appendix tags.
3. **Stay inside your file ownership boundary** (stated in your task). Other agents are editing
   other directories concurrently. Never edit outside your boundary; if a fix requires a change
   elsewhere, write it into your report under `CROSS-PACKAGE REQUESTS`.
4. **ATAK 5.6/5.7 and WinTAK 5.6/5.7 are forks of each other.** Any fix to one tree must be applied
   to the other unless the checklist says the trees legitimately diverge. C-17 documents that
   WinTAK 5.7 is a *regressed* fork — for that pair, 5.6 is the reference.
5. **No secrets, no history rewrite.** C-40 is adjudicated MEDIUM and explicitly requires **no**
   git history rewrite. Do not run `filter-branch`, `filter-repo`, or force-push anything.
6. **Do not push. Do not open PRs.** Commit locally on `audit-remediation` in focused commits.
7. **Do not modify `FEATURELINK-AUDIT-CHECKLIST.md`.** The integrator ticks the boxes at the end.

## Toolchain actually available on this machine

| Tool | Status |
|---|---|
| `node` 24.17.0 / `npm` 11.13.0 | available — CloudTAK and TAK Portal **can** be built/tested for real |
| `python` 3.13.14 | available — Infra-TAK Flask code **can** be linted/tested for real |
| `java`/`javac` 17.0.12 | available, but **`gradle` is MISSING and there is no ATAK SDK** — ATAK cannot be compiled here |
| `dotnet` 10.0.301 | available, but **`msbuild` is MISSING and there is no WinTAK SDK** — the .NET 4.8 WPF plugin cannot be compiled here |
| `git` 2.55.0 | available |

**Consequence:** for ATAK and WinTAK, correctness must be established by careful reading, by
symbol-level cross-checking (does the method exist, is the signature right, are the imports
present), and by writing tests that will run in CI later. **Say so honestly in your report** — do
not claim a build passed when no build was run. For CloudTAK/Portal/Infra-TAK, actually run the
compiler, the linter and the tests, and paste real output.

## Definition of done for a C-item

- The defect no longer reproduces at the cited line (or is proven NOT-A-DEFECT).
- A **regression test** exists that fails against `a8f6f4a` and passes after your change — for the
  platforms where a runner can exist (**C-14** requires standing one up if there is none).
  Where no runner is possible on this machine, land the test anyway so CI runs it, and mark it
  `TEST-NOT-EXECUTED-LOCALLY` in your report.
- The change is committed with a message naming the C-IDs it closes.

## Reporting

When you finish, write `docs/remediation/<your-package-id>.md` containing:

- **Per C-ID table:** `C-ID | status (FIXED / PARTIAL / NOT-A-DEFECT / BLOCKED) | files touched | test | evidence`
- **CROSS-PACKAGE REQUESTS** — changes needed in another agent's territory.
- **OWNER DECISIONS NEEDED** — anything you could not decide. Also append these, one bullet each
  with your package ID, to `QUESTIONS-FOR-OWNER.md` at the repo root. The owner is unavailable
  during this work; **never block on a question** — pick the defensible default, implement it, and
  record both the question and the default you chose.
- **Honest limitations** — what you could not verify and why.

## Cross-cutting conventions agreed for the whole suite

- **Pagination (C-06):** loop on `resultOffset`/`resultRecordCount`, honour `exceededTransferLimit`,
  read the service's `maxRecordCount`, and never report a truncated count as a complete download —
  surface a visible "truncated at N" warning in the UI.
- **ArcGIS error bodies (C-22):** ArcGIS returns `{"error":{…}}` with HTTP 200. Every parse site
  must check transport status **and** `json.error`, and surface a typed error. One central guard
  per codebase; no ad-hoc checks.
- **Tokens (C-21):** `Authorization: Bearer <token>` header, never `?token=` in a URL, and redact
  `token=` from every log line.
- **OAuth `state` (C-09):** CSPRNG nonce, held in memory only, rejected if it does not match a
  pending request. CloudTAK already does this correctly — **use CloudTAK as the reference.**
- **UID case folding (C-39):** `toLowerCase(Locale.ROOT)` in Java, `ToLowerInvariant()` in C#,
  `String.prototype.toLowerCase()` in JS, `str.lower()` in Python — all four must produce
  byte-identical output for the same input, including dotted/dotless I, CJK and emoji.
  Apply the 60-char `cleanSetName` cap **once**, to the base name, **before** the `" Icons"`
  suffix, and never again (the double-truncation bug).
- **Symbology (C-07):** the reference implementation is
  `TAKPortal/services/featurelinkArcgisIconset.service.js:236-244`. Port *from* it.
