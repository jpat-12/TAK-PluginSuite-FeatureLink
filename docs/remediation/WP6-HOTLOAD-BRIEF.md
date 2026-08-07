# WP6 — Server-Side Iconset Hot-Load for CloudTAK Feature Layers

Extracted from `FEATURELINK-AUDIT-CHECKLIST.md` §3 (lines 125–155) and Appendix B "NEW CAPABILITY"
(lines 1437–1477). This is the checklist's one **new capability**, as distinct from the C-01…C-40
defect remediation. It is gated on WP2 (CloudTAK) and WP4 (TAK Portal) landing first.

## Design, as agreed in the checklist

- **TAK Portal is the authoritative generator. CloudTAK's server proxies and caches. The browser
  never generates.** TAK Portal already implements the full spec with a *server-side* fetch
  (`featurelinkArcgisIconset.service.js:236-244`), which side-steps the browser CSP constraint
  entirely, and is the only implementation already returning `valueMap`/`defaultFilename`.
  The CloudTAK plugin must **not** re-implement extraction.
- **Consumption contract.** CloudTAK calls
  `POST /api/featurelink/admin/custom-icons/from-arcgis` (`featurelinkCustomIcons.routes.js:80`)
  with `{ url, field?, token?, renderer? }` and receives
  `{ ok, set, uid, group, field, canonicalUrl, iconCount, rendererType, valueMap, defaultFilename }`
  (`:87-100`). It registers those icons into CloudTAK's own iconset store under **the same UID and
  group**, so `api/web/src/base/cot.ts` resolves `{uid}/{group}/{file}` natively, and synthesizes the
  `DisplayConfig` from `valueMap`/`defaultFilename`.
  `GET /api/featurelink/admin/custom-icons/by-uid/:uid` (`:112`) is the **cache-check / cold-resolve**
  call: ask whether the deterministic UID already exists before generating, and use it to resolve an
  unknown `iconsetpath` UID seen in received CoT (spec §10).
- **Path contract.** `uid = sha256(canonicalUrl + '/' + fieldName)`, lowercase hex.
  `group = sanitizeGroupBase(layerName) + ' Icons'` with **exactly one** 60-char cap applied to the
  base **before** the suffix and **no second cap afterwards**. Every consumer stores and serves the
  **untruncated** group. `filename` per spec §5.2/§5.3, `Other.png` for an unlabeled default.
- **Caching.** Keyed by `uid`. Two tiers: TAK Portal's manifest as the durable store, CloudTAK's
  iconset table as the serving copy. A **negative-result cache** (`uid → 'no-icons'`) is required so
  an unstyled layer is not re-probed on every download.
- **Invalidation.** The UID is stable **by design** across symbol edits, so renderer drift is
  currently invisible. Add `rendererHash = sha256(canonical JSON of the renderer)` stored beside the
  set; compare on each layer download against the `drawingInfo` that the C-07/RC-3 fix now retains;
  on mismatch regenerate by writing new **bytes** under **unchanged** UID/group/filenames, so
  previously-emitted CoT keeps resolving. Add a manual per-layer "Refresh icons" action.
- **Honest limit on "hot".** No restart, no rebuild, no re-login — iconsets are data, not code.
  **But** `props.icon` is baked into each CoT feature at insert time (`cot.ts:66`), so a *path*
  change requires a layer re-sync; a *bytes-only* change does not. Document this explicitly in the
  acceptance criteria.
- **Scope.** Move off `scope: 'USER'` (`autoIconset.ts:183`) to a server-scoped set where the
  deployment permits, so N operators do not each upload the same ~40 icons. Needs a CloudTAK-side
  privileged endpoint; a plugin cannot do this itself (`autoIconset.ts:177`).

## Steps

| Step | Owner | Content |
|---|---|---|
| 0 | **WP2 + WP5 (done)** | Unblock: commit `autoSymbology.ts`, commit the lockfile, `eslint.config.js`, a `@tak-ps/cloudtak` type stub, vitest + CI green from a clean clone. |
| 1 | **WP2 (done)** | Land the §2 field-defect fixes: symbology on the download path, content-based config guard, per-sublayer enumeration. |
| 2 | **WP4 + WP1 + WP2 (done)** | Freeze and conformance-test the path contract; kill the `cleanSetName` double truncation; shared golden-vector fixture executed by the Node, Java, C# and TS suites at 10/54/55/60/70 chars plus CJK, emoji and path separators. |
| 3 | **WP6** | Server-side generation endpoint — expose `from-arcgis`/`by-uid` to CloudTAK with auth, CORS/proxy, rate limit, request-size cap and the SSRF allowlist. **Acceptance: CloudTAK obtains a full icon set with `NGINX_CSP_CONNECT_SRC` unset**, proving the CSP blocker is gone. |
| 4 | **WP6** | CloudTAK consumption + cache. `by-uid` → miss → `from-arcgis` → register under the same UID/group → build `DisplayConfig` from `valueMap`/`defaultFilename`. Keep the client-side path behind a fallback flag. **Acceptance: two users adding the same layer cause one generation.** |
| 5 | **WP6** | Invalidation via `rendererHash`. **Acceptance: editing a symbol in ArcGIS updates the rendered icon with no restart, re-login or re-add.** |
| 6 | **WP6** | Hot-apply to existing features — re-run `syncLayerMarkers`/`syncLayerShapes`. Requires the C-26 re-entrancy fix to be in first. **Acceptance: zero orphaned markers after 10 regeneration cycles.** |
| 7 | **WP6** | Server-scoped sets + `by-uid` cold resolve for CoT carrying an unknown iconset UID. **Acceptance: an operator who never added the layer renders another operator's icons.** 404 must fall back to a default marker, never a blank one. |
| 8 | **WP6** | Observability & UX — per-layer styling status, an Icon Sets panel, explicit failure reasons. **Acceptance: every styling failure has a distinct user-readable cause in the UI; nothing requires the browser console.** |
| 9 | **WP3 / WP6** | Port back to WinTAK; consume the same endpoints; run the Step-2 fixture corpus from C#. |
