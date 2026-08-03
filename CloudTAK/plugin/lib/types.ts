// Canonical data model — ported from the ATAK FeatureLink plugin's
// arcgis/ArcGISLayer.java and DisplayConfig.java.

export type LayerKind = 'private' | 'public';

// ArcGIS portal sharing scope for a "My ArcGIS Layers" item: 'public' (shared to Everyone),
// 'org', or 'private'. Only meaningful for layers returned by arcgisRest.searchUserLayers() —
// layers added by URL or received via a share default to 'org' since there's no portal item to
// ask. Mirrors ArcGISLayer.java's `access` field.
export type LayerAccess = 'public' | 'org' | 'private';

export interface ArcGISLayer {
    name: string;
    url: string;
    type: LayerKind;
    access: LayerAccess;
    // True only for a layer that came from the signed-in user's own "My ArcGIS Layers" browse
    // list (searchUserLayers) — never set for a layer added by pasting a URL or imported from a
    // share. Lets removePrivateLayer/removePublicLayer send it back to the browse list on
    // removal instead of just discarding it, since it's still the user's own ArcGIS item.
    ownedByMe: boolean;
    // Raw Esri geometryType of the layer ("esriGeometryPoint"/"esriGeometryPolyline"/
    // "esriGeometryPolygon"), resolved via fetchLayerInfo/fetchGeometryType and cached here so
    // it's only fetched once per layer. Empty until resolved — layerActions.downloadLayer()
    // lazily backfills it for layers added before this field existed.
    geometryType: string;
    featureCount: number;
    lastSync: number;               // epoch ms; 0 = never synced
    downloadEnabled: boolean;
    recurrenceInterval: number;     // 0 = auto-refresh disabled
    recurrenceUnit: 's' | 'min' | 'hr';
    isPliLayer: boolean;
    visible: boolean;
}

export function newLayer(name: string, url: string, type: LayerKind, access: LayerAccess = 'org', ownedByMe = false): ArcGISLayer {
    return {
        name, url, type, access, ownedByMe,
        geometryType: '',
        featureCount: 0,
        lastSync: 0,
        downloadEnabled: false,
        recurrenceInterval: 180,
        recurrenceUnit: 's',
        isPliLayer: false,
        visible: true,
    };
}

// Auto-refresh period in ms, or 0 if disabled. Ported from ArcGISLayer.recurrenceMillis() —
// the UI only ever writes "s" now, but "min"/"hr" values carried over from older saved layers
// must still resolve correctly.
export function recurrenceMillis(layer: ArcGISLayer): number {
    if (layer.recurrenceInterval <= 0) return 0;
    switch (layer.recurrenceUnit) {
        case 's':   return layer.recurrenceInterval * 1_000;
        case 'hr':  return layer.recurrenceInterval * 3_600_000;
        default:    return layer.recurrenceInterval * 60_000;
    }
}

// ── DisplayConfig — styling rules for a downloaded layer (ported from DisplayConfig.java) ──

export type SymOp = '=' | '!=' | 'contains' | 'startswith' | 'isempty' | 'isnotempty' | '>' | '<' | '>=' | '<=';

export interface SymValueEntry {
    v: string;
    c?: string;
    m?: 'shape' | 'icon';
    sh?: string;
    is?: string;      // legacy iconset name
    ic?: string;       // legacy icon filename
    up?: string | null; // server-resolved usericonPath, e.g. "<iconset-uid>/<group>/<file>"
}

export interface SymRuleEntry {
    f: string; o: SymOp; v: string;
    c?: string; sh?: string; m?: 'shape' | 'icon';
    is?: string; ic?: string; up?: string | null;
}

export interface SymClassBreak { mn: number; mx: number; c: string }

export interface SymConfig {
    t: 's' | 'uv' | 'rb' | 'cb' | 'ic' | 'adv';
    c?: string;   // fill/base color hex, default #3388ff
    oc?: string;  // outline color
    sz?: number;  // size px
    sh?: string;  // shape, default 'circle'
    op?: number;  // opacity 0-1
    f?: string;   // field driving uv/rb/cb/adv
    is?: string; ic?: string; up?: string | null;
    dc?: string;  // default color (rb)
    uv?: SymValueEntry[];
    vs?: SymValueEntry[];        // adv per-value entries
    rules?: SymRuleEntry[]; r?: SymRuleEntry[];
    cb?: SymClassBreak[];
}

