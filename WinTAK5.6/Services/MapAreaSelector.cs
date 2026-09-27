using System;
using System.Collections.Generic;
using System.Linq;
using TAKEngine.Core;
using WinTak.Display;
using WinTak.Graphics;

namespace FeatureLink.Services
{
    /// <summary>
    /// Puts the map into a selection mode — click features, drag a box, or drag a radius — and
    /// reports what the operator drew.
    ///
    /// <para>This is the only file in the selection feature that touches the WinTAK SDK. It
    /// converts host mouse events into plain latitude/longitude and hands them to
    /// <see cref="FeatureSelection"/>, where the arithmetic that decides what is actually inside a
    /// shape lives and is tested.</para>
    ///
    /// <para><b>The map is left modal while a mode is active</b>, via
    /// <c>PushMapEvents</c>/<c>PopMapEvents</c>, so a click to pick a marker does not also open
    /// WinTAK's wheel menu. It does NOT stop a drag from panning (observed on 5.6), which is why a
    /// box or radius can also be placed with two clicks — see <see cref="_hasPendingAnchor"/>. The matching
    /// Pop is the dangerous half — miss it and the operator's map stays unresponsive with nothing
    /// on screen explaining why, which is worse than the feature not existing. Every exit path
    /// therefore runs through <see cref="Stop"/>, the class is <see cref="IDisposable"/>, and Pop
    /// is guarded so a throw from a handler cannot strand it.</para>
    /// </summary>
    public sealed class MapAreaSelector : IDisposable
    {
        /// <summary>How the operator is picking features.</summary>
        public enum Mode
        {
            /// <summary>Off — the map behaves normally.</summary>
            None,

            /// <summary>Click individual features to add or remove them.</summary>
            Points,

            /// <summary>Drag a rectangle; everything inside is added.</summary>
            Box,

            /// <summary>Drag from a centre outwards; everything inside the radius is added.</summary>
            Radius,
        }

        /// <summary>Resolved on demand, not captured at construction: MEF sets the dock pane's
        /// map-controller import AFTER the constructor runs, so a controller captured then is
        /// always null.</summary>
        private readonly Func<IMapViewController> _resolve;

        private IMapViewController _controller { get { return _resolve != null ? _resolve() : null; } }

        /// <summary>True while map events are pushed, so Pop happens exactly once.</summary>
        private bool _pushed;

        /// <summary>Where a box or radius drag began, in world coordinates. Null between drags.</summary>
        private double _anchorLat, _anchorLon;
        private int _anchorX, _anchorY;
        private bool _dragging;

        /// <summary>First corner (box) or centre (radius) placed by a click, waiting for the
        /// second click. Observed on a live 5.6 map: <c>PushMapEvents</c> does not stop a drag from
        /// panning, so the ground point under the cursor never changes and a drag arrives as a
        /// zero-size shape. Two clicks do not pan, so they are the dependable path; a drag is
        /// still honoured whenever it produces a real shape.</summary>
        private bool _hasPendingAnchor;
        private double _pendingLat, _pendingLon;

        /// <summary>A press-and-release this close on screen, or this close on the ground, is a
        /// click rather than a drag.</summary>
        private const int ClickSlopPixels = 6;
        private const double ClickSlopMetres = 0.5;

        public Mode Current { get; private set; }

        public bool IsActive { get { return Current != Mode.None; } }

        /// <summary>Raised when the operator clicks in <see cref="Mode.Points"/>. The argument is
        /// the UID of EVERY map item under the click, the host-resolved one first: the topmost item
        /// is often not the feature marker (a label, the self marker, another overlay), so the
        /// listener picks the first UID that is actually a feature. Feature UIDs are marker UIDs
        /// because the plugin creates its markers with the feature UID.</summary>
        public event Action<IList<string>> FeatureClicked;

        /// <summary>Raised when a box or radius completes, with the shape drawn.</summary>
        public event Action<ISelectionShape> ShapeDrawn;

        /// <summary>Raised when a click places the first corner or centre and the mode is waiting
        /// for the second click.</summary>
        public event Action AnchorPlaced;

