// The single ArcGIS request/response guard for the whole plugin (C-22, C-21, C-33).
//
// Before this module every parse site did `res.json()` directly:
//   * `readJson()` never checked `res.ok`, so a 401/403/404/500 HTML error page was treated as
//     data — `queryFeatureCount` returned 0 for a 403 ("no features" instead of "access denied"),
//     `fetchLayerInfo` returned a layer named "Unknown Layer" for a 404.
//   * ArcGIS returns `{"error":{"code":498,"message":"Invalid token"}}` with **HTTP 200**, and
//     only 4 of ~14 sites checked for it, so an expired token also rendered as "0 features".
//   * The access token was placed in the URL query string, leaking it into ArcGIS/proxy/CDN access
//     logs, `Referer` headers, and any HAR export handed to support.
//   * No call had a timeout or an AbortController, so one hung endpoint stalled a scheduler tick
//     forever and every other layer stopped refreshing.
//
// Everything now goes through arcgisJson(): transport status AND `json.error` are both checked, a
// typed error is thrown, the token travels in a header and only to a host on the signed-in
// portal's own deployment, and every request has a deadline.

import { isTokenTrustedHost, redactUrl } from './arcgisUrl.ts';

export const DEFAULT_TIMEOUT_MS = 30_000;

export type ArcGISErrorKind =
    | 'network'      // fetch rejected / aborted / timed out
    | 'http'         // non-2xx transport status
    | 'auth'         // ArcGIS 498/499/403 — token invalid, expired, or insufficient
    | 'notfound'     // ArcGIS 400 "Invalid URL"/404
    | 'service'      // any other ArcGIS `error` envelope
    | 'malformed';   // 2xx with a body that is not the JSON we asked for

export class ArcGISError extends Error {
    readonly kind: ArcGISErrorKind;
    readonly status: number | null;
    readonly code: number | null;
    readonly url: string;
    readonly details: string[];

    constructor(kind: ArcGISErrorKind, message: string, opts: {
        url: string; status?: number | null; code?: number | null; details?: string[];
    }) {
        super(message);
        this.name = 'ArcGISError';
        this.kind = kind;
        this.status = opts.status ?? null;
        this.code = opts.code ?? null;
        this.url = redactUrl(opts.url);
        this.details = opts.details ?? [];
    }

    /** A message safe to show an operator: never contains a token, always names a cause. */
    get userMessage(): string {
        switch (this.kind) {
            case 'auth':     return `ArcGIS rejected the request: ${this.message}. Sign in again and retry.`;
            case 'notfound': return `ArcGIS could not find that layer: ${this.message}`;
            case 'network':  return `Could not reach ArcGIS: ${this.message}`;
            case 'http':     return `ArcGIS returned HTTP ${this.status ?? '?'}: ${this.message}`;
            case 'malformed':return `ArcGIS returned an unreadable response: ${this.message}`;
            default:         return `ArcGIS error: ${this.message}`;
        }
    }
}

/** ArcGIS `error` envelope, which ships with HTTP 200. */
interface ArcGISErrorEnvelope {
    error?: { code?: number; message?: string; messageCode?: string; details?: string[] };
}

const AUTH_CODES = new Set([498, 499, 403]);
const NOTFOUND_CODES = new Set([400, 404]);

function classify(code: number | null, message: string): ArcGISErrorKind {
    if (code !== null && AUTH_CODES.has(code)) return 'auth';
    if (code !== null && NOTFOUND_CODES.has(code) && /invalid url|does not exist|not found/i.test(message)) {
        return 'notfound';
    }
    return 'service';
}

export interface ArcGISRequestOptions {
    /** ArcGIS access token. Attached as a header, and ONLY when the target host is trusted. */
    token?: string | null;
    /** Portal the token belongs to. Required whenever `token` is set. */
    portalUrl?: string;
    /** Query params appended to the URL. Never include `token` here. */
    params?: Record<string, string>;
    method?: 'GET' | 'POST';
    /** POST form body. Never include `token` here either. */
    form?: Record<string, string>;
    /** Multipart body (file uploads). Mutually exclusive with `form`. */
    formData?: FormData;
    timeoutMs?: number;
    signal?: AbortSignal;
}

