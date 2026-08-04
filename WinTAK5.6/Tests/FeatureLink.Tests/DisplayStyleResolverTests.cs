using System.Collections.Generic;
using System.Drawing;
using FeatureLink.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FeatureLink.Tests
{
    /// <summary>
    /// C-07 / C-24. The shape-style resolver did not exist on WinTAK in any form, so a
    /// Portal-authored config carrying polyline/polygon styling was silently and completely
    /// dropped (Appendix F §5's concrete interop break). The precedence cases below also pin the
    /// C-24 correction: a per-value match wins over the single/default style, which is the
    /// opposite of what ATAK's <c>DisplayConfig.resolveShapeStyle</c> and CloudTAK's
    /// <c>resolveShapeStyle</c> currently do.
    /// </summary>
    public class DisplayStyleResolverTests
    {
        private static JObject J(string json) => JObject.Parse(json.Replace('\'', '"'));

        private static Dictionary<string, string> Attrs(params string[] kv)
        {
            var d = new Dictionary<string, string>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
            return d;
        }

        // ── shape style ──────────────────────────────────────────────────────────

        private const string ShpBoth = @"{
            'f':'STATUS',
            's':{'sc':'#FF808080','sw':1,'sd':'solid','fc':'#00000000','fs':'none'},
            'bv':{
              'OPEN':{'sc':'#FF00FF00','sw':4,'sd':'dash','fc':'#4000FF00','fs':'solid'},
              'CLOSED':{'sc':'#FFFF0000','sw':2,'sd':'dot','fc':'#40FF0000','fs':'solid'}
            }}";

        [Fact]
        public void ResolveShapeStyle_NullConfig_ReturnsNull()
        {
            Assert.Null(DisplayStyleResolver.ResolveShapeStyle(null, Attrs()));
        }

        [Fact]
        public void ResolveShapeStyle_PerValueMatch_WinsOverTheSingleStyle_C24()
        {
            var style = DisplayStyleResolver.ResolveShapeStyle(J(ShpBoth), Attrs("STATUS", "OPEN"));
            Assert.NotNull(style);
            // If the C-24 inversion were present, this would be the grey 1px single style.
            Assert.Equal(Color.FromArgb(255, 0, 255, 0), style.StrokeColor);
            Assert.Equal(4f, style.StrokeWidthPx);
            Assert.Equal("dash", style.StrokeDash);
            Assert.Equal("solid", style.FillStyle);
        }

        [Fact]
        public void ResolveShapeStyle_UnmatchedValue_FallsBackToTheSingleStyle()
        {
            var style = DisplayStyleResolver.ResolveShapeStyle(J(ShpBoth), Attrs("STATUS", "PENDING"));
            Assert.NotNull(style);
            Assert.Equal(Color.FromArgb(255, 128, 128, 128), style.StrokeColor);
            Assert.Equal("none", style.FillStyle);
        }

        [Fact]
        public void ResolveShapeStyle_MissingDrivingAttribute_FallsBackRatherThanMatchingAnEmptyKey()
        {
            var style = DisplayStyleResolver.ResolveShapeStyle(J(ShpBoth), Attrs());
            Assert.Equal(Color.FromArgb(255, 128, 128, 128), style.StrokeColor);
        }

        [Fact]
        public void ResolveShapeStyle_PerValueOnly_WithNoSingleStyle_StillResolves()
        {
            var shp = J("{'f':'K','bv':{'A':{'sc':'#FF112233','sw':2}}}");
            Assert.Equal(Color.FromArgb(255, 0x11, 0x22, 0x33),
                DisplayStyleResolver.ResolveShapeStyle(shp, Attrs("K", "A")).StrokeColor);
            Assert.Null(DisplayStyleResolver.ResolveShapeStyle(shp, Attrs("K", "B")));
        }

        [Fact]
        public void ParseShapeStyle_RejectsNonsenseWidthsAndDashes()
        {
            var s = DisplayStyleResolver.ParseShapeStyle(J("{'sw':-3,'sd':'wobbly','fs':'plaid'}"));
            Assert.Equal(2f, s.StrokeWidthPx);
            Assert.Equal("solid", s.StrokeDash);
            Assert.Equal("none", s.FillStyle);
        }

        [Fact]
        public void BuildShapeConfigJson_RoundTripsThroughResolveShapeStyle()
        {
            var extracted = AutoSymbology.Extract(J(@"{
              'type':'uniqueValue','field1':'STATUS',
              'uniqueValueInfos':[
                {'value':'OPEN','symbol':{'type':'esriSLS','color':[0,255,0,255],'width':4,'style':'esriSLSDash'}}
              ],
              'defaultSymbol':{'type':'esriSLS','color':[128,128,128,255],'width':1}}"));

            var shp = DisplayStyleResolver.BuildShapeConfigJson(extracted);
            Assert.NotNull(shp);
            Assert.Equal("STATUS", (string)shp["f"]);

            var open = DisplayStyleResolver.ResolveShapeStyle(shp, Attrs("STATUS", "OPEN"));
            Assert.Equal(Color.FromArgb(255, 0, 255, 0), open.StrokeColor);
            Assert.Equal(4f, open.StrokeWidthPx);
            Assert.Equal("dash", open.StrokeDash);

            var other = DisplayStyleResolver.ResolveShapeStyle(shp, Attrs("STATUS", "SOMETHING"));
            Assert.Equal(Color.FromArgb(255, 128, 128, 128), other.StrokeColor);
        }

        [Fact]
        public void BuildShapeConfigJson_ReturnsNullForAMarkerOnlyRenderer()
        {
            var extracted = AutoSymbology.Extract(J(
                "{'type':'simple','symbol':{'type':'esriSMS','color':[1,2,3],'style':'esriSMSSquare'}}"));
            Assert.Null(DisplayStyleResolver.BuildShapeConfigJson(extracted));
        }

        [Fact]
        public void CombineShapeStyle_PrefersTheFillsOwnOutlineWhenThereIsNoStandaloneStroke()
        {
            var extracted = AutoSymbology.Extract(J(@"{
              'type':'simple','symbol':{'type':'esriSFS','style':'esriSFSSolid','color':[0,0,255,60],
                'outline':{'type':'esriSLS','color':[255,255,0,255],'width':5,'style':'esriSLSDot'}}}"));
            var shp = DisplayStyleResolver.BuildShapeConfigJson(extracted);
            var style = DisplayStyleResolver.ResolveShapeStyle(shp, Attrs());
            Assert.Equal(Color.FromArgb(255, 255, 255, 0), style.StrokeColor);
            Assert.Equal(5f, style.StrokeWidthPx);
            Assert.Equal("dot", style.StrokeDash);
            Assert.Equal(60, style.FillColor.A);
        }

        // ── marker sym config synthesis ──────────────────────────────────────────

        [Fact]
        public void BuildMarkerSymConfigJson_UniqueValueMarkers_BecomeAUvConfigResolveColorUnderstands()
        {
            var extracted = AutoSymbology.Extract(J(@"{
              'type':'uniqueValue','field1':'T',
              'uniqueValueInfos':[
                {'value':'A','symbol':{'type':'esriSMS','color':[255,0,0]}},
                {'value':'B','symbol':{'type':'esriSMS','color':[0,0,255]}}
              ],
              'defaultSymbol':{'type':'esriSMS','color':[1,2,3]}}"));

            var sym = DisplayStyleResolver.BuildMarkerSymConfigJson(extracted);
            Assert.Equal("uv", (string)sym["t"]);
            Assert.Equal(Color.FromArgb(255, 255, 0, 0), DisplayStyleResolver.ResolveColor(sym, Attrs("T", "A")));
            Assert.Equal(Color.FromArgb(255, 0, 0, 255), DisplayStyleResolver.ResolveColor(sym, Attrs("T", "B")));
            Assert.Equal(Color.FromArgb(255, 1, 2, 3), DisplayStyleResolver.ResolveColor(sym, Attrs("T", "Z")));
        }

        [Fact]
        public void BuildMarkerSymConfigJson_SingleMarker_BecomesAnSConfig()
        {
            var extracted = AutoSymbology.Extract(J(
                "{'type':'simple','symbol':{'type':'esriSMS','color':[16,32,48],'style':'esriSMSDiamond','size':9}}"));
            var sym = DisplayStyleResolver.BuildMarkerSymConfigJson(extracted);
            Assert.Equal("s", (string)sym["t"]);
            Assert.Equal("diamond", (string)sym["sh"]);
            Assert.Equal(9, (int)sym["sz"]);
            Assert.Equal(Color.FromArgb(255, 16, 32, 48), DisplayStyleResolver.ResolveColor(sym, Attrs()));
        }

        // ── colour ───────────────────────────────────────────────────────────────

        [Fact]
        public void ResolveColor_NullSym_ReturnsNull_NotASilentBlue()
        {
            Assert.Null(DisplayStyleResolver.ResolveColor(null, Attrs()));
        }

        [Fact]
        public void ResolveColor_UnknownSymType_ReturnsNull_SoTheMarkerKeepsItsNativeStyling()
        {
            Assert.Null(DisplayStyleResolver.ResolveColor(J("{'t':'wat','c':'#ff0000'}"), Attrs()));
        }

        [Theory]
        [InlineData("#abc", 255, 0xAA, 0xBB, 0xCC)]
        [InlineData("#AABBCC", 255, 0xAA, 0xBB, 0xCC)]
        [InlineData("AABBCC", 255, 0xAA, 0xBB, 0xCC)]
        [InlineData("0xAABBCC", 255, 0xAA, 0xBB, 0xCC)]
        [InlineData("#80AABBCC", 0x80, 0xAA, 0xBB, 0xCC)]
        public void ParseHexColor_AcceptsThreeSixAndEightDigitForms(string hex, int a, int r, int g, int b)
        {
            Assert.Equal(Color.FromArgb(a, r, g, b), DisplayStyleResolver.ParseHexColor(hex, Color.Black));
        }

        [Theory]
        [InlineData("#12345")]
        [InlineData("nonsense")]
        [InlineData("#GGGGGG")]
        public void ParseHexColor_MalformedInput_UsesTheFallback(string hex)
        {
            Assert.Equal(Color.Black, DisplayStyleResolver.ParseHexColor(hex, Color.Black));
        }

        [Theory]
        [InlineData(1.0, 255)]
        [InlineData(0.5, 128)]
        [InlineData(0.0, 0)]
        [InlineData(-1.0, 0)]
        [InlineData(5.0, 255)]
        public void Opacity_IsClamped(double op, int expectedAlpha)
        {
            var sym = new JObject { ["t"] = "s", ["c"] = "#ffffff", ["op"] = op };
            Assert.Equal(expectedAlpha, DisplayStyleResolver.ResolveColor(sym, Attrs()).Value.A);
        }

        [Fact]
        public void Opacity_NaN_DoesNotProduceAnInvisibleMarker()
        {
            // (int)Math.Round(double.NaN) is int.MinValue in an unchecked context, which the old
            // code then clamped to alpha 0 — a silently invisible feature.
            var sym = new JObject { ["t"] = "s", ["c"] = "#ffffff", ["op"] = double.NaN };
            Assert.Equal(255, DisplayStyleResolver.ResolveColor(sym, Attrs()).Value.A);
        }

        [Fact]
        public void ClassBreaks_MalformedEntry_IsSkippedInsteadOfAbortingTheLayer()
        {
            var sym = J(@"{'t':'cb','f':'N','c':'#000000','cb':[
                {'mn':'oops','mx':'oops','c':'#ff0000'},
                {'mn':0,'mx':10,'c':'#00ff00'}]}");
            Assert.Equal(Color.FromArgb(255, 0, 255, 0),
                DisplayStyleResolver.ResolveColor(sym, Attrs("N", "5")));
        }

        [Fact]
        public void ClassBreaks_AreParsedInvariantly_UnderDeDE()
        {
            CultureScope.With("de-DE", () =>
            {
                var sym = J("{'t':'cb','f':'N','c':'#000000','cb':[{'mn':0,'mx':10,'c':'#00ff00'}]}");
                // "1.5" must read as one-and-a-half, not fifteen (which would miss the 0..10 break).
                Assert.Equal(Color.FromArgb(255, 0, 255, 0),
                    DisplayStyleResolver.ResolveColor(sym, Attrs("N", "1.5")));
            });
        }

        // ── rule operators ───────────────────────────────────────────────────────

        [Theory]
        [InlineData("A", "=", "A", true)]
        [InlineData("A", "=", "B", false)]
        [InlineData("A", "==", "A", true)]
        [InlineData("A", "≠", "B", true)]   // ≠
        [InlineData("A", "!=", "B", true)]        // ASCII spelling accepted (was silently false)
        [InlineData("A", "<>", "B", true)]
        [InlineData("HELLO", "contains", "ELL", true)]
        [InlineData("HELLO", "contains", "xyz", false)]
        [InlineData("HELLO", "starts with", "HE", true)]
        [InlineData("HELLO", "starts with", "EL", false)]
        [InlineData("", "is empty", "", true)]
        [InlineData("x", "is empty", "", false)]
        [InlineData("x", "is not empty", "", true)]
        [InlineData("5", ">", "3", true)]
        [InlineData("5", "<", "3", false)]
        [InlineData("3", ">=", "3", true)]
        [InlineData("3", "<=", "3", true)]
        [InlineData("abc", ">", "3", false)]
        [InlineData("x", "unknown-op", "y", false)]
        public void MatchesRule_OperatorTable(string fieldVal, string op, string ruleVal, bool expected)
        {
            Assert.Equal(expected, DisplayStyleResolver.MatchesRule(fieldVal, op, ruleVal));
        }

        [Fact]
        public void MatchesRule_NumericComparisonsAreInvariant_UnderDeDE()
        {
            CultureScope.With("de-DE", () =>
                Assert.True(DisplayStyleResolver.MatchesRule("1.5", ">", "1.2")));
        }

        [Fact]
        public void MatchesRule_StringComparisonsAreOrdinal_UnderTrTR()
        {
            // Culture-sensitive StartsWith under tr-TR is the classic dotted/dotless-I trap.
            CultureScope.With("tr-TR", () =>
            {
                Assert.True(DisplayStyleResolver.MatchesRule("INDIGO", "starts with", "IN"));
                Assert.False(DisplayStyleResolver.MatchesRule("INDIGO", "starts with", "ın"));
                Assert.False(DisplayStyleResolver.MatchesRule("i", "=", "I"));
            });
        }

        // ── label / remarks ──────────────────────────────────────────────────────

        [Fact]
        public void ResolveLabel_FallsBackWhenTheFieldIsMissingOrEmpty()
        {
            var lbl = J("{'f':'NAME'}");
            Assert.Equal("Bravo", DisplayStyleResolver.ResolveLabel(lbl, Attrs("NAME", "Bravo"), "fallback"));
            Assert.Equal("fallback", DisplayStyleResolver.ResolveLabel(lbl, Attrs("NAME", ""), "fallback"));
            Assert.Equal("fallback", DisplayStyleResolver.ResolveLabel(lbl, Attrs(), "fallback"));
            Assert.Equal("fallback", DisplayStyleResolver.ResolveLabel(null, Attrs(), "fallback"));
        }

        [Fact]
        public void BuildRemarks_HandlesBareNamesAndNameAliasPairs()
        {
            var popup = J("{'flds':['A',['B','Beta'],['C']]}");
            var text = DisplayStyleResolver.BuildRemarks(popup, Attrs("A", "1", "B", "2", "C", "3"));
            Assert.Equal("A: 1\nBeta: 2\nC: 3", text);
        }

        [Fact]
        public void BuildRemarks_EmptyFldsEntry_DoesNotThrow()
        {
            // "flds":[[]] previously threw ArgumentOutOfRangeException and aborted the download.
            var text = DisplayStyleResolver.BuildRemarks(J("{'flds':[[], 'A']}"), Attrs("A", "1"));
            Assert.Equal("A: 1", text);
        }

        [Fact]
        public void BuildRemarks_IsCapped_SoAHugePopupCannotBeBroadcastAsCot()
        {
            var attrs = new Dictionary<string, string>();
            var flds = new JArray();
            for (int i = 0; i < 60; i++)
            {
                attrs["F" + i] = new string('x', 500);
                flds.Add("F" + i);
            }
            var text = DisplayStyleResolver.BuildRemarks(new JObject { ["flds"] = flds }, attrs);
            Assert.True(text.Length <= DisplayStyleResolver.MaxRemarksLength);
        }

        // ── icon ─────────────────────────────────────────────────────────────────

        [Fact]
        public void ResolveIconsetPath_PrefersTheServerResolvedUpOverLegacyIsIc()
        {
            var sym = J("{'t':'ic','up':'uid/group/file.png','is':'Set','ic':'file.png'}");
            Assert.Equal("uid/group/file.png", DisplayStyleResolver.ResolveIconsetPath(sym, Attrs()));

            var legacy = J("{'t':'ic','is':'Set','ic':'file.png'}");
            Assert.Equal("Set/file.png", DisplayStyleResolver.ResolveIconsetPath(legacy, Attrs()));

            Assert.Null(DisplayStyleResolver.ResolveIconsetPath(J("{'t':'ic'}"), Attrs()));
        }

        [Fact]
        public void ResolveIconsetPath_AdvPerValue_MatchesOnlyIconModeEntries()
        {
            var sym = J(@"{'t':'adv','f':'K','vs':[
                {'v':'A','m':'shape','c':'#ff0000'},
                {'v':'B','m':'icon','up':'u/g/b.png'}]}");
            Assert.Null(DisplayStyleResolver.ResolveIconsetPath(sym, Attrs("K", "A")));
            Assert.Equal("u/g/b.png", DisplayStyleResolver.ResolveIconsetPath(sym, Attrs("K", "B")));
        }

        [Fact]
        public void ResolveIconsetPath_AdvRules_AcceptBothTheRAndRulesKeys()
        {
            var rKey = J("{'t':'adv','r':[{'f':'K','o':'=','v':'X','m':'icon','up':'u/g/x.png'}]}");
            var rulesKey = J("{'t':'adv','rules':[{'f':'K','o':'=','v':'X','m':'icon','up':'u/g/x.png'}]}");
            Assert.Equal("u/g/x.png", DisplayStyleResolver.ResolveIconsetPath(rKey, Attrs("K", "X")));
            Assert.Equal("u/g/x.png", DisplayStyleResolver.ResolveIconsetPath(rulesKey, Attrs("K", "X")));
        }
    }
}
