/**
 * ArcGIS renderer → ATAK iconset generator (TAK Portal side, Phase D of
 * WebMapFeatureLayer-AutoConfigurator.md). Implements AUTO-ICONSET-SPEC.md.
 *
 * Given a FeatureServer/{layerId} link, this reads the layer's renderer, pulls every
 * picture-marker (esriPMS) symbol's embedded base64 imageData, and registers them as a
 * custom icon set whose UID/group/filenames are computed *deterministically from the spec*
 * — so ATAK, WinTAK and CloudTAK, each running the same spec, produce string-identical
 * `{uid}/{group}/{filename}` references for the same source layer without any of them talking
 * to each other (see spec §0 for why this works: ATAK reads the UID from iconset.xml verbatim,
 * and group/filename from the zip entry paths, so matching PNG *bytes* is unnecessary).
 *
 * This is NOT the zip-upload path: we already have the decoded PNGs and the spec-computed
 * names, so we register them straight into the custom-icons manifest via registerArcgisSet()
 * rather than building a zip only to unpack it again. The one thing that MUST match every
 * other platform is the strings, and those come from this file's canonicalize()/uid()/naming
 * logic — kept byte-for-byte aligned with the Java/C#/TS ports.
 *
 * Requires Node 18+ (global fetch). The host TAK Portal supplies fs/path/crypto; no image or
 * zip dependency is needed because we neither resize (cosmetic only — deferred, spec §0) nor
 * repackage.
 */

const crypto = require("crypto");
const { registerArcgisSet } = require("./featurelinkCustomIcons.service");

const SPEC_VERSION = 1;

// Mirrors AUTO-ICONSET-SPEC.md §5 sanitization (and NAME_RE/FILE_RE in the custom-icons service).
const GROUP_BAD_RE = /[^A-Za-z0-9 _-]/g;
const FILE_BAD_RE = /[^A-Za-z0-9._-]/g;

// -------------------------------------------------------------------------
// §2 — URL canonicalization
// -------------------------------------------------------------------------

/** True for a Web Map item link (portal item), which §2.3 resolves separately. */
function isWebMapLink(url) {
  return /\/home\/item\.html/i.test(url) || /\/sharing\/rest\/content\/items\//i.test(url);
}

/**
 * §2.1–§2.2: normalize to `{scheme}://{host}{/path}/FeatureServer|MapServer/{layerId}`.
 * scheme+host lowercased, path case preserved, query/fragment/trailing-slash removed, service
 * root gets `/0`. Throws on a Web Map link (§2.3 — resolution not yet implemented; reject
 * rather than hash a raw web-map URL, which would produce a non-matching UID).
 */
function canonicalizeUrl(sourceUrl) {
  const raw = String(sourceUrl || "").trim();
  if (!raw) throw new Error("A FeatureServer layer URL is required.");
  if (isWebMapLink(raw)) {
    throw new Error(
      "Web Map links aren't resolved yet — paste the FeatureServer layer URL " +
        "(…/FeatureServer/0). See AUTO-ICONSET-SPEC.md §2.3."
    );
  }

  let u;
  try {
    u = new URL(raw);
  } catch (_) {
    throw new Error(`Not a valid URL: ${raw}`);
  }

  const scheme = u.protocol.toLowerCase().replace(/:$/, "");
  const host = u.host.toLowerCase();
  let path = u.pathname.replace(/\/{2,}/g, "/").replace(/\/+$/, "");

  // Locate the FeatureServer|MapServer boundary; keep at most one numeric layer id after it.
  const m = path.match(/^(.*\/(?:FeatureServer|MapServer))(?:\/(\d+))?$/i);
  if (!m) {
    throw new Error(
      `URL does not point at a FeatureServer/MapServer layer: ${raw} ` +
        "(expected …/FeatureServer or …/FeatureServer/{layerId})."
    );
  }
  const base = m[1];
  const layerId = m[2] != null ? m[2] : "0"; // §2.2 — service root defaults to layer 0
  return `${scheme}://${host}${base}/${layerId}`;
}

// -------------------------------------------------------------------------
// §4 — UID
// -------------------------------------------------------------------------

/** §4: lowercase hex sha256 of `canonicalUrl + "/" + fieldName`. Written verbatim into iconset.xml. */
function uidFor(canonicalUrl, fieldName) {
  return crypto
    .createHash("sha256")
    .update(canonicalUrl + "/" + (fieldName || ""), "utf8")
    .digest("hex");
}

// -------------------------------------------------------------------------
// §5 — naming
// -------------------------------------------------------------------------

