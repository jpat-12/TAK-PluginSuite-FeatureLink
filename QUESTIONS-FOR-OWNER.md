# Questions for the Owner — FeatureLink Audit Remediation

The owner was unavailable during this remediation pass. **Nothing here blocked work.** Every item
below was implemented using the defensible default stated in the "Default taken" column. Each is
reversible; review at your convenience and tell us where you want a different call.

Branch: `audit-remediation`. Baseline: `a8f6f4a`.

---

## Decisions taken up front by the integrator

| # | Question | Default taken | How to reverse |
|---|---|---|---|
| I-1 | Should remediation land on `dev` directly? | No — a new branch `audit-remediation` was cut from `dev` @ `d5a7ad9` and nothing was pushed. | `git merge audit-remediation` from `dev` when you're satisfied. |
| I-2 | C-40 keystore: rotate the signing key now? | No key was rotated and **no git history was rewritten** (Appendix F §2.1 adjudicates this MEDIUM and explicitly says no rewrite is required). The build config was changed to read the release password from an environment variable that **fails closed**, so a real key can be substituted without editing tracked source. | Generate a 4096-bit/SHA-256 project key and supply it via CI secrets. |
| I-3 | `README.md.bak` / `README2.md.bak` (backup-file rot, Appendix E §3.6) | Deleted. Both are recoverable from git history. | `git checkout d5a7ad9 -- README.md.bak` |
| I-4 | Infra-TAK is labelled "Deprecated & not supported" yet ships live XSS and a root-privileged installer (Appendix F §5). Archive it out of the repo, or patch it to the same standard? | **Patched, not archived** — deleting a component is a product decision, not an audit fix. The security defects (C-05, C-12, C-38) were fixed in place. The 7,450 duplicate icons were left alone pending your call on I-5. | Deleting `Infra-TAK/` remains a one-commit operation. |

---

## Raised by the work packages

<!-- Work-package agents append here, one bullet each, prefixed with their package ID. -->
