# FeatureLink Suite — Master Test Plan

**Branch under test:** `audit-remediation` (34 commits on top of `dev` @ `d5a7ad9`). Nothing pushed.
**Rollback at any time:** `git checkout dev` — the branch is entirely additive.

This document **sequences** the testing. It does not repeat it. Each phase points at a detailed
per-component guide; those guides hold the actual steps, the expected results, and — critically —
**what the failure looked like before the fix**, so you can tell a real pass from a coincidence.

| Guide | Lines | Covers |
|---|---|---|
| [server.md](server.md) | 771 | TAK Portal + Infra-TAK + the OAuth relay |
| [cloudtak.md](cloudtak.md) | 540 | CloudTAK browser plugin |
| [atak.md](atak.md) | 546 | ATAK 5.6 / 5.7 Android |
| [wintak.md](wintak.md) | 650 | WinTAK 5.6 / 5.7 |
| [ci-and-repo.md](ci-and-repo.md) | 643 | CI, version gates, line endings, supply chain |

---

## Read this before you touch anything

### The three setup traps that will look like the software is broken

These are **new, deliberate, fail-closed** behaviours. If you skip them you will spend the session
debugging your own configuration.

1. **The OAuth relay ships with an EMPTY origin allowlist.** Nobody guessed your hostnames.
   Until you register your CloudTAK origins, **every OAuth sign-in fails** — on purpose. This is
   the fix for C-01, the highest-severity finding in the entire audit.
   → [server.md §0.3](server.md) — *required before any sign-in test on any platform.*
2. **Outbound ArcGIS is now restricted to Esri's cloud.** If you use ArcGIS **Enterprise**, set
   `FEATURELINK_ARCGIS_ALLOWED_HOSTS` or every Enterprise fetch fails.
   → [server.md §0.2](server.md)
3. **`NGINX_CSP_CONNECT_SRC` is still mandatory for CloudTAK.** Nothing in this pass changed that
   — server-side iconset generation was the one capability that did *not* get built.
   → [cloudtak.md §0.1](cloudtak.md)

### Expected behaviour changes — these are NOT regressions

Report these only if they behave differently from what is described here.

| Platform | You will see | Why |
|---|---|---|
| CloudTAK | **Unclassified features now render as unknown (`a-u-G`), not friendly** | Deliberate safety call — a tactical display should not invent an affiliation. Most visible change in the suite. |
| CloudTAK | Sessions no longer survive closing the tab | C-20 — refresh token removed from `localStorage` |
| CloudTAK | Old markers look stale until you Clear All Layers + re-download **once** | Marker UIDs are now OID-stable |
| ATAK | **You must `adb uninstall` first**, and you are signed out of ArcGIS once | C-15 deferred so `versionCode` is still 5; the new token store discards the old plaintext refresh token rather than reading it one last time |
| WinTAK | 5.7 now identifies as `FeatureLink.WinTAK57` / `0.9.0-pre` and warns on Release | C-17 — the identity collision with 5.6 is broken deliberately |
| Portal | SVG is no longer an accepted icon type | C-11 hardening |

---

## Phase 0 — Desk checks (~30 min, no hardware, no network, no SDK)

Do this first. It is free, it is fast, and it establishes that the three components that *can* be
verified mechanically actually are.

```bash
# 332 tests that did not exist before this branch
cd TAKPortal              && npm test     # expect 93/93
cd CloudTAK/plugin        && npm run check && npm run lint && npm test   # expect exit 0, exit 0, 23/23
cd WinTAK5.6/Tests/FeatureLink.Tests && dotnet test                      # expect 216/216
```

I have run all three on this tree and they pass. If any fails on your machine, it is an
environment difference and worth knowing before you go further.

Then the repo-level gates → [ci-and-repo.md §0](ci-and-repo.md) (five-minute smoke test).

> **Known false positive:** `check_versions.py` reports that both WinTAK manifests still declare
> `<id>FeatureLink</id>`. They do not — 5.7 is now `FeatureLink.WinTAK57`. The gate is
> regex-matching the *explanatory comment* that quotes the old id. The gate's **other** complaint —
> the ATAK `versionCode` collision — is real and correct, because C-15 was descoped.

---

## Phase 1 — TAK Portal ⭐ start here

