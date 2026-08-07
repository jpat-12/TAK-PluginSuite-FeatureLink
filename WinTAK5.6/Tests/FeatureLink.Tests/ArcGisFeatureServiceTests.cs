using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using FeatureLink.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FeatureLink.Tests
{
    /// <summary>
    /// C-06 (pagination), C-21 (token placement), C-22 (error bodies with HTTP 200), plus the
    /// spatial-reference and coordinate guards. These are the highest-value suites in the package:
    /// before them, a layer over the service <c>maxRecordCount</c> silently synced only its first
    /// page and reported success, and a 401 rendered as "0 features".
    /// </summary>
    public class ArcGisFeatureServiceTests
    {
        private const string Url = "https://services.example.com/arcgis/rest/services/Demo/FeatureServer/0";

        private static string MetaBody(int maxRecordCount = 1000, string renderer = null) =>
            "{\"name\":\"Demo\",\"geometryType\":\"esriGeometryPoint\",\"objectIdField\":\"OBJECTID\","
            + "\"maxRecordCount\":" + maxRecordCount
            + (renderer == null ? "" : ",\"drawingInfo\":{\"renderer\":" + renderer + "}")
            + "}";

        private static string Page(int count, bool exceeded, int startOid = 1)
        {
            var features = new JArray();
            for (int i = 0; i < count; i++)
            {
                features.Add(new JObject
                {
                    ["attributes"] = new JObject { ["OBJECTID"] = startOid + i, ["NAME"] = "F" + (startOid + i) },
                    ["geometry"] = new JObject { ["x"] = -117.0 + i * 0.001, ["y"] = 34.0 },
                });
            }
            var o = new JObject
            {
                ["spatialReference"] = new JObject { ["wkid"] = 4326 },
                ["features"] = features,
            };
            if (exceeded) o["exceededTransferLimit"] = true;
            return o.ToString(Newtonsoft.Json.Formatting.None);
        }

        // ── C-06 pagination ──────────────────────────────────────────────────────

        [Fact]
        public async Task Download_PagesUntilTheServiceStopsSettingExceededTransferLimit()
        {
            var stub = new StubHandler();
            stub.Enqueue(MetaBody(2));                    // metadata -> page size 2
            stub.Enqueue(Page(2, exceeded: true, startOid: 1));
            stub.Enqueue(Page(2, exceeded: true, startOid: 3));
            stub.Enqueue(Page(1, exceeded: false, startOid: 5));

            var svc = new ArcGisFeatureService(stub);
            var result = await svc.DownloadLayerAsCotAsync(Url, null);

            Assert.Equal(5, result.Features.Count);
            Assert.Equal(3, result.PagesFetched);
            Assert.False(result.Truncated);

            // resultOffset must advance by the number of features actually returned.
            var queries = stub.RequestUris.Where(u => u.Contains("/query?")).ToList();
            Assert.Contains("resultOffset=0", queries[0]);
            Assert.Contains("resultOffset=2", queries[1]);
            Assert.Contains("resultOffset=4", queries[2]);
            Assert.All(queries, q => Assert.Contains("resultRecordCount=2", q));
        }

        [Fact]
        public async Task Download_SinglePageShorterThanTheLimit_StopsAfterOneRequest()
        {
            var stub = new StubHandler();
            stub.Enqueue(MetaBody(1000));
            stub.Enqueue(Page(3, exceeded: false));

            var result = await new ArcGisFeatureService(stub).DownloadLayerAsCotAsync(Url, null);
            Assert.Equal(3, result.Features.Count);
            Assert.Equal(1, result.PagesFetched);
        }

        [Fact]
        public async Task Download_EmptyLayer_IsNotAnError()
        {
            var stub = new StubHandler();
            stub.Enqueue(MetaBody(1000));
            stub.Enqueue(Page(0, exceeded: false));

            var result = await new ArcGisFeatureService(stub).DownloadLayerAsCotAsync(Url, null);
            Assert.Empty(result.Features);
        }

        [Fact]
        public async Task Download_ExactlyOneFullPageThenAnEmptyOne_TerminatesCleanly()
        {
            var stub = new StubHandler();
            stub.Enqueue(MetaBody(2));
            stub.Enqueue(Page(2, exceeded: true, startOid: 1));
            stub.Enqueue(Page(0, exceeded: false, startOid: 3));

            var result = await new ArcGisFeatureService(stub).DownloadLayerAsCotAsync(Url, null);
            Assert.Equal(2, result.Features.Count);
        }

        [Fact]
        public async Task Search_PagesOnNextStart()
        {
            var stub = new StubHandler();
            stub.Enqueue("{\"total\":3,\"nextStart\":2,\"results\":["
                + "{\"title\":\"A\",\"url\":\"https://x/FeatureServer\",\"access\":\"public\"}]}");
            stub.Enqueue("{\"total\":3,\"nextStart\":3,\"results\":["
                + "{\"title\":\"B\",\"url\":\"https://y/FeatureServer\",\"access\":\"org\"}]}");
            stub.Enqueue("{\"total\":3,\"nextStart\":-1,\"results\":["
                + "{\"title\":\"C\",\"url\":\"https://z/FeatureServer\",\"access\":\"private\"}]}");

            var result = await new ArcGisFeatureService(stub)
                .SearchUserLayersAsync("https://www.arcgis.com", "tok", "alice");

            Assert.Equal(3, result.Layers.Count);
            Assert.False(result.Truncated);
            Assert.Contains("start=1", stub.RequestUris[0]);
            Assert.Contains("start=2", stub.RequestUris[1]);
            Assert.Contains("start=3", stub.RequestUris[2]);
        }

        [Fact]
        public async Task Search_QuotesTheUsername_SoItCannotAlterTheQuery()
        {
            var stub = new StubHandler();
            stub.Enqueue("{\"total\":0,\"nextStart\":-1,\"results\":[]}");
            await new ArcGisFeatureService(stub)
                .SearchUserLayersAsync("https://www.arcgis.com", null, "a\" OR owner:*");

            string decoded = Uri.UnescapeDataString(stub.RequestUris[0]);
            Assert.Contains("owner:\"a\\\" OR owner:*\"", decoded);
        }

        [Theory]
        [InlineData("alice", "\"alice\"")]
        [InlineData("a\"b", "\"a\\\"b\"")]
        [InlineData("", "\"\"")]
        [InlineData(null, "\"\"")]
        public void QuoteSearchTerm_EscapesEmbeddedQuotes(string input, string expected)
        {
            Assert.Equal(expected, ArcGisFeatureService.QuoteSearchTerm(input));
        }

        // ── C-22 error bodies ────────────────────────────────────────────────────

        [Fact]
        public async Task ErrorBodyWithHttp200_Throws_InsteadOfReportingZeroFeatures()
        {
            var stub = new StubHandler();
            stub.AlwaysBody = "{\"error\":{\"code\":498,\"message\":\"Invalid token\"}}";

            var ex = await Assert.ThrowsAsync<ArcGisServiceException>(
                () => new ArcGisFeatureService(stub).QueryFeatureCountAsync(Url, "bad"));
            Assert.Equal(498, ex.Code);
            Assert.True(ex.IsAuthFailure);
            Assert.Contains("Invalid token", ex.Message);
        }

        [Fact]
        public async Task ErrorBodyDetails_AreSurfaced()
        {
            var stub = new StubHandler();
            stub.AlwaysBody =
                "{\"error\":{\"code\":400,\"message\":\"Unable to complete operation\",\"details\":[\"Invalid where clause\"]}}";
            var ex = await Assert.ThrowsAsync<ArcGisServiceException>(
                () => new ArcGisFeatureService(stub).QueryFeatureCountAsync(Url, null));
            Assert.Contains("Invalid where clause", ex.Message);
        }

        [Fact]
        public async Task NonSuccessStatus_IsReportedWithItsStatusCode()
        {
            var stub = new StubHandler();
            stub.Enqueue("<html>gateway timeout</html>", HttpStatusCode.GatewayTimeout);
            var ex = await Assert.ThrowsAsync<ArcGisServiceException>(
                () => new ArcGisFeatureService(stub).QueryFeatureCountAsync(Url, null));
            Assert.Equal(504, ex.Code);
        }

        [Fact]
        public async Task NonJsonBody_GivesAnActionableMessage_NotAParserStackTrace()
        {
            var stub = new StubHandler();
            stub.Enqueue("<!DOCTYPE html><html><body>Sign in</body></html>");
            var ex = await Assert.ThrowsAsync<ArcGisServiceException>(
                () => new ArcGisFeatureService(stub).FetchLayerMetadataAsync(Url));
            Assert.Contains("not JSON", ex.Message);
        }

        [Fact]
        public async Task FeatureCount_MissingCountField_IsAnError_NotZero()
        {
            var stub = new StubHandler();
            stub.Enqueue("{\"someOtherShape\":true}");
            await Assert.ThrowsAsync<ArcGisServiceException>(
                () => new ArcGisFeatureService(stub).QueryFeatureCountAsync(Url, null));
        }

        [Fact]
        public async Task FeatureCount_HappyPath()
        {
            var stub = new StubHandler();
            stub.Enqueue("{\"count\":1234}");
            Assert.Equal(1234, await new ArcGisFeatureService(stub).QueryFeatureCountAsync(Url, null));
        }

        // ── C-21 token placement ─────────────────────────────────────────────────

        [Fact]
        public async Task Token_TravelsInAHeader_NeverInTheQueryString()
        {
            var stub = new StubHandler();
            stub.Enqueue("{\"count\":1}");
            await new ArcGisFeatureService(stub).QueryFeatureCountAsync(Url, "SECRET-TOKEN");

            Assert.DoesNotContain("token=", stub.RequestUris[0]);
            Assert.DoesNotContain("SECRET-TOKEN", stub.RequestUris[0]);

            var req = stub.Requests[0];
            Assert.True(req.Headers.Contains(ArcGisHttp.EsriAuthorizationHeader));
            Assert.Equal("Bearer SECRET-TOKEN",
                string.Join("", req.Headers.GetValues(ArcGisHttp.EsriAuthorizationHeader)));
        }

        [Fact]
        public async Task NoToken_SendsNoAuthorizationHeaderAtAll()
        {
            var stub = new StubHandler();
            stub.Enqueue("{\"count\":0}");
            await new ArcGisFeatureService(stub).QueryFeatureCountAsync(Url, null);
            Assert.False(stub.Requests[0].Headers.Contains(ArcGisHttp.EsriAuthorizationHeader));
        }

        [Theory]
        [InlineData("https://h/rest/services/X/FeatureServer/query?token=abc123&f=json",
                    "https://h/rest/services/X/FeatureServer/query?token=<redacted>&f=json")]
        [InlineData("code=AUTHCODE&state=x", "code=<redacted>&state=x")]
        [InlineData("Authorization: Bearer eyJhbGciOi", "Authorization: Bearer <redacted>")]
        public void Redact_StripsSecretsFromDiagnostics(string input, string expected)
        {
            Assert.Equal(expected, Log.Redact(input));
        }

        // ── spatial reference / coordinates ──────────────────────────────────────

        [Fact]
        public async Task WebMercatorResponse_IsRejected_NotPlottedAsDegrees()
        {
            var stub = new StubHandler();
            stub.Enqueue(MetaBody());
            stub.Enqueue("{\"spatialReference\":{\"wkid\":102100},\"features\":[]}");

            var ex = await Assert.ThrowsAsync<ArcGisServiceException>(
                () => new ArcGisFeatureService(stub).DownloadLayerAsCotAsync(Url, null));
            Assert.Contains("102100", ex.Message);
        }

        [Fact]
        public async Task StringTypedCoordinates_AreParsed_NotSilentlyDropped()
        {
            var stub = new StubHandler();
            stub.Enqueue(MetaBody());
            stub.Enqueue("{\"spatialReference\":{\"wkid\":4326},\"features\":[{"
                + "\"attributes\":{\"OBJECTID\":1},\"geometry\":{\"x\":\"-117.3\",\"y\":\"34.05\"}}]}");

            var result = await new ArcGisFeatureService(stub).DownloadLayerAsCotAsync(Url, null);
            Assert.Single(result.Features);
            Assert.Equal(-117.3, result.Features[0].Lon, 6);
            Assert.Equal(34.05, result.Features[0].Lat, 6);
        }

        [Fact]
        public async Task OutOfRangeAndNullGeometryFeatures_AreCountedRatherThanSilentlyLost()
        {
            var stub = new StubHandler();
            stub.Enqueue(MetaBody());
            stub.Enqueue("{\"spatialReference\":{\"wkid\":4326},\"features\":["
                + "{\"attributes\":{\"OBJECTID\":1},\"geometry\":{\"x\":-117,\"y\":34}},"
                + "{\"attributes\":{\"OBJECTID\":2},\"geometry\":{\"x\":5000000,\"y\":700000}},"
                + "{\"attributes\":{\"OBJECTID\":3}}"
                + "]}");

            var result = await new ArcGisFeatureService(stub).DownloadLayerAsCotAsync(Url, null);
            Assert.Single(result.Features);
            Assert.Equal(1, result.OutOfRangeCoordinates);
            Assert.Equal(1, result.SkippedFeatures);
        }

        [Theory]
        [InlineData("paths", "polyline")]
        [InlineData("rings", "polygon")]
        public async Task PolylineAndPolygon_UseTheFirstVertex_AndCarryTheirGeometryKind(
            string key, string expectedKind)
        {
            var stub = new StubHandler();
            stub.Enqueue(MetaBody());
            stub.Enqueue("{\"spatialReference\":{\"wkid\":4326},\"features\":[{"
                + "\"attributes\":{\"OBJECTID\":1},"
                + "\"geometry\":{\"" + key + "\":[[[-117.5,34.5],[-117.4,34.6]]]}}]}");

            var result = await new ArcGisFeatureService(stub).DownloadLayerAsCotAsync(Url, null);
            Assert.Single(result.Features);
            Assert.Equal(expectedKind, result.Features[0].GeometryKind);
            Assert.Equal(-117.5, result.Features[0].Lon, 6);
        }

        [Fact]
        public async Task AttributeValues_AreStringifiedInvariantly_UnderDeDE()
        {
            string body = "{\"spatialReference\":{\"wkid\":4326},\"features\":[{"
                + "\"attributes\":{\"OBJECTID\":1,\"SPEED\":1.5},\"geometry\":{\"x\":-117,\"y\":34}}]}";

            LayerDownloadResult result = null;
            CultureScope.With("de-DE", () =>
            {
                var stub = new StubHandler();
                stub.Enqueue(MetaBody());
                stub.Enqueue(body);
                result = new ArcGisFeatureService(stub).DownloadLayerAsCotAsync(Url, null)
                    .GetAwaiter().GetResult();
            });

            // "1,5" here would make DisplayStyleResolver's invariant TryParse read 15.
            Assert.Equal("1.5", result.Features[0].Attributes["SPEED"]);
        }

        // ── C-07: drawingInfo is surfaced, not discarded ─────────────────────────

        [Fact]
        public async Task FetchLayerMetadata_SurfacesTheRenderer()
        {
            var stub = new StubHandler();
            stub.Enqueue(MetaBody(renderer:
                "{\"type\":\"simple\",\"symbol\":{\"type\":\"esriSLS\",\"color\":[1,2,3],\"width\":2}}"));

            var meta = await new ArcGisFeatureService(stub).FetchLayerMetadataAsync(Url);
            Assert.NotNull(meta.Renderer);
            Assert.Equal("simple", (string)meta.Renderer["type"]);
            Assert.Equal(1000, meta.MaxRecordCount);
            Assert.Equal("OBJECTID", meta.ObjectIdField);
        }

        [Fact]
        public async Task FetchLayerMetadata_NoDrawingInfo_LeavesTheRendererNull()
        {
            var stub = new StubHandler();
            stub.Enqueue(MetaBody());
            var meta = await new ArcGisFeatureService(stub).FetchLayerMetadataAsync(Url);
            Assert.Null(meta.Renderer);
        }

        // ── URL shapes ───────────────────────────────────────────────────────────

        [Theory]
        [InlineData("https://h/rest/services/X/FeatureServer", "https://h/rest/services/X/FeatureServer/0")]
        [InlineData("https://h/rest/services/X/FeatureServer/", "https://h/rest/services/X/FeatureServer/0")]
        [InlineData("https://h/rest/services/X/FeatureServer/2", "https://h/rest/services/X/FeatureServer/2")]
        [InlineData("https://h/rest/services/X/MapServer", "https://h/rest/services/X/MapServer/0")]
        [InlineData("https://h/rest/services/X/MapServer/3", "https://h/rest/services/X/MapServer/3")]
        [InlineData("https://h/rest/services/X/FeatureServer?f=json", "https://h/rest/services/X/FeatureServer/0")]
        [InlineData("  https://h/rest/services/X/FeatureServer  ", "https://h/rest/services/X/FeatureServer/0")]
        [InlineData("", "")]
        public void EnsureLayerIndex_HandlesTheEightUrlShapes(string input, string expected)
        {
            Assert.Equal(expected, ArcGisFeatureService.EnsureLayerIndex(input));
        }

        [Theory]
        [InlineData(null, "https://www.arcgis.com")]
        [InlineData("", "https://www.arcgis.com")]
        [InlineData("   ", "https://www.arcgis.com")]
        [InlineData(" https://portal.example.com/ ", "https://portal.example.com")]
        public void NormalizePortal_TrimsWhitespaceAndTrailingSlash(string input, string expected)
        {
            Assert.Equal(expected, ArcGisFeatureService.NormalizePortal(input));
        }

        [Theory]
        [InlineData(0, 0, true)]
        [InlineData(90, 180, true)]
        [InlineData(-90, -180, true)]
        [InlineData(90.1, 0, false)]
        [InlineData(0, 180.1, false)]
        [InlineData(double.NaN, 0, false)]
        [InlineData(double.PositiveInfinity, 0, false)]
        public void IsPlottable_RangeChecks(double lat, double lon, bool expected)
        {
            Assert.Equal(expected, ArcGisFeatureService.IsPlottable(lat, lon));
        }
    }
}
