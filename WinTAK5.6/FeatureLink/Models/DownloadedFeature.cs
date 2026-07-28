using System.Collections.Generic;

namespace FeatureLink.Models
{
    /// <summary>
    /// One feature downloaded from an ArcGIS Feature Service layer, ready to convert into a
    /// CoT event. Mirrors ArcGISRestClient.DownloadedFeature from the ATAK plugin.
    /// </summary>
    public sealed class DownloadedFeature
    {
        public string Uid { get; }
        public string CotType { get; }
        public string Callsign { get; }
        public string Remarks { get; }
        public double Lat { get; }
        public double Lon { get; }
        public double Hae { get; }

        /// <summary>All ArcGIS attributes as strings, keyed by field name — kept around for any
        /// future display-config / symbology resolution (see DisplayConfig.java, out of scope
        /// for this pass).</summary>
        public IReadOnlyDictionary<string, string> Attributes { get; }

        public DownloadedFeature(string uid, string cotType, string callsign, string remarks,
            double lat, double lon, double hae, IReadOnlyDictionary<string, string> attributes)
        {
            Uid = uid;
            CotType = cotType;
            Callsign = callsign;
            Remarks = remarks;
            Lat = lat;
            Lon = lon;
            Hae = hae;
            Attributes = attributes ?? new Dictionary<string, string>();
        }
    }
}
