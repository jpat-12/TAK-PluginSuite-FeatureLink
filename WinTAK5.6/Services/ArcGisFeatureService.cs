using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FeatureLink.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FeatureLink.Services
{
    /// <summary>Layer metadata plus the renderer needed to resolve symbology (C-07).</summary>
    public sealed class LayerInfo
    {
        public string Name { get; set; }
        public string GeometryType { get; set; }
        public int MaxRecordCount { get; set; }
        public string ObjectIdField { get; set; }

        /// <summary>The layer's own <c>displayField</c> — the attribute ArcGIS itself uses to
        /// label a feature, and what its pop-ups title on. Every hosted feature layer declares
        /// one, so it is the correct source for a marker callsign and far better than a
        /// positional "Feature-N".</summary>
        public string DisplayField { get; set; }
        /// <summary>The layer's <c>drawingInfo.renderer</c>. This was previously read and thrown
        /// away, which is the whole of C-07 on this platform.</summary>
        public JObject Renderer { get; set; }
        public JObject Raw { get; set; }
    }

    /// <summary>Result of a paged feature download (C-06). <see cref="Truncated"/> is true when the
    /// service stopped returning pages before the layer was exhausted — the UI must never present
    /// a truncated download as a complete one.</summary>
    public sealed class LayerDownloadResult
    {
        public List<DownloadedFeature> Features { get; } = new List<DownloadedFeature>();
        public bool Truncated { get; set; }
        public int SkippedFeatures { get; set; }
        public int PagesFetched { get; set; }
        public int? SpatialReferenceWkid { get; set; }
        public int OutOfRangeCoordinates { get; set; }
    }

    /// <summary>Result of a paged portal search (C-06, search half).</summary>
    public sealed class LayerSearchResult
    {
        public List<ArcGisLayer> Layers { get; } = new List<ArcGisLayer>();
        public bool Truncated { get; set; }
        public int Total { get; set; }
    }

    /// <summary>
    /// ArcGIS REST API client — ported from the ATAK plugin's <c>ArcGISRestClient.java</c>.
    ///
    /// Audit remediation applied here:
    ///  • <b>C-06</b> — <c>resultOffset</c>/<c>resultRecordCount</c> paging driven by the service's
    ///    own <c>maxRecordCount</c> and by <c>exceededTransferLimit</c>, for both the feature query
    ///    and the portal search. Previously a layer over ~2000 features silently synced only its
    ///    first page and reported success.
    ///  • <b>C-22</b> — every parse goes through <see cref="ArcGisHttp"/>, which checks the HTTP
    ///    status <b>and</b> the <c>{"error":{…}}</c> body ArcGIS returns with HTTP 200.
    ///  • <b>C-21</b> — the token travels in an <c>X-Esri-Authorization: Bearer</c> header, never
    ///    in the query string, and never reaches a log line.
    ///  • <b>C-07</b> — <see cref="FetchLayerInfoAsync"/> now surfaces <c>drawingInfo.renderer</c>
    ///    instead of discarding it.
    ///  • Spatial reference is asserted, coordinates are range-checked, attribute values are
    ///    stringified with <see cref="CultureInfo.InvariantCulture"/>, and feature uids are
    ///    derived deterministically so a re-sync updates markers in place instead of churning them.
    ///
    /// The <see cref="HttpMessageHandler"/> is injectable purely so the test suite can drive every
    /// one of these paths without a network.
    /// </summary>
    public sealed class ArcGisFeatureService
    {
        private const int DefaultPageSize = 1000;
        private const int MaxPages = 5000;         // 5M features at the default page size
        private const int SearchPageSize = 100;
        private const int MaxSearchPages = 50;

        private static readonly HttpClient SharedHttp = CreateClient(null);

        private readonly HttpClient _http;
        private readonly Dictionary<string, string> _objectIdFieldCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _cacheLock = new object();

        public ArcGisFeatureService() { _http = SharedHttp; }

        /// <summary>Test seam — supply a stub handler to exercise paging/error handling offline.</summary>
        public ArcGisFeatureService(HttpMessageHandler handler) { _http = CreateClient(handler); }

        private static HttpClient CreateClient(HttpMessageHandler handler)
        {
            ArcGisHttp.EnsureTlsConfigured();
            var client = handler == null ? new HttpClient() : new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(30);
            return client;
        }

        // -------------------------------------------------------------------------
        // Layer search
        // -------------------------------------------------------------------------

        /// <summary>Searches the portal for Feature Services owned by the authenticated user,
        /// paging through <c>start</c>/<c>nextStart</c> rather than taking only the first 100
        /// results (C-06). Throws <see cref="ArcGisServiceException"/> rather than returning a
        /// partially populated list presented as complete.</summary>
        public async Task<LayerSearchResult> SearchUserLayersAsync(string portalUrl, string token, string username,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var result = new LayerSearchResult();
            // The username is server-supplied but not necessarily benign in a federated/Portal
            // deployment: an embedded quote or " AND " previously altered the query semantics.
            string q = "type:\"Feature Service\" AND owner:" + QuoteSearchTerm(username);
            string baseEndpoint = NormalizePortal(portalUrl) + "/sharing/rest/search?q=" + Uri.EscapeDataString(q)
                + "&num=" + SearchPageSize.ToString(CultureInfo.InvariantCulture) + "&f=json&start=";

            int start = 1;
            for (int page = 0; page < MaxSearchPages; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var json = await ArcGisHttp.GetJsonAsync(_http,
                    baseEndpoint + start.ToString(CultureInfo.InvariantCulture), token, cancellationToken)
                    .ConfigureAwait(false);

                result.Total = (int?)json["total"] ?? result.Total;
                if (!(json["results"] is JArray results) || results.Count == 0) return result;

                foreach (var item in results)
                {
                    if (!(item is JObject obj)) continue;
                    string url = (string)obj["url"] ?? string.Empty;
                    if (string.IsNullOrEmpty(url)) continue;
                    result.Layers.Add(new ArcGisLayer(
                        UrlGuard.SanitizeDisplayName((string)obj["title"] ?? "Unnamed"), url, "private")
                    {
                        // Portal item "access": "public" (shared to Everyone), "org", or "private".
                        Access = (string)obj["access"] ?? "private",
                        // Kept so symbology saved on the item's Visualization tab can be found —
                        // that styling never reaches the feature service. See FetchItemRendererAsync.
                        ItemId = (string)obj["id"],
                    });
                }

                int nextStart = (int?)json["nextStart"] ?? -1;
                if (nextStart <= 0 || nextStart == start) return result;
                start = nextStart;
            }

            result.Truncated = true;
            Log.Warn($"Portal search stopped after {MaxSearchPages} pages ({result.Layers.Count} items).");
            return result;
        }

        /// <summary>Quotes and escapes a term for an ArcGIS search query so embedded quotes cannot
        /// change the query's meaning.</summary>
        internal static string QuoteSearchTerm(string term)
        {
            if (string.IsNullOrEmpty(term)) return "\"\"";
            return "\"" + term.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        // -------------------------------------------------------------------------
        // Feature count
        // -------------------------------------------------------------------------

        public async Task<long> QueryFeatureCountAsync(string serviceUrl, string token,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            string layerUrl = EnsureLayerIndex(serviceUrl);
            var json = await ArcGisHttp.GetJsonAsync(_http,
                layerUrl + "/query?where=1%3D1&returnCountOnly=true&f=json", token, cancellationToken)
                .ConfigureAwait(false);
            var count = (long?)json["count"];
            if (count == null)
                throw new ArcGisServiceException(
                    "The service did not return a feature count — the URL may not be a queryable layer.");
            return count.Value;
        }

        /// <summary>
        /// Fetches the renderer an operator configured on the portal item's <b>Visualization</b>
        /// tab, which is NOT the same document as the service's own renderer.
        ///
        /// <para>ArcGIS stores item-level styling at
        /// <c>/sharing/rest/content/items/{itemId}/data</c> under
        /// <c>layers[].layerDefinition.drawingInfo.renderer</c>, and saving it does not modify the
        /// feature service at all. A layer published with one default symbol and then styled by
        /// unique value in the web UI therefore still reports a <c>simple</c> renderer at
        /// <c>{serviceUrl}/{layerId}?f=json</c> — which is exactly why such a layer rendered as one
        /// repeated marker here while ArcGIS showed a dozen distinct symbols.</para>
        ///
        /// <para>Returns null when the item has no data, no layer override, or cannot be read —
        /// all of which are entirely normal. The caller falls back to the service renderer, so
        /// this is logged at info, never as a failure.</para>
        /// </summary>
        /// <param name="layerId">Sublayer index to match within <c>layers[]</c>; -1 accepts the
        /// first override found.</param>
        public async Task<JObject> FetchItemRendererAsync(string portalUrl, string itemId, int layerId,
            string token, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrEmpty(itemId)) return null;

            string url = NormalizePortal(portalUrl) + "/sharing/rest/content/items/"
                       + Uri.EscapeDataString(itemId) + "/data?f=json";
            try
            {
                var data = await ArcGisHttp.GetJsonAsync(_http, url, token, cancellationToken)
                    .ConfigureAwait(false);

                if (!(data?["layers"] is JArray layers)) return null;

                foreach (var entry in layers.OfType<JObject>())
                {
                    if (layerId >= 0)
                    {
                        int? id = (int?)entry["id"];
                        if (id.HasValue && id.Value != layerId) continue;
                    }

                    var renderer = (entry["layerDefinition"] as JObject)?["drawingInfo"] as JObject;
                    var resolved = renderer?["renderer"] as JObject;
                    if (resolved != null) return resolved;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // An item with no /data, or no read access to it, is normal — not a failure.
                Log.Info($"No item-level renderer for item {itemId}: {ex.Message}");
            }
            return null;
        }

        // ---------------------------------------------------------------------
        // Sublayer enumeration (C-08)
        // ---------------------------------------------------------------------

        /// <summary>One queryable sublayer of a FeatureServer or MapServer.</summary>
        public sealed class SubLayerRef
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string GeometryType { get; set; }

            /// <summary>Fully-qualified URL, <c>&lt;serviceRoot&gt;/&lt;id&gt;</c>. This, not the
            /// service root, is what an <see cref="ArcGisLayer"/> is keyed by — so every existing
            /// URL-keyed structure (display configs, iconset uids, extents, the marker map, the
            /// dedupe checks) keeps working with no change at all.</summary>
            public string Url { get; set; }

            /// <summary>What the picker shows: the layer's own name, with its geometry as a hint
            /// so an operator can tell a point layer from the lines drawn over it.</summary>
            public string Display
            {
                get
                {
                    string name = string.IsNullOrWhiteSpace(Name) ? "Layer " + Id : Name;
                    string kind = FriendlyGeometry(GeometryType);
                    return kind == null ? name : name + "  (" + kind + ")";
                }
            }

            internal static string FriendlyGeometry(string esri)
            {
                if (string.IsNullOrEmpty(esri)) return null;
                switch (esri)
                {
                    case "esriGeometryPoint": return "points";
                    case "esriGeometryMultipoint": return "points";
                    case "esriGeometryPolyline": return "lines";
                    case "esriGeometryPolygon": return "areas";
                    default: return null;
                }
            }
        }

        /// <summary>
        /// Lists the sublayers of a FeatureServer, so a multi-layer service becomes N selectable
        /// layers instead of silently collapsing to layer 0.
        ///
        /// <para><b>The defect this closes.</b> <see cref="EnsureLayerIndex"/> appends <c>/0</c> to
        /// a bare service root and nothing ever enumerated the rest, so adding a five-layer
        /// FeatureServer gave the operator one layer with no error, no warning and nothing in the
        /// log. It is silent data loss in the plugin's core function, and it is the likeliest
        /// explanation for two field reports: "most of the layers weren't being downloaded", and a
        /// line layer reporting zero features.</para>
        ///
        /// <para>A URL already addressing a sublayer returns exactly that one, so pasting
        /// <c>…/FeatureServer/3</c> still means layer 3. A service that advertises no
        /// <c>layers</c> array, or a request that fails, returns an EMPTY list and the caller
        /// falls back to the legacy <c>/0</c> — never drop the layer because enumeration did not
        /// work.</para>
        /// </summary>
        public async Task<List<SubLayerRef>> ListSubLayersAsync(string serviceUrl, string token,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            string url = CanonicalServiceUrl(serviceUrl);
            if (string.IsNullOrEmpty(url)) return new List<SubLayerRef>();

            // Already pointed at one sublayer: fetch its metadata so the picker can name it, but
            // never widen the operator's explicit choice to the whole service.
            if (LayerIndexOf(url) >= 0)
            {
                var single = new SubLayerRef
                {
                    Id = LayerIndexOf(url),
                    Url = url,
                    Name = string.Empty,
                    GeometryType = string.Empty,
                };
                try
                {
                    var meta = await ArcGisHttp.GetJsonAsync(_http, url + "?f=json", token,
                        cancellationToken).ConfigureAwait(false);
                    single.Name = (string)meta["name"] ?? string.Empty;
                    single.GeometryType = (string)meta["geometryType"] ?? string.Empty;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // A name is cosmetic; the url is what matters and we already have it.
                    Log.Info("Could not read metadata for " + Log.Redact(url) + ": " + ex.Message);
                }
                return new List<SubLayerRef> { single };
            }

            if (!IsServiceRoot(url)) return new List<SubLayerRef>();

            JObject root;
            try
            {
                root = await ArcGisHttp.GetJsonAsync(_http, url + "?f=json", token, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn("Could not list sublayers of " + Log.Redact(url) + ": " + ex.Message);
                return new List<SubLayerRef>();
            }

            return ParseSubLayers(root, url);
        }

        /// <summary>The parsing half of <see cref="ListSubLayersAsync"/>, split out so the rules
        /// below are tested against fixtures rather than against a live service.</summary>
        internal static List<SubLayerRef> ParseSubLayers(JObject root, string serviceRootUrl)
        {
            var found = new List<SubLayerRef>();
            if (root == null || string.IsNullOrEmpty(serviceRootUrl)) return found;

            string baseUrl = serviceRootUrl.TrimEnd('/');
            var layers = root["layers"] as JArray;
            if (layers == null) return found;

            foreach (var entry in layers.OfType<JObject>())
            {
                // A GROUP layer has subLayerIds, no geometry of its own, and cannot be queried.
                // Offering it would give the operator a row that can only ever fail to download.
                var groupChildren = entry["subLayerIds"];
                if (groupChildren != null && groupChildren.Type != JTokenType.Null) continue;

                int? id = SubLayerId(entry["id"]);
                if (id == null || id.Value < 0) continue;

                found.Add(new SubLayerRef
                {
                    Id = id.Value,
                    Name = (string)entry["name"] ?? string.Empty,
                    GeometryType = (string)entry["geometryType"] ?? string.Empty,
                    Url = baseUrl + "/" + id.Value.ToString(CultureInfo.InvariantCulture),
                });
            }

            return found;
        }

        /// <summary>A layer id from the service metadata, or null when it is absent or not a
        /// number. ArcGIS has been seen to send these as both JSON numbers and strings.</summary>
        private static int? SubLayerId(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.Integer) return (int)token;

            int parsed;
            return int.TryParse(token.ToString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out parsed) ? parsed : (int?)null;
        }

        /// <summary>True when the URL ends at a FeatureServer/MapServer root with no layer id.</summary>
        internal static bool IsServiceRoot(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            string trimmed = url.TrimEnd('/');
            return trimmed.EndsWith("FeatureServer", StringComparison.OrdinalIgnoreCase)
                || trimmed.EndsWith("MapServer", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Strips a query string and trailing slashes, leaving the bare service or layer
        /// URL. Shared by enumeration and <see cref="EnsureLayerIndex"/> so the two can never
        /// disagree about what a URL points at.</summary>
        internal static string CanonicalServiceUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return string.Empty;
            string trimmed = url.Trim().TrimEnd('/');
            int query = trimmed.IndexOf('?');
            if (query >= 0) trimmed = trimmed.Substring(0, query).TrimEnd('/');
            return trimmed;
        }

        /// <summary>The sublayer index a canonical layer URL addresses, or -1 when it has none.
        /// Used to pick the right entry out of an item's <c>layers[]</c> override list.</summary>
        public static int LayerIndexOf(string layerUrl)
        {
            if (string.IsNullOrEmpty(layerUrl)) return -1;
            string trimmed = layerUrl.TrimEnd('/');
            int slash = trimmed.LastIndexOf('/');
            if (slash < 0 || slash == trimmed.Length - 1) return -1;

            string tail = trimmed.Substring(slash + 1);
            int parsed;
            return int.TryParse(tail, System.Globalization.NumberStyles.None,
                CultureInfo.InvariantCulture, out parsed) ? parsed : -1;
        }

        // -------------------------------------------------------------------------
        // Layer info (+ renderer — C-07)
        // -------------------------------------------------------------------------

        /// <summary>Fetches a layer's metadata, <b>including its <c>drawingInfo.renderer</c></b>.
        /// Throws <see cref="ArcGisServiceException"/> with an actionable reason instead of
        /// collapsing every failure (typo, 401, timeout, non-ArcGIS host) into a bare null.</summary>
        public async Task<LayerInfo> FetchLayerMetadataAsync(string serviceUrl, string token = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            string url = EnsureLayerIndex(serviceUrl);
            var json = await ArcGisHttp.GetJsonAsync(_http, url + "?f=json", token, cancellationToken)
                .ConfigureAwait(false);

            var info = new LayerInfo
            {
                Name = (string)json["name"] ?? (string)json["serviceDescription"] ?? "Unknown Layer",
                GeometryType = (string)json["geometryType"] ?? string.Empty,
                MaxRecordCount = (int?)json["maxRecordCount"] ?? DefaultPageSize,
                ObjectIdField = (string)json["objectIdField"] ?? "OBJECTID",
                DisplayField = (string)json["displayField"],
                Renderer = (json["drawingInfo"] as JObject)?["renderer"] as JObject,
                Raw = json,
            };
            if (info.MaxRecordCount <= 0) info.MaxRecordCount = DefaultPageSize;

            lock (_cacheLock) _objectIdFieldCache[url] = info.ObjectIdField;
            return info;
        }

        /// <summary>Convenience wrapper returning an <see cref="ArcGisLayer"/> for the Add-Layer /
        /// share-import paths. Never returns null — it throws a typed error the caller surfaces.</summary>
        public async Task<ArcGisLayer> FetchLayerInfoAsync(string serviceUrl, string token = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var info = await FetchLayerMetadataAsync(serviceUrl, token, cancellationToken).ConfigureAwait(false);
            return new ArcGisLayer(UrlGuard.SanitizeDisplayName(info.Name), serviceUrl, "public");
        }

        // -------------------------------------------------------------------------
        // Feature download — paged (C-06)
        // -------------------------------------------------------------------------

        /// <summary>
        /// Downloads <b>all</b> features from a layer as CoT-ready value objects, paging on
        /// <c>resultOffset</c>/<c>resultRecordCount</c> until the service stops setting
        /// <c>exceededTransferLimit</c> and returns a short page. Supports point, polyline and
        /// polygon geometry (polyline/polygon uses the first vertex, the same simplification the
        /// ATAK plugin makes).
        /// </summary>
        /// <param name="displayField">The layer's own <c>displayField</c>, used to name each
        /// marker. Null falls back to the positional "Feature-N".</param>
        public async Task<LayerDownloadResult> DownloadLayerAsCotAsync(string serviceUrl, string token,
            int? pageSizeOverride = null, CancellationToken cancellationToken = default(CancellationToken),
            string displayField = null)
        {
            var result = new LayerDownloadResult();
            string layerUrl = EnsureLayerIndex(serviceUrl);

            int pageSize = pageSizeOverride ?? DefaultPageSize;
            if (pageSizeOverride == null)
            {
                try
                {
                    var meta = await FetchLayerMetadataAsync(serviceUrl, token, cancellationToken).ConfigureAwait(false);
                    pageSize = Math.Max(1, meta.MaxRecordCount);
                }
                catch (ArcGisServiceException ex)
                {
                    // Metadata is an optimisation for the page size; a failure here must not stop
                    // the download, but it is worth a log line (it never used to produce one).
                    Log.Warn("Could not read the layer's maxRecordCount, using the default page size: " + ex.Message);
                }
            }

            string uidSalt = LayerUidSalt(layerUrl);

            // Shared across every page of the download, so two features on different pages cannot
            // be issued the same uid, and so the tier tally covers the whole layer.
            var takenUids = new HashSet<string>(StringComparer.Ordinal);
            var uidTally = new Dictionary<UidSource, int>();

            int offset = 0;
            int page = 0;

            while (page < MaxPages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string query = layerUrl + "/query?where=1%3D1&outFields=*&outSR=4326&returnGeometry=true&f=json"
                    + "&resultOffset=" + offset.ToString(CultureInfo.InvariantCulture)
                    + "&resultRecordCount=" + pageSize.ToString(CultureInfo.InvariantCulture);

                var json = await ArcGisHttp.GetJsonAsync(_http, query, token, cancellationToken).ConfigureAwait(false);
                page++;
                result.PagesFetched = page;

                int? wkid = ReadWkid(json["spatialReference"] as JObject);
                if (wkid.HasValue)
                {
                    result.SpatialReferenceWkid = wkid;
                    // outSR=4326 was requested; some services ignore it for certain geometry types
                    // and return Web Mercator metres, which the reader would otherwise interpret
                    // as degrees and plot as garbage with no error at all.
                    if (wkid.Value != 4326 && wkid.Value != 4269)
                        throw new ArcGisServiceException(
                            $"The service returned coordinates in spatial reference {wkid.Value} despite "
                            + "outSR=4326 being requested. Refusing to plot them as latitude/longitude.");
                }

                if (!(json["features"] is JArray features))
                    throw new ArcGisServiceException(
                        "The service response contained no \"features\" array — the URL may not be a queryable layer.");

                foreach (var feat in features)
                {
                    var parsed = ParseFeature(feat as JObject, uidSalt, result.Features.Count,
                                              displayField, takenUids, uidTally);
                    if (parsed == null) { result.SkippedFeatures++; continue; }
                    if (!IsPlottable(parsed.Lat, parsed.Lon)) { result.OutOfRangeCoordinates++; continue; }
                    result.Features.Add(parsed);
                }

                bool exceeded = (bool?)json["exceededTransferLimit"] ?? false;
                if (!exceeded && features.Count < pageSize) break;
                if (features.Count == 0) break;

                offset += features.Count;
            }

            if (page >= MaxPages)
            {
                result.Truncated = true;
                Log.Warn($"Layer download stopped at the {MaxPages}-page safety limit "
                         + $"({result.Features.Count} features).");
            }

            if (result.SkippedFeatures > 0 || result.OutOfRangeCoordinates > 0)
                Log.Warn($"Layer download skipped {result.SkippedFeatures} malformed and "
                         + $"{result.OutOfRangeCoordinates} out-of-range features.");

            LogUidTiers(layerUrl, uidTally);

            return result;
        }

        private static int? ReadWkid(JObject spatialReference)
        {
            if (spatialReference == null) return null;
            return (int?)spatialReference["wkid"] ?? (int?)spatialReference["latestWkid"];
        }

        internal static bool IsPlottable(double lat, double lon) =>
            !double.IsNaN(lat) && !double.IsNaN(lon)
            && !double.IsInfinity(lat) && !double.IsInfinity(lon)
            && lat >= -90 && lat <= 90 && lon >= -180 && lon <= 180;

        private static DownloadedFeature ParseFeature(JObject feat, string uidSalt, int index,
            string displayField, ISet<string> takenUids, IDictionary<UidSource, int> uidTally)
        {
            if (feat == null) return null;
            try
            {
                var attrs = feat["attributes"] as JObject;
                var geom = feat["geometry"] as JObject;
                if (geom == null) return null;

                double lat, lon;
                string geometryKind;
                if (!TryReadCoordinates(geom, out lon, out lat, out geometryKind)) return null;

                var attrMap = new Dictionary<string, string>(StringComparer.Ordinal);
                string uid = null, cotType = null, callsign = null, remarks = null;
                double hae = double.NaN;

                if (attrs != null)
                {
                    foreach (var prop in attrs.Properties())
                    {
                        if (prop.Value == null || prop.Value.Type == JTokenType.Null) continue;
                        attrMap[prop.Name] = StringifyInvariant(prop.Value);
                    }

                    uid = GetString(attrs, "uid");
                    cotType = GetString(attrs, "cot_type");
                    callsign = GetString(attrs, "tak_callsign");
                    remarks = GetString(attrs, "tak_remarks");
                    hae = TryDouble(attrs["hae"]);
                }

                UidSource uidSource = UidSource.Declared;
                if (string.IsNullOrEmpty(uid))
                {
                    uid = DeriveStableUid(uidSalt, attrs, geom, index, lat, lon,
                                          takenUids, out uidSource);
                }
                else if (takenUids != null)
                {
                    takenUids.Add(uid);
                }

                if (uidTally != null)
                {
                    int seen;
                    uidTally[uidSource] = uidTally.TryGetValue(uidSource, out seen) ? seen + 1 : 1;
                }
                if (string.IsNullOrEmpty(cotType))
                    cotType = string.Empty; // the caller applies the "unknown" fallback + validation
                if (string.IsNullOrEmpty(callsign))
                    callsign = DeriveCallsign(attrMap, displayField, index);

                return new DownloadedFeature(uid, cotType, callsign, remarks ?? string.Empty,
                    lat, lon, hae, attrMap, geometryKind);
            }
            catch (Exception ex)
            {
                Log.Warn("Skipping a malformed feature: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Names a marker from the layer's own data rather than its position in the result set.
        ///
        /// <para>Order: the explicit <c>tak_callsign</c> column if the layer carries one (handled
        /// by the caller), then the layer's declared <c>displayField</c> — which is precisely what
        /// ArcGIS itself labels features with and titles pop-ups on, so it is what the operator
        /// already recognises — then a short list of conventional name columns for layers whose
        /// <c>displayField</c> is unhelpful (it often defaults to <c>OBJECTID</c>), and only then
        /// the positional fallback.</para>
        ///
        /// <para>A <c>displayField</c> pointing at the object-id column is deliberately skipped:
        /// "1", "2", "3" is no more meaningful than "Feature-1" and loses the hint that the name
        /// is a placeholder.</para>
        /// </summary>
        internal static string DeriveCallsign(IReadOnlyDictionary<string, string> attrs,
            string displayField, int index)
        {
            string value;

            if (!string.IsNullOrEmpty(displayField)
                && !LooksLikeObjectId(displayField)
                && TryAttr(attrs, displayField, out value))
                return value;

            foreach (string candidate in ConventionalNameFields)
                if (TryAttr(attrs, candidate, out value))
                    return value;

            return "Feature-" + index.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Conventional name columns, in preference order, for layers whose
        /// <c>displayField</c> is missing or points at the object id.</summary>
        private static readonly string[] ConventionalNameFields =
        {
            "name", "Name", "NAME", "title", "TITLE", "label", "LABEL",
            "callsign", "CALLSIGN", "description", "DESCRIPTION",
        };

        private static bool LooksLikeObjectId(string field) =>
            field.Equals("OBJECTID", StringComparison.OrdinalIgnoreCase)
            || field.Equals("FID", StringComparison.OrdinalIgnoreCase)
            || field.Equals("OID", StringComparison.OrdinalIgnoreCase)
            || field.Equals("ObjectId", StringComparison.OrdinalIgnoreCase);

        /// <summary>Case-insensitive attribute lookup that rejects blank and null-ish values —
        /// ArcGIS writes a literal "&lt;Null&gt;" often enough to be worth excluding, and naming a
        /// marker that would be worse than the positional fallback.</summary>
        private static bool TryAttr(IReadOnlyDictionary<string, string> attrs, string field, out string value)
        {
            value = null;
            if (attrs == null || string.IsNullOrEmpty(field)) return false;

            foreach (var kv in attrs)
            {
                if (!string.Equals(kv.Key, field, StringComparison.OrdinalIgnoreCase)) continue;
                string candidate = kv.Value?.Trim();
                if (string.IsNullOrEmpty(candidate)) return false;
                if (candidate.Equals("<Null>", StringComparison.OrdinalIgnoreCase)) return false;
                if (candidate.Equals("null", StringComparison.OrdinalIgnoreCase)) return false;
                value = candidate;
                return true;
            }
            return false;
        }

        private static bool TryReadCoordinates(JObject geom, out double lon, out double lat, out string kind)
        {
            lon = double.NaN; lat = double.NaN; kind = "point";

            if (geom["x"] != null && geom["y"] != null)
            {
                lon = TryDouble(geom["x"]);
                lat = TryDouble(geom["y"]);
                kind = "point";
                return !double.IsNaN(lat) && !double.IsNaN(lon);
            }

            var paths = geom["paths"] as JArray;
            if (paths != null)
            {
                kind = "polyline";
                return TryFirstVertex(paths, out lon, out lat);
            }

            var rings = geom["rings"] as JArray;
            if (rings != null)
            {
                kind = "polygon";
                return TryFirstVertex(rings, out lon, out lat);
            }

            var points = geom["points"] as JArray;
            if (points != null && points.Count > 0)
            {
                kind = "multipoint";
                var pt = points[0] as JArray;
                if (pt != null && pt.Count >= 2)
                {
                    lon = TryDouble(pt[0]);
                    lat = TryDouble(pt[1]);
                    return !double.IsNaN(lat) && !double.IsNaN(lon);
                }
            }

            return false;
        }

        private static bool TryFirstVertex(JArray partArray, out double lon, out double lat)
        {
            lon = double.NaN; lat = double.NaN;
            if (partArray.Count == 0) return false;
            var part = partArray[0] as JArray;
            if (part == null || part.Count == 0) return false;
            var pt = part[0] as JArray;
            if (pt == null || pt.Count < 2) return false;
            lon = TryDouble(pt[0]);
            lat = TryDouble(pt[1]);
            return !double.IsNaN(lat) && !double.IsNaN(lon);
        }

        /// <summary>Culture-invariant, exception-free numeric read. A JSON string value
        /// (<c>"x": "-117.3"</c>) previously threw out of an explicit <c>JToken</c>→<c>double</c>
        /// cast and silently discarded the whole feature.</summary>
        internal static double TryDouble(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return double.NaN;
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) return (double)t;
            double parsed;
            return double.TryParse(t.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                ? parsed : double.NaN;
        }

        /// <summary>Stringifies an attribute value with the invariant culture. <c>JToken.ToString()</c>
        /// formats some <c>JValue</c> paths with the <b>current</b> culture, so on a de-DE machine a
        /// numeric attribute round-tripped through "1,5" and the symbology resolver's
        /// <c>double.TryParse</c> read it as 15 — wrong colours, silently, off-locale only.</summary>
        internal static string StringifyInvariant(JToken value)
        {
            if (value == null || value.Type == JTokenType.Null) return string.Empty;
            if (value.Type == JTokenType.Object || value.Type == JTokenType.Array)
                return value.ToString(Formatting.None);
            var jv = value as JValue;
            return jv != null ? Convert.ToString(jv.Value, CultureInfo.InvariantCulture) ?? string.Empty
                              : value.ToString(Formatting.None);
        }

        private static string GetString(JObject obj, string field)
        {
            var t = obj[field];
            return t == null || t.Type == JTokenType.Null ? string.Empty : StringifyInvariant(t);
        }

        /// <summary>Which rule produced a feature's uid. Logged once per download so a layer
        /// sitting on a weaker tier is visible rather than only showing up as markers that flash
        /// on every refresh.</summary>
        internal enum UidSource
        {
            /// <summary>The layer carries its own <c>uid</c> column. Fully stable.</summary>
            Declared,

            /// <summary>Derived from the object id. Stable across refreshes AND across edits.</summary>
            ObjectId,

            /// <summary>Derived from the feature's position. Stable across attribute edits; changes
            /// only if the feature moves.</summary>
            Position,

            /// <summary>Derived from the whole feature. Changes whenever anything changes.</summary>
            Content,

            /// <summary>Position in the result set. Last resort.</summary>
            Index,
        }

        /// <summary>
        /// Deterministic uid for a feature whose source layer has no <c>uid</c> attribute.
        ///
        /// <para>The original synthesis was <c>$"FL-{i}-{UtcNow}"</c>, which produced a
        /// <b>different</b> uid on every re-sync — so every refresh disposed and recreated every
        /// marker (full flash) and, on 5.7 where the diff sweep was missing entirely, they
        /// accumulated without bound.</para>
        ///
        /// <para><b>Why position comes before content.</b> The fallback used to hash the
        /// attributes and the geometry together, which is stable only for a feature nobody has
        /// touched: editing ANY attribute — including the symbology field an operator edits
        /// precisely to change how a marker looks — produced a new uid, so the marker was disposed
        /// and recreated and every data package naming the old uid went stale. Position is the
        /// closest thing to an identity such a feature has: it survives every attribute edit, and
        /// only changes if the feature actually moves. Content hashing remains below it for the
        /// case where a position cannot be read at all.</para>
        ///
        /// <para>C-39: the layer URL is folded with <see cref="string.ToLowerInvariant"/>, not
        /// <c>ToLower()</c>. C#'s culture-sensitive <c>ToLower()</c> maps 'I' to 'ı' under tr-TR
        /// and would derive a different uid on a Turkish-locale machine for the same layer.</para>
        /// </summary>
        /// <param name="taken">Uids already issued in this download, so two features that would
        /// collide are separated deterministically instead of one silently replacing the other.
        /// May be null.</param>
        internal static string DeriveStableUid(string uidSalt, JObject attrs, JObject geom,
            int index, double lat, double lon, ISet<string> taken, out UidSource source)
        {
            string objectId = null;
            if (attrs != null)
            {
                foreach (var candidate in new[] { "OBJECTID", "objectid", "ObjectID", "FID", "OID", "fid" })
                {
                    var t = attrs[candidate];
                    if (t != null && t.Type != JTokenType.Null)
                    {
                        objectId = StringifyInvariant(t);
                        break;
                    }
                }
            }

            if (!string.IsNullOrEmpty(objectId))
            {
                source = UidSource.ObjectId;
                // An object id is unique within its layer by definition, so it is not de-duplicated
                // — a clash here would mean the service contradicted itself, and quietly renaming
                // one of them would hide that.
                return "FL-" + uidSalt + "-" + objectId;
            }

            if (IsPlottable(lat, lon))
            {
                source = UidSource.Position;
                // Seven decimal places is ~11 mm: fine enough that two distinct features are not
                // merged, coarse enough that float noise in the service's own output does not
                // invent a new identity for a stationary feature.
                string position = lat.ToString("F7", CultureInfo.InvariantCulture)
                    + "," + lon.ToString("F7", CultureInfo.InvariantCulture);
                return Unique("FL-" + uidSalt + "-p" + ShortHash(position), taken);
            }

            string content = (attrs?.ToString(Formatting.None) ?? string.Empty)
                + "|" + (geom?.ToString(Formatting.None) ?? string.Empty);
            if (content.Length <= 1)
            {
                source = UidSource.Index;
                return Unique("FL-" + uidSalt + "-i"
                    + index.ToString(CultureInfo.InvariantCulture), taken);
            }

            source = UidSource.Content;
            return Unique("FL-" + uidSalt + "-h" + ShortHash(content), taken);
        }

        /// <summary>Returns <paramref name="candidate"/>, or the first free <c>_2</c>, <c>_3</c>…
        /// variant when it is already taken. Two features at the same position are rare but real
        /// (a stacked pair of observations); letting them share a uid would mean the second
        /// silently replacing the first on the map and in any package.</summary>
        private static string Unique(string candidate, ISet<string> taken)
        {
            if (taken == null) return candidate;
            if (taken.Add(candidate)) return candidate;

            for (int n = 2; n < int.MaxValue; n++)
            {
                string next = candidate + "_" + n.ToString(CultureInfo.InvariantCulture);
                if (taken.Add(next)) return next;
            }
            return candidate;
        }


        /// <summary>
        /// Says which rule produced this layer's marker uids.
        ///
        /// <para>Worth a line in the log because the weaker tiers are otherwise invisible until
        /// the operator notices markers flashing on every refresh, or a data package they built
        /// last week no longer matches the layer. The message names the consequence rather than
        /// the tier, because the tier means nothing to the person reading it.</para>
        /// </summary>
        private static void LogUidTiers(string layerUrl, IDictionary<UidSource, int> tally)
        {
            if (tally == null || tally.Count == 0) return;

            int declared = Count(tally, UidSource.Declared);
            int objectId = Count(tally, UidSource.ObjectId);
            int position = Count(tally, UidSource.Position);
            int content = Count(tally, UidSource.Content);
            int index = Count(tally, UidSource.Index);

            if (declared > 0 && position + content + index == 0)
            {
                Log.Info($"Marker ids for \"{layerUrl}\" come from the layer's own uid column; "
                         + "they are stable across refreshes and edits.");
                return;
            }

            if (objectId > 0 && position + content + index == 0)
            {
                Log.Info($"Marker ids for \"{layerUrl}\" come from its object id; they are stable "
                         + "across refreshes and edits.");
                return;
            }

            if (position > 0 && content + index == 0)
            {
                Log.Info($"\"{layerUrl}\" has no uid or object id column, so marker ids come from "
                         + $"each feature's POSITION ({position} features). They survive attribute "
                         + "edits, but MOVING a feature gives it a new id — which recreates its "
                         + "marker and leaves any data package naming the old id out of date. Add "
                         + "a uid column to the layer to make ids fully stable.");
                return;
            }

            Log.Warn($"\"{layerUrl}\" produced marker ids from mixed sources "
                     + $"(uid column {declared}, object id {objectId}, position {position}, "
                     + $"content {content}, list order {index}). The content and list-order ones "
                     + "change whenever the feature or the query does, so their markers are "
                     + "recreated on every refresh and data packages naming them go out of date. "
                     + "Add a uid or object id column to the layer.");
        }

        private static int Count(IDictionary<UidSource, int> tally, UidSource source)
        {
            int n;
            return tally.TryGetValue(source, out n) ? n : 0;
        }

        internal static string LayerUidSalt(string layerUrl) =>
            ShortHash((layerUrl ?? string.Empty).Trim().ToLowerInvariant());

        internal static string ShortHash(string input)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input ?? string.Empty));
                var sb = new StringBuilder(12);
                for (int i = 0; i < 6; i++) sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        // -------------------------------------------------------------------------
        // PLI add/update (applyEdits)
        // -------------------------------------------------------------------------

        private static JObject BuildPliAttributes(
            string uid, string cotType, string callsign, string iconPath, string remarks, string how,
            string sentByUser, string groupName, string groupRole,
            double lat, double lon, double hae, double ce, double le,
            long timeMs, long startMs, long staleMs, string rawCotXml)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return new JObject
            {
                ["uid"] = uid ?? string.Empty,
                ["source_system"] = "WinTAK",
                ["source_layer"] = string.Empty,
                ["source_objectid"] = string.Empty,
                ["cot_type"] = cotType ?? string.Empty,
                ["tak_callsign"] = callsign ?? string.Empty,
                ["tak_icon"] = iconPath ?? string.Empty,
                ["tak_remarks"] = remarks ?? string.Empty,
                ["group_name"] = groupName ?? string.Empty,
                ["group_role"] = groupRole ?? string.Empty,
                ["latitude"] = SafeDouble(lat),
                ["longitude"] = SafeDouble(lon),
                ["hae"] = SafeDouble(hae),
                ["ce"] = SafeDouble(ce),
                ["le"] = SafeDouble(le),
                ["time"] = timeMs,
                ["start_time"] = startMs,
                ["stale_time"] = staleMs,
                ["sent_toFL_time"] = now,
                ["sent_by_user"] = sentByUser ?? string.Empty,
                ["how"] = how ?? string.Empty,
                ["sync_status"] = "synced",
                ["last_synced"] = now,
                ["raw_cot_xml"] = rawCotXml ?? string.Empty,
            };
        }

        private static JObject BuildPliGeometry(double lat, double lon)
        {
            return new JObject
            {
                ["x"] = lon,
                ["y"] = lat,
                ["spatialReference"] = new JObject { ["wkid"] = 4326 },
            };
        }

        /// <summary>Adds a new PLI feature. Returns the new objectId, or -1 on a server-reported
        /// failure; throws <see cref="ArcGisServiceException"/> for transport/error-body failures
        /// so the caller can distinguish "the row was rejected" from "the service is unreachable".</summary>
        public async Task<long> AddPliFeatureAsync(string serviceUrl, string token,
            string uid, string cotType, string callsign, string iconPath, string remarks, string how,
            string sentByUser, string groupName, string groupRole,
            double lat, double lon, double hae, double ce, double le,
            long timeMs, long startMs, long staleMs, string rawCotXml,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!IsPlottable(lat, lon))
                throw new ArcGisServiceException(
                    "Refusing to send a position with an invalid latitude/longitude (no GPS fix?).");

            string layerUrl = EnsureLayerIndex(serviceUrl);
            var feature = new JObject
            {
                ["geometry"] = BuildPliGeometry(lat, lon),
                ["attributes"] = BuildPliAttributes(uid, cotType, callsign, iconPath, remarks, how,
                    sentByUser, groupName, groupRole, lat, lon, hae, ce, le, timeMs, startMs, staleMs, rawCotXml),
            };

            var form = new Dictionary<string, string>
            {
                ["adds"] = new JArray { feature }.ToString(Formatting.None),
                ["f"] = "json",
            };

            var json = await ArcGisHttp.PostFormJsonAsync(_http, layerUrl + "/applyEdits", form, token, cancellationToken)
                .ConfigureAwait(false);

            if (!(json["addResults"] is JArray addResults) || addResults.Count == 0) return -1;
            var result = addResults[0] as JObject;
            if (result == null) return -1;
            if (!((bool?)result["success"] ?? false))
            {
                Log.Error("ArcGIS rejected the PLI add: " + DescribeEditError(result));
                return -1;
            }
            return (long?)result["objectId"] ?? -1;
        }

        /// <summary>Updates an existing PLI feature by objectId. Returns false if the server reports
        /// failure (e.g. the row no longer exists) — the caller forgets the objectId and re-adds.</summary>
        public async Task<bool> UpdatePliFeatureAsync(string serviceUrl, string token, long objectId,
            string uid, string cotType, string callsign, string iconPath, string remarks, string how,
            string sentByUser, string groupName, string groupRole,
            double lat, double lon, double hae, double ce, double le,
            long timeMs, long startMs, long staleMs, string rawCotXml,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!IsPlottable(lat, lon))
                throw new ArcGisServiceException(
                    "Refusing to send a position with an invalid latitude/longitude (no GPS fix?).");

            string layerUrl = EnsureLayerIndex(serviceUrl);
            string objectIdField = await GetObjectIdFieldAsync(layerUrl, token, cancellationToken).ConfigureAwait(false);

            var attributes = BuildPliAttributes(uid, cotType, callsign, iconPath, remarks, how,
                sentByUser, groupName, groupRole, lat, lon, hae, ce, le, timeMs, startMs, staleMs, rawCotXml);
            attributes[objectIdField] = objectId;

            var form = new Dictionary<string, string>
            {
                ["updates"] = new JArray
                {
                    new JObject { ["geometry"] = BuildPliGeometry(lat, lon), ["attributes"] = attributes }
                }.ToString(Formatting.None),
                ["f"] = "json",
            };

            var json = await ArcGisHttp.PostFormJsonAsync(_http, layerUrl + "/applyEdits", form, token, cancellationToken)
                .ConfigureAwait(false);

            if (!(json["updateResults"] is JArray updateResults) || updateResults.Count == 0) return false;
            var result = updateResults[0] as JObject;
            if (result == null) return false;
            bool ok = (bool?)result["success"] ?? false;
            if (!ok) Log.Warn("ArcGIS rejected the PLI update: " + DescribeEditError(result));
            return ok;
        }

        /// <summary>ArcGIS returns the only actionable diagnostic for a failed edit (schema
        /// mismatch, missing field, invalid geometry) inside <c>error.description</c>; it used to
        /// be discarded entirely.</summary>
        private static string DescribeEditError(JObject editResult)
        {
            var err = editResult["error"] as JObject;
            if (err == null) return "no reason supplied";
            return string.Format(CultureInfo.InvariantCulture, "{0} ({1})",
                (string)err["description"] ?? (string)err["message"] ?? "unknown", (int?)err["code"] ?? 0);
        }

        /// <summary>Per-layer cached objectId field. This used to be an extra HTTP round trip on
        /// <b>every</b> 30-second PLI tick, forever.</summary>
        private async Task<string> GetObjectIdFieldAsync(string layerUrl, string token,
            CancellationToken cancellationToken)
        {
            lock (_cacheLock)
            {
                string cached;
                if (_objectIdFieldCache.TryGetValue(layerUrl, out cached)) return cached;
            }
            var meta = await FetchLayerMetadataAsync(layerUrl, token, cancellationToken).ConfigureAwait(false);
            return meta.ObjectIdField;
        }

        private static JToken SafeDouble(double v) =>
            double.IsNaN(v) || double.IsInfinity(v) ? JValue.CreateNull() : new JValue(v);

        // -------------------------------------------------------------------------
        // Create PLI feature service (addItem -> publish -> updateDefinition)
        // -------------------------------------------------------------------------

        private const string SchemaCsv =
            "uid,source_system,source_layer,source_objectid,cot_type,tak_callsign,tak_icon," +
            "tak_remarks,latitude,longitude,hae,ce,le,time,start_time,stale_time," +
            "sent_toFL_time,sent_by_user,how,sync_status,last_synced,raw_cot_xml\n" +
            "SCHEMA_TEMPLATE,WinTAK,,,a-f-G-U-C,EXAMPLE,,Schema row," +
            "34.052235,-117.307899,285.4,9.0,2.0," +
            "2025-01-01T00:00:00Z,2025-01-01T00:00:00Z,2025-01-01T00:00:30Z," +
            "2025-01-01T00:00:00Z,admin,m-g,synced,2025-01-01T00:00:00Z,\n";

        /// <summary>Creates a hosted Feature Layer by uploading the schema CSV then publishing it.
        /// Returns the URL to layer 0 of the new service. Throws
        /// <see cref="ArcGisServiceException"/> with the real reason on failure — including a
        /// publish that timed out or a service that came back non-editable, both of which used to
        /// be swallowed and reported to the operator as success.</summary>
        public async Task<string> CreatePliFeatureServiceAsync(string portalUrl, string username, string token,
            string layerName, CancellationToken cancellationToken = default(CancellationToken))
        {
            string portal = NormalizePortal(portalUrl);
            if (string.IsNullOrEmpty(token))
                throw new ArcGisServiceException("Sign in before creating a PLI feature service.");

            string displayName = !string.IsNullOrWhiteSpace(layerName)
                ? layerName.Trim() : $"FeatureLink PLI {username}";
            if (displayName.Length > 200) displayName = displayName.Substring(0, 200);

            string itemId = await UploadCsvItemAsync(portal, username, token, displayName, cancellationToken)
                .ConfigureAwait(false);
            if (itemId == null)
                throw new ArcGisServiceException("The portal did not return an item id for the uploaded schema.");

            var published = await PublishCsvItemAsync(portal, username, token, itemId, displayName, cancellationToken)
                .ConfigureAwait(false);
            if (published.serviceUrl == null)
                throw new ArcGisServiceException("The portal did not return a service URL for the published layer.");
            string serviceUrl = published.serviceUrl.TrimEnd('/');

            if (!string.IsNullOrEmpty(published.jobId))
            {
                bool completed = await WaitForPublishJobAsync(portal, username, token,
                    published.serviceItemId, published.jobId, cancellationToken).ConfigureAwait(false);
                if (!completed)
                    throw new ArcGisServiceException(
                        "The feature service did not finish publishing. Check your ArcGIS content — "
                        + "a large publish can take longer than the wait window.");
            }

            await EnableEditingAsync(serviceUrl, token, cancellationToken).ConfigureAwait(false);
            return serviceUrl + "/0";
        }

        private async Task<string> UploadCsvItemAsync(string portal, string username, string token, string title,
            CancellationToken cancellationToken)
        {
            string endpoint = $"{portal}/sharing/rest/content/users/{Uri.EscapeDataString(username)}/addItem";
            var csvBytes = Encoding.UTF8.GetBytes(SchemaCsv);

            using (var content = new MultipartFormDataContent("FeatureLinkBoundary" + Guid.NewGuid().ToString("N")))
            using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                content.Add(new StringContent(title), "title");
                content.Add(new StringContent("CSV"), "type");
                content.Add(new StringContent("FeatureLink,WinTAK,PLI"), "tags");
                content.Add(new StringContent("FeatureLink PLI schema — created by WinTAK FeatureLink plugin"), "description");
                content.Add(new StringContent("json"), "f");

                var fileContent = new ByteArrayContent(csvBytes);
                fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
                content.Add(fileContent, "file", "featurelink_schema.csv");

                request.Content = content;
                request.Headers.TryAddWithoutValidation(ArcGisHttp.EsriAuthorizationHeader, "Bearer " + token);
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

                var json = await ArcGisHttp.SendForJsonAsync(_http, request, endpoint, cancellationToken)
                    .ConfigureAwait(false);
                return (string)json["id"];
            }
        }

        private async Task<(string serviceItemId, string serviceUrl, string jobId)> PublishCsvItemAsync(
            string portal, string username, string token, string itemId, string name,
            CancellationToken cancellationToken)
        {
            var fields = new JArray
            {
                SchemaField("uid", "esriFieldTypeString", "UID", 100),
                SchemaField("source_system", "esriFieldTypeString", "Source System", 100),
                SchemaField("source_layer", "esriFieldTypeString", "Source Layer", 255),
                SchemaField("source_objectid", "esriFieldTypeString", "Source Object ID", 50),
                SchemaField("cot_type", "esriFieldTypeString", "CoT Type", 100),
                SchemaField("tak_callsign", "esriFieldTypeString", "TAK Callsign", 255),
                SchemaField("tak_icon", "esriFieldTypeString", "TAK Icon", 512),
                SchemaField("tak_remarks", "esriFieldTypeString", "TAK Remarks", 1000),
                SchemaField("latitude", "esriFieldTypeDouble", "Latitude", -1),
                SchemaField("longitude", "esriFieldTypeDouble", "Longitude", -1),
                SchemaField("hae", "esriFieldTypeDouble", "HAE (m)", -1),
                SchemaField("ce", "esriFieldTypeDouble", "CE (m)", -1),
                SchemaField("le", "esriFieldTypeDouble", "LE (m)", -1),
                SchemaField("time", "esriFieldTypeDate", "Time", -1),
                SchemaField("start_time", "esriFieldTypeDate", "Start Time", -1),
                SchemaField("stale_time", "esriFieldTypeDate", "Stale Time", -1),
                SchemaField("sent_toFL_time", "esriFieldTypeDate", "Sent to FL Time", -1),
                SchemaField("sent_by_user", "esriFieldTypeString", "Sent By User", 255),
                SchemaField("how", "esriFieldTypeString", "How", 50),
                SchemaField("sync_status", "esriFieldTypeString", "Sync Status", 50),
                SchemaField("last_synced", "esriFieldTypeDate", "Last Synced", -1),
                SchemaField("raw_cot_xml", "esriFieldTypeString", "Raw CoT XML", 32000),
            };

            string safeName = System.Text.RegularExpressions.Regex.Replace(name, "[^a-zA-Z0-9 _]", "").Trim();
            if (safeName.Length > 100) safeName = safeName.Substring(0, 100).Trim();
            if (string.IsNullOrEmpty(safeName))
            {
                Log.Warn($"Layer name \"{name}\" contains no characters ArcGIS accepts in a service "
                         + "name — falling back to \"FeatureLink PLI\".");
                safeName = "FeatureLink PLI";
            }

            var publishParams = new JObject
            {
                ["name"] = safeName,
                ["locationType"] = "coordinates",
                ["latitudeFieldName"] = "latitude",
                ["longitudeFieldName"] = "longitude",
                ["coordinateFieldType"] = "LatLong",
                ["hasStaticData"] = false,
                ["maxRecordCount"] = 2000,
                ["capabilities"] = "Create,Delete,Query,Update,Editing",
                ["layerInfo"] = new JObject { ["fields"] = fields },
            };

            string endpoint = $"{portal}/sharing/rest/content/users/{Uri.EscapeDataString(username)}/publish";
            var form = new Dictionary<string, string>
            {
                ["itemId"] = itemId,
                ["filetype"] = "csv",
                ["publishParameters"] = publishParams.ToString(Formatting.None),
                ["f"] = "json",
            };

            var json = await ArcGisHttp.PostFormJsonAsync(_http, endpoint, form, token, cancellationToken)
                .ConfigureAwait(false);

            if (!(json["services"] is JArray services) || services.Count == 0) return (null, null, null);
            var svc = services[0] as JObject;
            if (svc == null) return (null, null, null);
            return ((string)svc["serviceItemId"],
                    (string)svc["serviceurl"] ?? (string)svc["encodedServiceURL"],
                    (string)svc["jobId"]);
        }

        private async Task<bool> WaitForPublishJobAsync(string portal, string username, string token,
            string serviceItemId, string jobId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(serviceItemId) || string.IsNullOrEmpty(jobId)) return true;

            string statusUrl = $"{portal}/sharing/rest/content/users/{Uri.EscapeDataString(username)}"
                + $"/items/{Uri.EscapeDataString(serviceItemId)}/status?jobId={Uri.EscapeDataString(jobId)}"
                + "&jobType=publish&f=json";

            // 150 × 2 s = 5 minutes; the old 60 s window silently expired on any large publish and
            // the caller ignored the result anyway.
            for (int i = 0; i < 150; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                try
                {
                    var json = await ArcGisHttp.GetJsonAsync(_http, statusUrl, token, cancellationToken)
                        .ConfigureAwait(false);
                    string status = (string)json["status"] ?? string.Empty;
                    if (status == "completed") return true;
                    if (status == "failed" || status == "cancelled")
                    {
                        Log.Error("Publish job reported status: " + status);
                        return false;
                    }
                }
                catch (ArcGisServiceException ex)
                {
                    Log.Warn("Publish status poll failed, retrying: " + ex.Message);
                }
            }
            return false;
        }

        private async Task EnableEditingAsync(string featureServerUrl, string token,
            CancellationToken cancellationToken)
        {
            var def = new JObject
            {
                ["capabilities"] = "Create,Delete,Query,Update,Editing",
                ["hasStaticData"] = false,
                ["allowGeometryUpdates"] = true,
            };
            var form = new Dictionary<string, string>
            {
                ["updateDefinition"] = def.ToString(Formatting.None),
                ["f"] = "json",
            };

            var json = await ArcGisHttp.PostFormJsonAsync(_http, featureServerUrl + "/updateDefinition",
                form, token, cancellationToken).ConfigureAwait(false);

            if (!((bool?)json["success"] ?? true))
                throw new ArcGisServiceException(
                    "The new feature service could not be made editable — PLI sends to it would fail silently.");
        }

        private static JObject SchemaField(string name, string type, string alias, int length)
        {
            var f = new JObject
            {
                ["name"] = name,
                ["type"] = type,
                ["alias"] = alias,
                ["nullable"] = true,
            };
            if (length > 0) f["length"] = length;
            return f;
        }

        // -------------------------------------------------------------------------
        // URL helpers
        // -------------------------------------------------------------------------

        public const string DefaultPortalUrl = "https://www.arcgis.com";

        internal static string NormalizePortal(string portalUrl) =>
            string.IsNullOrWhiteSpace(portalUrl) ? DefaultPortalUrl : portalUrl.Trim().TrimEnd('/');

        /// <summary>Ensures the URL points at a specific layer index of a FeatureServer/MapServer.
        /// A URL that already carries an index is left alone; one that ends at the service root
        /// gets <c>/0</c> appended.
        ///
        /// <para>This used to BE the sublayer story, and it was silent data loss: a five-layer
        /// service became layer 0 with no warning. <see cref="ListSubLayersAsync"/> now enumerates
        /// before this is reached, so the <c>/0</c> is a genuine fallback for a service that
        /// advertises no layers array rather than the normal path.</para></summary>
        internal static string EnsureLayerIndex(string url)
        {
            string canonical = CanonicalServiceUrl(url);
            if (canonical.Length == 0) return string.Empty;

            // Still the last-resort fallback, and now only that: Add Layer enumerates first via
            // ListSubLayersAsync, so this is reached for a service that advertises no layers array
            // or whose enumeration failed. Dropping the layer instead would be worse.
            return IsServiceRoot(canonical) ? canonical + "/0" : canonical;
        }
    }
}
