using System.Drawing;
using FeatureLink.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FeatureLink.Tests
{
    /// <summary>
    /// C-07. AutoSymbology is a from-scratch implementation on WinTAK — the platform had no
    /// renderer extraction of any kind, so <c>drawingInfo</c> was read and discarded and no
    /// symbology was ever resolved on the download path. These cases pin its behaviour against
    /// the ATAK <c>AutoSymbology.java</c> and CloudTAK <c>autoSymbology.ts</c> semantics it is
    /// ported from.
    /// </summary>
    public class AutoSymbologyTests
    {
        private static JObject J(string json) => JObject.Parse(json);

        [Fact]
        public void NullRenderer_YieldsEmptyResult()
        {
            var r = AutoSymbology.Extract(null);
            Assert.True(r.IsEmpty);
            Assert.False(r.HasShapeStyling);
            Assert.Equal("", r.Field);
        }

        [Fact]
        public void SimpleRenderer_LineSymbol_ExtractsStroke()
        {
            var r = AutoSymbology.Extract(J(@"{
                'type':'simple',
                'symbol':{'type':'esriSLS','color':[255,0,0,200],'width':3.5,'style':'esriSLSDash'}
            }".Replace('\'', '"')));

            Assert.NotNull(r.SingleStroke);
            Assert.Equal(Color.FromArgb(200, 255, 0, 0), r.SingleStroke.Color);
            Assert.Equal(3.5f, r.SingleStroke.WidthPx);
            Assert.Equal("dash", r.SingleStroke.Dash);
            Assert.True(r.HasShapeStyling);
            Assert.Null(r.SingleMarker);
        }

        [Fact]
        public void SimpleRenderer_FillSymbol_ExtractsFillAndItsOutline()
        {
            var r = AutoSymbology.Extract(J(@"{
                'type':'simple',
                'symbol':{'type':'esriSFS','style':'esriSFSSolid','color':[0,128,255,80],
                          'outline':{'type':'esriSLS','color':[0,0,0,255],'width':1,'style':'esriSLSDot'}}
            }".Replace('\'', '"')));

            Assert.NotNull(r.SingleFill);
            Assert.Equal("solid", r.SingleFill.Style);
            // Alpha is preserved: translucent polygon fills are ordinary ArcGIS styling.
            Assert.Equal(80, r.SingleFill.Color.A);
            Assert.NotNull(r.SingleFill.Outline);
            Assert.Equal("dot", r.SingleFill.Outline.Dash);
            Assert.True(r.HasShapeStyling);
        }

        [Fact]
        public void EsriSFSNull_MapsToFillStyleNone()
        {
            var r = AutoSymbology.Extract(J(
                "{\"type\":\"simple\",\"symbol\":{\"type\":\"esriSFS\",\"style\":\"esriSFSNull\",\"color\":[1,2,3,4]}}"));
            Assert.Equal("none", r.SingleFill.Style);
        }

        [Theory]
        [InlineData("esriSMSCircle", "circle")]
        [InlineData("esriSMSSquare", "square")]
        [InlineData("esriSMSDiamond", "diamond")]
        [InlineData("esriSMSTriangle", "triangle")]
        [InlineData("esriSMSCross", "cross")]
        [InlineData("esriSMSX", "x")]
        [InlineData("somethingUnknown", "circle")]
        [InlineData("", "circle")]
        public void MarkerShapeMapping_MatchesTheJavaTable(string esriStyle, string expected)
        {
            var r = AutoSymbology.Extract(J(
                "{\"type\":\"simple\",\"symbol\":{\"type\":\"esriSMS\",\"style\":\"" + esriStyle
                + "\",\"color\":[10,20,30],\"size\":12}}"));
            Assert.Equal(expected, r.SingleMarker.Shape);
            Assert.Equal(12f, r.SingleMarker.SizePx);
            // No alpha channel supplied -> opaque.
            Assert.Equal(255, r.SingleMarker.Color.A);
            // A marker-only renderer carries no polyline/polygon styling.
            Assert.False(r.HasShapeStyling);
        }

        [Theory]
        [InlineData("esriSLSDash", "dash")]
        [InlineData("esriSLSDashDot", "dash")]
        [InlineData("esriSLSDashDotDot", "dash")]
        [InlineData("esriSLSDot", "dot")]
        [InlineData("esriSLSSolid", "solid")]
        [InlineData("nonsense", "solid")]
        public void DashMapping_MatchesTheJavaTable(string esriStyle, string expected)
        {
            var r = AutoSymbology.Extract(J(
                "{\"type\":\"simple\",\"symbol\":{\"type\":\"esriSLS\",\"style\":\"" + esriStyle + "\"}}"));
            Assert.Equal(expected, r.SingleStroke.Dash);
        }

        [Fact]
        public void UniqueValueRenderer_KeysByValue_AndUsesDefaultSymbolAsTheSingleStyle()
        {
            var r = AutoSymbology.Extract(J(@"{
              'type':'uniqueValue','field1':'STATUS',
              'uniqueValueInfos':[
                {'value':'OPEN','symbol':{'type':'esriSLS','color':[0,255,0,255],'width':2}},
                {'value':'CLOSED','symbol':{'type':'esriSFS','style':'esriSFSSolid','color':[255,0,0,120]}}
              ],
              'defaultSymbol':{'type':'esriSLS','color':[128,128,128,255],'width':1}
            }".Replace('\'', '"')));

            Assert.Equal("STATUS", r.Field);
            Assert.True(r.StrokeByValue.ContainsKey("OPEN"));
            Assert.True(r.FillByValue.ContainsKey("CLOSED"));
            Assert.NotNull(r.SingleStroke);
            Assert.Equal(Color.FromArgb(255, 128, 128, 128), r.SingleStroke.Color);
        }

        [Fact]
        public void UniqueValueRenderer_FallsBackToFieldWhenField1Absent()
        {
            var r = AutoSymbology.Extract(J(
                "{\"type\":\"uniqueValueRenderer\",\"field\":\"KIND\",\"uniqueValueInfos\":[]}"));
            Assert.Equal("KIND", r.Field);
        }

        [Fact]
        public void UniqueValueRenderer_SkipsEntriesWithNoValueOrNoSymbol()
        {
            var r = AutoSymbology.Extract(J(@"{
              'type':'uniqueValue','field1':'F',
              'uniqueValueInfos':[
                {'value':'','symbol':{'type':'esriSLS'}},
                {'value':'X'},
                {'value':'Y','symbol':{'type':'esriSLS','color':[1,2,3]}}
              ]}".Replace('\'', '"')));

            Assert.Single(r.StrokeByValue);
            Assert.True(r.StrokeByValue.ContainsKey("Y"));
        }

        [Fact]
        public void ClassBreaks_UsesDefaultSymbol_AsTheOnlyResolvableStyle()
        {
            var r = AutoSymbology.Extract(J(@"{
              'type':'classBreaks','field':'POP',
              'classBreakInfos':[{'classMinValue':0,'classMaxValue':10,
                                  'symbol':{'type':'esriSLS','color':[9,9,9],'width':1}}],
              'defaultSymbol':{'type':'esriSLS','color':[5,5,5],'width':4}
            }".Replace('\'', '"')));

            Assert.Equal("POP", r.Field);
            Assert.Equal(4f, r.SingleStroke.WidthPx);   // defaultSymbol wins over the first break
            Assert.Empty(r.StrokeByValue);              // ranges are not resolvable per feature
        }

        [Fact]
        public void ClassBreaks_WithNoDefaultSymbol_FallsBackToTheFirstBreak()
        {
            var r = AutoSymbology.Extract(J(@"{
              'type':'classBreaksRenderer','field':'POP',
              'classBreakInfos':[{'symbol':{'type':'esriSLS','color':[9,9,9],'width':7}}]
            }".Replace('\'', '"')));
            Assert.Equal(7f, r.SingleStroke.WidthPx);
        }

        [Fact]
        public void FieldOverride_WinsOverTheRenderersOwnField()
        {
            var r = AutoSymbology.Extract(
                J("{\"type\":\"uniqueValue\",\"field1\":\"A\",\"uniqueValueInfos\":[]}"), "B");
            Assert.Equal("B", r.Field);
        }

        [Fact]
        public void PictureMarkerOnlyRenderer_YieldsNothing_ItNeedsAnIconset()
        {
            var r = AutoSymbology.Extract(J(
                "{\"type\":\"simple\",\"symbol\":{\"type\":\"esriPMS\",\"imageData\":\"AAAA\"}}"));
            Assert.True(r.IsEmpty);
        }

        [Fact]
        public void MixedSymbolRenderer_ResolvesPerSymbolType_NotByGeometryHint()
        {
            var r = AutoSymbology.Extract(J(@"{
              'type':'uniqueValue','field1':'T',
              'uniqueValueInfos':[
                {'value':'line','symbol':{'type':'esriSLS','color':[1,1,1]}},
                {'value':'area','symbol':{'type':'esriSFS','color':[2,2,2]}},
                {'value':'point','symbol':{'type':'esriSMS','color':[3,3,3]}}
              ]}".Replace('\'', '"')));

            Assert.Single(r.StrokeByValue);
            Assert.Single(r.FillByValue);
            Assert.Single(r.MarkerByValue);
        }

        [Fact]
        public void EsriColor_TreatsShortArraysAsAbsent_AndClampsOutOfRange()
        {
            var tooShort = AutoSymbology.Extract(J(
                "{\"type\":\"simple\",\"symbol\":{\"type\":\"esriSLS\",\"color\":[1,2]}}"));
            Assert.Equal(Color.Blue, tooShort.SingleStroke.Color); // documented esriSLS fallback

            var clamped = AutoSymbology.Extract(J(
                "{\"type\":\"simple\",\"symbol\":{\"type\":\"esriSMS\",\"color\":[999,-5,20,300]}}"));
            Assert.Equal(Color.FromArgb(255, 255, 0, 20), clamped.SingleMarker.Color);
        }

        [Fact]
        public void NumericFieldsGivenAsStrings_ParseInvariantly_NotWithTheCurrentCulture()
        {
            // A de-DE machine parsing "3.5" with the current culture reads 35.
            CultureScope.With("de-DE", () =>
            {
                var r = AutoSymbology.Extract(J(
                    "{\"type\":\"simple\",\"symbol\":{\"type\":\"esriSLS\",\"width\":\"3.5\",\"color\":[1,2,3]}}"));
                Assert.Equal(3.5f, r.SingleStroke.WidthPx);
            });
        }
    }
}