/** §5.1: sanitize + collapse whitespace + trim + cap 60. Caller appends " Icons". */
function sanitizeGroupBase(s) {
  return String(s || "")
    .replace(GROUP_BAD_RE, "_")
    .replace(/\s+/g, " ")
    .trim()
    .slice(0, 60);
}

/** §5.2: label/value → strip path prefix → strip trailing .png → sanitize → append .png. */
function fileNameFor(rawLabel) {
  let base = String(rawLabel == null ? "" : rawLabel);
  base = base.split(/[\\/]/).pop(); // strip embedded path prefix
  base = base.replace(/\.png$/i, ""); // strip trailing .png
  base = base.replace(FILE_BAD_RE, "_");
  if (!base) base = "icon";
  return base + ".png";
}

/** §5.3: append _2/_3/... before .png until unique within `seen` (a Set of taken names). */
function dedupe(fileName, seen) {
  if (!seen.has(fileName)) {
    seen.add(fileName);
    return fileName;
  }
  const base = fileName.replace(/\.png$/i, "");
  let i = 2;
  while (seen.has(`${base}_${i}.png`)) i++;
  const out = `${base}_${i}.png`;
  seen.add(out);
  return out;
}

// -------------------------------------------------------------------------
// §3 — renderer fetch + esriPMS extraction
// -------------------------------------------------------------------------

async function fetchJson(url) {
  if (typeof fetch !== "function") {
    throw new Error("This TAK Portal's Node runtime has no global fetch (needs Node 18+).");
  }
  const res = await fetch(url, { headers: { Accept: "application/json" } });
  if (!res.ok) throw new Error(`ArcGIS request failed (${res.status}) for ${url}`);
  const json = await res.json();
  if (json && json.error) {
    throw new Error(`ArcGIS error: ${json.error.message || JSON.stringify(json.error)}`);
  }
  return json;
}

/** One picture-marker entry pulled from a renderer. imageData is base64 PNG (no data: prefix). */
function pmsFrom(symbol, rawLabel) {
  if (!symbol || symbol.type !== "esriPMS" || !symbol.imageData) return null;
  return { rawLabel, imageData: symbol.imageData };
}

/**
 * §3: from the renderer, return { field, entries[] } where each entry has a label and base64 PNG.
 * Only esriPMS symbols are collected; esriSMS (colored shapes) are the display-config path's job.
 * Renderer array order is preserved (spec §5.3 needs it for deterministic collision suffixes).
 */
function extractPmsEntries(renderer, fieldOverride) {
  const entries = [];
  let field = "";
  if (!renderer || typeof renderer !== "object") return { field: fieldOverride || "", entries, rendererType: "simple" };

  const rawType = renderer.type || "simple";
  const type = /^uniqueValue/i.test(rawType) ? "uniqueValue" : /^classBreaks/i.test(rawType) ? "classBreaks" : "simple";
  const pushDefault = () => {
    const d = pmsFrom(renderer.defaultSymbol, renderer.defaultLabel || null);
    if (d) {
      d.isDefault = !renderer.defaultLabel; // no label → Other.png (§6)
      entries.push(d);
    }
  };

  if (type === "uniqueValue") {
    field = renderer.field1 || renderer.field || "";
    for (const info of renderer.uniqueValueInfos || []) {
      const e = pmsFrom(info.symbol, info.label || info.value);
      // Carried through to generateFromArcgis() so the caller can build a per-value icon
      // symbology mapping (value -> generated filename), not just register the icon set.
      if (e) { e.value = info.value; entries.push(e); }
    }
    pushDefault();
  } else if (type === "classBreaks") {
    field = renderer.field || "";
    for (const info of renderer.classBreakInfos || []) {
      const e = pmsFrom(info.symbol, info.label);
      if (e) entries.push(e);
    }
    pushDefault();
  } else {
    // simple / simpleRenderer / bare symbol — single-symbol, no field (§3)
    const e = pmsFrom(renderer.symbol, renderer.label || null);
    if (e) {
      e.isDefault = !renderer.label;
      entries.push(e);
    }
  }

  if (fieldOverride != null && fieldOverride !== "") field = fieldOverride;
  return { field, entries, rendererType: type };
}

// -------------------------------------------------------------------------
// Orchestration
// -------------------------------------------------------------------------

