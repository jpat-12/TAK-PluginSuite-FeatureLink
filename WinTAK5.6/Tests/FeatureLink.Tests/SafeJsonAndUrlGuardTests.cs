using System;
using System.IO;
using System.Text;
using FeatureLink.Services;
using Newtonsoft.Json;
using Xunit;

namespace FeatureLink.Tests
{
    /// <summary>C-19 — peer-supplied JSON was parsed with default settings and no depth or size
    /// limit. Stack exhaustion from a nested document raises <c>StackOverflowException</c>, which
    /// is uncatchable on .NET Framework and terminates the whole WinTAK process: a remote denial
    /// of service from one Mission Package.</summary>
    public class SafeJsonTests
    {
        private static string Nested(int depth)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            for (int i = 0; i < depth; i++) sb.Append("\"a\":{");
            sb.Append("\"b\":1");
            for (int i = 0; i < depth; i++) sb.Append('}');
            sb.Append('}');
            return sb.ToString();
        }

        [Fact]
        public void ParseObject_AcceptsADocumentWithinTheDepthLimit()
        {
            var o = SafeJson.ParseObject(Nested(10));
            Assert.NotNull(o);
        }

        [Fact]
        public void ParseObject_RejectsADocumentOverTheDepthLimit()
        {
            Assert.Throws<JsonReaderException>(() => SafeJson.ParseObject(Nested(SafeJson.MaxDepth + 5)));
        }

        [Fact]
        public void ParseObject_RejectsAPathologicallyDeepDocument_WithoutBlowingTheStack()
        {
            // 100k levels is the actual attack shape; it must fail as a caught exception.
            Assert.Throws<JsonReaderException>(() => SafeJson.ParseObject(Nested(100_000)));
        }

        [Fact]
        public void ParseObject_RejectsAnOversizedDocument()
        {
            string big = "{\"a\":\"" + new string('x', SafeJson.MaxUntrustedBytes + 10) + "\"}";
            Assert.Throws<InvalidDataException>(() => SafeJson.ParseObject(big));
        }

        [Fact]
        public void ParseObject_RejectsANonObjectRoot()
        {
            Assert.Throws<JsonReaderException>(() => SafeJson.ParseObject("[1,2,3]"));
        }

        [Fact]
        public void ParseObject_RejectsTrailingContent()
        {
            Assert.Throws<JsonReaderException>(() => SafeJson.ParseObject("{\"a\":1} {\"b\":2}"));
        }

        [Fact]
        public void ParseObjectOrNull_DegradesInsteadOfThrowing()
        {
            Assert.Null(SafeJson.ParseObjectOrNull("{not json"));
            Assert.Null(SafeJson.ParseObjectOrNull(null));
            Assert.Null(SafeJson.ParseObjectOrNull(""));
            Assert.NotNull(SafeJson.ParseObjectOrNull("{\"a\":1}"));
        }

