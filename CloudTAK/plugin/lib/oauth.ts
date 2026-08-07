// ArcGIS OAuth2 PKCE flow — port of the ATAK plugin's OAuthHelper.java. The web version's
// redirect_uri is a fixed relay page (see ARCGIS_OAUTH_RELAY_URL in config.ts) rather than
// this deployment's own origin, so it works on every CloudTAK hostname with zero per-deployment
// ArcGIS app registration — see arcgisAuth.ts's beginSignIn() for how the popup + relay page
// gets the result back to this origin.

import { ARCGIS_OAUTH_RELAY_URL } from './config.ts';

export interface OAuthTokens {
    accessToken: string;
    refreshToken: string | null;
    username: string | null;
    expiresInSeconds: number;
}

function base64UrlEncode(bytes: Uint8Array): string {
    let binary = '';
    for (const b of bytes) binary += String.fromCharCode(b);
    return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

export function redirectUri(): string {
    return ARCGIS_OAUTH_RELAY_URL;
}

export async function generatePkce(): Promise<{ codeVerifier: string; codeChallenge: string }> {
    const bytes = new Uint8Array(32);
    crypto.getRandomValues(bytes);
    const codeVerifier = base64UrlEncode(bytes);

    const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(codeVerifier));
    const codeChallenge = base64UrlEncode(new Uint8Array(digest));

    return { codeVerifier, codeChallenge };
}

function normalizePortal(url: string): string {
    return (url || 'https://www.arcgis.com').replace(/\/+$/, '');
}

/**
 * The portal is a persisted value that anything able to write localStorage could set, and it is
 * interpolated straight into the token endpoint — so an unvalidated portal directs the OAuth code
 * exchange, carrying the authorization code and PKCE verifier, to an arbitrary host (§10.7).
 * Only https ArcGIS Online, or an explicit Enterprise portal path, is accepted.
 */
export function assertAllowedPortal(url: string): string {
    const normalized = normalizePortal(url);
    let u: URL;
    try { u = new URL(normalized); }
    catch { throw new Error(`Not a valid portal URL: ${url}`); }
    if (u.protocol !== 'https:') throw new Error('The ArcGIS portal URL must use https.');
    const host = u.host.toLowerCase();
    const isAgol = host === 'arcgis.com' || host.endsWith('.arcgis.com');
    // ArcGIS Enterprise portals live at https://<host>/<webadaptor> — require that shape rather
    // than accepting any bare hostname.
    const isEnterprise = /^\/[A-Za-z0-9._-]+$/.test(u.pathname.replace(/\/+$/, '') || '/');
    if (!isAgol && !isEnterprise) {
        throw new Error(`Refusing to use "${host}" as an ArcGIS portal — expected arcgis.com or an Enterprise portal URL.`);
    }
    return normalized;
}

export function buildAuthUrl(portalUrl: string, clientId: string, codeChallenge: string, state: string): string {
    const params = new URLSearchParams({
        client_id: clientId,
        response_type: 'code',
        redirect_uri: redirectUri(),
        code_challenge: codeChallenge,
        code_challenge_method: 'S256',
        state,
    });
    return `${normalizePortal(portalUrl)}/sharing/rest/oauth2/authorize?${params.toString()}`;
}

interface TokenResponse {
    access_token?: string;
    refresh_token?: string;
    username?: string;
    expires_in?: number;
    error?: string;
    error_description?: string;
}

async function postForTokens(endpoint: string, body: URLSearchParams): Promise<OAuthTokens> {
    const res = await fetch(endpoint, {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded', Accept: 'application/json' },
        body: body.toString(),
        signal: AbortSignal.timeout(30_000),
    });

    // `res.json()` was called unconditionally: a 400 with an HTML body threw
    // "SyntaxError: Unexpected token <", which AccountView rendered verbatim to the operator on
    // the single most important failure path in the plugin (§10.7).
    let json: TokenResponse;
    try {
        json = await res.json() as TokenResponse;
    } catch {
        throw new Error(
            res.ok
                ? 'ArcGIS returned an unreadable response from the token endpoint.'
                : `ArcGIS sign-in failed (HTTP ${res.status} ${res.statusText}).`,
        );
    }

    if (json.error) throw new Error(json.error_description ?? json.error);
    if (!res.ok) throw new Error(`ArcGIS sign-in failed (HTTP ${res.status}).`);
    if (!json.access_token) throw new Error('OAuth token endpoint returned no access_token');
    // A fabricated 1-hour lifetime for a token that was really shorter means every request in the
    // gap fails with 498 — and, before C-22, that failure was read as data (§10.7).
    if (typeof json.expires_in !== 'number' || json.expires_in <= 0) {
        throw new Error('ArcGIS did not report a token lifetime (expires_in) — refusing to guess one.');
    }

    return {
        accessToken: json.access_token,
        refreshToken: json.refresh_token ?? null,
        username: json.username ?? null,
        expiresInSeconds: json.expires_in,
    };
}

export async function exchangeCode(portalUrl: string, clientId: string, code: string, codeVerifier: string): Promise<OAuthTokens> {
    const endpoint = `${assertAllowedPortal(portalUrl)}/sharing/rest/oauth2/token`;
    const body = new URLSearchParams({
        grant_type: 'authorization_code',
        client_id: clientId,
        code,
        redirect_uri: redirectUri(),
        code_verifier: codeVerifier,
    });
    return postForTokens(endpoint, body);
}

export async function refreshAccessToken(portalUrl: string, clientId: string, refreshToken: string): Promise<OAuthTokens> {
    const endpoint = `${assertAllowedPortal(portalUrl)}/sharing/rest/oauth2/token`;
    const body = new URLSearchParams({
        grant_type: 'refresh_token',
        client_id: clientId,
        refresh_token: refreshToken,
    });
    return postForTokens(endpoint, body);
}
