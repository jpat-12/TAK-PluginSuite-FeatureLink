# Security Policy

FeatureLink is public-safety software. It handles responder position data and authenticates
against organisational ArcGIS accounts. Security reports are treated accordingly.

This document closes the "no `SECURITY.md`" gap (checklist Appendix E §7) and records the
release-signing posture required by **C-40**.

---

## Reporting a vulnerability

**Do not open a public issue, pull request or discussion for a security problem.**

Report privately, by either route:

1. **GitHub private vulnerability reporting** — *Security* tab → *Report a vulnerability*.
   Preferred: it keeps the report, the fix and the advisory in one place.
2. **Email** — `security@` the project maintainer's domain. If you do not know it, open a
   public issue containing **only** the words "requesting security contact" and no detail,
   and a maintainer will provide an address.

> **PLACEHOLDER:** a monitored security address has not yet been registered for this project.
> Until it is, route 1 is the only reliable channel. Registering the address is an outstanding
> owner action recorded in `QUESTIONS-FOR-OWNER.md`.

### What to include

- Which component and version (`VERSION` at the repo root, or the artifact filename).
- What an attacker achieves, and what access they need to start.
- Reproduction steps, proof-of-concept, or the file and line.
- Whether you believe it is being exploited.

**Do not include live responder data, a live ArcGIS service URL, or a real access token in
a report.** Redact them. If a PLI layer is exposed, say so without publishing the URL.

### Response commitment

| Stage | Target |
|---|---|
| Acknowledgement of receipt | **3 business days** |
| Initial triage and severity assessment | **10 business days** |
| Status update cadence thereafter | **every 14 days** until resolved |
| Fix for CRITICAL / HIGH | **30 days** from triage |
| Fix for MEDIUM / LOW | next scheduled release |
| Public advisory | within **7 days** of a fix being released |

This project is maintained by a small volunteer team. These are honest targets, not a
contractual SLA. If a deadline will slip, you will be told why rather than left waiting.

### Disclosure

Coordinated disclosure, **90 days** from acknowledgement by default. We will work with you on
a shorter or longer window where the facts warrant it, and we will credit you in the advisory
unless you ask us not to.

If a vulnerability is being actively exploited against responders in the field, tell us — we
will prioritise an emergency release over the disclosure timetable.

### Safe harbour

We will not pursue or support legal action against good-faith research that: stays within
your own deployment or a test environment; does not access, modify or exfiltrate another
organisation's data; does not degrade a live incident response; and reports privately and
gives us reasonable time to fix.

**Never test against a live SAR or CAP incident.** Stand up your own ArcGIS layer and TAK
server.

---

## Supported versions

The suite ships one version across all seven components (see `VERSIONING.md`).

| Suite version | Status | Security fixes |
|---|---|---|
| `2.7.x` | **Current** | Yes |
| `2.6.x` | End of life | **No** — superseded; upgrade |
| `< 2.6` | End of life | No |

**Only the current MINOR line receives security fixes.** There is no long-term-support
branch. Backporting to an older line will be considered only for a CRITICAL affecting a
deployment that demonstrably cannot upgrade.

### Host-platform support

| FeatureLink | Host |
|---|---|
| ATAK build | ATAK-CIV 5.6.x, and ATAK-CIV 5.7.x (separate artifact) |
| WinTAK build | WinTAK 5.6.0.151, and WinTAK 5.7.0.144 (separate artifact) |
| CloudTAK plugin | CloudTAK 13.x |

The two ATAK artifacts and the two WinTAK artifacts are **distinct products with distinct
package identities** — see `VERSIONING.md` §2. Installing the wrong one is not a supported
configuration.

> **Known gap.** Every release prior to this remediation was built on a developer
> workstation, because the four CI workflows that existed were in `<component>/workflows/`
> where GitHub never looks and had therefore never executed. Artifacts `2.6.16`–`2.6.24`
> have **no build provenance**. Treat them as unverified.

