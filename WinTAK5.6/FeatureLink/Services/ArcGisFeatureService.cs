using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using FeatureLink.Models;
using Newtonsoft.Json.Linq;

namespace FeatureLink.Services
{
    /// <summary>
    /// ArcGIS REST API client — ported from the ATAK plugin's ArcGISRestClient.java
    /// (com.atakmap.android.featurelink.arcgis). Uses HttpClient + Newtonsoft.Json.Linq instead
    /// of HttpURLConnection + org.json, matching the HTTP/JSON style already used by this SDK's
    /// OpenAtlas sample (OpenAtlas/Services/ArcGisFeatureService.cs). All methods are async and
    /// safe to call from a UI-bound command handler.
    /// </summary>
    public sealed class ArcGisFeatureService
    {
        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        // -------------------------------------------------------------------------
        // Layer search
        // -------------------------------------------------------------------------

        /// <summary>Searches the portal for Feature Services owned by the authenticated user.</summary>
        public async Task<List<ArcGisLayer>> SearchUserLayersAsync(string portalUrl, string token, string username)
        {
            var layers = new List<ArcGisLayer>();
            try
            {
                string q = $"type:\"Feature Service\" AND owner:{username}";
                string endpoint = NormalizePortal(portalUrl)
                    + "/sharing/rest/search?q=" + Uri.EscapeDataString(q)
                    + "&num=100&f=json&token=" + Uri.EscapeDataString(token);

                string response = await Http.GetStringAsync(endpoint).ConfigureAwait(false);
                var json = JObject.Parse(response);
                var results = json["results"] as JArray;
                if (results == null) return layers;

                foreach (var item in results)
                {
                    string name = (string)item["title"] ?? "Unnamed";
                    string url = (string)item["url"] ?? string.Empty;
                    if (!string.IsNullOrEmpty(url))
                        layers.Add(new ArcGisLayer(name, url, "private"));
                }
            }
            catch
            {
                // Network/parse failure — return whatever was collected (usually empty); caller
                // surfaces this as "0 layers found" rather than crashing the refresh.
            }
            return layers;
        }

        // -------------------------------------------------------------------------
        // Feature count
        // -------------------------------------------------------------------------

        public async Task<long> QueryFeatureCountAsync(string serviceUrl, string token)
        {
            string layerUrl = EnsureLayerIndex(serviceUrl);
            string paramsStr = "where=1%3D1&returnCountOnly=true&f=json"
                + (token != null ? "&token=" + Uri.EscapeDataString(token) : string.Empty);
            string response = await Http.GetStringAsync(layerUrl + "/query?" + paramsStr).ConfigureAwait(false);
            var json = JObject.Parse(response);
            return (long?)json["count"] ?? 0;
        }

        // -------------------------------------------------------------------------
        // Layer info
        // -------------------------------------------------------------------------

        public async Task<ArcGisLayer> FetchLayerInfoAsync(string serviceUrl)
        {
            try
            {
                string url = EnsureLayerIndex(serviceUrl);
                string response = await Http.GetStringAsync(url + "?f=json").ConfigureAwait(false);
                var json = JObject.Parse(response);
                if (json["error"] != null) return null;
                string name = (string)json["name"] ?? (string)json["serviceDescription"] ?? "Unknown Layer";
                return new ArcGisLayer(name, serviceUrl, "public");
            }
            catch
            {
                return null;
            }
        }

        // -------------------------------------------------------------------------
        // Feature download
        // -------------------------------------------------------------------------

