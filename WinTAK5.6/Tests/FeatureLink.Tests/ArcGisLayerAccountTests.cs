using System.Xml.Linq;
using FeatureLink.Models;
using Xunit;

namespace FeatureLink.Tests
{
    /// <summary>
    /// The owning-account binding on a layer (multi-account, commit 4).
    ///
    /// The whole point of these cases is that adding OwnerAccountKey is *additive*: an existing
    /// operator's settings.xml, written by a build that never heard of the field, must still load
    /// with every other field intact. FromXElement reads by element name (el.Element(name)) and
    /// never positionally, so a new element can appear anywhere without shifting the others —
    /// these tests are the standing proof of that.
    /// </summary>
    public class ArcGisLayerAccountTests
    {
        private const string Key = "https://www.arcgis.com|jdoe_org";

        /// <summary>A layer with every persisted field set, so the schema-wide regression guard
        /// and the legacy-XML case both exercise the full element set.</summary>
        private static ArcGisLayer FullyPopulated()
        {
            return new ArcGisLayer("Search Grid", "https://services.example.com/FeatureServer/0", "private")
            {
                Access = "private",
                HasDisplayConfig = true,
                LargeDownloadAccepted = true,
                SymJson = "{\"t\":\"s\",\"c\":\"#3388ff\"}",
                LblJson = "{\"f\":\"NAME\"}",
                PopupJson = "{\"t\":\"NAME\",\"flds\":[\"A\"]}",
                ShpJson = "{\"s\":{\"sc\":\"#FF112233\"}}",
                FeatureCount = 4213,
                LastSyncTicks = 638000000000000000L,
                DownloadEnabled = true,
                RecurrenceUnit = "min",
                RecurrenceInterval = 5,
                IsPliLayer = true,
                Visible = false,
                OwnerAccountKey = Key,
            };
        }

        private static void AssertAllFieldsEqual(ArcGisLayer expected, ArcGisLayer actual)
        {
            Assert.Equal(expected.Name, actual.Name);
            Assert.Equal(expected.Url, actual.Url);
            Assert.Equal(expected.Type, actual.Type);
            Assert.Equal(expected.IsPrivate, actual.IsPrivate);
            Assert.Equal(expected.Access, actual.Access);
            Assert.Equal(expected.HasDisplayConfig, actual.HasDisplayConfig);
            Assert.Equal(expected.LargeDownloadAccepted, actual.LargeDownloadAccepted);
            Assert.Equal(expected.SymJson, actual.SymJson);
            Assert.Equal(expected.LblJson, actual.LblJson);
            Assert.Equal(expected.PopupJson, actual.PopupJson);
            Assert.Equal(expected.ShpJson, actual.ShpJson);
            Assert.Equal(expected.FeatureCount, actual.FeatureCount);
            Assert.Equal(expected.LastSyncTicks, actual.LastSyncTicks);
            Assert.Equal(expected.DownloadEnabled, actual.DownloadEnabled);
            Assert.Equal(expected.RecurrenceUnit, actual.RecurrenceUnit);
            Assert.Equal(expected.RecurrenceInterval, actual.RecurrenceInterval);
            Assert.Equal(expected.IsPliLayer, actual.IsPliLayer);
            Assert.Equal(expected.Visible, actual.Visible);
        }

        [Fact]
        public void OwnerAccountKey_SurvivesTheXmlRoundTrip_Verbatim()
        {
            var original = new ArcGisLayer("n", "https://u/FeatureServer/0", "private")
            {
                OwnerAccountKey = Key,
            };

            var reloaded = ArcGisLayer.FromXElement(original.ToXElement());

            Assert.Equal(Key, reloaded.OwnerAccountKey);
        }

        [Fact]
        public void UnboundOwnerAccountKey_RoundTripsAsNull_NotEmptyString()
        {
            // null is written as an empty element, so without NullIfEmpty on the read side an
            // unbound layer would come back as "" — a key that matches no account and is not null,
            // which would make "is this layer unbound?" answer wrong everywhere it is asked.
            var original = new ArcGisLayer("n", "https://u/FeatureServer/0", "public");
            Assert.Null(original.OwnerAccountKey);

            var reloaded = ArcGisLayer.FromXElement(original.ToXElement());

            Assert.Null(reloaded.OwnerAccountKey);
        }

        [Fact]
        public void SettingsXmlWithoutTheElement_YieldsNull_AndKeepsEveryOtherField()
        {
            // An existing operator's settings.xml, reproduced by removing the element from a
            // current write rather than hand-writing XML, so this case keeps covering the real
            // schema as fields are added to the model.
            var original = FullyPopulated();
            XElement legacy = original.ToXElement();
            legacy.Element("OwnerAccountKey").Remove();
            Assert.Empty(legacy.Elements("OwnerAccountKey"));

            var reloaded = ArcGisLayer.FromXElement(legacy);

            Assert.Null(reloaded.OwnerAccountKey);
            AssertAllFieldsEqual(original, reloaded);
        }

        [Fact]
        public void KeyWithXmlSignificantCharacters_SurvivesEscapingIntact()
        {
            // The key is portalUrl|username and both halves are server-supplied; an & in a portal
            // query string or a quote in a display name must not corrupt settings.xml or come back
            // altered.
            const string hostile = "https://p.example.com/?a=1&b=2|o'brien <\"test\"> &amp;";
            var original = new ArcGisLayer("n", "https://u/FeatureServer/0", "private")
            {
                OwnerAccountKey = hostile,
            };

            // Through real serialised text, not just the in-memory XElement, because escaping is
            // what this asserts.
            var reloaded = ArcGisLayer.FromXElement(XElement.Parse(original.ToXElement().ToString()));

            Assert.Equal(hostile, reloaded.OwnerAccountKey);
        }

        [Fact]
        public void FullyPopulatedLayer_RoundTripsEveryPersistedField()
        {
            var original = FullyPopulated();

            var reloaded = ArcGisLayer.FromXElement(original.ToXElement());

            AssertAllFieldsEqual(original, reloaded);
            Assert.Equal(original.OwnerAccountKey, reloaded.OwnerAccountKey);
        }

        [Fact]
        public void OwnerAccountKey_IsIndependentOfIsPrivate()
        {
            // IsPrivate stays the public/private classification and is deliberately NOT derived
            // from, nor a substitute for, the owning account: a private layer can be unbound
            // (received while signed out) and the two must not be conflated at the sync site.
            var privateUnbound = new ArcGisLayer("n", "u", "private");
            Assert.True(privateUnbound.IsPrivate);
            Assert.Null(privateUnbound.OwnerAccountKey);

            privateUnbound.OwnerAccountKey = Key;
            Assert.True(privateUnbound.IsPrivate);
        }

        [Fact]
        public void SettingAnOwnerAccountKey_RaisesPropertyChanged_SoTheLayerRowRebinds()
        {
            var layer = new ArcGisLayer("n", "u", "private");
            string raised = null;
            layer.PropertyChanged += (s, e) => raised = e.PropertyName;

            layer.OwnerAccountKey = Key;

            Assert.Equal("OwnerAccountKey", raised);
        }
    }
}