/**
 * Generate + register an iconset from a FeatureServer layer link, per AUTO-ICONSET-SPEC.md.
 *
 * @param {object}  args
 * @param {string}  args.sourceUrl        pasted FeatureServer/{layerId} URL
 * @param {string} [args.field]           renderer field override (else taken from the renderer)
 * @param {string} [args.token]           ArcGIS token for non-public layers
 * @param {string} [args.actorUsername]   who triggered it (manifest audit)
 * @param {object} [args.renderer]        pre-resolved renderer JSON to use instead of re-fetching
 *   the layer's own default — e.g. a Web Map's per-layer style override (spec §2.3: "The override
 *   may change the renderer used for symbol extraction, but the naming source stays the
 *   FeatureServer layer"). The caller (the configurator) already resolved which renderer applies;
 *   re-fetching here would silently ignore that and extract from the wrong one.
 * @returns {Promise<{success:true, set, uid, group, canonicalUrl, iconCount} | {success:false, error}>}
 */
async function generateFromArcgis({ sourceUrl, field, token, actorUsername, renderer: rendererOverride } = {}) {
  let canonicalUrl;
  try {
    canonicalUrl = canonicalizeUrl(sourceUrl);
  } catch (err) {
    return { success: false, error: err.message };
  }

  // Naming (layer name -> group) still comes from the FeatureServer layer itself even when the
  // renderer is an override — only the renderer source changes, per spec §2.3 — so this fetch
  // always happens, purely for layerName below.
  const tokenQs = token ? `&token=${encodeURIComponent(token)}` : "";
  let meta;
  try {
    meta = await fetchJson(`${canonicalUrl}?f=json${tokenQs}`);
  } catch (err) {
    return { success: false, error: err.message };
  }

  const layerName = meta.name || meta.serviceDescription || "Layer";
  const renderer = rendererOverride || (meta.drawingInfo && meta.drawingInfo.renderer);
  const { field: driveField, entries, rendererType } = extractPmsEntries(renderer, field);

  if (!entries.length) {
    return {
      success: false,
      error:
        "This layer's renderer has no picture-marker (esriPMS) symbols to extract — " +
        "its markers are colored shapes, which the display config already handles.",
    };
  }

  const uid = uidFor(canonicalUrl, driveField);
  const group = `${sanitizeGroupBase(layerName)} Icons`;

  // §5.2/§5.3 naming + base64 decode. value/isDefault ride along on each icon entry (rather
  // than being re-derived from `entries` by position afterward) so a skipped undecodable
  // symbol can't shift a later entry's value out of alignment with its filename.
  const seen = new Set();
  const icons = [];
  for (const e of entries) {
    const atakName = dedupe(e.isDefault && !e.rawLabel ? "Other.png" : fileNameFor(e.rawLabel), seen);
    let bytes;
    try {
      bytes = Buffer.from(e.imageData, "base64");
    } catch (_) {
      continue; // skip an undecodable symbol rather than fail the whole set
    }
    if (bytes && bytes.length) icons.push({ atakName, bytes, value: e.value, isDefault: !!e.isDefault });
  }

  if (!icons.length) {
    return { success: false, error: "All picture-marker symbols failed to decode." };
  }

  const result = registerArcgisSet({
    name: group, // storage/display name == spec group
    uid,
    group,
    icons,
    sourceUrl: canonicalUrl,
    field: driveField,
    specVersion: SPEC_VERSION,
    actorUsername,
  });
  if (!result.success) return result;

  // For a caller that wants to auto-build display-config symbology (not just register the
  // set): each non-default icon's driving-field value -> generated filename, plus the
  // fallback/default icon's filename, if any. classBreaks renderers intentionally have no
  // per-entry `value` (numeric ranges aren't a single match value) so valueMap is empty for
  // them — icons are still generated/registered for sharing, just not auto-assigned here.
  const valueMap = icons
    .filter((i) => !i.isDefault && typeof i.value !== "undefined")
    .map((i) => ({ value: i.value, filename: i.atakName }));
  const defaultIcon = icons.find((i) => i.isDefault);

  return {
    success: true,
    set: result.set,
    uid,
    // The manifest's own stored name, not the local `group` var — registerArcgisSet() runs
    // it through cleanSetName() (trim + 60-char cap) before storing, which for a long layer
    // name could differ from the pre-registration string by a trailing char or two. Callers
    // need the name that will actually resolve via resolveUsericonPath()/the icons API.
    group: result.set.name,
    canonicalUrl,
    field: driveField,
    iconCount: icons.length,
    rendererType,
    valueMap,
    defaultFilename: defaultIcon ? defaultIcon.atakName : null,
  };
}

module.exports = {
  generateFromArcgis,
  // exported for the cross-platform conformance test (spec §4/§5/§9)
  canonicalizeUrl,
  uidFor,
  sanitizeGroupBase,
  fileNameFor,
  dedupe,
  extractPmsEntries,
  SPEC_VERSION,
};
