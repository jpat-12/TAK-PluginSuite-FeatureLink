using System;
using System.Collections.Generic;
using System.Linq;
using FeatureLink.Models;
using Xunit;

namespace FeatureLink.Tests
{
    /// <summary>
    /// Covers the "zoom to layer" framing maths.
    ///
    /// <para>The extent is computed from the features actually plotted, not from the service's
    /// published extent: downloaded features are already WGS84 (the query asks
    /// <c>outSR=4326</c>) so no reprojection is needed, and they describe what is on the
    /// operator's map rather than the whole source layer — which is what zooming to a layer
    /// should frame when a download was capped or filtered.</para>
    /// </summary>
    public class LayerExtentTests
    {
        private static LayerExtent Of(params double[] latLon)
        {
            var pts = new List<Tuple<double, double>>();
            for (int i = 0; i < latLon.Length; i += 2) pts.Add(Tuple.Create(latLon[i], latLon[i + 1]));
            return LayerExtent.FromPoints(pts);
        }

        // ── bounds ──────────────────────────────────────────────────────────────────

        [Fact]
        public void The_extent_spans_every_plotted_point()
        {
            var e = Of(39.0, -88.0, 40.0, -89.0, 38.5, -87.5);

            Assert.Equal(38.5, e.MinLat);
            Assert.Equal(40.0, e.MaxLat);
            Assert.Equal(-89.0, e.MinLon);
            Assert.Equal(-87.5, e.MaxLon);
        }

        [Fact]
        public void The_centre_is_the_midpoint_of_the_span()
        {
            var e = Of(38.0, -90.0, 40.0, -88.0);

            Assert.Equal(39.0, e.CenterLat, 6);
            Assert.Equal(-89.0, e.CenterLon, 6);
        }

        [Fact]
        public void Out_of_range_and_non_finite_coordinates_are_ignored()
        {
            var e = Of(39.0, -88.0, 999.0, -88.0, double.NaN, -88.0, 40.0, -89.0);

            Assert.Equal(39.0, e.MinLat);
            Assert.Equal(40.0, e.MaxLat);
        }

        [Fact]
        public void No_usable_points_yields_no_extent()
        {
            Assert.Null(LayerExtent.FromPoints(null));
            Assert.Null(LayerExtent.FromPoints(new List<Tuple<double, double>>()));
            Assert.Null(Of(999.0, 999.0));
        }

        // ── resolution ──────────────────────────────────────────────────────────────

        /// <summary>The user's own framing: features roughly a hundred miles apart must produce a
        /// resolution that shows all of them, not one that frames a single town.</summary>
        [Fact]
        public void Features_a_hundred_miles_apart_zoom_out_far_enough_to_show_them_all()
        {
            // ~100 miles of latitude is ~1.45 degrees.
            var e = Of(39.0, -88.0, 40.45, -88.0);

            double mpp = e.ResolutionMetresPerPixel();
            double metresOnScreen = mpp * 1200.0;   // the assumed viewport width

            double span = 1.45 * 111_320.0;
            Assert.True(metresOnScreen >= span,
                $"viewport covers {metresOnScreen:N0} m but the data spans {span:N0} m");
        }

        /// <summary>A wider layer must zoom out further than a narrow one — the property that
        /// makes this useful at all.</summary>
        [Fact]
        public void A_wider_extent_yields_a_coarser_resolution()
        {
            double narrow = Of(39.0, -88.0, 39.05, -88.05).ResolutionMetresPerPixel();
            double wide = Of(30.0, -100.0, 45.0, -80.0).ResolutionMetresPerPixel();

            Assert.True(wide > narrow, $"wide={wide} should exceed narrow={narrow}");
        }

        /// <summary>Longitude degrees shrink toward the poles. Without a cosine correction a
        /// high-latitude layer frames far too loosely.</summary>
        [Fact]
        public void East_west_span_is_corrected_for_latitude()
        {
            double equator = Of(0.0, -1.0, 0.0, 1.0).ResolutionMetresPerPixel();
            double arctic = Of(70.0, -1.0, 70.0, 1.0).ResolutionMetresPerPixel();

            Assert.True(arctic < equator,
                $"the same degree span is physically narrower at 70°N (arctic={arctic}, equator={equator})");
        }

        [Fact]
        public void A_single_point_still_yields_a_usable_resolution()
        {
            double mpp = Of(39.0, -88.0).ResolutionMetresPerPixel();

            Assert.True(mpp > 0);
            Assert.False(double.IsNaN(mpp) || double.IsInfinity(mpp));
        }

        [Fact]
        public void Identical_points_do_not_zoom_to_an_absurd_level()
        {
            double mpp = Of(39.0, -88.0, 39.0, -88.0, 39.0, -88.0).ResolutionMetresPerPixel();

            Assert.True(mpp >= 2.0, $"a zero-span extent gave {mpp} m/px");
        }

        [Fact]
        public void A_polar_extent_does_not_divide_by_zero()
        {
            double mpp = Of(89.999, -179.0, 89.999, 179.0).ResolutionMetresPerPixel();

            Assert.False(double.IsNaN(mpp) || double.IsInfinity(mpp));
            Assert.True(mpp > 0);
        }

        // ── persistence ─────────────────────────────────────────────────────────────

        [Fact]
        public void The_extent_survives_the_settings_round_trip()
        {
            var original = Of(38.5, -89.0, 40.0, -87.5);

            var restored = LayerExtent.FromXElement(original.ToXElement());

            Assert.Equal(original.MinLat, restored.MinLat, 9);
            Assert.Equal(original.MinLon, restored.MinLon, 9);
            Assert.Equal(original.MaxLat, restored.MaxLat, 9);
            Assert.Equal(original.MaxLon, restored.MaxLon, 9);
        }

        [Fact]
        public void A_missing_or_malformed_extent_element_reads_as_none()
        {
            Assert.Null(LayerExtent.FromXElement(null));
            Assert.Null(LayerExtent.FromXElement(new System.Xml.Linq.XElement("Extent")));
        }

        /// <summary>The coordinates are written invariant, so a de-DE machine's comma decimal
        /// separator cannot corrupt a settings file another machine reads.</summary>
        [Theory]
        [InlineData("de-DE")]
        [InlineData("fr-FR")]
        [InlineData("tr-TR")]
        public void Coordinates_round_trip_under_any_culture(string culture)
        {
            LayerExtent restored = null;
            var original = Of(38.5, -89.25, 40.75, -87.5);

            CultureScope.With(culture, () =>
                restored = LayerExtent.FromXElement(original.ToXElement()));

            Assert.Equal(-89.25, restored.MinLon, 9);
            Assert.Equal(40.75, restored.MaxLat, 9);
        }

        [Fact]
        public void A_layer_reports_whether_it_can_be_zoomed_to()
        {
            var layer = new ArcGisLayer("L", "https://h/a/rest/services/X/FeatureServer/0", "private");
            Assert.False(layer.CanZoomTo);

            layer.Extent = Of(39.0, -88.0, 40.0, -89.0);
            Assert.True(layer.CanZoomTo);
        }
    }
}
