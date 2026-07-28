// Deployment-specific constants.

export const DEFAULT_PORTAL_URL = 'https://www.arcgis.com';

// ArcGIS OAuth application client ID. Reuses the same ArcGIS Application item as the ATAK
// plugin's CLIENT_ID (ArcGISAuthManager.java) — this works unmodified for every CloudTAK
// deployment (see ARCGIS_OAUTH_RELAY_URL below for how).
export const ARCGIS_OAUTH_CLIENT_ID = 'RXtGmClVuYd1Sp7d';

// Fixed OAuth redirect_uri, the same for every CloudTAK deployment. ArcGIS requires an exact
// pre-registered redirect URI per app (no wildcards) — rather than every CloudTAK hostname
// needing its own registration, they all redirect to this one static relay page
// (docs/featurelink-oauth-relay.html in this repo, published via GitHub Pages), which forwards
// the auth result back to whichever origin actually opened the sign-in popup. Sign-in only
// requires this ONE URI to ever be registered on the ArcGIS OAuth app, regardless of how many
// CloudTAK instances use it.
export const ARCGIS_OAUTH_RELAY_URL = 'https://jpat-12.github.io/TAK-PluginSuite-FeatureLink/featurelink-oauth-relay.html';
