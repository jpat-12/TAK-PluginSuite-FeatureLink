// ArcGIS session manager — port of the ATAK plugin's arcgis/ArcGISAuthManager.java. Uses the
// same OAuth2 PKCE flow (lib/oauth.ts) redirecting to ArcGIS's own hosted sign-in page, but in
// a popup window rather than navigating this tab away — this page's own origin never has to be
// a registered ArcGIS redirect_uri (see oauth.ts / config.ts's ARCGIS_OAUTH_RELAY_URL).
//
// TOKEN STORAGE (C-20). This used to keep BOTH the access token and the refresh token in
// `localStorage` in plaintext. On the web that is materially weaker than ATAK's app-private
// SharedPreferences: any XSS anywhere in the CloudTAK origin — CloudTAK itself, a compromised
// dependency, or another plugin — reads both, and the refresh token grants long-lived offline
// access to the operator's entire ArcGIS account. Now:
//
//   * the ACCESS token lives in a module variable and is never written to any storage at all;
//   * the REFRESH token lives in `sessionStorage`, which is per-tab and cleared when the tab
//     closes, rather than `localStorage`, which survives indefinitely across every tab;
//   * only non-secret preferences (the portal URL) remain in `localStorage`.
//
// The cost is that a session no longer survives closing the tab. That is the defensible default
// for a credential of this blast radius; see QUESTIONS-FOR-OWNER.md (wp2) for the alternative the
// owner may prefer (a CloudTAK-server-side token broker, which removes the refresh token from the
// browser entirely).

import { reactive, computed } from 'vue';
import type { ComputedRef } from 'vue';
import * as oauth from './oauth.ts';
import { singleFlight } from './asyncLock.ts';
import { DEFAULT_PORTAL_URL, ARCGIS_OAUTH_CLIENT_ID, ARCGIS_OAUTH_RELAY_URL } from './config.ts';

const SESSION_KEY = 'cloudtak-featurelink:auth';   // sessionStorage — refresh token + username
const PREFS_KEY = 'cloudtak-featurelink:auth-prefs'; // localStorage — portal URL only
const RELAY_ORIGIN = new URL(ARCGIS_OAUTH_RELAY_URL).origin;

/** A token this close to expiry is treated as already expired (§10.7 — no skew margin before). */
const EXPIRY_SKEW_MS = 60_000;
/** Hard cap on how long the sign-in popup may take before the flow is abandoned (§5.4). */
const SIGNIN_TIMEOUT_MS = 5 * 60_000;

interface SessionState {
    username: string | null;
    refreshToken: string | null;
}

interface RelayMessage {
    source: string;
    code?: string | null;
    error?: string | null;
    errorDescription?: string | null;
    state?: string | null;
}

// ── In-memory secrets ─────────────────────────────────────────────────────────

let accessToken: string | null = null;
let accessTokenExpiry = 0;

// ── Persisted, non-secret ─────────────────────────────────────────────────────

function loadPrefs(): { portalUrl: string } {
    try {
        const raw = localStorage.getItem(PREFS_KEY);
        if (raw) {
            const parsed = JSON.parse(raw) as { portalUrl?: unknown };
            if (typeof parsed.portalUrl === 'string' && parsed.portalUrl) return { portalUrl: parsed.portalUrl };
        }
    } catch { /* fall through */ }
    return { portalUrl: DEFAULT_PORTAL_URL };
}

function savePrefs(portalUrl: string): void {
    try { localStorage.setItem(PREFS_KEY, JSON.stringify({ portalUrl })); } catch { /* quota */ }
}

function loadSession(): SessionState {
    try {
        const raw = sessionStorage.getItem(SESSION_KEY);
        if (raw) {
            const parsed = JSON.parse(raw) as Partial<SessionState>;
            return {
                username: typeof parsed.username === 'string' ? parsed.username : null,
                refreshToken: typeof parsed.refreshToken === 'string' ? parsed.refreshToken : null,
            };
        }
    } catch { /* fall through */ }
    return { username: null, refreshToken: null };
}

function saveSession(s: SessionState): void {
    try {
        if (!s.refreshToken && !s.username) sessionStorage.removeItem(SESSION_KEY);
        else sessionStorage.setItem(SESSION_KEY, JSON.stringify(s));
    } catch { /* quota */ }
}

const prefs = loadPrefs();
const session = loadSession();

/**
 * Reactive so components can show sign-in state without polling. Every consumer previously used a
 * `void authState.username;` dependency-forcing hack copy-pasted into six components; `isAuthedRef`
 * below replaces it with one exported computed (Appendix B §1.2).
 */
export const authState = reactive({
    username: session.username,
    /** Set when a session ends because the token could not be refreshed, so the UI can say so. */
    expiredMessage: '' as string,
});

export const isAuthedRef: ComputedRef<boolean> = computed(() => authState.username !== null);
export const usernameRef: ComputedRef<string | null> = computed(() => authState.username);

export function isAuthenticated(): boolean {
    return authState.username !== null;
}

export function getUsername(): string | null {
    return authState.username;
}

export function getPortalUrl(): string {
    return prefs.portalUrl;
}

/**
 * A valid access token, silently refreshing via the refresh token when the access token is expired
 * (or within the skew margin). Returns null when there is no usable session.
 *
 * Concurrent callers collapse onto ONE refresh: ArcGIS rotates refresh tokens, so two simultaneous
 * refreshes made the second fail, and the failure path calls signOut() — producing spurious
 * sign-outs under entirely normal concurrent use (§10.7).
 */
