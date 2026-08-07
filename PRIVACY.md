# FeatureLink — Privacy and Data-Handling Statement

**Applies to:** the FeatureLink plugin suite — ATAK 5.6/5.7, WinTAK 5.6/5.7, CloudTAK,
TAK Portal, Infra-TAK.
**Status of this document:** it describes the software **as it actually behaves today**,
including behaviour that is a problem. §6 records a retention gap that is unresolved in
code. Nothing here is aspirational.

> **Read §2 and §6 before you enable PLI auto-send.** This software transmits a responder's
> real-time position to a third-party commercial cloud service, and there is currently no
> way to delete it from within any FeatureLink client.

Audit provenance: this document closes **C-35** (adjudicated CRITICAL, governance) and
records the PLI-retention coverage gap from checklist Appendix F §5.

---

## 1. Who this affects

FeatureLink is used by Civil Air Patrol and other public-safety and SAR organisations. The
people whose data it handles are **responders in the field** — often volunteers, frequently
minors in CAP cadet programmes, and in some incidents people whose whereabouts are
operationally sensitive.

The suite does not collect data about members of the public. It collects data about the
people operating it.

---

## 2. What leaves the device

### 2.1 Position Location Information (PLI) auto-send

When an operator configures a **PLI Feature Layer** and enables auto-send, the client
transmits the operator's position to the configured ArcGIS Feature Service **every 30
seconds**, for as long as the plugin is running.

The interval is fixed in code and is not user-adjustable:
`FeatureLinkDropDownReceiver.startPliScheduler()` schedules `sendPliUpdate` at
`scheduleAtFixedRate(..., 0, 30, TimeUnit.SECONDS)`.

Each transmission writes an ArcGIS point feature whose geometry is the operator's
latitude/longitude in WGS-84 (`wkid: 4326`), plus the following **24 attributes**. This list
is taken from the code, not from documentation — ATAK
`ArcGISRestClient.buildPliAttributes()`, WinTAK `ArcGisFeatureService`, and CloudTAK
`arcgisRest.ts` all build the **identical** schema:

| Field | Type | What it contains | Sensitivity |
|---|---|---|---|
| `uid` | string | The device's TAK unique identifier | **Persistent device identifier** — links every position report from this device together, across incidents |
| `source_system` | string | `ATAK`, `WinTAK` or `CloudTAK` | Reveals the operator's platform |
| `source_layer` | string | Origin layer, when the feature was relayed | Low |
| `source_objectid` | string | Origin object id, when relayed | Low |
| `cot_type` | string | Cursor-on-Target type (e.g. `a-f-G-U-C`) | Reveals affiliation and unit role |
| `tak_callsign` | string | The device callsign | **Directly identifies the operator** in most CAP/SAR deployments |
| `tak_icon` | string | Iconset path for the marker | Low |
| `tak_remarks` | string | The operator's free-text remarks on their own marker | **Unbounded free text — may contain anything the operator typed** |
| `group_name` | string | TAK team colour / group | Reveals team structure |
| `group_role` | string | TAK role (Team Lead, Medic, RTO, …) | Reveals individual function |
| `latitude` | double | WGS-84 latitude | **Precise location** |
| `longitude` | double | WGS-84 longitude | **Precise location** |
| `hae` | double | Height above ellipsoid | Location |
| `ce` | double | Circular error (horizontal accuracy, metres) | Reveals GPS quality |
| `le` | double | Linear error (vertical accuracy, metres) | Reveals GPS quality |
| `time` | epoch ms | Position timestamp | **Location + time = movement pattern** |
| `start_time` | epoch ms | CoT validity start | Timing |
| `stale_time` | epoch ms | CoT staleness (send time + 30 s) | Timing |
| `sent_toFL_time` | epoch ms | When FeatureLink transmitted it | Timing |
| `sent_by_user` | string | **The ArcGIS account username** that authenticated | **Directly identifies the account holder** |
| `how` | string | CoT provenance (`m-g` = machine/GPS) | Low |
| `sync_status` | string | Always `synced` | None |
| `last_synced` | epoch ms | Sync timestamp | Timing |
| `raw_cot_xml` | string | The raw CoT XML, when supplied | **May carry any CoT detail the caller passed** |

Taken together this is a **continuously updating, individually attributable record of a named
responder's location, team, role and free-text notes, at 30-second granularity.**

### 2.2 Send-item-to-layer and Mission Package share

Separately from auto-send, an operator can push a **selected map item** to a feature layer,
or share a layer configuration. These use the same 24-field schema and transmit whatever the
selected item contains — which may be another person's marker, not the operator's own.

### 2.3 What is *not* transmitted

- No contacts list, no message history, no chat.
- No device telemetry, analytics, crash reporting or usage metrics — the suite contains no
  analytics SDK of any kind.
