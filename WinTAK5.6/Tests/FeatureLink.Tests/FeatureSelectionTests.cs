using System;
using System.Collections.Generic;
using System.Linq;
using FeatureLink.Services;
using Xunit;

namespace FeatureLink.Tests
{
    /// <summary>
    /// Covers the feature-selection geometry and set behaviour.
    ///
    /// <para>This is the arithmetic that decides what an operator actually sends. A feature
    /// wrongly excluded from a drawn area does not announce itself — the package sends, the status
    /// line is green, and a searcher's marker simply is not in it. That asymmetry is why the
    /// boundary cases below are tested rather than eyeballed.</para>
    /// </summary>
    public class FeatureSelectionTests
    {
        private static SelectableFeature F(string uid, double lat, double lon,
            string layerUrl = "https://h/a/FeatureServer/0", string layerName = "Teams")
        {
            return new SelectableFeature
            {
                Uid = uid,
                Callsign = uid,
                Lat = lat,
                Lon = lon,
                LayerUrl = layerUrl,
                LayerName = layerName,
            };
        }

        // ── distance ────────────────────────────────────────────────────────────

        /// <summary>One degree of latitude is ~111.2 km anywhere on the globe.</summary>
        [Fact]
        public void A_degree_of_latitude_is_about_111_kilometres()
        {
            double d = GeoMath.DistanceMetres(39.0, -88.0, 40.0, -88.0);

            Assert.InRange(d, 111_000, 111_400);
        }

        /// <summary>Longitude degrees shrink toward the poles — the property an equirectangular
        /// approximation gets wrong and a search-area radius depends on.</summary>
        [Fact]
        public void A_degree_of_longitude_shrinks_with_latitude()
        {
            double equator = GeoMath.DistanceMetres(0.0, 0.0, 0.0, 1.0);
            double high = GeoMath.DistanceMetres(60.0, 0.0, 60.0, 1.0);

            Assert.InRange(equator, 111_000, 111_400);
            // cos(60°) = 0.5, so roughly half.
            Assert.InRange(high, 55_000, 56_200);
        }

        [Fact]
        public void The_distance_from_a_point_to_itself_is_zero()
        {
            Assert.Equal(0.0, GeoMath.DistanceMetres(39.0, -88.0, 39.0, -88.0), 6);
        }

        [Fact]
        public void Antipodal_points_do_not_produce_NaN()
        {
            double d = GeoMath.DistanceMetres(0.0, 0.0, 0.0, 180.0);

            Assert.False(double.IsNaN(d));
            Assert.InRange(d, 20_000_000, 20_040_000);
        }

        [Theory]
        [InlineData(91.0, 0.0)]
        [InlineData(-91.0, 0.0)]
        [InlineData(0.0, 181.0)]
        [InlineData(double.NaN, 0.0)]
        [InlineData(double.PositiveInfinity, 0.0)]
        public void Out_of_range_coordinates_are_not_usable(double lat, double lon)
        {
            Assert.False(GeoMath.IsUsable(lat, lon));
        }

        // ── box ─────────────────────────────────────────────────────────────────

        [Fact]
        public void A_box_contains_what_is_inside_it()
        {
            var box = GeoBounds.FromCorners(39.0, -89.0, 40.0, -88.0);

            Assert.True(box.Contains(39.5, -88.5));
            Assert.False(box.Contains(41.0, -88.5));
            Assert.False(box.Contains(39.5, -87.0));
        }

        /// <summary>A drag can start at any corner; the box is the same either way.</summary>
        [Fact]
        public void A_box_is_the_same_whichever_corner_the_drag_started_from()
        {
            var a = GeoBounds.FromCorners(39.0, -89.0, 40.0, -88.0);
            var b = GeoBounds.FromCorners(40.0, -88.0, 39.0, -89.0);

            Assert.Equal(a.MinLat, b.MinLat);
            Assert.Equal(a.MaxLat, b.MaxLat);
            Assert.Equal(a.MinLon, b.MinLon);
            Assert.Equal(a.MaxLon, b.MaxLon);
        }

        /// <summary>A feature exactly on the edge is inside. Inclusive is the defensible choice:
        /// the operator drew the line through it, and silently dropping it is the failure mode
        /// nobody can see.</summary>
        [Fact]
        public void A_feature_on_the_boundary_is_included()
        {
            var box = GeoBounds.FromCorners(39.0, -89.0, 40.0, -88.0);

            Assert.True(box.Contains(39.0, -89.0));
            Assert.True(box.Contains(40.0, -88.0));
            Assert.True(box.Contains(39.0, -88.5));
        }