        /// <summary>Downloads all features from a layer as CoT-ready value objects. Supports
        /// point, polyline, and polygon geometry (polyline/polygon uses the first vertex, same
        /// simplification the ATAK plugin makes).</summary>
        public async Task<List<DownloadedFeature>> DownloadLayerAsCotAsync(string serviceUrl, string token)
        {
            var results = new List<DownloadedFeature>();
            string layerUrl = EnsureLayerIndex(serviceUrl);
            string paramsStr = "where=1%3D1&outFields=*&outSR=4326&f=json"
                + (token != null ? "&token=" + Uri.EscapeDataString(token) : string.Empty);

            string response = await Http.GetStringAsync(layerUrl + "/query?" + paramsStr).ConfigureAwait(false);
            var json = JObject.Parse(response);
            var features = json["features"] as JArray;
            if (features == null) return results;

            int i = 0;
            foreach (var feat in features)
            {
                try
                {
                    var attrs = feat["attributes"] as JObject;
                    var geom = feat["geometry"] as JObject;
                    if (geom == null) { i++; continue; }

                    double lat = double.NaN, lon = double.NaN, hae = double.NaN;
                    if (geom["x"] != null && geom["y"] != null)
                    {
                        lon = (double)geom["x"];
                        lat = (double)geom["y"];
                    }
                    else
                    {
                        var vertex = ExtractFirstVertex(geom);
                        if (vertex != null) { lon = vertex.Value.x; lat = vertex.Value.y; }
                    }
                    if (double.IsNaN(lat) || double.IsNaN(lon)) { i++; continue; }

                    var attrMap = new Dictionary<string, string>();
                    string uid, cotType, callsign, remarks;
                    if (attrs != null)
                    {
                        foreach (var prop in attrs.Properties())
                        {
                            if (prop.Value != null && prop.Value.Type != JTokenType.Null)
                                attrMap[prop.Name] = prop.Value.ToString();
                        }

                        uid = GetString(attrs, "uid");
                        cotType = GetString(attrs, "cot_type");
                        callsign = GetString(attrs, "tak_callsign");
                        remarks = GetString(attrs, "tak_remarks");
                        hae = (double?)attrs["hae"] ?? double.NaN;

                        if (string.IsNullOrEmpty(uid)) uid = $"FL-{i}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
                        if (string.IsNullOrEmpty(cotType)) cotType = "a-f-G";
                        if (string.IsNullOrEmpty(callsign)) callsign = $"Feature-{i}";
                    }
                    else
                    {
                        uid = $"FL-{i}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
                        cotType = "a-f-G";
                        callsign = $"Feature-{i}";
                        remarks = string.Empty;
                    }

                    results.Add(new DownloadedFeature(uid, cotType, callsign, remarks ?? string.Empty,
                        lat, lon, hae, attrMap));
                }
                catch
                {
                    // Skip malformed feature, keep going — same tolerance as the ATAK version.
                }
                i++;
            }
            return results;
        }

        private static (double x, double y)? ExtractFirstVertex(JObject geom)
        {
            var paths = geom["paths"] as JArray;
            if (paths != null && paths.Count > 0)
            {
                var path = paths[0] as JArray;
                if (path != null && path.Count > 0)
                {
                    var pt = path[0] as JArray;
                    if (pt != null && pt.Count >= 2)
                        return ((double)pt[0], (double)pt[1]);
                }
            }
            var rings = geom["rings"] as JArray;
            if (rings != null && rings.Count > 0)
            {
                var ring = rings[0] as JArray;
                if (ring != null && ring.Count > 0)
                {
                    var pt = ring[0] as JArray;
                    if (pt != null && pt.Count >= 2)
                        return ((double)pt[0], (double)pt[1]);
                }
            }
            return null;
        }

        private static string GetString(JObject obj, string field) => (string)obj[field] ?? string.Empty;

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

        /// <summary>Adds a new PLI feature. Returns the new objectId, or -1 on failure — callers
        /// should remember it and switch to <see cref="UpdatePliFeatureAsync"/> for subsequent
        /// sends, exactly like ArcGISRestClient.addPliFeature's contract.</summary>
        public async Task<long> AddPliFeatureAsync(string serviceUrl, string token,
            string uid, string cotType, string callsign, string iconPath, string remarks, string how,
            string sentByUser, string groupName, string groupRole,
            double lat, double lon, double hae, double ce, double le,
            long timeMs, long startMs, long staleMs, string rawCotXml)
        {
            string layerUrl = EnsureLayerIndex(serviceUrl);

            var feature = new JObject
            {
                ["geometry"] = BuildPliGeometry(lat, lon),
                ["attributes"] = BuildPliAttributes(uid, cotType, callsign, iconPath, remarks, how,
                    sentByUser, groupName, groupRole, lat, lon, hae, ce, le, timeMs, startMs, staleMs, rawCotXml),
            };
            var adds = new JArray { feature };

            var form = new Dictionary<string, string>
            {
                ["adds"] = adds.ToString(Newtonsoft.Json.Formatting.None),
                ["f"] = "json",
            };
            if (token != null) form["token"] = token;

            string response = await PostFormAsync(layerUrl + "/applyEdits", form).ConfigureAwait(false);
            if (response == null) return -1;

            var json = JObject.Parse(response);
            var addResults = json["addResults"] as JArray;
            if (addResults == null || addResults.Count == 0) return -1;
            var result = (JObject)addResults[0];
            if (!(bool)(result["success"] ?? false)) return -1;
            return (long?)result["objectId"] ?? -1;
        }

