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

        /// <summary>All ArcGIS attributes as strings (culture-invariant), keyed by field name —
        /// the input to <see cref="Services.DisplayStyleResolver"/>'s icon/colour/label/remarks
        /// and shape-style resolution.</summary>
        public IReadOnlyDictionary<string, string> Attributes { get; }

        /// <summary>"point" | "polyline" | "polygon" | "multipoint" — which esri geometry the
        /// feature came from. Needed so shape styling (esriSLS/esriSFS) is applied only to the
        /// features it actually describes; the coordinate itself is still the first vertex, the
        /// same simplification the ATAK plugin makes.</summary>
        public string GeometryKind { get; }

        public DownloadedFeature(string uid, string cotType, string callsign, string remarks,
            double lat, double lon, double hae, IReadOnlyDictionary<string, string> attributes,
            string geometryKind = "point")
        {
            GeometryKind = string.IsNullOrEmpty(geometryKind) ? "point" : geometryKind;
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