- Nothing is sent to the FeatureLink authors or to any FeatureLink-operated server. There is
  no FeatureLink backend. All traffic goes to the ArcGIS endpoint **the operator configured**
  and to the TAK server the client is already connected to.

---

## 3. Where it goes

**Destination: an Esri ArcGIS hosted feature service — a third-party commercial cloud.**

Which one depends on configuration:

| Deployment | Destination | Who can read it |
|---|---|---|
| **ArcGIS Online** (`*.arcgis.com`) | Esri's multi-tenant public cloud, hosted on infrastructure Esri controls | Esri, your ArcGIS organisation administrators, and anyone the layer is shared with |
| **ArcGIS Enterprise** (self-hosted portal) | Infrastructure **you** control | Your organisation only |
| **ArcGIS Location Platform** | Esri's cloud | As above |

If your organisation's threat model does not permit responder location data on
Esri-controlled infrastructure, **use ArcGIS Enterprise, or do not enable PLI auto-send.**

Data handling once it reaches ArcGIS is governed by **Esri's** terms, privacy policy and
data-processing agreements, not by this project. FeatureLink is a client. Review Esri's
terms — including data residency, sub-processors and government-access provisions — before
enabling PLI for an operational deployment.

### 3.1 The sharing-level hazard

> ### ⚠️ NEVER SET A PLI LAYER TO "PUBLIC" (SHARED WITH EVERYONE)
>
> ArcGIS layer sharing is set **in ArcGIS, not in FeatureLink**. FeatureLink cannot detect,
> warn about, or override it.
>
> A PLI feature layer shared publicly publishes **the live positions, callsigns, teams,
> roles and ArcGIS usernames of every responder feeding it** to the open internet, queryable
> by anyone who finds the service URL, with no authentication. ArcGIS service URLs are
> discoverable — they are indexed, they appear in referrer logs, and they are guessable
> within an organisation.
>
> For a SAR incident this is a direct personal-safety exposure for responders, and for CAP
> operations involving cadets it is a child-safety exposure.

**Required configuration for any PLI layer:**

- Sharing level: **Owner** or **Organisation**. Never *Everyone (public)*.
- Never enable *Allow anonymous access* / *Public data collection* on a PLI layer.
- Do not place a PLI layer in a public Web Map, public Dashboard, public Experience Builder
  app, or any public group. Sharing a *map* that contains the layer can expose the layer.
- Audit the layer's sharing after every ArcGIS platform upgrade and after any group
  membership change.
- Treat the service URL itself as sensitive; do not paste it into public issue trackers,
  screenshots or QR codes that leave the organisation.

**Verify before every operational use:**
`https://<your-service-url>/0?f=json` opened in a logged-out private browser window must
return an authentication error. If it returns layer metadata, the layer is public — stop and
fix the sharing level before any responder enables auto-send.

---

## 4. What is stored on the device

| Platform | What is stored | Where | Protection |
|---|---|---|---|
| **ATAK 5.6 / 5.7** | OAuth access + refresh token, ArcGIS username, portal URL, PLI layer URL, saved layers, display configs | Android `SharedPreferences`, app-private storage | App-private (OS-enforced, other apps cannot read it). **Not additionally encrypted at rest** — `EncryptedSharedPreferences` is not used, so the values are readable on a rooted device or from an unencrypted backup |
| **WinTAK 5.6 / 5.7** | Same | Windows user profile, via `SettingsStore` | **DPAPI**, `DataProtectionScope.CurrentUser` with additional entropy — decryptable only by the same Windows user on the same machine. This is the strongest of the three |
| **CloudTAK** | Portal URL in `localStorage`; refresh token and username in **`sessionStorage`** | Browser storage for the CloudTAK origin | Plaintext, and readable by any script running on that origin. `sessionStorage` is per-tab and cleared when the tab closes, which bounds exposure — but **this remains the weakest of the three**, and any cross-site-scripting flaw in the CloudTAK origin reads it |

**On-device PLI breadcrumb history** is capped at **5 points** (`MAX_HISTORY = 5` in both
`PliHistoryOverlay.java` and `cot.ts`), is held in memory / on the map only, and is discarded
when the plugin restarts. It is never uploaded as history.

**This on-device cap does not limit what has already been sent to the cloud.** See §6.

---

## 5. Retention — what actually happens

**On the device:** the 5-point breadcrumb, cleared on restart. Saved layers, display configs
and settings persist until the plugin is uninstalled or its data cleared.

**In the ArcGIS hosted feature layer** — this is the part that matters:

Each client remembers the `objectId` of the PLI feature it created and thereafter **updates
that same row in place** rather than appending a new one
(`ArcGISRestClient.addPliFeature` → store `objectId` → `updatePliFeature`). So under normal
operation the layer holds **one current row per device**, not a growing track.

That is the design intent. Three things break it:

