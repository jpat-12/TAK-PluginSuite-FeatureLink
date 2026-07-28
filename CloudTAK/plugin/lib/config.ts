// Deployment-specific constants.

export const DEFAULT_PORTAL_URL = 'https://www.arcgis.com';

// ArcGIS OAuth application client ID for this CloudTAK deployment. Unlike the ATAK plugin
// (which uses a custom `featurelink://auth` URI scheme redirect that works from any device),
// a web deployment's OAuth redirect_uri is this origin's URL — and ArcGIS only allows
// pre-registered redirect URIs per app, so each CloudTAK deployment needs its own registered
// ArcGIS OAuth application (Content > New Item > Application, type "Web App", redirect URI =
// this origin's URL) with its client ID pasted in here.
export const ARCGIS_OAUTH_CLIENT_ID = '';