        /// <summary>Updates an existing PLI feature by objectId. Returns false if the server
        /// reports failure (e.g. the row no longer exists) — caller should forget the objectId
        /// and re-add next tick, same as the ATAK plugin's sendPliUpdate().</summary>
        public async Task<bool> UpdatePliFeatureAsync(string serviceUrl, string token, long objectId,
            string uid, string cotType, string callsign, string iconPath, string remarks, string how,
            string sentByUser, string groupName, string groupRole,
            double lat, double lon, double hae, double ce, double le,
            long timeMs, long startMs, long staleMs, string rawCotXml)
        {
            string layerUrl = EnsureLayerIndex(serviceUrl);
            string objectIdField = await FetchObjectIdFieldAsync(layerUrl).ConfigureAwait(false);

            var attributes = BuildPliAttributes(uid, cotType, callsign, iconPath, remarks, how,
                sentByUser, groupName, groupRole, lat, lon, hae, ce, le, timeMs, startMs, staleMs, rawCotXml);
            attributes[objectIdField] = objectId;

            var feature = new JObject
            {
                ["geometry"] = BuildPliGeometry(lat, lon),
                ["attributes"] = attributes,
            };
            var updates = new JArray { feature };

            var form = new Dictionary<string, string>
            {
                ["updates"] = updates.ToString(Newtonsoft.Json.Formatting.None),
                ["f"] = "json",
            };
            if (token != null) form["token"] = token;

            string response = await PostFormAsync(layerUrl + "/applyEdits", form).ConfigureAwait(false);
            if (response == null) return false;

            var json = JObject.Parse(response);
            var updateResults = json["updateResults"] as JArray;
            if (updateResults == null || updateResults.Count == 0) return false;
            return (bool)((JObject)updateResults[0])["success"];
        }

        private async Task<string> FetchObjectIdFieldAsync(string layerUrl)
        {
            try
            {
                string response = await Http.GetStringAsync(layerUrl + "?f=json").ConfigureAwait(false);
                var json = JObject.Parse(response);
                return (string)json["objectIdField"] ?? "OBJECTID";
            }
            catch
            {
                return "OBJECTID";
            }
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

        /// <summary>Creates a hosted Feature Layer by uploading the schema CSV then publishing
        /// it, exactly like ArcGISRestClient.createPliFeatureService. Returns the URL to layer 0
        /// of the new service, or null on failure.</summary>
        public async Task<string> CreatePliFeatureServiceAsync(string portalUrl, string username, string token,
            string layerName)
        {
            try
            {
                string portal = NormalizePortal(portalUrl);
                string displayName = !string.IsNullOrWhiteSpace(layerName)
                    ? layerName.Trim() : $"FeatureLink PLI {username}";

                string itemId = await UploadCsvItemAsync(portal, username, token, displayName).ConfigureAwait(false);
                if (itemId == null) return null;

                var (serviceItemId, serviceUrlRaw, jobId) =
                    await PublishCsvItemAsync(portal, username, token, itemId, displayName).ConfigureAwait(false);
                if (serviceUrlRaw == null) return null;
                string serviceUrl = serviceUrlRaw.TrimEnd('/');

                if (!string.IsNullOrEmpty(jobId))
                    await WaitForPublishJobAsync(portal, username, token, serviceItemId, jobId).ConfigureAwait(false);

                await EnableEditingAsync(serviceUrl, token).ConfigureAwait(false);

                return serviceUrl + "/0";
            }
            catch
            {
                return null;
            }
        }

        private async Task<string> UploadCsvItemAsync(string portal, string username, string token, string title)
        {
            string endpoint = $"{portal}/sharing/rest/content/users/{Uri.EscapeDataString(username)}/addItem";
            var csvBytes = Encoding.UTF8.GetBytes(SchemaCsv);

            using (var content = new MultipartFormDataContent("FeatureLinkBoundary" + Guid.NewGuid().ToString("N")))
            {
                content.Add(new StringContent(title), "title");
                content.Add(new StringContent("CSV"), "type");
                content.Add(new StringContent("FeatureLink,WinTAK,PLI"), "tags");
                content.Add(new StringContent("FeatureLink PLI schema — created by WinTAK FeatureLink plugin"), "description");
                content.Add(new StringContent("json"), "f");
                content.Add(new StringContent(token), "token");

                var fileContent = new ByteArrayContent(csvBytes);
                fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
                content.Add(fileContent, "file", "featurelink_schema.csv");

                using (var response = await Http.PostAsync(endpoint, content).ConfigureAwait(false))
                {
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var json = JObject.Parse(body);
                    if (json["error"] != null) return null;
                    return (string)json["id"];
                }
            }
        }

        private async Task<(string serviceItemId, string serviceUrl, string jobId)> PublishCsvItemAsync(
            string portal, string username, string token, string itemId, string name)
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

            var layerInfo = new JObject { ["fields"] = fields };

            string safeName = System.Text.RegularExpressions.Regex.Replace(name, "[^a-zA-Z0-9 _]", "").Trim();
            if (string.IsNullOrEmpty(safeName)) safeName = "FeatureLink PLI";

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
                ["layerInfo"] = layerInfo,
            };

