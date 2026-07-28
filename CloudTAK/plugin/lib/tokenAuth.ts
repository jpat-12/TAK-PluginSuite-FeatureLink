// ArcGIS "built-in" username/password token auth — POSTs directly to
// /sharing/rest/generateToken. Replaces OAuth PKCE: no app registration, no redirect URI,
// works immediately from any origin. Trade-offs (documented in README.md): the password is
// sent straight to ArcGIS and never stored; there is no refresh token, so once the issued
// token expires the user must re-enter their password; this only works for ArcGIS "built-in"
// accounts, not SSO/enterprise/social logins.

export interface GeneratedToken {
    token: string;
    expiresAt: number; // epoch ms
    username: string;
}

function normalizePortal(url: string): string {
    return (url || 'https://www.arcgis.com').replace(/\/+$/, '');
}

// Requests the longest-lived token the org's token-expiration policy allows by asking for
// two weeks (20160 minutes); ArcGIS silently clamps this down to the org's actual maximum.
const REQUESTED_EXPIRATION_MINUTES = 20_160;

export async function generateToken(portalUrl: string, username: string, password: string): Promise<GeneratedToken> {
    const endpoint = `${normalizePortal(portalUrl)}/sharing/rest/generateToken`;
    const body = new URLSearchParams({
        username,
        password,
        client: 'referer',
        referer: window.location.origin,
        expiration: String(REQUESTED_EXPIRATION_MINUTES),
        f: 'json',
    });

    const res = await fetch(endpoint, {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        body: body.toString(),
    });
    const json = await res.json() as { token?: string; expires?: number; error?: { message?: string; details?: string[] } };

    if (json.error) {
        throw new Error(json.error.message ?? 'Sign-in failed');
    }
    if (!json.token) {
        throw new Error('generateToken returned no token');
    }
    return { token: json.token, expiresAt: json.expires ?? Date.now() + 60 * 60 * 1000, username };
}