        [Fact]
        public void ReadTextCapped_RefusesAnOversizedFile()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".featurelinkshare");
            try
            {
                File.WriteAllText(path, new string('x', 5000));
                Assert.Null(SafeJson.ReadTextCapped(path, 1000));
                Assert.NotNull(SafeJson.ReadTextCapped(path, 10000));
            }
            finally { try { File.Delete(path); } catch { } }
        }

        [Fact]
        public void StrictSettings_PinTypeNameHandlingOff()
        {
            // Every WinTAK plugin shares one AppDomain, so JsonConvert.DefaultSettings is not ours
            // to trust — a neighbour enabling TypeNameHandling would otherwise make our config and
            // token parsing a deserialization sink.
            Assert.Equal(TypeNameHandling.None, SafeJson.StrictSettings.TypeNameHandling);
            Assert.Equal(SafeJson.MaxDepth, SafeJson.StrictSettings.MaxDepth);
        }
    }

    /// <summary>SSRF guards (audit S3/S4) backing the C-02 consent flow: the share importer took a
    /// peer-supplied URL straight from a Mission Package and fetched it with no scheme or host
    /// check at all.</summary>
    public class UrlGuardTests
    {
        [Theory]
        [InlineData("https://services.arcgis.com/abc/ArcGIS/rest/services/X/FeatureServer/0")]
        [InlineData("https://services.arcgis.com/abc/ArcGIS/rest/services/X/FeatureServer")]
        [InlineData("https://services.arcgis.com/abc/ArcGIS/rest/services/X/MapServer/2")]
        public void ValidateServiceUrl_AcceptsRealArcGisUrls(string url)
        {
            var r = UrlGuard.ValidateServiceUrl(url);
            Assert.True(r.Ok, r.Reason);
        }

        [Theory]
        [InlineData("", "empty")]
        [InlineData("   ", "empty")]
        [InlineData("not a url", "absolute")]
        [InlineData("http://services.arcgis.com/x/FeatureServer/0", "https")]
        [InlineData("ftp://services.arcgis.com/x/FeatureServer/0", "https")]
        [InlineData("file:///C:/windows/win.ini", "https")]
        [InlineData("javascript:alert(1)", "https")]
        public void ValidateServiceUrl_RejectsNonHttpsAndMalformed(string url, string reasonFragment)
        {
            var r = UrlGuard.ValidateServiceUrl(url);
            Assert.False(r.Ok);
            Assert.Contains(reasonFragment, r.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("https://127.0.0.1/x/FeatureServer/0")]
        [InlineData("https://localhost/x/FeatureServer/0")]
        [InlineData("https://10.0.0.1/admin/FeatureServer/0")]
        [InlineData("https://192.168.1.1/x/FeatureServer/0")]
        [InlineData("https://172.16.4.4/x/FeatureServer/0")]
        [InlineData("https://169.254.169.254/latest/FeatureServer/0")]  // cloud metadata
        [InlineData("https://100.64.0.1/x/FeatureServer/0")]            // CGNAT
        [InlineData("https://[::1]/x/FeatureServer/0")]
        public void ValidateServiceUrl_RejectsPrivateLoopbackAndLinkLocalHosts(string url)
        {
            Assert.False(UrlGuard.ValidateServiceUrl(url).Ok);
        }

        [Fact]
        public void ValidateServiceUrl_RejectsAPathThatIsNotAnArcGisService()
        {
            Assert.False(UrlGuard.ValidateServiceUrl("https://example.com/some/other/path").Ok);
            Assert.True(UrlGuard.ValidateServiceUrl("https://example.com/some/other/path", requireServicePath: false).Ok);
        }

        [Fact]
        public void ValidateServiceUrl_RejectsAnAbsurdlyLongUrl()
        {
            Assert.False(UrlGuard.ValidateServiceUrl(
                "https://example.com/" + new string('a', UrlGuard.MaxUrlLength) + "/FeatureServer/0").Ok);
        }

        [Theory]
        [InlineData("uid/group/file.png", true)]
        [InlineData("34ae/Responder Icons/ambulance.png", true)]
        [InlineData("../../etc/passwd", false)]
        [InlineData("uid/group/\"onload=alert(1)", false)]
        [InlineData("uid/<script>/x.png", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsSafeIconsetPath_GuardsWhatGetsBroadcastAsCot(string path, bool expected)
        {
            Assert.Equal(expected, UrlGuard.IsSafeIconsetPath(path));
        }

        [Fact]
        public void IsSafeIconsetPath_RejectsControlCharactersAndOverlongPaths()
        {
            Assert.False(UrlGuard.IsSafeIconsetPath("uid/group/a\u0000b.png"));
            Assert.False(UrlGuard.IsSafeIconsetPath(new string('a', 600)));
        }

        [Fact]
        public void SanitizeDisplayName_CapsLengthAndStripsControlCharacters()
        {
            // Each control character becomes one space (a 1:1 substitution, not a collapse), so a
            // peer cannot use CR/LF to fake extra lines in the consent dialog or a status message.
            Assert.Equal("a  b", UrlGuard.SanitizeDisplayName("a\r\nb"));
            Assert.Equal("a b", UrlGuard.SanitizeDisplayName("a\nb"));
            Assert.Equal(20, UrlGuard.SanitizeDisplayName(new string('x', 1000), 20).Length);
            Assert.Null(UrlGuard.SanitizeDisplayName(null));
        }
    }
}