            string endpoint = $"{portal}/sharing/rest/content/users/{Uri.EscapeDataString(username)}/publish";
            var form = new Dictionary<string, string>
            {
                ["itemId"] = itemId,
                ["filetype"] = "csv",
                ["publishParameters"] = publishParams.ToString(Newtonsoft.Json.Formatting.None),
                ["f"] = "json",
                ["token"] = token,
            };

            string resp = await PostFormAsync(endpoint, form).ConfigureAwait(false);
            if (resp == null) return (null, null, null);

            var json = JObject.Parse(resp);
            if (json["error"] != null) return (null, null, null);

            var services = json["services"] as JArray;
            if (services == null || services.Count == 0) return (null, null, null);

            var svc = (JObject)services[0];
            string serviceItemId = (string)svc["serviceItemId"];
            string serviceUrl = (string)svc["serviceurl"] ?? (string)svc["encodedServiceURL"];
            string jobId = (string)svc["jobId"];
            return (serviceItemId, serviceUrl, jobId);
        }

        private async Task<bool> WaitForPublishJobAsync(string portal, string username, string token,
            string serviceItemId, string jobId)
        {
            if (string.IsNullOrEmpty(serviceItemId) || string.IsNullOrEmpty(jobId)) return true;
            try
            {
                string statusUrl = $"{portal}/sharing/rest/content/users/{Uri.EscapeDataString(username)}"
                    + $"/items/{Uri.EscapeDataString(serviceItemId)}/status?jobId={Uri.EscapeDataString(jobId)}"
                    + $"&jobType=publish&f=json&token={Uri.EscapeDataString(token)}";

                for (int i = 0; i < 30; i++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    string resp = await Http.GetStringAsync(statusUrl).ConfigureAwait(false);
                    string status = (string)JObject.Parse(resp)["status"] ?? string.Empty;
                    if (status == "completed") return true;
                    if (status == "failed" || status == "cancelled") return false;
                }
            }
            catch
            {
                // fall through to false below
            }
            return false;
        }

        private async Task EnableEditingAsync(string featureServerUrl, string token)
        {
            try
            {
                var def = new JObject
                {
                    ["capabilities"] = "Create,Delete,Query,Update,Editing",
                    ["hasStaticData"] = false,
                    ["allowGeometryUpdates"] = true,
                };
                var form = new Dictionary<string, string>
                {
                    ["updateDefinition"] = def.ToString(Newtonsoft.Json.Formatting.None),
                    ["f"] = "json",
                    ["token"] = token,
                };
                await PostFormAsync(featureServerUrl + "/updateDefinition", form).ConfigureAwait(false);
            }
            catch
            {
                // Non-fatal — same as the ATAK version's enableEditing().
            }
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
        // HTTP helpers
        // -------------------------------------------------------------------------

        private static async Task<string> PostFormAsync(string url, Dictionary<string, string> form)
        {
            using (var content = new FormUrlEncodedContent(form))
            using (var response = await Http.PostAsync(url, content).ConfigureAwait(false))
            {
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }

        private static string NormalizePortal(string portalUrl) =>
            string.IsNullOrWhiteSpace(portalUrl) ? "https://www.arcgis.com" : portalUrl.Trim().TrimEnd('/');

        /// <summary>Ensures the URL points at layer index 0 of a FeatureServer.</summary>
        private static string EnsureLayerIndex(string url)
        {
            if (string.IsNullOrEmpty(url)) return string.Empty;
            url = url.Trim().TrimEnd('/');
            return url.EndsWith("FeatureServer", StringComparison.OrdinalIgnoreCase) ? url + "/0" : url;
        }
    }
}
