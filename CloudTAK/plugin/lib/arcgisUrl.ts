// ArcGIS URL canonicalization + request-target trust.
//
// Two jobs, both previously spread across the codebase in incompatible forms:
//
//  1. Canonicalization. `arcgisRest.ensureLayerIndex()` hard-appended `/0` to anything matching
//     /FeatureServer$/ and did nothing at all for a MapServer root, while `autoIconset.canonicalizeUrl()`
//     handled both plus an explicit `/{layerId}`. Two different URL contracts in one plugin
//     (C-08, Appendix B RC-3). There is now one implementation; autoIconset re-exports it.
//
//  2. Request-target trust (C-33 / Appendix B §8.1, §9.7). An ingested config could name any host
//     and mark it `private`, which made `downloadLayer` attach the operator's ArcGIS OAuth token to
//     that host. A token is now only ever attached to a host that belongs to the portal the user is
//     actually signed in to — see isTokenTrustedHost().

// ── Canonicalization ─────────────────────────────────────────────────────────

function isWebMapLink(url: string): boolean {
    return /\/home\/item\.html/i.test(url) || /\/sharing\/rest\/content\/items\//i.test(url);
}

const SERVICE_PATH_RE = /^(.*\/(?:FeatureServer|MapServer))(?:\/(\d+))?$/i;

export interface CanonicalLayer {
    /** `{scheme}://{host}{/path}/FeatureServer|MapServer/{layerId}` — the value hashed for the iconset UID. */
    url: string;
    /** Service root with no layer index. */
    serviceRoot: string;
    layerId: number;
    /** True when the caller's URL carried no explicit `/{layerId}` and layer 0 was assumed. */
    layerIdAssumed: boolean;
}

/**
 * AUTO-ICONSET-SPEC §2.1–§2.2. Scheme+host lowercased, path case preserved, query/fragment/
 * trailing slash stripped, a bare service root defaulting to layer 0. Throws on a Web Map link
 * (§2.3 is not implemented — rejecting beats hashing a raw web-map URL into a UID that matches
 * nothing on any other platform) and on anything that is not a Feature/MapServer layer.
 */
export function canonicalizeLayerUrl(sourceUrl: string): CanonicalLayer {
    const raw = (sourceUrl || '').trim();
    if (!raw) throw new Error('A FeatureServer layer URL is required.');
    if (isWebMapLink(raw)) {
        throw new Error(
            "Web Map links aren't resolved yet — paste the FeatureServer layer URL "
            + '(…/FeatureServer/0). See AUTO-ICONSET-SPEC.md §2.3.',
        );
    }

    let u: URL;
    try { u = new URL(raw); }
    catch { throw new Error(`Not a valid URL: ${raw}`); }

    if (u.protocol.toLowerCase() !== 'https:') {
        // http:// is mixed content inside CloudTAK and fails opaquely in the browser; refusing it
        // here turns "Failed to fetch" in the console into a message the operator can act on.
        throw new Error(`Only https:// service URLs are supported (got ${u.protocol.replace(/:$/, '')}://).`);
    }

    const scheme = 'https';
    const host = u.host.toLowerCase();
    const path = u.pathname.replace(/\/{2,}/g, '/').replace(/\/+$/, '');

    const m = SERVICE_PATH_RE.exec(path);
    if (!m || !m[1]) {
        throw new Error(
            `URL does not point at a FeatureServer/MapServer layer: ${raw} `
            + '(expected …/FeatureServer or …/FeatureServer/{layerId}).',
        );
    }
    const base = m[1];
    const explicit = m[2];
    const layerId = explicit === undefined ? 0 : Number(explicit);
    return {
        url: `${scheme}://${host}${base}/${layerId}`,
        serviceRoot: `${scheme}://${host}${base}`,
        layerId,
        layerIdAssumed: explicit === undefined,
    };
}

/**
 * Fail-soft variant for the many call sites that only need "the URL I should query". Returns the
 * input trimmed of a trailing slash when the URL cannot be canonicalized, so a malformed URL
 * produces an ArcGIS error the response guard can report rather than a thrown TypeError deep in a
 * scheduler tick.
 */
export function layerQueryUrl(sourceUrl: string): string {
    try { return canonicalizeLayerUrl(sourceUrl).url; }
    catch { return (sourceUrl ?? '').trim().replace(/\/+$/, ''); }
}

/** Service root for a layer URL, or null if it is not a recognizable service URL. */
export function serviceRootOf(sourceUrl: string): string | null {
    try { return canonicalizeLayerUrl(sourceUrl).serviceRoot; }
    catch { return null; }
}

// ── Request-target trust ─────────────────────────────────────────────────────

function hostOf(url: string): string | null {
    try { return new URL(url).host.toLowerCase(); } catch { return null; }
}

function registrableSuffixMatch(host: string, portalHost: string): boolean {
    // ArcGIS Online splits the portal (www.arcgis.com) from the data tier
    // (services7.arcgis.com, tiles.arcgis.com, …), so an exact host match is too strict for
    // AGOL and an "endsWith any suffix" rule is far too loose for Enterprise. Only the
    // arcgis.com family gets the sibling-host allowance; everything else must match exactly.
    if (host === portalHost) return true;
    if (portalHost === 'arcgis.com' || portalHost.endsWith('.arcgis.com')) {
        return host === 'arcgis.com' || host.endsWith('.arcgis.com');
    }
    return false;
}

/**
 * True when `targetUrl` belongs to the same ArcGIS deployment as `portalUrl` — the only case in
 * which the operator's access token may be attached to a request.
 *
 * C-33: an imported `.featurelinkshare` could previously set `"private": true` plus an arbitrary
 * `"url"`, and the download path would send the token to that host as a query parameter. With this
 * check the flag can no longer move the token off the portal's own deployment even if the consent
 * dialog is accepted.
 */
export function isTokenTrustedHost(targetUrl: string, portalUrl: string): boolean {
    const target = hostOf(targetUrl);
    const portal = hostOf(portalUrl) ?? 'www.arcgis.com';
    if (!target) return false;
    return registrableSuffixMatch(target, portal);
}

/** Redacts `token=`/`code=` values so a URL can safely be logged (C-21). */
export function redactUrl(url: string): string {
    return url
        .replace(/([?&](?:token|code|refresh_token|access_token)=)[^&#]*/gi, '$1[REDACTED]');
}