function buildUrl(base: string, params: Record<string, string> | undefined): string {
    if (!params || Object.keys(params).length === 0) return base;
    const u = new URL(base);
    for (const [k, v] of Object.entries(params)) u.searchParams.set(k, v);
    return u.toString();
}

/**
 * Attaches the token as a header rather than `?token=` (C-21). ArcGIS Online and Enterprise both
 * accept `X-Esri-Authorization: Bearer <token>`; `Authorization` is sent alongside it because some
 * Enterprise reverse proxies forward only the standard header. Returns `{}` — dropping the token
 * entirely — when the target is not on the portal's deployment (C-33).
 */
function authHeaders(url: string, token: string | null | undefined, portalUrl: string | undefined): Record<string, string> {
    if (!token) return {};
    const portal = portalUrl ?? 'https://www.arcgis.com';
    if (!isTokenTrustedHost(url, portal)) {
        console.warn(
            '[featurelink] refusing to send the ArcGIS token to an untrusted host:',
            redactUrl(url), '— portal is', portal,
        );
        return {};
    }
    return {
        'X-Esri-Authorization': `Bearer ${token}`,
        Authorization: `Bearer ${token}`,
    };
}

function combineSignals(timeoutMs: number, external: AbortSignal | undefined): AbortSignal {
    const timeout = AbortSignal.timeout(timeoutMs);
    if (!external) return timeout;
    // AbortSignal.any is ES2024-era but present in every browser that has DecompressionStream,
    // which this plugin already requires (zipReader.ts).
    return AbortSignal.any([timeout, external]);
}

/**
 * Performs one ArcGIS request and returns its parsed JSON, throwing an `ArcGISError` for every
 * failure mode — transport, envelope, or malformed body. This is the ONLY place in the plugin
 * that is allowed to call `res.json()` on an ArcGIS response.
 */
export async function arcgisJson<T>(baseUrl: string, opts: ArcGISRequestOptions = {}): Promise<T> {
    const method = opts.method ?? 'GET';
    const url = buildUrl(baseUrl, opts.params);
    const headers: Record<string, string> = {
        Accept: 'application/json',
        ...authHeaders(url, opts.token, opts.portalUrl),
    };

    let body: BodyInit | undefined;
    if (opts.formData) {
        body = opts.formData;
    } else if (opts.form) {
        headers['Content-Type'] = 'application/x-www-form-urlencoded';
        body = new URLSearchParams(opts.form).toString();
    }

    let res: Response;
    try {
        res = await fetch(url, {
            method,
            headers,
            ...(body === undefined ? {} : { body }),
            signal: combineSignals(opts.timeoutMs ?? DEFAULT_TIMEOUT_MS, opts.signal),
        });
    } catch (e) {
        const msg = e instanceof Error ? e.message : String(e);
        throw new ArcGISError('network', msg, { url });
    }

    if (!res.ok) {
        throw new ArcGISError('http', res.statusText || `HTTP ${res.status}`, { url, status: res.status });
    }

    let json: unknown;
    try {
        json = await res.json();
    } catch (e) {
        // The classic symptom: an HTML error/login page served with 200 produces
        // "Unexpected token <". Report it as a service problem, not as empty data.
        const msg = e instanceof Error ? e.message : String(e);
        throw new ArcGISError('malformed', msg, { url, status: res.status });
    }

    if (typeof json !== 'object' || json === null) {
        throw new ArcGISError('malformed', 'response body was not a JSON object', { url, status: res.status });
    }

    const envelope = json as ArcGISErrorEnvelope;
    if (envelope.error) {
        const code = typeof envelope.error.code === 'number' ? envelope.error.code : null;
        const message = envelope.error.message ?? 'unspecified ArcGIS error';
        throw new ArcGISError(classify(code, message), message, {
            url, status: res.status, code, details: envelope.error.details ?? [],
        });
    }

    return json as T;
}

/** Human-readable cause for any thrown value, with tokens redacted. */
export function describeError(e: unknown): string {
    if (e instanceof ArcGISError) return e.userMessage;
    if (e instanceof Error) return redactUrl(e.message);
    return redactUrl(String(e));
}