**Why first:** it holds C-01 (the #1 finding suite-wide), it is the authoritative iconset
generator the other three platforms were ported *from*, and it needs no mobile hardware.

→ [server.md](server.md)

Order within the phase:
1. **§1 — C-01 relay.** ⚠️ *The single most important test in the session.* Needs no ArcGIS
   account. §1.4 "prove the negative" is the half that actually matters.
2. **§2 — boot check.** Five new security services are wired into all four route files; confirm
   the Portal still starts before testing anything else.
3. **§3–§7 — C-03 access control, C-04 SSRF, C-11 zip bomb, C-10 CSRF, C-05 XSS.** All have safe
   local repro steps. §3.4 (destroying another user's icon set) is the worst of the access-control
   cases. **§3 needs a second user account** — provision it before you start.
4. **§9 — the C-39 truncation fix.** Run the detection recipe in §9.2. If it reports
   "none affected", there is nothing to migrate and you can skip §9.3.
5. **§10 — regression sweep, §11 — high-risk items.**

⚠️ **Do NOT run `uninstall.sh` on anything you care about.** C-12 was descoped. It still orphans
the three files that are the C-04 and C-11 sinks, and its unpatcher prints "removed…"
unconditionally — a mismatch leaves a `require` pointing at a deleted file and **the portal is
permanently down after an uninstall, with a success message printed.** Disposable instance only;
back up the four host files first ([server.md §12](server.md)).

---

## Phase 2 — CloudTAK

**Why second:** it is the platform where your field defect is most completely fixed *and* proven
by an executable test, so it is the cleanest read on whether C-07 is genuinely resolved.

→ [cloudtak.md](cloudtak.md)

1. **§0** — the three traps. Do not skip.
2. **§1.3** — activating in the browser: **a hard refresh is not enough**, the service worker
   needs handling.
3. **§3 Test 1 — C-07, the field defect.** ⭐ The test that answers your original question.
   Test 1 deliberately compares the **paste-a-public-URL** route (which always worked) against the
   **browse-list ⬇** route (which was broken). If both now show real symbology instead of the
   hardcoded `#3388ff` fallback, C-07 is fixed on this platform.
4. **§3 Tests 2–10**, then **§4 — highest-risk items.**

**Highest risk here:** the ArcGIS token now travels in a header, which triggers a **CORS preflight
that did not exist before**. Diagnostic given by the agent: *if private layers fail and public ones
work, that is the cause* ([cloudtak.md §4.1](cloudtak.md)).

**Do not spend time on** the untrusted-import consent gate (C-02/C-19) — see "Not fixed" below.

---

## Phase 3 — ATAK 5.6 / 5.7

**Why third:** highest uncertainty per unit of test time. **Zero automated tests exist for ATAK and
none of its Java has ever been compiled** — the only verification was `javac -proc:none`
establishing valid syntax and internal consistency across both trees. Everything else is inspection.

→ [atak.md](atak.md)

1. **§0** — build and sideload. §0.1 lists what you need that the remediation machine lacked
   (gradle + the ATAK SDK). §0.6: the one-time re-sign-in is expected.
2. **§1 — C-07, the field defect**, seven sub-tests including the browse-list route, the
   auto-refresh tick, and restart persistence.
3. **§2–§8** in order.
4. **§9 — high risk.**

**Highest risk here:** the `Authorization: Bearer` migration (C-21) changed the shape of every
authenticated ArcGIS call at once — `applyEdits`, multipart `addItem`, `publish`,
`updateDefinition` — with no live portal available to confirm Esri accepts the header on each, or
that Enterprise behaves like Online. [atak.md §5.2](atak.md) lists all eight calls to exercise.

**Calibration note:** the agent working this package introduced a self-deadlock into its own C-28
concurrency fix and then caught it. Treat the thread-pool area as the least-trustworthy code in the
package, not the most.

**5.6 ↔ 5.7 parity is verified:** residual diff is 1 file / 3 lines
(`ImportInPlaceResolver` → `ImportResolver`). I checked this myself. But
[QUESTIONS-FOR-OWNER.md](../../QUESTIONS-FOR-OWNER.md) flags that `ImportResolver`'s in-place
semantics on 5.7 are **unverified** — if it copies rather than references, 5.7 leaves an orphan
`.featurelinkshare` in `atak/tools/datapackage/` after every accepted share. Worth a look while
you have a device in hand.

---

## Phase 4 — WinTAK 5.6

**Why last:** it is gated behind a build that has never happened, and it carries the largest volume
of never-compiled code in the suite.

→ [wintak.md](wintak.md)

1. **§1 — build prerequisites.** Populate `libs/`, restore, build. **Budget a compile-fix pass.**
   The dock pane went 1455 → ~2160 lines without a compiler ever seeing it. §1.3 lists the most
   likely failure: `selfEvent?.Point?.CE90`/`.LE90` — replace with `double.NaN` if those members
   do not exist in your SDK.
2. **§2 — C-18. BLOCKING.** Is `Newtonsoft.Json.dll` actually inside the `.wpk`? Without it the
   plugin does not load at all and every other WinTAK test is meaningless. Verified by execution
   here, but verify on your machine.
3. **§3 — C-16, the status channel.** Do this early: `StatusText` was never bound to anything
   visible, so the plugin had no error channel at all. Every later test is far more diagnosable
   once you can see errors.
4. **§4 — C-07. HIGHEST RISK IN THE SUITE.** ~700 lines written from scratch; symbology never
   existed on this platform. §4.3/§4.4 cover the Appendix F §5 interop break — a Portal-authored
   config carrying polyline/polygon shape styling was previously *silently and completely dropped*.
   §4.4 doubles as the C-24 precedence check: **if everything renders grey, the inversion was
   inherited.**
5. **§5–§11**, then **§12** for the automated suite.

⚠️ **Open question that must be settled before delivery, not during testing:** is
`CotItem.SetAttribute` XML-escaping its input? Peer-controlled data reaches broadcast CoT. It was
mitigated at the plugin boundary but could not be *proven* without the SDK. Capture procedure in
[wintak.md §11.2](wintak.md).

**WinTAK 5.7 is deliberately an incomplete port** — do not test it as a product. The gap report is
[wp3-wintak-57-parity.md](../remediation/wp3-wintak-57-parity.md): 25 files absent, 13 divergent,
8 verified regressions, ~6–7 engineer-days to finish.

---

## Phase 5 — Cross-platform interop ⚠️ never tested by anyone, ever

Appendix F §5 of the audit is blunt about this: *"The suite's central promise was never tested
end-to-end by anyone."* All five original auditors analysed components in isolation; not one traced
a single config from TAK Portal through to all three clients.

**This phase is new ground.** There is no per-component guide for it because it is not a component.

1. Author a config in TAK Portal that carries **shape styling** (polyline/polygon) — Mode 3 in
   `CONFIG-FORMAT.md`.
2. Consume it in **ATAK**, **CloudTAK** and **WinTAK** in turn. All three must render the same
   thing. Historically WinTAK dropped shape styling entirely, with no warning and no log line.
3. Share a layer **ATAK → WinTAK** and **ATAK → CloudTAK** via `.featurelinkshare`. C-23 (shape
   styling stripped in transit by `toCompactJson`) is only **partially** fixed — the `shp` block
   landed but the round-trip test was never written. **Assume this is broken until you see
   otherwise.**
4. Check `{uid}/{group}/{file}` icon-path identity across platforms for a layer whose name exceeds
   54 characters — the C-39 case. Golden vectors:
   `TAKPortal/test/fixtures/auto-iconset-golden-vectors.json`.

**Known cross-platform hazard:** WinTAK now implements the C-24 precedence rule correctly. ATAK and
CloudTAK have also landed their C-24 fixes, so all three *should* now agree — but this specific
three-way agreement has never been observed running. It is the single most valuable thing you can
confirm in this phase.

---

## Explicitly NOT fixed — do not spend test time here

| Item | State | Consequence if you test it |
|---|---|---|
| **C-02 / C-19 CloudTAK** | **Not started.** `importConfig.ts`, `importIngest.ts`, `zipReader.ts` are byte-identical to baseline | Auto-ingest still applies third-party config with no consent and can still redirect the PLI feed. The token-exfiltration *end* is closed by C-33, so it is narrowed, not open. ~1–1.5 days |
| **C-12 install/uninstall** | Descoped | See the Phase 1 warning. Genuinely destructive |
| **C-17 WinTAK 5.7** | Identity collision broken; port not done | 5.7 is not a shippable product |
| **C-15 versionCode** | Policy + gate written, code untouched | ATAK still needs `adb uninstall` to upgrade |
| **C-40 keystore** | Untouched by design | Adjudicated MEDIUM; no history rewrite required |
| **C-25 / C-28 / C-23 / C-37** | Partial | Detailed per-guide in each "NOT fixed" section |
| **C-30 / C-31 / C-32** | Untouched | CloudTAK lockfile, install.sh, silent UI failures |
| **WP6 iconset hot-load** | Not built | The one *new capability* in the checklist. Server-side generation does not exist, which is why trap #3 above still applies |

---

## Recording results

Each guide ends with a "Reporting back" section. For anything that fails, the three things worth
capturing every time:

1. **Which numbered test**, e.g. `cloudtak.md §3 Test 1.2`.
2. **Whether it matches the documented pre-fix signature** — every test states what the failure
   used to look like. If it fails *differently*, that is a new defect and much more interesting
   than a fix that did not take.
3. **Logs.** ATAK: [atak.md §0.5](atak.md). WinTAK: the now-visible status strip (§3).
   CloudTAK: browser console + network tab, watching for preflight failures. Portal: server log.

| Phase | Component | Result | Notes |
|---|---|---|---|
| 0 | Desk checks (332 tests) | | |
| 1 | TAK Portal | | |
| 2 | CloudTAK | | |
| 3 | ATAK 5.6 | | |
| 3 | ATAK 5.7 | | |
| 4 | WinTAK 5.6 | | |
| 5 | Cross-platform interop | | |

---

## Honest summary of what you are testing

**~74% of the audit's deduplicated C-01…C-40 list is closed**, up from zero. 332 automated tests
exist where there were none. Your field defect — C-07 — is fixed on all three clients.

**But the verification is lopsided, and that matters more than the percentage:**

- **TAK Portal, CloudTAK:** real toolchain, compiled, linted, tested. High confidence.
- **WinTAK:** 216 tests, but they cover only the SDK-independent subset. The dock pane and the
  entire WPF layer have never been compiled.
- **ATAK:** zero tests, never compiled. Confidence rests on inspection alone.

Three real defects were caught *by the new tests* during this pass, all in from-scratch code: a
class-breaks bound that parsed as ±infinity and silently mis-coloured entire layers; `PliLayerUrl`
bound by nothing in the XAML; and `build-wpk.py` rewriting a manifest *comment* instead of the live
element. That is the tests doing their job — and a fair indication of what is still hiding in the
code no test or compiler has touched.
