using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FeatureLink.Services
{
    /// <summary>
    /// The set of individual features an operator has picked for a data package, and the geometry
    /// that picks them.
    ///
    /// <para>SDK-free on purpose. Which features fall inside a dragged box or a drawn radius is
    /// exact, checkable arithmetic, and it is the part that decides what an operator actually
    /// sends — an off-by-one on a boundary means a searcher's marker silently does not travel.
    /// <c>MapAreaSelector</c> holds the WinTAK event plumbing and hands plain coordinates here.</para>
    /// </summary>
    public static class GeoMath
    {
        /// <summary>Mean earth radius, metres (WGS84 authalic).</summary>
        public const double EarthRadiusMetres = 6_371_008.8;

        /// <summary>
        /// Great-circle distance in metres.
        ///
        /// <para>Haversine rather than the cheaper equirectangular approximation: a selection
        /// radius is a claim about what the operator included, and at the hundred-kilometre scale
        /// a search area can reach, the flat approximation is wrong by enough to drop or add
        /// features near the edge.</para>
        /// </summary>
        public static double DistanceMetres(double lat1, double lon1, double lat2, double lon2)
        {
            double phi1 = lat1 * Math.PI / 180.0;
            double phi2 = lat2 * Math.PI / 180.0;
            double dPhi = (lat2 - lat1) * Math.PI / 180.0;
            double dLambda = (lon2 - lon1) * Math.PI / 180.0;

            double a = Math.Sin(dPhi / 2) * Math.Sin(dPhi / 2)
                     + Math.Cos(phi1) * Math.Cos(phi2) * Math.Sin(dLambda / 2) * Math.Sin(dLambda / 2);

            // Clamp: accumulated error can push `a` a hair above 1 for antipodal points, and
            // Math.Sqrt of a negative then yields NaN for a distance that is merely very large.
            if (a > 1.0) a = 1.0;
            if (a < 0.0) a = 0.0;

            return 2.0 * EarthRadiusMetres * Math.Asin(Math.Sqrt(a));
        }

        public static bool IsUsable(double lat, double lon)
        {
            return !double.IsNaN(lat) && !double.IsNaN(lon)
                && !double.IsInfinity(lat) && !double.IsInfinity(lon)
                && lat >= -90 && lat <= 90 && lon >= -180 && lon <= 180;
        }
    }

    /// <summary>A shape the operator drew on the map to select features with.</summary>
    public interface ISelectionShape
    {
        /// <summary>True when a feature at this position is inside the shape.</summary>
        bool Contains(double lat, double lon);

        /// <summary>One line naming the shape, for the review step.</summary>
        string Describe();
    }

    /// <summary>
    /// An axis-aligned latitude/longitude box — the "map area" selection, built from the two
    /// corners of a drag.
    /// </summary>
    public sealed class GeoBounds : ISelectionShape
    {
        public double MinLat { get; }
        public double MinLon { get; }
        public double MaxLat { get; }
        public double MaxLon { get; }

        /// <summary>True when the box was drawn across the antimeridian, so it wraps rather than
        /// spanning the long way round the globe.</summary>
        public bool CrossesAntimeridian { get; }

        private GeoBounds(double minLat, double minLon, double maxLat, double maxLon, bool wraps)
        {
            MinLat = minLat; MinLon = minLon; MaxLat = maxLat; MaxLon = maxLon;
            CrossesAntimeridian = wraps;
        }

        /// <summary>
        /// Builds a box from the two corners of a drag, in any order.
        ///
        /// <para>When the two longitudes are more than 180° apart the operator dragged across the
        /// antimeridian, and the box they meant is the short way round. Taking a naive min/max
        /// there would select nearly the whole world instead of the strip they drew.</para>
        /// </summary>
        public static GeoBounds FromCorners(double lat1, double lon1, double lat2, double lon2)
        {
            if (!GeoMath.IsUsable(lat1, lon1) || !GeoMath.IsUsable(lat2, lon2)) return null;

            double minLat = Math.Min(lat1, lat2);
            double maxLat = Math.Max(lat1, lat2);
            double minLon = Math.Min(lon1, lon2);
            double maxLon = Math.Max(lon1, lon2);

            bool wraps = (maxLon - minLon) > 180.0;
            if (wraps)
            {
                // Swap the edges: the selected strip runs from the eastern corner, over +180,
                // and on to the western one.
                double west = maxLon, east = minLon;
                return new GeoBounds(minLat, west, maxLat, east, true);
            }

            return new GeoBounds(minLat, minLon, maxLat, maxLon, false);
        }

        public bool Contains(double lat, double lon)
        {
            if (!GeoMath.IsUsable(lat, lon)) return false;
            if (lat < MinLat || lat > MaxLat) return false;

            return CrossesAntimeridian
                ? (lon >= MinLon || lon <= MaxLon)   // outside the gap, i.e. over the dateline
                : (lon >= MinLon && lon <= MaxLon);
        }

        public string Describe()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "area {0:0.0000},{1:0.0000} to {2:0.0000},{3:0.0000}", MinLat, MinLon, MaxLat, MaxLon);
        }
    }

    /// <summary>A circle around a centre point — the "draw tool" selection, where the radius is
    /// the distance from where the drag began to where it ended.</summary>
    public sealed class GeoCircle : ISelectionShape
    {
        public double CenterLat { get; }
        public double CenterLon { get; }
        public double RadiusMetres { get; }

        private GeoCircle(double lat, double lon, double radius)
        {
            CenterLat = lat; CenterLon = lon; RadiusMetres = radius;
        }

        public static GeoCircle FromCenterAndEdge(double centerLat, double centerLon,
                                                  double edgeLat, double edgeLon)
        {
            if (!GeoMath.IsUsable(centerLat, centerLon) || !GeoMath.IsUsable(edgeLat, edgeLon))
                return null;

            double radius = GeoMath.DistanceMetres(centerLat, centerLon, edgeLat, edgeLon);
            return FromRadius(centerLat, centerLon, radius);
        }

        public static GeoCircle FromRadius(double centerLat, double centerLon, double radiusMetres)
        {
            if (!GeoMath.IsUsable(centerLat, centerLon)) return null;
            if (double.IsNaN(radiusMetres) || radiusMetres < 0) return null;
            return new GeoCircle(centerLat, centerLon, radiusMetres);
        }

        public bool Contains(double lat, double lon)
        {
            if (!GeoMath.IsUsable(lat, lon)) return false;
            return GeoMath.DistanceMetres(CenterLat, CenterLon, lat, lon) <= RadiusMetres;
        }

        public string Describe()
        {
            string radius = RadiusMetres >= 1000
                ? (RadiusMetres / 1000.0).ToString("0.##", CultureInfo.InvariantCulture) + " km"
                : RadiusMetres.ToString("0", CultureInfo.InvariantCulture) + " m";

            return string.Format(CultureInfo.InvariantCulture,
                "{0} radius around {1:0.0000}, {2:0.0000}", radius, CenterLat, CenterLon);
        }
    }

    /// <summary>One feature available to, or chosen for, a package.</summary>
    public sealed class SelectableFeature
    {
        public string LayerUrl { get; set; }
        public string LayerName { get; set; }
        public string Uid { get; set; }
        public string Callsign { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }

        public string PositionText
        {
            get
            {
                return string.Format(CultureInfo.InvariantCulture, "{0:0.00000}, {1:0.00000}", Lat, Lon);
            }
        }

        /// <summary>What the review list shows: the feature's name and which layer it came from,
        /// because a selection spanning layers is otherwise a list of unattributed names.</summary>
        public string DisplayName
        {
            get { return string.IsNullOrEmpty(Callsign) ? Uid : Callsign; }
        }
    }

    /// <summary>
    /// The features chosen so far, across any number of layers.
    ///
    /// <para>Keyed by feature UID, which is what makes "add more" work the way the operator
    /// expects: re-entering the selection step keeps everything already chosen, and drawing a
    /// second area that overlaps the first adds only what is new rather than double-counting or
    /// toggling existing picks back off.</para>
    /// </summary>
    public sealed class FeatureSelection
    {
        private readonly Dictionary<string, SelectableFeature> _byUid =
            new Dictionary<string, SelectableFeature>(StringComparer.Ordinal);

        /// <summary>Insertion order, so the review list does not reshuffle as the operator adds.</summary>
        private readonly List<string> _order = new List<string>();

        public int Count { get { return _byUid.Count; } }

        public bool IsEmpty { get { return _byUid.Count == 0; } }

        public bool Contains(string uid)
        {
            return !string.IsNullOrEmpty(uid) && _byUid.ContainsKey(uid);
        }

        /// <summary>Adds a feature. Returns true when it was not already selected.</summary>
        public bool Add(SelectableFeature feature)
        {
            if (feature == null || string.IsNullOrEmpty(feature.Uid)) return false;
            if (_byUid.ContainsKey(feature.Uid)) return false;

            _byUid[feature.Uid] = feature;
            _order.Add(feature.Uid);
            return true;
        }

        /// <summary>Removes a feature. Returns true when it was selected.</summary>
        public bool Remove(string uid)
        {
            if (string.IsNullOrEmpty(uid) || !_byUid.Remove(uid)) return false;
            _order.Remove(uid);
            return true;
        }

        /// <summary>Adds if absent, removes if present — the click-a-point-on-the-map gesture.
        /// Returns true when the feature ended up selected.</summary>
        public bool Toggle(SelectableFeature feature)
        {
            if (feature == null || string.IsNullOrEmpty(feature.Uid)) return false;
            if (_byUid.ContainsKey(feature.Uid)) { Remove(feature.Uid); return false; }
            Add(feature);
            return true;
        }

        public void Clear()
        {
            _byUid.Clear();
            _order.Clear();
        }

        /// <summary>The selection in the order it was built.</summary>
        public IReadOnlyList<SelectableFeature> Items
        {
            get { return _order.Select(uid => _byUid[uid]).ToList(); }
        }

        /// <summary>
        /// Adds every candidate inside the shape. Returns how many were newly added — which is
        /// what the status line reports, because "added 0" after drawing an area is the signal
        /// that the operator drew somewhere empty rather than that nothing happened.
        /// </summary>
        public int AddWithin(ISelectionShape shape, IEnumerable<SelectableFeature> candidates)
        {
            if (shape == null) return 0;

            int added = 0;
            foreach (var feature in candidates ?? Enumerable.Empty<SelectableFeature>())
            {
                if (feature == null) continue;
                if (!shape.Contains(feature.Lat, feature.Lon)) continue;
                if (Add(feature)) added++;
            }
            return added;
        }

        /// <summary>Removes every selected feature inside the shape. Returns how many went.</summary>
        public int RemoveWithin(ISelectionShape shape)
        {
            if (shape == null) return 0;

            var doomed = Items.Where(f => shape.Contains(f.Lat, f.Lon)).Select(f => f.Uid).ToList();
            foreach (string uid in doomed) Remove(uid);
            return doomed.Count;
        }

        /// <summary>The selection grouped by source layer — the form the package builder needs,
        /// since configs and iconsets are per layer while the features are per point.</summary>
        public IReadOnlyList<LayerGroup> ByLayer()
        {
            return Items
                .GroupBy(f => f.LayerUrl ?? string.Empty, StringComparer.Ordinal)
                .Select(g => new LayerGroup(
                    g.Key,
                    g.Select(f => f.LayerName).FirstOrDefault(n => !string.IsNullOrEmpty(n)),
                    g.ToList()))
                .ToList();
        }

        /// <summary>Selected features from one layer.</summary>
        public sealed class LayerGroup
        {
            public LayerGroup(string layerUrl, string layerName, IReadOnlyList<SelectableFeature> features)
            {
                LayerUrl = layerUrl;
                LayerName = layerName;
                Features = features;
            }

            public string LayerUrl { get; }
            public string LayerName { get; }
            public IReadOnlyList<SelectableFeature> Features { get; }
        }

        /// <summary>One line for the review step and the status bar.</summary>
        public string Describe()
        {
            if (IsEmpty) return "no features selected";

            int layers = ByLayer().Count;
            string features = Count == 1 ? "1 feature" : Count + " features";
            string from = layers == 1 ? "1 layer" : layers + " layers";
            return features + " from " + from;
        }
    }
}
