// Reactive token invalidation — the CloudTAK half of the defect field-diagnosed on ATAK.
//
// arcgisAuth's expiry clock is only an ESTIMATE derived from `expires_in` at issue time. When
// ArcGIS rejects a token EARLIER than that estimate (revocation, portal-side invalidation, device
// clock drift), nothing used to tell arcgisAuth about it, so getToken() kept handing back the same
// dead token until its own clock caught up — every request in between came back 498/499 and the
// operator saw a session that "kept expiring" no matter how often they signed in.

import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { FakeArcGIS, LAYER_0 } from './fixtures.ts';

const realFetch = globalThis.fetch;

async function freshAuth(): Promise<typeof import('../lib/arcgisAuth.ts')> {
    return import('../lib/arcgisAuth.ts');
}

beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
});

afterEach(() => { globalThis.fetch = realFetch; });

/**
 * ArcGIS reports an invalid token as HTTP 200 + an `error` envelope — the whole point of C-22.
 * Scoped to /query so it cannot also answer the OAuth token endpoint.
 */
function routeTokenError(fake: FakeArcGIS, code: number): void {
    fake.route((u) => u.pathname.endsWith('/query') ? { body: { error: { code, message: 'Invalid token.' } } } : null);
}

describe('498/499 drops the cached access token', () => {
    for (const code of [498, 499]) {
        it(`error ${code} on a tokened request makes the next getToken() refresh`, async () => {
            const auth = await freshAuth();
            const { arcgisJson, ArcGISError } = await import('../lib/arcgisHttp.ts');

            auth.__setSessionForTest({ username: 'op', accessToken: 'dead-token', expiresInMs: 3_600_000, refreshToken: null });
            // The estimate says this token is good for another hour.
            expect(await auth.getToken()).toBe('dead-token');

            const fake = new FakeArcGIS();
            routeTokenError(fake, code);
            fake.install();

            await expect(arcgisJson(`${LAYER_0}/query`, { token: 'dead-token', portalUrl: 'https://www.arcgis.com' }))
                .rejects.toThrow(ArcGISError);

            // The cached token is gone, so the next call cannot re-present it. There is no refresh
            // token here, so getToken() reports "no usable session" rather than the dead token.
            expect(await auth.getToken()).toBeNull();
        });
    }

    it('a transport 401 counts too — an Enterprise proxy rejects the bearer before the envelope', async () => {
        const auth = await freshAuth();
        const { arcgisJson } = await import('../lib/arcgisHttp.ts');

        auth.__setSessionForTest({ username: 'op', accessToken: 'dead-token', refreshToken: null });
        const fake = new FakeArcGIS();
        fake.route((u) => u.pathname.endsWith('/query') ? { status: 401, body: { error: 'unauthorized' } } : null);
        fake.install();

        await expect(arcgisJson(`${LAYER_0}/query`, { token: 'dead-token', portalUrl: 'https://www.arcgis.com' }))
            .rejects.toMatchObject({ kind: 'auth' });
        expect(await auth.getToken()).toBeNull();
    });

    it('an expired token is refreshed silently once invalidated, not surfaced as a sign-out', async () => {
        const auth = await freshAuth();
        const { arcgisJson } = await import('../lib/arcgisHttp.ts');

        auth.__setSessionForTest({ username: 'op', accessToken: 'dead-token', refreshToken: 'refresh-1' });

        const fake = new FakeArcGIS();
        routeTokenError(fake, 498);
        // The refresh endpoint arcgisAuth reaches for via oauth.refreshAccessToken.
        fake.route((u) => u.pathname.includes('/oauth2/token')
            ? { body: { access_token: 'fresh-token', expires_in: 3600, refresh_token: 'refresh-2', username: 'op' } }
            : null);
        fake.install();

        await expect(arcgisJson(`${LAYER_0}/query`, { token: 'dead-token', portalUrl: 'https://www.arcgis.com' })).rejects.toThrow();

        expect(await auth.getToken()).toBe('fresh-token');
        expect(auth.isAuthenticated()).toBe(true);
        expect(auth.authState.expiredMessage).toBe('');
    });
});

describe('invalidation is scoped to requests that actually carried the token', () => {
    it('a 403 on an UNTOKENED public-layer request leaves the session alone', async () => {
        const auth = await freshAuth();
        const { arcgisJson } = await import('../lib/arcgisHttp.ts');

        auth.__setSessionForTest({ username: 'op', accessToken: 'good-token', refreshToken: null });

        const fake = new FakeArcGIS();
        routeTokenError(fake, 403);
        fake.install();

        // No token supplied — this is the public-layer path. A 403 here means "that layer is not
        // public", not "your token is dead"; dropping the token would force a pointless refresh.
        await expect(arcgisJson(`${LAYER_0}/query`, {})).rejects.toMatchObject({ kind: 'auth' });
        expect(await auth.getToken()).toBe('good-token');
    });

    it('a token dropped for an untrusted host cannot invalidate the session either', async () => {
        const auth = await freshAuth();
        const { arcgisJson } = await import('../lib/arcgisHttp.ts');

        auth.__setSessionForTest({ username: 'op', accessToken: 'good-token', refreshToken: null });

        const fake = new FakeArcGIS();
        routeTokenError(fake, 498);
        fake.install();

        // authHeaders() refuses to attach the token off the portal's own deployment (C-33), so the
        // 498 says nothing about our token's validity.
        await expect(arcgisJson('https://evil.example.com/rest/services/x/0/query', {
            token: 'good-token', portalUrl: 'https://www.arcgis.com',
        })).rejects.toThrow();
        expect(await auth.getToken()).toBe('good-token');
    });

    it('a non-auth ArcGIS error never touches the token', async () => {
        const auth = await freshAuth();
        const { arcgisJson } = await import('../lib/arcgisHttp.ts');

        auth.__setSessionForTest({ username: 'op', accessToken: 'good-token', refreshToken: null });

        const fake = new FakeArcGIS();
        routeTokenError(fake, 500);
        fake.install();

        await expect(arcgisJson(`${LAYER_0}/query`, { token: 'good-token', portalUrl: 'https://www.arcgis.com' })).rejects.toThrow();
        expect(await auth.getToken()).toBe('good-token');
    });
});