1. **ArcGIS editor tracking / archiving.** If the hosted feature layer has *Keep track of
   edits* or archiving enabled — a per-layer ArcGIS setting FeatureLink neither sets nor
   reads — then **every 30-second update is retained server-side as a historical edit**. The
   result is a complete, timestamped movement track of every responder, retained under
   whatever policy the ArcGIS organisation has (often: forever). FeatureLink cannot detect
   this and gives no warning.
2. **Orphaned rows on layer change.** `setPliLayerUrl()` clears the remembered `objectId`
   whenever the operator points PLI at a different layer. The row previously written to the
   old layer is **never deleted** — it is simply abandoned, frozen at the operator's last
   reported position, and remains there indefinitely.
3. **Orphaned rows on failure.** If an update fails, the client clears the remembered
   `objectId` and adds a **fresh** row on the next tick. The previous row is left behind.

---

## 6. ⚠️ Known gap: there is no way to delete PLI data from any FeatureLink client

**This is an unresolved defect, stated plainly because a privacy notice that omitted it
would be false.**

Verified by inspection of every client
(`grep -rniE "deletes=|deleteFeatures|purge|retention|ttl"` across the ATAK, WinTAK and
CloudTAK sources returns **no PLI deletion path**):

- There is **no purge command**, in any client, at any privilege level.
- There is **no TTL or expiry** applied to written features. The `stale_time` attribute is a
  Cursor-on-Target *display* hint consumed by TAK clients — it does **not** cause ArcGIS to
  delete or expire anything.
- There is **no delete path** — no "stop sharing and remove my position", no end-of-incident
  cleanup, no "forget me".
- There is **no operator-visible control** over what has already been transmitted, and no way
  to see it from within the plugin.

**Consequence.** Once a responder's position has been written to a hosted feature layer, the
only way to remove it is for an **ArcGIS administrator** to delete the feature (or the layer)
using ArcGIS tooling outside FeatureLink. A responder cannot remove their own data. A team
lead cannot clear an incident. If editor tracking is on, the accumulated history may not be
removable at all without deleting the layer.

For CAP and SAR this is a **records-retention and personal-safety exposure**, not merely a
missing document.

**Interim mitigations, until a purge capability ships:**

1. Use a **dedicated PLI layer per incident**, and delete the layer when the incident closes.
   Deleting the layer is currently the only reliable erasure mechanism.
2. **Do not enable editor tracking or archiving** on a PLI layer unless your organisation has
   consciously decided to retain responder movement history and has a written retention
   schedule for it.
3. Assign an owner responsible for post-incident deletion, with a deadline, in the incident
   action plan.
4. Disable PLI auto-send when it is not operationally needed. It is off by default and
   requires deliberate configuration.

**Planned remediation** (specified, not implemented — see
`docs/remediation/wp5-crosscutting.md`): a `purgePliFeatures()` operation in the shared REST
client on all three platforms, exposed as an explicit operator action, plus an
end-of-session prompt and a documented ArcGIS-side retention configuration.

---

## 7. Consent and operator control

- **PLI auto-send is off by default** and requires the operator to configure a layer URL and
  enable it. It never starts on its own.
- Sign-in uses **OAuth 2.0 with PKCE**. FeatureLink never sees or stores the operator's
  ArcGIS password.
- An operator can stop transmission at any time by disabling auto-send or signing out.
  **Stopping transmission does not remove data already sent** (§6).
- Configuration can arrive from a QR code, a deep link or a shared config file. Any config
  that sets a PLI endpoint changes **where a responder's position is sent** — treat an
  unexpected FeatureLink config exactly as you would treat an unexpected link, and never
  accept one from an untrusted source.

---

## 8. Guidance for deploying organisations

Before an operational deployment:

1. Decide **ArcGIS Online vs ArcGIS Enterprise** on the basis of whether responder location
   may reside on Esri-controlled infrastructure. Record the decision.
2. Set and verify the PLI layer sharing level (§3.1). Verify with the logged-out check.
3. Decide and record a **retention period**, and name the person responsible for deletion —
   noting §6, that deletion is currently an ArcGIS-side administrator task.
4. Decide whether editor tracking / archiving is enabled, and record why.
5. Tell responders, before they enable it, what is transmitted and to whom. §2.1 is written
   to be usable directly in a briefing.
6. For CAP: confirm this is consistent with CAPR 110-1 and your wing's information-handling
   policy, and with cadet-protection requirements where minors are involved.

---

## 9. Contact

Privacy questions and data-handling concerns: see the contact in `SECURITY.md`.

To report a **privacy incident** — a PLI layer discovered public, responder data exposed —
follow the vulnerability-disclosure process in `SECURITY.md`. Do not open a public issue
containing a live service URL or responder data.

---

*Last reviewed: 2026-08-03. This document is maintained alongside the code; a change that
alters what data leaves the device, or where it goes, must update this file in the same pull
request — enforced by the checklist in `.github/pull_request_template.md`.*