        /// <summary>Dragging across the antimeridian must select the strip drawn, not the rest of
        /// the world. A naive min/max would select everything except the strip.</summary>
        [Fact]
        public void A_box_drawn_across_the_antimeridian_selects_the_short_way_round()
        {
            var box = GeoBounds.FromCorners(39.0, 179.0, 40.0, -179.0);

            Assert.True(box.CrossesAntimeridian);
            Assert.True(box.Contains(39.5, 179.5));
            Assert.True(box.Contains(39.5, -179.5));
            Assert.True(box.Contains(39.5, 180.0));
            Assert.False(box.Contains(39.5, 0.0));
            Assert.False(box.Contains(39.5, 100.0));
        }

        [Fact]
        public void An_ordinary_wide_box_is_not_mistaken_for_an_antimeridian_drag()
        {
            var box = GeoBounds.FromCorners(39.0, -100.0, 40.0, -80.0);

            Assert.False(box.CrossesAntimeridian);
            Assert.True(box.Contains(39.5, -90.0));
            Assert.False(box.Contains(39.5, 0.0));
        }

        [Fact]
        public void A_box_from_unusable_corners_is_no_box_at_all()
        {
            Assert.Null(GeoBounds.FromCorners(999.0, 0.0, 40.0, -88.0));
            Assert.Null(GeoBounds.FromCorners(39.0, -89.0, double.NaN, -88.0));
        }

        // ── circle ──────────────────────────────────────────────────────────────

        [Fact]
        public void A_radius_is_the_distance_from_the_centre_to_where_the_drag_ended()
        {
            var circle = GeoCircle.FromCenterAndEdge(39.0, -88.0, 40.0, -88.0);

            Assert.InRange(circle.RadiusMetres, 111_000, 111_400);
        }

        [Fact]
        public void A_circle_contains_what_is_inside_the_radius()
        {
            var circle = GeoCircle.FromRadius(39.0, -88.0, 10_000);

            Assert.True(circle.Contains(39.0, -88.0));
            Assert.True(circle.Contains(39.05, -88.0));     // ~5.5 km
            Assert.False(circle.Contains(39.5, -88.0));     // ~55 km
        }

        [Fact]
        public void A_feature_exactly_on_the_radius_is_included()
        {
            double radius = GeoMath.DistanceMetres(39.0, -88.0, 39.1, -88.0);
            var circle = GeoCircle.FromRadius(39.0, -88.0, radius);

            Assert.True(circle.Contains(39.1, -88.0));
        }

        [Fact]
        public void A_zero_radius_drag_selects_only_the_exact_centre()
        {
            var circle = GeoCircle.FromCenterAndEdge(39.0, -88.0, 39.0, -88.0);

            Assert.Equal(0.0, circle.RadiusMetres, 6);
            Assert.True(circle.Contains(39.0, -88.0));
            Assert.False(circle.Contains(39.001, -88.0));
        }

        [Fact]
        public void A_circle_from_unusable_input_is_no_circle_at_all()
        {
            Assert.Null(GeoCircle.FromCenterAndEdge(999.0, 0.0, 39.0, -88.0));
            Assert.Null(GeoCircle.FromRadius(39.0, -88.0, double.NaN));
            Assert.Null(GeoCircle.FromRadius(39.0, -88.0, -5));
        }

        [Fact]
        public void A_circle_describes_itself_in_kilometres_when_it_is_large()
        {
            Assert.Contains("km", GeoCircle.FromRadius(39.0, -88.0, 5000).Describe());
            Assert.Contains(" m ", GeoCircle.FromRadius(39.0, -88.0, 500).Describe() + " ");
        }

        // ── selection set ───────────────────────────────────────────────────────

        [Fact]
        public void Adding_the_same_feature_twice_selects_it_once()
        {
            var selection = new FeatureSelection();

            Assert.True(selection.Add(F("a", 39, -88)));
            Assert.False(selection.Add(F("a", 39, -88)));
            Assert.Equal(1, selection.Count);
        }

        [Fact]
        public void Toggling_adds_then_removes()
        {
            var selection = new FeatureSelection();

            Assert.True(selection.Toggle(F("a", 39, -88)));
            Assert.Equal(1, selection.Count);
            Assert.False(selection.Toggle(F("a", 39, -88)));
            Assert.Equal(0, selection.Count);
        }

        [Fact]
        public void The_review_list_keeps_the_order_features_were_added()
        {
            var selection = new FeatureSelection();
            selection.Add(F("c", 39, -88));
            selection.Add(F("a", 39, -88));
            selection.Add(F("b", 39, -88));

            Assert.Equal(new[] { "c", "a", "b" }, selection.Items.Select(f => f.Uid).ToArray());
        }

