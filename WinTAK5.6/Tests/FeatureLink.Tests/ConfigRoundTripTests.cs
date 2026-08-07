using System;
using System.Collections.Generic;
using System.Drawing;
using System.Xml.Linq;
using FeatureLink.Models;
using FeatureLink.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FeatureLink.Tests
{
    /// <summary>
    /// CONFIG-FORMAT.md conformance for the WinTAK consumer, and the settings round-trip.
    ///
    /// Appendix F §5's concrete interop break was that a Portal-authored config carrying
    /// polyline/polygon shape styling was silently and completely dropped on WinTAK. These cases
    /// take a Mode-2 compact payload through the exact path the plugin uses — parse, persist to
    /// settings.xml, reload, resolve per feature — and assert the styling survives every hop.
    /// </summary>
    public class ConfigRoundTripTests
    {
        /// <summary>A Mode-2 (v:2) compact share, as TAK Portal's <c>buildUrlConfigExport()</c>
        /// produces, extended with the <c>shp</c> block this remediation adds.</summary>
        private const string Mode2Share = @"{
          'v': 2,
          'url': 'https://services.example.com/arcgis/rest/services/Demo/FeatureServer/0',
          'layer': { 'name': 'Demo Layer', 'opacity': 1.0, 'visible': true },
          'sym': { 't':'uv', 'c':'#3388ff', 'f':'STATUS', 'op':1.0,
                   'uv':[{'v':'OPEN','c':'#00ff00'},{'v':'CLOSED','c':'#ff0000'}] },
          'lbl': { 'f':'NAME', 'sz':12, 'c':'#ffffff' },
          'popup': { 't':'NAME', 'flds':[['NAME','Name'],'STATUS'] },
          'shp': { 'f':'STATUS',
                   's':{'sc':'#FF808080','sw':1,'sd':'solid','fc':'#00000000','fs':'none'},
                   'bv':{'OPEN':{'sc':'#FF00FF00','sw':4,'sd':'dash','fc':'#4000FF00','fs':'solid'}} },
          'freq': { 'iv': 180, 'u': 's' },
          'private': false
        }";

        private static Dictionary<string, string> OpenFeature() => new Dictionary<string, string>
        {
            ["NAME"] = "Alpha",
            ["STATUS"] = "OPEN",
        };

        private static ArcGisLayer LayerFromShare(string shareJson)
        {
            var o = SafeJson.ParseObject(shareJson.Replace('\'', '"'));
            var layer = new ArcGisLayer((string)o["layer"]["name"], (string)o["url"], "public")
            {
                RecurrenceUnit = (string)o["freq"]["u"],
                RecurrenceInterval = (int)o["freq"]["iv"],
                HasDisplayConfig = true,
                SymJson = o["sym"]?.ToString(Newtonsoft.Json.Formatting.None),
                LblJson = o["lbl"]?.ToString(Newtonsoft.Json.Formatting.None),
                PopupJson = o["popup"]?.ToString(Newtonsoft.Json.Formatting.None),
                ShpJson = o["shp"]?.ToString(Newtonsoft.Json.Formatting.None),
            };
            return layer;
        }

        [Fact]
        public void Mode2Share_ResolvesColourLabelRemarksAndShapeStyle()
        {
            var layer = LayerFromShare(Mode2Share);
            var attrs = OpenFeature();

            var colour = DisplayStyleResolver.ResolveColor(SafeJson.ParseObject(layer.SymJson), attrs);
            Assert.Equal(Color.FromArgb(255, 0, 255, 0), colour);

            var label = DisplayStyleResolver.ResolveLabel(SafeJson.ParseObject(layer.LblJson), attrs, "fallback");
            Assert.Equal("Alpha", label);

            var remarks = DisplayStyleResolver.BuildRemarks(SafeJson.ParseObject(layer.PopupJson), attrs);
            Assert.Equal("Name: Alpha\nSTATUS: OPEN", remarks);

            // This is the assertion that would have failed on every previous WinTAK build,
            // silently and with no log line.
            var shape = DisplayStyleResolver.ResolveShapeStyle(SafeJson.ParseObject(layer.ShpJson), attrs);
            Assert.NotNull(shape);
            Assert.Equal(Color.FromArgb(255, 0, 255, 0), shape.StrokeColor);
            Assert.Equal(4f, shape.StrokeWidthPx);
            Assert.Equal("dash", shape.StrokeDash);
            Assert.Equal("solid", shape.FillStyle);
        }

        [Fact]
        public void Mode2Share_SurvivesTheSettingsXmlRoundTrip()
        {
            var original = LayerFromShare(Mode2Share);
            var reloaded = ArcGisLayer.FromXElement(original.ToXElement());

            Assert.Equal(original.Name, reloaded.Name);
            Assert.Equal(original.Url, reloaded.Url);
            Assert.Equal(original.Type, reloaded.Type);
            Assert.Equal(original.SymJson, reloaded.SymJson);
            Assert.Equal(original.LblJson, reloaded.LblJson);
            Assert.Equal(original.PopupJson, reloaded.PopupJson);
            Assert.Equal(original.ShpJson, reloaded.ShpJson);   // C-23's WinTAK half
            Assert.Equal(original.RecurrenceInterval, reloaded.RecurrenceInterval);
            Assert.Equal(original.RecurrenceUnit, reloaded.RecurrenceUnit);
            Assert.True(reloaded.HasDisplayConfig);

            var shape = DisplayStyleResolver.ResolveShapeStyle(
                SafeJson.ParseObject(reloaded.ShpJson), OpenFeature());
            Assert.Equal(Color.FromArgb(255, 0, 255, 0), shape.StrokeColor);
        }

        [Fact]
        public void AutoDerivedConfig_FromAPolygonRenderer_ProducesAResolvableShpBlock()
        {
            // The end-to-end C-07 path: layer metadata -> AutoSymbology -> compact "shp" ->
            // per-feature resolution. None of this existed on WinTAK.
            var renderer = JObject.Parse(@"{
              ""type"":""uniqueValue"",""field1"":""ZONE"",
              ""uniqueValueInfos"":[
                {""value"":""HOT"",""symbol"":{""type"":""esriSFS"",""style"":""esriSFSSolid"",
                   ""color"":[255,0,0,80],
                   ""outline"":{""type"":""esriSLS"",""color"":[255,0,0,255],""width"":3,""style"":""esriSLSSolid""}}}
              ],
              ""defaultSymbol"":{""type"":""esriSFS"",""style"":""esriSFSNull"",""color"":[0,0,0,0],
                   ""outline"":{""type"":""esriSLS"",""color"":[128,128,128,255],""width"":1}}}");

            var extracted = AutoSymbology.Extract(renderer);
            Assert.True(extracted.HasShapeStyling);

            var shp = DisplayStyleResolver.BuildShapeConfigJson(extracted);
            Assert.NotNull(shp);

            var hot = DisplayStyleResolver.ResolveShapeStyle(shp,
                new Dictionary<string, string> { ["ZONE"] = "HOT" });
            Assert.Equal(Color.FromArgb(255, 255, 0, 0), hot.StrokeColor);
            Assert.Equal(3f, hot.StrokeWidthPx);
            Assert.Equal(80, hot.FillColor.A);
            Assert.Equal("solid", hot.FillStyle);

            var cold = DisplayStyleResolver.ResolveShapeStyle(shp,
                new Dictionary<string, string> { ["ZONE"] = "COLD" });
            Assert.Equal(Color.FromArgb(255, 128, 128, 128), cold.StrokeColor);
            Assert.Equal("none", cold.FillStyle);
        }

        [Fact]
        public void ShareWithNoShapeStyling_LeavesShpNull_AndNothingBreaks()
        {
            string share = Mode2Share.Replace(
                @"'shp': { 'f':'STATUS',
                   's':{'sc':'#FF808080','sw':1,'sd':'solid','fc':'#00000000','fs':'none'},
                   'bv':{'OPEN':{'sc':'#FF00FF00','sw':4,'sd':'dash','fc':'#4000FF00','fs':'solid'}} },", "");
            var layer = LayerFromShare(share);
            Assert.Null(layer.ShpJson);
            Assert.Null(DisplayStyleResolver.ResolveShapeStyle(
                SafeJson.ParseObjectOrNull(layer.ShpJson), OpenFeature()));
        }
    }

    /// <summary>The layer model's own contracts, including the three-way default disagreement the
    /// audit found and the peer-supplied interval clamp.</summary>
    public class ArcGisLayerTests
    {
        [Fact]
        public void MissingRecurrenceUnit_DefaultsToSeconds_Consistently()
        {
            // The field initialiser said "s", ToXElement wrote "s", FromXElement read "min" and
            // RecurrenceTimeSpan defaulted to "min" — a layer whose element was absent silently
            // changed its refresh period by 60x.
            var reloaded = ArcGisLayer.FromXElement(XElement.Parse(
                "<Layer><Name>x</Name><Url>u</Url><RecurrenceInterval>60</RecurrenceInterval></Layer>"));
            Assert.Equal(ArcGisLayer.DefaultRecurrenceUnit, reloaded.RecurrenceUnit);
            Assert.Equal(TimeSpan.FromSeconds(60), reloaded.RecurrenceTimeSpan());
        }

        [Theory]
        [InlineData(1, ArcGisLayer.MinRecurrenceSeconds)]
        [InlineData(0, 0)]
        [InlineData(-5, 0)]
        [InlineData(180, 180)]
        [InlineData(int.MaxValue, 1_000_000)]
        public void RecurrenceInterval_IsClamped_SoAPeerCannotDriveASecondlyReDownload(int input, int expected)
        {
            // {"freq":{"iv":1}} from a received share meant a full layer re-download every second,
            // forever, on a network peer's say-so.
            var layer = new ArcGisLayer("n", "u", "public") { RecurrenceUnit = "s", RecurrenceInterval = input };
            Assert.Equal(expected, layer.RecurrenceInterval);
        }

        [Theory]
        [InlineData("s", 60, 60)]
        [InlineData("min", 2, 120)]
        [InlineData("hr", 1, 3600)]
        public void RecurrenceTimeSpan_HonoursAllThreeUnits(string unit, int interval, int expectedSeconds)
        {
            var layer = new ArcGisLayer("n", "u", "public") { RecurrenceUnit = unit, RecurrenceInterval = interval };
            Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), layer.RecurrenceTimeSpan());
        }

        [Fact]
        public void UnknownRecurrenceUnit_FallsBackToTheDefault()
        {
            var layer = new ArcGisLayer("n", "u", "public") { RecurrenceUnit = "fortnights" };
            Assert.Equal(ArcGisLayer.DefaultRecurrenceUnit, layer.RecurrenceUnit);
        }

        [Fact]
        public void MalformedElements_CostOneFieldEach_NotTheWholeConfiguration()
        {
            // A single <RecurrenceInterval>abc</> used to throw FormatException into
            // SettingsStore.Load's bare catch and discard every layer the operator had.
            var layer = ArcGisLayer.FromXElement(XElement.Parse(
                "<Layer><Name>Kept</Name><Url>https://u/FeatureServer/0</Url>"
                + "<RecurrenceInterval>abc</RecurrenceInterval>"
                + "<Visible>notabool</Visible>"
                + "<LastSyncTicks>xyz</LastSyncTicks></Layer>"));

            Assert.Equal("Kept", layer.Name);
            Assert.Equal("https://u/FeatureServer/0", layer.Url);
            Assert.Equal(0, layer.RecurrenceInterval);
            Assert.True(layer.Visible);          // documented default
            Assert.Equal(0, layer.LastSyncTicks);
        }

        [Fact]
        public void LastSync_TreatsAnUnspecifiedKindAsUtc_NotLocal()
        {
            var layer = new ArcGisLayer("n", "u", "public");
            var unspecified = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified);
            layer.LastSync = unspecified;
            Assert.Equal(unspecified.Ticks, layer.LastSyncTicks);
            Assert.Equal(DateTimeKind.Utc, layer.LastSync.Kind);
        }

        [Fact]
        public void AllPersistedFields_SurviveARoundTrip()
        {
            var original = new ArcGisLayer("Name", "https://u/FeatureServer/0", "private")
            {
                Access = "org",
                HasDisplayConfig = true,
                SymJson = "{\"t\":\"s\"}",
                LblJson = "{\"f\":\"N\"}",
                PopupJson = "{\"flds\":[\"A\"]}",
                ShpJson = "{\"s\":{\"sc\":\"#FF112233\"}}",
                FeatureCount = 42,
                LastSyncTicks = 638000000000000000L,
                DownloadEnabled = true,
                RecurrenceUnit = "min",
                RecurrenceInterval = 5,
                IsPliLayer = true,
                Visible = false,
            };

            var r = ArcGisLayer.FromXElement(original.ToXElement());
            Assert.Equal(original.Name, r.Name);
            Assert.Equal(original.Url, r.Url);
            Assert.Equal(original.Type, r.Type);
            Assert.Equal(original.Access, r.Access);
            Assert.Equal(original.HasDisplayConfig, r.HasDisplayConfig);
            Assert.Equal(original.SymJson, r.SymJson);
            Assert.Equal(original.LblJson, r.LblJson);
            Assert.Equal(original.PopupJson, r.PopupJson);
            Assert.Equal(original.ShpJson, r.ShpJson);
            Assert.Equal(original.FeatureCount, r.FeatureCount);
            Assert.Equal(original.LastSyncTicks, r.LastSyncTicks);
            Assert.Equal(original.DownloadEnabled, r.DownloadEnabled);
            Assert.Equal(original.RecurrenceUnit, r.RecurrenceUnit);
            Assert.Equal(original.RecurrenceInterval, r.RecurrenceInterval);
            Assert.Equal(original.IsPliLayer, r.IsPliLayer);
            Assert.Equal(original.Visible, r.Visible);
        }
    }
}
