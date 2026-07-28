// ArcGIS session manager — port of the ATAK plugin's arcgis/ArcGISAuthManager.java. Uses the
// same OAuth2 PKCE flow (lib/oauth.ts) redirecting to ArcGIS's own hosted sign-in page, but in
// a popup window rather than navigating this tab away — this page's own origin never has to be
// a registered ArcGIS redirect_uri (see oauth.ts / config.ts's ARCGIS_OAUTH_RELAY_URL for how
// the popup's result gets back here).

import { reactive } from 'vue';
import * as oauth from './oauth.ts';
import { DEFAULT_PORTAL_URL, ARCGIS_OAUTH_CLIENT_ID, ARCGIS_OAUTH_RELAY_URL } from './config.ts';

const KEY = 'cloudtak-featurelink:auth';
const RELAY_ORIGIN = new URL(ARCGIS_OAUTH_RELAY_URL).origin;

interface Persisted {
    username: string | null;
    token: string | null;
    refreshToken: string | null;
    portalUrl: string;
    tokenExpiry: number;
}

interface RelayMessage {
    source: string;
    code?: string | null;
    error?: string | null;
    errorDescription?: string | null;
    state?: string | null;
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

// Waits for the relay page (running in `popup`) to post back its result, matching it to
// `expectedState` so a stray/late message from a previous attempt can't be mistaken for this
// one. Rejects if the user closes the popup before completing sign-in.
function waitForRelayMessage(popup: Window, expectedState: string): Promise<RelayMessage> {
    return new Promise((resolve, reject) => {
        let settled = false;
        function cleanup(): void {
            window.removeEventListener('message', onMessage);
            window.clearInterval(closedCheck);
        }
        function onMessage(event: MessageEvent): void {
            if (event.origin !== RELAY_ORIGIN) return;
            const data = event.data as RelayMessage | undefined;
            if (!data || data.source !== 'featurelink-oauth-relay' || data.state !== expectedState) return;
            settled = true;
            cleanup();
            resolve(data);
        }
        window.addEventListener('message', onMessage);
        const closedCheck = window.setInterval(() => {
            if (popup.closed && !settled) {
                cleanup();
                reject(new Error('Sign-in window was closed before completing'));
            }
        }, 500);
    });
}

// Opens ArcGIS's hosted OAuth sign-in page in a popup and resolves once the user has signed in
// (or rejects on cancel/error). Must be called directly from a click handler — the popup is
// opened synchronously, before any awaits, so browsers don't treat it as a blocked pop-up.
export async function beginSignIn(portalUrl: string = DEFAULT_PORTAL_URL): Promise<void> {
    if (!ARCGIS_OAUTH_CLIENT_ID) {
        throw new Error('This CloudTAK deployment has no ArcGIS OAuth client ID configured (see plugin/lib/config.ts)');
    }

    const popup = window.open('about:blank', 'featurelink-oauth', 'width=480,height=720');
    if (!popup) {
        throw new Error('Sign-in popup was blocked — please allow popups for this site and try again');
    }

    try {
        const { codeVerifier, codeChallenge } = await oauth.generatePkce();
        const state = `${window.location.origin}::${crypto.randomUUID()}`;
        popup.location.href = oauth.buildAuthUrl(portalUrl, ARCGIS_OAUTH_CLIENT_ID, codeChallenge, state);

        const relayResult = await waitForRelayMessage(popup, state);
        if (relayResult.error) throw new Error(relayResult.errorDescription ?? relayResult.error);
        if (!relayResult.code) throw new Error('ArcGIS sign-in returned no authorization code');

        const tokens = await oauth.exchangeCode(portalUrl, ARCGIS_OAUTH_CLIENT_ID, relayResult.code, codeVerifier);
        persisted.username = tokens.username;
        persisted.token = tokens.accessToken;
        persisted.refreshToken = tokens.refreshToken;
        persisted.tokenExpiry = Date.now() + tokens.expiresInSeconds * 1000;
        persisted.portalUrl = portalUrl;
        savePersisted(persisted);
        authState.username = tokens.username;
    } finally {
        if (!popup.closed) popup.close();
    }
}

export function signOut(): void {
    persisted.username = null;
    persisted.token = null;
    persisted.refreshToken = null;
    persisted.tokenExpiry = 0;
    savePersisted(persisted);
    authState.username = null;
}