export async function getToken(): Promise<string | null> {
    if (accessToken && Date.now() + EXPIRY_SKEW_MS < accessTokenExpiry) return accessToken;
    if (!session.refreshToken) {
        if (authState.username !== null) endSession('Your ArcGIS session has expired — please sign in again.');
        return null;
    }

    return singleFlight('arcgis-refresh', async () => {
        // Re-check inside the critical section: a queued caller may be arriving just after the
        // refresh it was waiting on already succeeded.
        if (accessToken && Date.now() + EXPIRY_SKEW_MS < accessTokenExpiry) return accessToken;
        const refreshToken = session.refreshToken;
        if (!refreshToken) return null;
        try {
            const tokens = await oauth.refreshAccessToken(prefs.portalUrl, ARCGIS_OAUTH_CLIENT_ID, refreshToken);
            accessToken = tokens.accessToken;
            accessTokenExpiry = Date.now() + tokens.expiresInSeconds * 1000;
            if (tokens.refreshToken) {
                session.refreshToken = tokens.refreshToken;
                saveSession(session);
            }
            authState.expiredMessage = '';
            return accessToken;
        } catch (e) {
            console.warn('[featurelink] silent token refresh failed — re-login required', e);
            endSession('Your ArcGIS session could not be renewed — please sign in again.');
            return null;
        }
    });
}

/** True when a token acquisition would need user interaction. Distinct from isAuthenticated(), which only reports UI state (§10.7). */
export function isSessionValid(): boolean {
    if (accessToken && Date.now() + EXPIRY_SKEW_MS < accessTokenExpiry) return true;
    return session.refreshToken !== null;
}

/**
 * Waits for the relay page to post back its result, matching `expectedState` so a stray or late
 * message from a previous attempt cannot be mistaken for this one. Rejects on popup close and on a
 * hard timeout — without the timeout, a COOP policy that makes `popup.closed` unreadable left this
 * promise pending forever, the Sign In button disabled forever, and a listener plus a 500 ms
 * interval leaked for the life of the page (§5.4).
 */
function waitForRelayMessage(popup: Window, expectedState: string): Promise<RelayMessage> {
    return new Promise((resolve, reject) => {
        let settled = false;
        const finish = (fn: () => void): void => {
            if (settled) return;
            settled = true;
            window.removeEventListener('message', onMessage);
            window.clearInterval(closedCheck);
            window.clearTimeout(deadline);
            fn();
        };
        function onMessage(event: MessageEvent): void {
            if (event.origin !== RELAY_ORIGIN) return;
            const data = event.data as RelayMessage | undefined;
            if (!data || data.source !== 'featurelink-oauth-relay' || data.state !== expectedState) return;
            finish(() => resolve(data));
        }
        window.addEventListener('message', onMessage);
        const closedCheck = window.setInterval(() => {
            // `popup.closed` can throw under some cross-origin isolation policies; an unguarded
            // read threw every 500 ms forever with no handler (§10.7).
            let closed: boolean;
            try { closed = popup.closed; } catch { closed = false; }
            if (closed) finish(() => reject(new Error('Sign-in window was closed before completing')));
        }, 500);
        const deadline = window.setTimeout(() => {
            finish(() => reject(new Error('Sign-in timed out after 5 minutes — please try again')));
        }, SIGNIN_TIMEOUT_MS);
    });
}

/**
 * Opens ArcGIS's hosted OAuth sign-in page in a popup and resolves once the user has signed in.
 * Must be called directly from a click handler — the popup is opened synchronously, before any
 * awaits, so browsers do not treat it as a blocked pop-up.
 */
export async function beginSignIn(portalUrl: string = DEFAULT_PORTAL_URL): Promise<void> {
    if (!ARCGIS_OAUTH_CLIENT_ID) {
        throw new Error('This CloudTAK deployment has no ArcGIS OAuth client ID configured (see plugin/lib/config.ts)');
    }
    oauth.assertAllowedPortal(portalUrl);

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
        accessToken = tokens.accessToken;
        accessTokenExpiry = Date.now() + tokens.expiresInSeconds * 1000;
        session.username = tokens.username;
        session.refreshToken = tokens.refreshToken;
        saveSession(session);
        prefs.portalUrl = portalUrl;
        savePrefs(portalUrl);
        authState.username = tokens.username;
        authState.expiredMessage = '';
    } finally {
        let closed = true;
        try { closed = popup.closed; } catch { /* cross-origin */ }
        if (!closed) popup.close();
    }
}

function endSession(message: string): void {
    accessToken = null;
    accessTokenExpiry = 0;
    session.username = null;
    session.refreshToken = null;
    saveSession(session);
    authState.username = null;
    authState.expiredMessage = message;
}

export function signOut(): void {
    endSession('');
}

/** Test seam: installs a session without running the OAuth popup flow. */
export function __setSessionForTest(s: { username: string | null; accessToken: string | null; expiresInMs?: number; refreshToken?: string | null }): void {
    accessToken = s.accessToken;
    accessTokenExpiry = Date.now() + (s.expiresInMs ?? 3_600_000);
    session.username = s.username;
    session.refreshToken = s.refreshToken ?? null;
    authState.username = s.username;
}
