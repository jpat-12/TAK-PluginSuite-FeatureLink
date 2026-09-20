using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;

namespace FeatureLink.Models
{
    /// <summary>
    /// The geographic bounds of a layer's plotted features, and the camera settings needed to
    /// frame them.
    ///
    /// <para>Computed from the DOWNLOADED features rather than from the service's published
    /// <c>extent</c>, for two reasons. The published extent is in the layer's native spatial
    /// reference — usually Web Mercator — and would need reprojecting, whereas downloaded features
    /// are already WGS84 because the query asks for <c>outSR=4326</c>. And the published extent
    /// describes the whole source layer, while this describes what is actually on the operator's
    /// map, which is what "zoom to this layer" should mean when a download was capped or
    /// filtered.</para>
    /// </summary>
    public sealed class LayerExtent
    {
        public double MinLat { get; set; }
        public double MinLon { get; set; }
        public double MaxLat { get; set; }
        public double MaxLon { get; set; }

        public bool IsValid =>
            !double.IsNaN(MinLat) && !double.IsNaN(MinLon)
            && !double.IsNaN(MaxLat) && !double.IsNaN(MaxLon)
            && MinLat <= MaxLat && MinLon <= MaxLon;

        public double CenterLat => (MinLat + MaxLat) / 2.0;
        public double CenterLon => (MinLon + MaxLon) / 2.0;

        /// <summary>North–south span in degrees.</summary>
        public double LatSpan => MaxLat - MinLat;

        /// <summary>East–west span in degrees.</summary>
        public double LonSpan => MaxLon - MinLon;

        /// <summary>Metres per degree of latitude. Constant enough for framing a viewport.</summary>
        private const double MetresPerDegreeLat = 111_320.0;

        /// <summary>Assumed map viewport width in pixels. The real one is not reachable from a
        /// plugin, and being wrong here only means the layer is framed slightly loose or tight —
        /// the padding below absorbs it.</summary>
        private const double AssumedViewportPixels = 1200.0;

        /// <summary>Extra room around the data so the outermost markers are not against the edge.</summary>
        private const double PaddingFactor = 1.25;

        /// <summary>Floor for the computed resolution, so a single feature — or several at the
        /// same position — does not zoom to a metre per pixel and lose all context.</summary>
        private const double MinMetresPerPixel = 2.0;

        /// <summary>
        /// Ground resolution, in metres per pixel, that frames this extent.
        ///
        /// <para>Longitude degrees shrink with latitude, so the east–west span is scaled by
        /// <c>cos(latitude)</c>; without that, a wide extent at high latitude frames far too
        /// loosely. The larger of the two axes wins, so the whole extent fits rather than only
        /// its narrower dimension.</para>
        /// </summary>
        public double ResolutionMetresPerPixel()
        {
            double latMetres = Math.Abs(LatSpan) * MetresPerDegreeLat;

            double cos = Math.Cos(CenterLat * Math.PI / 180.0);
            if (cos < 0.01) cos = 0.01;   // guard the poles
            double lonMetres = Math.Abs(LonSpan) * MetresPerDegreeLat * cos;

            double widest = Math.Max(latMetres, lonMetres);
            if (widest <= 0) return MinMetresPerPixel;

            return Math.Max(MinMetresPerPixel, widest * PaddingFactor / AssumedViewportPixels);
        }

        /// <summary>Builds an extent from plotted coordinates, or null when none are usable.</summary>
        public static LayerExtent FromPoints(IEnumerable<Tuple<double, double>> latLonPairs)
        {
            if (latLonPairs == null) return null;

            double minLat = double.MaxValue, minLon = double.MaxValue;
            double maxLat = double.MinValue, maxLon = double.MinValue;
            bool any = false;

            foreach (var p in latLonPairs)
            {
                double lat = p.Item1, lon = p.Item2;
                if (double.IsNaN(lat) || double.IsNaN(lon)) continue;
                if (lat < -90 || lat > 90 || lon < -180 || lon > 180) continue;

                if (lat < minLat) minLat = lat;
                if (lat > maxLat) maxLat = lat;
                if (lon < minLon) minLon = lon;
                if (lon > maxLon) maxLon = lon;
                any = true;
            }

            if (!any) return null;
            return new LayerExtent { MinLat = minLat, MinLon = minLon, MaxLat = maxLat, MaxLon = maxLon };
        }

        public XElement ToXElement() => new XElement("Extent",
            new XAttribute("minLat", MinLat.ToString("R", CultureInfo.InvariantCulture)),
            new XAttribute("minLon", MinLon.ToString("R", CultureInfo.InvariantCulture)),
            new XAttribute("maxLat", MaxLat.ToString("R", CultureInfo.InvariantCulture)),
            new XAttribute("maxLon", MaxLon.ToString("R", CultureInfo.InvariantCulture)));

        public static LayerExtent FromXElement(XElement el)
        {
            if (el == null) return null;
            var extent = new LayerExtent
            {
                MinLat = Attr(el, "minLat"),
                MinLon = Attr(el, "minLon"),
                MaxLat = Attr(el, "maxLat"),
                MaxLon = Attr(el, "maxLon"),
            };
            return extent.IsValid ? extent : null;
        }

        private static double Attr(XElement el, string name)
        {
            var a = el.Attribute(name);
            double v;
            return a != null && double.TryParse(a.Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out v) ? v : double.NaN;
        }
    }

    /// <summary>One plotted feature, as the layer's feature list shows it.</summary>
    public sealed class LayerFeature
    {
        public string Uid { get; set; }
        public string Callsign { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }

        /// <summary>Position shown beside the name, in the degrees-and-minutes form the rest of
        /// WinTAK uses for a readout.</summary>
        public string PositionText => string.Format(CultureInfo.InvariantCulture,
            "{0:0.00000}, {1:0.00000}", Lat, Lon);
    }
}