        /// <summary>The behaviour the "add more" step depends on: re-drawing over ground already
        /// selected must add only what is new, never toggle existing picks back off.</summary>
        [Fact]
        public void Drawing_a_second_overlapping_area_adds_only_what_is_new()
        {
            var candidates = new List<SelectableFeature>
            {
                F("a", 39.1, -88.1), F("b", 39.2, -88.2), F("c", 39.8, -88.8),
            };
            var selection = new FeatureSelection();

            int first = selection.AddWithin(GeoBounds.FromCorners(39.0, -88.5, 39.5, -88.0), candidates);
            int second = selection.AddWithin(GeoBounds.FromCorners(39.0, -89.0, 40.0, -88.0), candidates);

            Assert.Equal(2, first);
            Assert.Equal(1, second);          // only "c" was new
            Assert.Equal(3, selection.Count); // and nothing was toggled off
        }

        [Fact]
        public void Drawing_somewhere_empty_adds_nothing_and_keeps_the_selection()
        {
            var candidates = new List<SelectableFeature> { F("a", 39.1, -88.1) };
            var selection = new FeatureSelection();
            selection.AddWithin(GeoBounds.FromCorners(39.0, -88.5, 39.5, -88.0), candidates);

            int added = selection.AddWithin(GeoBounds.FromCorners(10.0, 10.0, 11.0, 11.0), candidates);

            Assert.Equal(0, added);
            Assert.Equal(1, selection.Count);
        }

        [Fact]
        public void A_radius_selects_what_is_inside_it()
        {
            var candidates = new List<SelectableFeature>
            {
                F("near", 39.01, -88.0), F("far", 40.0, -88.0),
            };
            var selection = new FeatureSelection();

            selection.AddWithin(GeoCircle.FromRadius(39.0, -88.0, 5_000), candidates);

            Assert.Equal(new[] { "near" }, selection.Items.Select(f => f.Uid).ToArray());
        }

        [Fact]
        public void Features_with_unusable_coordinates_are_never_selected_by_a_shape()
        {
            var candidates = new List<SelectableFeature> { F("bad", double.NaN, -88.0) };
            var selection = new FeatureSelection();

            Assert.Equal(0, selection.AddWithin(GeoBounds.FromCorners(-90, -180, 90, 180), candidates));
        }

        [Fact]
        public void Removing_within_a_shape_takes_out_only_what_is_inside()
        {
            var selection = new FeatureSelection();
            selection.Add(F("in", 39.1, -88.1));
            selection.Add(F("out", 50.0, -88.0));

            int removed = selection.RemoveWithin(GeoBounds.FromCorners(39.0, -89.0, 40.0, -88.0));

            Assert.Equal(1, removed);
            Assert.Equal(new[] { "out" }, selection.Items.Select(f => f.Uid).ToArray());
        }

        [Fact]
        public void A_feature_with_no_uid_cannot_be_selected()
        {
            var selection = new FeatureSelection();

            Assert.False(selection.Add(F(null, 39, -88)));
            Assert.False(selection.Add(F("", 39, -88)));
            Assert.False(selection.Add(null));
            Assert.True(selection.IsEmpty);
        }

        // ── grouping ────────────────────────────────────────────────────────────

        [Fact]
        public void The_selection_groups_by_source_layer()
        {
            var selection = new FeatureSelection();
            selection.Add(F("a", 39, -88, "https://h/a/FeatureServer/0", "Teams"));
            selection.Add(F("b", 39, -88, "https://h/a/FeatureServer/0", "Teams"));
            selection.Add(F("c", 39, -88, "https://h/b/FeatureServer/0", "ICP"));

            var groups = selection.ByLayer();

            Assert.Equal(2, groups.Count);
            Assert.Equal(2, groups.Single(g => g.LayerName == "Teams").Features.Count);
            Assert.Equal(1, groups.Single(g => g.LayerName == "ICP").Features.Count);
        }

        [Fact]
        public void The_summary_counts_features_and_layers()
        {
            var selection = new FeatureSelection();
            Assert.Equal("no features selected", selection.Describe());

            selection.Add(F("a", 39, -88, "https://h/a/FeatureServer/0", "Teams"));
            Assert.Equal("1 feature from 1 layer", selection.Describe());

            selection.Add(F("b", 39, -88, "https://h/b/FeatureServer/0", "ICP"));
            Assert.Equal("2 features from 2 layers", selection.Describe());
        }

        [Fact]
        public void Clearing_empties_the_selection()
        {
            var selection = new FeatureSelection();
            selection.Add(F("a", 39, -88));

            selection.Clear();

            Assert.True(selection.IsEmpty);
            Assert.Empty(selection.Items);
        }

        [Fact]
        public void A_feature_falls_back_to_its_uid_when_it_has_no_callsign()
        {
            var feature = F("uid-1", 39, -88);
            feature.Callsign = null;

            Assert.Equal("uid-1", feature.DisplayName);
        }
    }
}
