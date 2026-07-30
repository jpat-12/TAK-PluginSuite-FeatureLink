using System.Xml.Linq;

namespace FeatureLink.Models
{
    /// <summary>
    /// PLI (Position Location Information) auto-send configuration — mirrors the
    /// PREF_PLI_LAYER_URL / PREF_PLI_OBJECT_ID / PREF_PLI_AUTO_SEND SharedPreferences keys from
    /// FeatureLinkDropDownReceiver.java.
    /// </summary>
    public class PliSettings
    {
        public string PliLayerUrl { get; set; }

        /// <summary>ObjectID of this device's own PLI row once the first add succeeds; -1 = not
        /// yet created, so the next send does an add rather than an update.</summary>
        public long PliObjectId { get; set; } = -1;

        public bool AutoSendEnabled { get; set; }

        public XElement ToXElement()
        {
            return new XElement("Pli",
                new XElement("PliLayerUrl", PliLayerUrl ?? string.Empty),
                new XElement("PliObjectId", PliObjectId),
                new XElement("AutoSendEnabled", AutoSendEnabled));
        }

        public static PliSettings FromXElement(XElement el)
        {
            if (el == null) return new PliSettings();
            return new PliSettings
            {
                PliLayerUrl = (string)el.Element("PliLayerUrl"),
                PliObjectId = (long?)el.Element("PliObjectId") ?? -1,
                AutoSendEnabled = (bool?)el.Element("AutoSendEnabled") ?? false,
            };
        }
    }
}