export interface LabelConfig { f: string; sz?: number; c?: string; b?: boolean; i?: boolean }

export type PopupField = string | [string, string];
export interface PopupConfig { t?: string; flds?: PopupField[] }

export interface CotMapping {
    uf?: string | string[];
    tf?: string | string[];
    cf?: string | string[];
    rf?: string | string[];
}

// Stroke + fill styling for a polyline/polygon feature — only ever populated by the on-device
// auto-symbology path (autoIconset.ts's generateAutoIconset via displayConfig.ts's
// buildShapeStyleFields), never round-tripped through the QR/TAK-Portal "sym" schema, which has
// no polyline/polygon styling concept (yet). Ported from DisplayConfig.java's ShapeStyle.
export interface ShapeStyle {
    strokeColor: string;   // '#rrggbb'
    strokeOpacity: number; // 0-1
    strokeWidthPx: number;
    // Parsed from esriSLS for parity with the ATAK/WinTAK ports, but CloudTAK's own CoT bridge
    // (@tak-ps/node-cot's normalize_geojson) has no dasharray concept — see cot.ts's upsertShape,
    // this is carried through the model but currently has no renderable effect.
    strokeDash: 'solid' | 'dash' | 'dot';
    fillColor: string;
    fillOpacity: number; // 0 when fillStyle === 'none'
    fillStyle: 'solid' | 'none';
}

export interface DisplayConfig {
    v: number;
    url?: string;
    layer?: { name?: string; opacity?: number; visible?: boolean };
    sym?: SymConfig;
    lbl?: LabelConfig;
    popup?: PopupConfig;
    cm?: CotMapping;
    freq?: { iv: number; u: 's' | 'min' | 'hr' };
    // Driving field for shapeStyleByValue below; empty for a single-symbol renderer.
    shapeField?: string;
    // Stroke/fill style for a single-symbol renderer, or the fallback for a uniqueValue one.
    singleShapeStyle?: ShapeStyle;
    // uniqueValue renderer: field VALUE -> stroke/fill style.
    shapeStyleByValue?: Record<string, ShapeStyle>;
}

// ── Feature download (mirrors ArcGISRestClient.DownloadedFeature / CotFieldMapping) ──

export interface CotFieldMapping {
    uidFields: string[];
    typeFields: string[];
    callsignFields: string[];
    remarksFields: string[];
}

export interface DownloadedFeature {
    uid: string;
    cotType: string;
    callsign: string;
    remarks: string;
    lat: number; lon: number; hae: number;
    attributes: Record<string, string>;
    // Full polyline geometry: one inner array per part, each a [lon,lat] vertex in order.
    // Empty for point/polygon features.
    paths: number[][][];
    // Full polygon geometry: one inner array per ring (first ring is the outer boundary,
    // subsequent rings are holes per Esri's ring-orientation convention), each a [lon,lat]
    // vertex in order. Empty for point/polyline features.
    rings: number[][][];
}

// ── PLI / send-to-layer payload (mirrors ArcGISRestClient.buildPliAttributes) ──

export interface PliFeatureInput {
    uid: string;
    cotType: string;
    callsign: string;
    iconPath: string;
    remarks: string;
    how: string;
    sentByUser: string;
    groupName: string;
    groupRole: string;
    lat: number; lon: number; hae: number; ce: number; le: number;
    timeMs: number; startMs: number; staleMs: number;
    rawCotXml: string;
}

// ── Generic operational JSON payload types (kept for paste/upload import — the ATAK
// version's QR "credentials"/"pli_config" variants carried a plaintext ArcGIS password,
// which doesn't fit this plugin's OAuth-only sign-in; dropped rather than ported). ──

export type OperationalPayload =
    | { v: 1; type: 'pli_endpoint'; url: string }
    | { v: 1; type: 'layer_config'; url: string; name?: string; private?: boolean };
