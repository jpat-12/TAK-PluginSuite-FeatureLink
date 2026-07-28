// Deployment-specific constants.

export const DEFAULT_PORTAL_URL = 'https://www.arcgis.com';

// ArcGIS OAuth application client ID for this CloudTAK deployment. Reuses the same ArcGIS
// Application item as the ATAK plugin's CLIENT_ID (ArcGISAuthManager.java) — ArcGIS lets one
// app register multiple redirect URIs, so this deployment's origin (https://map.prod.ilwg.us/)
// was added to that app's Redirect URIs list alongside ATAK's `featurelink://auth`. A
// different CloudTAK deployment (different origin) would need its own redirect URI added to
// an app it has access to — either this same one, or its own newly-registered app.
export const ARCGIS_OAUTH_CLIENT_ID = 'RXtGmClVuYd1Sp7d';
