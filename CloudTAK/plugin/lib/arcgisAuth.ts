// ArcGIS session manager — port of the ATAK plugin's arcgis/ArcGISAuthManager.java, but
// backed by username/password token auth (lib/tokenAuth.ts) instead of OAuth PKCE. There is
// no refresh token with this flow: once the issued token expires, getToken() clears the
// session and the user must sign in again with their password.

import { reactive } from 'vue';
import { generateToken } from './tokenAuth.ts';
import { DEFAULT_PORTAL_URL } from './config.ts';

const KEY = 'cloudtak-featurelink:auth';

// The token itself is persisted (so a page reload doesn't force a re-login within its
// validity window) — the password never is.
interface Persisted {
    username: string | null;
    token: string | null;
    portalUrl: string;
    tokenExpiry: number;
}

function loadPersisted(): Persisted {
    try {
        const raw = localStorage.getItem(KEY);
        if (raw) return { username: null, token: null, portalUrl: DEFAULT_PORTAL_URL, tokenExpiry: 0, ...JSON.parse(raw) as Partial<Persisted> };
    } catch { /* fall through */ }
    return { username: null, token: null, portalUrl: DEFAULT_PORTAL_URL, tokenExpiry: 0 };
}

function savePersisted(p: Persisted): void {
    try { localStorage.setItem(KEY, JSON.stringify(p)); } catch { /* quota */ }
}

const persisted = loadPersisted();

// Reactive so components can show sign-in state without polling. Cleared automatically once
// the token expires (see getToken()) since there's no way to silently refresh it.
export const authState = reactive({
    username: (persisted.token && Date.now() < persisted.tokenExpiry) ? persisted.username : null,
});

if (!(persisted.token && Date.now() < persisted.tokenExpiry)) {
    persisted.username = null; persisted.token = null; persisted.tokenExpiry = 0;
    savePersisted(persisted);
}

export function isAuthenticated(): boolean {
    return authState.username !== null && Date.now() < persisted.tokenExpiry;
}

export function getUsername(): string | null {
    return authState.username;
}

export function getPortalUrl(): string {
    return persisted.portalUrl;
}

// Returns the current token, or null once it has expired — caller should prompt the user to
// sign in again (no silent refresh is possible without re-collecting the password).
export async function getToken(): Promise<string | null> {
    if (persisted.token && Date.now() < persisted.tokenExpiry) return persisted.token;
    if (persisted.token) signOut(); // expired — clear the stale session
    return null;
}

export async function signIn(portalUrl: string, username: string, password: string): Promise<void> {
    const result = await generateToken(portalUrl || DEFAULT_PORTAL_URL, username, password);
    persisted.username = result.username;
    persisted.token = result.token;
    persisted.tokenExpiry = result.expiresAt;
    persisted.portalUrl = portalUrl || DEFAULT_PORTAL_URL;
    savePersisted(persisted);
    authState.username = result.username;
}

export function signOut(): void {
    persisted.username = null;
    persisted.token = null;
    persisted.tokenExpiry = 0;
    savePersisted(persisted);
    authState.username = null;
}