        /// <summary>Raised when a mode ends, for whatever reason, so the UI can un-press its
        /// button. Always fires exactly once per <see cref="Start"/>.</summary>
        public event Action Stopped;

        /// <summary>True when the host exposed a map controller at all. When false every method
        /// here is a no-op and the caller should say so rather than appearing to work.</summary>
        public bool IsAvailable { get { return _controller != null; } }

        public MapAreaSelector(Func<IMapViewController> resolveController)
        {
            _resolve = resolveController;
        }

        /// <summary>
        /// Enters a selection mode, replacing any active one.
        /// </summary>
        /// <returns>False when the host has no map controller, or the mode is
        /// <see cref="Mode.None"/>.</returns>
        public bool Start(Mode mode)
        {
            var controller = _controller;
            if (controller == null)
            {
                Log.Warn("Cannot start map selection: WinTAK did not export a map view controller.");
                return false;
            }

            Stop();
            if (mode == Mode.None) return false;

            try
            {
                // Keep only what this mode needs and suppress the rest, so the map does not pan,
                // zoom or open a wheel menu under the gesture.
                MapMouseEvents keep = mode == Mode.Points
                    ? MapMouseEvents.ItemClick | MapMouseEvents.MapClick
                    : MapMouseEvents.MapMouseDown | MapMouseEvents.MapMouseMove | MapMouseEvents.MapMouseUp;

                controller.PushMapEvents(keep);
                _pushed = true;

                if (mode == Mode.Points)
                {
                    controller.ItemClick += OnItemClick;
                    controller.MapClick += OnMapClick;
                }
                else
                {
                    controller.MapMouseDown += OnMouseDown;
                    controller.MapMouseUp += OnMouseUp;
                }

                Current = mode;
                Log.Info("Map selection mode started: " + mode + ".");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Could not start map selection mode " + mode + ".", ex);
                // Started half-way: unwind rather than leaving the map modal.
                Stop();
                return false;
            }
        }

        /// <summary>
        /// Leaves selection mode and gives the map back. Safe to call when not active, and safe to
        /// call twice.
        /// </summary>
        public void Stop()
        {
            if (Current == Mode.None && !_pushed) return;

            Mode was = Current;
            Current = Mode.None;
            _dragging = false;
            _hasPendingAnchor = false;

            var controller = _controller;

            // Unsubscribe first and independently: a throw while detaching one handler must not
            // prevent the PopMapEvents below, which is what gives the operator their map back.
            Detach(controller, h => controller.ItemClick -= h, OnItemClick);
            Detach(controller, h => controller.MapClick -= h, OnMapClick);
            Detach(controller, h => controller.MapMouseDown -= h, OnMouseDown);
            Detach(controller, h => controller.MapMouseUp -= h, OnMouseUp);

            if (_pushed && controller != null)
            {
                _pushed = false;
                try { controller.PopMapEvents(); }
                catch (Exception ex)
                {
                    // Nothing further can be done, but this must be loud: the operator's map is
                    // now potentially stuck in a suppressed state and only a restart clears it.
                    Log.Error("Could not restore normal map interaction after selection mode. "
                              + "The map may not respond until WinTAK is restarted.", ex);
                }
            }

            if (was != Mode.None)
            {
                Log.Info("Map selection mode ended.");
                Raise(Stopped);
            }
        }