---

## Release signing and artifact verification

### Current state — read this before trusting a released artifact

All ATAK artifacts released to date are signed with the **stock ATAK CIV SDK development
keystore** (`wintec_mapping`, `O=WinTec Arrowmaker`, created 2011, **1024-bit RSA /
SHA-1**), which is shipped inside the publicly downloadable ATAK SDK and is byte-identical
to the SDK's own `android_keystore`.

Two consequences, stated plainly:

- **The signature attests nothing.** Anyone who downloads the free ATAK SDK holds the same
  key. A released FeatureLink APK is **unattributable and trivially forgeable**. This is not
  a leak — the key was never secret — but it means the signature must not be relied on as
  evidence of origin.
- **The algorithms are deprecated.** 1024-bit RSA and SHA-1 are being retired by both Android
  and the TAK Product Center.

### Required remediation (C-40)

1. Generate a **project-specific 4096-bit RSA / SHA-256** release keystore.
2. Store it **only** in CI secrets or a KMS — never in the repository.
3. Source signing passwords from the environment and **fail the build when absent**, rather
   than falling back to a literal.
4. Remove the `!…featurelink.keystore` negations from the `.gitignore` files and
   `git rm --cached` both blobs. Source the debug key from `$ATAK_SDK_PATH/android_keystore`.
5. **Do not rewrite git history.** The key is public; a history rewrite imposes real
   disruption for no security benefit. Rotate forward instead.

### Verifying a release

From `2.7.0` onward, `.github/workflows/release.yml` publishes a `SHA256SUMS` file, a
CycloneDX SBOM, and a GitHub build-provenance attestation with every artifact:

```bash
sha256sum -c SHA256SUMS
gh attestation verify <artifact> --repo <owner>/TAK-PluginSuite-FeatureLink
```

An artifact without an attestation did not come from this pipeline.

---

## Security controls in the build

| Control | Where | What it does |
|---|---|---|
| Secret scanning | `.github/workflows/security.yml` | gitleaks over full history, with suite-specific rules for keystore passwords and tokens-in-URLs |
| Static analysis | same | CodeQL across JavaScript/TypeScript, Python, Java and C#; `bandit` for Infra-TAK |
| Dependency scanning | same + `.github/dependabot.yml` | `npm audit`, dependency review, weekly Dependabot across five ecosystems |
| Licence gate | same | Blocks GPL/AGPL/LGPL-3.0/SSPL dependencies into an Apache-2.0 suite |
| Binary integrity | `.github/pinned-binaries.sha256` | Every tracked `.jar`/`.aar`/`.dll`/`.exe`/`.so` is pinned by SHA-256 and verified before the build runs. Fails on any **new** unpinned executable blob |
| SDK integrity | `.github/workflows/ci.yml` | The ATAK `main.jar` and WinTAK SDK archive are SHA-256-verified after download. Previously fetched with no checksum at all |
| Least privilege | all workflows | Every job declares explicit `permissions:`; all third-party actions are pinned by 40-character commit SHA |

### Recommended repository settings

Enable **secret scanning with push protection** — it is what would have stopped the keystore
reaching the repository originally — plus Dependabot alerts and private vulnerability
reporting. See `CONTRIBUTING.md` § "Branch protection" for the full list.

---

## Scope

**In scope:** all seven components in this repository; the shared OAuth relay page
(`docs/featurelink-oauth-relay.html`); the install/uninstall scripts; the CI/CD pipeline and
its supply chain.

**Out of scope, report upstream:** ATAK, WinTAK, CloudTAK and the TAK Server itself; Esri
ArcGIS Online / Enterprise; a misconfigured ArcGIS layer in *your* organisation (that is a
configuration issue — see `PRIVACY.md` §3.1, and fix the sharing level).

---

*See `PRIVACY.md` for what data the suite handles and where it goes, and `VERSIONING.md` for
the release and version scheme.*
