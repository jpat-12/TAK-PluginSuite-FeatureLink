// ArcGIS session manager — port of the ATAK plugin's arcgis/ArcGISAuthManager.java. Uses the
// same OAuth2 PKCE flow (lib/oauth.ts) redirecting to ArcGIS's own hosted sign-in page, rather
// than an in-app username/password form. See config.ts for the redirect_uri/client_id caveat
// that's specific to the web port.

import { reactive } from 'vue';
import * as oauth from './oauth.ts';
import { DEFAULT_PORTAL_URL, ARCGIS_OAUTH_CLIENT_ID } from './config.ts';

const KEY = 'cloudtak-featurelink:auth';
const PENDING_KEY = 'cloudtak-featurelink:oauth-pending';
const ERROR_KEY = 'cloudtak-featurelink:oauth-error';

interface Persisted {
    username: string | null;
    token: string | null;
    refreshToken: string | null;
    portalUrl: string;
    tokenExpiry: number;
}

interface PendingOAuth {
    codeVerifier: string;
    portalUrl: string;
    state: string;
}

function loadPersisted(): Persisted {
    try {
        const raw = localStorage.getItem(KEY);
        if (raw) {
            return {
                username: null, token: null, refreshToken: null, portalUrl: DEFAULT_PORTAL_URL, tokenExpiry: 0,
                ...JSON.parse(raw) as Partial<Persisted>,
            };
        }
    } catch { /* fall through */ }
    return { username: null, token: null, refreshToken: null, portalUrl: DEFAULT_PORTAL_URL, tokenExpiry: 0 };
}

function savePersisted(p: Persisted): void {
    try { localStorage.setItem(KEY, JSON.stringify(p)); } catch { /* quota */ }
}

const persisted = loadPersisted();

// Reactive so components can show sign-in state without polling. Cleared automatically once
// the access token AND refresh token are both no longer usable (see getToken()).
export const authState = reactive({
    username: (persisted.token && Date.now() < persisted.tokenExpiry) ? persisted.username : null,
});

if (!(persisted.token && Date.now() < persisted.tokenExpiry) && !persisted.refreshToken) {
    persisted.username = null; persisted.token = null; persisted.tokenExpiry = 0;
    savePersisted(persisted);
}

export function isAuthenticated(): boolean {
    return authState.username !== null;
}

export function getUsername(): string | null {
    return authState.username;
}

export function getPortalUrl(): string {
    return persisted.portalUrl;
}

// Returns a valid access token, silently refreshing via the OAuth refresh token if the access
// token has expired. Returns null if there is no session, or the refresh token has also
// expired — caller should prompt the user to sign in again.
export async function getToken(): Promise<string | null> {
    if (persisted.token && Date.now() < persisted.tokenExpiry) return persisted.token;

    if (persisted.refreshToken) {
        try {
            const tokens = await oauth.refreshAccessToken(persisted.portalUrl, ARCGIS_OAUTH_CLIENT_ID, persisted.refreshToken);
            persisted.token = tokens.accessToken;
            persisted.tokenExpiry = Date.now() + tokens.expiresInSeconds * 1000;
            if (tokens.refreshToken) persisted.refreshToken = tokens.refreshToken;
            savePersisted(persisted);
            return persisted.token;
        } catch (e) {
            console.warn('[featurelink] silent token refresh failed — re-login required', e);
        }
    }

    signOut();
    return null;
}

// Redirects the browser to ArcGIS's hosted OAuth sign-in page. Never returns — call this from
// a click handler and nothing after it.
export async function beginSignIn(portalUrl: string = DEFAULT_PORTAL_URL): Promise<void> {
    if (!ARCGIS_OAUTH_CLIENT_ID) {
        throw new Error('This CloudTAK deployment has no ArcGIS OAuth client ID configured (see plugin/lib/config.ts)');
    }
    const { codeVerifier, codeChallenge } = await oauth.generatePkce();
    const state = crypto.randomUUID();
    const pending: PendingOAuth = { codeVerifier, portalUrl, state };
    sessionStorage.setItem(PENDING_KEY, JSON.stringify(pending));
    window.location.assign(oauth.buildAuthUrl(portalUrl, ARCGIS_OAUTH_CLIENT_ID, codeChallenge, state));
}

// Call once at plugin startup. If the current URL carries an ArcGIS OAuth redirect (?code=
// or ?error=), completes the flow and strips those params from the URL bar. No-op otherwise.
export async function completeSignInIfPresent(): Promise<void> {
    const url = new URL(window.location.href);
    const code = url.searchParams.get('code');
    const error = url.searchParams.get('error');
    const state = url.searchParams.get('state');
    if (!code && !error) return;

    url.searchParams.delete('code');
    url.searchParams.delete('state');
    url.searchParams.delete('error');
    url.searchParams.delete('error_description');
    window.history.replaceState(null, '', url.toString());

    const pendingRaw = sessionStorage.getItem(PENDING_KEY);
    sessionStorage.removeItem(PENDING_KEY);

    if (error) {
        sessionStorage.setItem(ERROR_KEY, `Sign-in failed: ${error}`);
        return;
    }
    if (!pendingRaw) {
        sessionStorage.setItem(ERROR_KEY, 'Sign-in session expired — please try again');
        return;
    }
    const pending = JSON.parse(pendingRaw) as PendingOAuth;
    if (pending.state !== state) {
        sessionStorage.setItem(ERROR_KEY, 'Sign-in failed: state mismatch');
        return;
    }

    try {
        const tokens = await oauth.exchangeCode(pending.portalUrl, ARCGIS_OAUTH_CLIENT_ID, code as string, pending.codeVerifier);
        persisted.username = tokens.username;
        persisted.token = tokens.accessToken;
        persisted.refreshToken = tokens.refreshToken;
        persisted.tokenExpiry = Date.now() + tokens.expiresInSeconds * 1000;
        persisted.portalUrl = pending.portalUrl;
        savePersisted(persisted);
        authState.username = tokens.username;
    } catch (e) {
        sessionStorage.setItem(ERROR_KEY, e instanceof Error ? e.message : 'Sign-in failed');
    }
}

// One-shot read of an error left behind by completeSignInIfPresent() (e.g. denied consent) —
// AccountView displays it once, then it's gone.
export function takePendingError(): string | null {
    const msg = sessionStorage.getItem(ERROR_KEY);
    if (msg) sessionStorage.removeItem(ERROR_KEY);
    return msg;
}

export function signOut(): void {
    persisted.username = null;
    persisted.token = null;
    persisted.refreshToken = null;
    persisted.tokenExpiry = 0;
    savePersisted(persisted);
    authState.username = null;
}