        private static void Detach(IMapViewController controller,
            Action<MapMouseEventHandler> remove, MapMouseEventHandler handler)
        {
            if (controller == null) return;
            try { remove(handler); }
            catch (Exception ex) { Log.Warn("Could not detach a map handler: " + ex.Message); }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Handlers
        // ─────────────────────────────────────────────────────────────────────────

        private void OnItemClick(object sender, MapMouseEventArgs e)
        {
            // Host callback: an escaping exception crosses back into WinTAK's input pipeline.
            try
            {
                var uids = UidsOf(e);
                if (uids.Count > 0) Raise(FeatureClicked, uids);
            }
            catch (Exception ex) { Log.Warn("Map item click could not be handled: " + ex.Message); }
        }

        private void OnMapClick(object sender, MapMouseEventArgs e)
        {
            // A click that missed every marker. Reported as a miss rather than ignored, so the
            // "clicked and nothing happened" case has an explanation in the log.
            try
            {
                var uids = UidsOf(e);
                if (uids.Count > 0) Raise(FeatureClicked, uids);
            }
            catch (Exception ex) { Log.Warn("Map click could not be handled: " + ex.Message); }
        }

        /// <summary>The UIDs of everything the click landed on: the item the host resolved first,
        /// then every other item under the cursor, without duplicates.</summary>
        private static IList<string> UidsOf(MapMouseEventArgs e)
        {
            var uids = new List<string>();
            if (e == null) return uids;

            var item = e.Item as MapItem;
            if (item != null && !item.IsDisposed && !string.IsNullOrEmpty(item.Uid)) uids.Add(item.Uid);

            if (e.MapItems != null)
            {
                foreach (var i in e.MapItems)
                {
                    if (i == null || i.IsDisposed || string.IsNullOrEmpty(i.Uid)) continue;
                    if (!uids.Contains(i.Uid)) uids.Add(i.Uid);
                }
            }
            return uids;
        }

        private void OnMouseDown(object sender, MapMouseEventArgs e)
        {
            try
            {
                var world = e?.WorldLocation;
                if (world == null) return;

                _anchorLat = world.Latitude;
                _anchorLon = world.Longitude;
                _anchorX = e.X;
                _anchorY = e.Y;
                _dragging = GeoMath.IsUsable(_anchorLat, _anchorLon);
            }
            catch (Exception ex) { Log.Warn("Could not start the selection drag: " + ex.Message); }
        }

        private void OnMouseUp(object sender, MapMouseEventArgs e)
        {
            try
            {
                if (!_dragging) return;
                _dragging = false;

                var world = e?.WorldLocation;
                if (world == null) return;

                double lat = world.Latitude, lon = world.Longitude;
                if (!GeoMath.IsUsable(lat, lon)) return;

                bool isClick =
                    (Math.Abs(e.X - _anchorX) <= ClickSlopPixels && Math.Abs(e.Y - _anchorY) <= ClickSlopPixels)
                    || GeoMath.DistanceMetres(_anchorLat, _anchorLon, lat, lon) < ClickSlopMetres;

                double fromLat, fromLon;
                if (!isClick)
                {
                    // A real drag.
                    fromLat = _anchorLat;
                    fromLon = _anchorLon;
                }
                else if (!_hasPendingAnchor)
                {
                    // First of two clicks: remember it and wait for the second.
                    _hasPendingAnchor = true;
                    _pendingLat = lat;
                    _pendingLon = lon;
                    Raise(AnchorPlaced);
                    return;
                }
                else
                {
                    fromLat = _pendingLat;
                    fromLon = _pendingLon;
                    _hasPendingAnchor = false;
                }

                ISelectionShape shape = Current == Mode.Radius
                    ? (ISelectionShape)GeoCircle.FromCenterAndEdge(fromLat, fromLon, lat, lon)
                    : GeoBounds.FromCorners(fromLat, fromLon, lat, lon);

                if (shape == null) return;

                // One shape per activation. Leaving the mode on would let a stray click after the
                // drag draw a second, zero-sized shape and quietly select nothing.
                Stop();
                Raise(ShapeDrawn, shape);
            }
            catch (Exception ex)
            {
                Log.Warn("Could not complete the selection drag: " + ex.Message);
                Stop();
            }
        }

        // ─────────────────────────────────────────────────────────────────────────

        private static void Raise(Action handler)
        {
            if (handler == null) return;
            try { handler(); }
            catch (Exception ex) { Log.Warn("A selection listener threw: " + ex.Message); }
        }

        private static void Raise<T>(Action<T> handler, T argument)
        {
            if (handler == null) return;
            try { handler(argument); }
            catch (Exception ex) { Log.Warn("A selection listener threw: " + ex.Message); }
        }

        public void Dispose()
        {
            Stop();
            FeatureClicked = null;
            ShapeDrawn = null;
            AnchorPlaced = null;
            Stopped = null;
        }
    }
}
