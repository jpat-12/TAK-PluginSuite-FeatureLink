using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace FeatureLink.Models
{
    /// <summary>
    /// One browsed/subscribed ArcGIS Feature Service layer.
    /// Ported field-for-field from the ATAK plugin's
    /// com.atakmap.android.featurelink.arcgis.ArcGISLayer (toJson/fromJson), with the same
    /// "recurrence interval + unit" auto-refresh model. Persisted to XML by
    /// <see cref="Services.SettingsStore"/> rather than Android SharedPreferences JSON.
    ///
    /// Implements INotifyPropertyChanged (the Java original didn't need to — Android's
    /// LayerListAdapter just re-binds views on every list refresh) so the WPF layer-row template
    /// in FeatureLinkView.xaml — mirroring item_layer.xml's eye-icon/interval/action-button row —
    /// updates live when the user toggles visibility or edits the refresh interval, without
    /// requiring a full ObservableCollection reset.
    /// </summary>
    public class ArcGisLayer : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private void RaisePropertyChanged([CallerMemberName] string propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        private string _name;
        public string Name { get => _name; set { _name = value; RaisePropertyChanged(); } }

        private string _url;
        public string Url { get => _url; set { _url = value; RaisePropertyChanged(); } }

        private string _type = "public";
        /// <summary>"private" (owned by the signed-in user) or "public" (added by URL/QR/import).</summary>
        public string Type
        {
            get => _type;
            set { _type = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(IsPrivate)); }
        }

        public bool IsPrivate => Type == "private";

        private string _ownerAccountKey;
        /// <summary>Which signed-in ArcGIS account owns this layer, and therefore whose token
        /// syncs it once more than one account can be signed in at a time.
        ///
        /// <para>Null means UNBOUND: public layers, layers persisted by a build from before
        /// multi-account existed, and layers received while signed out.</para>
        ///
        /// <para>It is <b>this</b> field — not <see cref="IsPrivate"/> — that selects the token.
        /// <see cref="IsPrivate"/> is unchanged and remains the public/private classification, but
        /// with several accounts signed in "private" no longer answers <i>whose</i> token, so it is
        /// no longer sufficient on its own.</para>
        ///
        /// <para>The value is an opaque key of the form <c>portalUrl|username</c>. Do not parse it
        /// here; only compare and store it.</para></summary>
        public string OwnerAccountKey
        {
            get => _ownerAccountKey;
            set { _ownerAccountKey = value; RaisePropertyChanged(); }
        }

        private string _access = "org";
        /// <summary>ArcGIS portal sharing scope for a "My ArcGIS Layers" browse item: "public"
        /// (shared to Everyone), "org", or "private". Only meaningful for items returned by
        /// ArcGisFeatureService.SearchUserLayersAsync() — decides whether
        /// MoveMyArcGisLayerOnDownload() files a downloaded item into PublicLayers or
        /// SharedPrivateLayers. Layers added via URL or received via a share default to "org"
        /// since there's no portal item to ask.</summary>
        public string Access { get => _access; set { _access = value; RaisePropertyChanged(); } }

        private LayerExtent _extent;
        /// <summary>Bounds of this layer's plotted features, for "zoom to layer". Persisted so the
        /// affordance works immediately after a restart, before any re-sync.</summary>
        public LayerExtent Extent
        {
            get => _extent;
            set { _extent = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(CanZoomTo)); }
        }

        /// <summary>Whether "zoom to layer" can do anything — drives the title's hit-testing so a
        /// click that could not move the map is not offered.</summary>
        public bool CanZoomTo => _extent != null && _extent.IsValid;

        private System.Collections.ObjectModel.ObservableCollection<LayerFeature> _features
            = new System.Collections.ObjectModel.ObservableCollection<LayerFeature>();
        /// <summary>The features this layer last plotted, for the row's expandable list.
        ///
        /// <para>Deliberately NOT persisted: it is a view of what is currently on the map, it is
        /// rebuilt by every download, and a layer of 8,000 features would bloat settings.xml for
        /// no benefit. Capped — see <see cref="MaxListedFeatures"/>.</para></summary>
        public System.Collections.ObjectModel.ObservableCollection<LayerFeature> Features
        {
            get => _features;
            set { _features = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(HasFeatures)); }
        }

        /// <summary>Ceiling on the row's feature list. The list is a convenience for finding one
        /// feature, not a data browser, and realizing thousands of rows inside a scrolling panel
        /// would cost more than it gives.</summary>
        public const int MaxListedFeatures = 500;

        /// <summary>
        /// Every feature this layer plotted, uncapped — the pool a data-package selection draws on.
        ///
        /// <para>Separate from <see cref="Features"/>, which is capped at
        /// <see cref="MaxListedFeatures"/> because it is realized into a scrolling panel. This one
        /// is never bound to the UI, so the cap would buy nothing and would cost correctness: an
        /// operator dragging a box round the south end of an 8,000-feature layer must select what
        /// is inside it, not whichever of the first 500 happen to fall there. Not persisted, for
        /// the same reasons as <see cref="Features"/>.</para>
        /// </summary>
        public System.Collections.Generic.List<LayerFeature> AllFeatures { get; set; }
            = new System.Collections.Generic.List<LayerFeature>();

        public bool HasFeatures => _features != null && _features.Count > 0;

        /// <summary>Call after rebuilding <see cref="Features"/> in place. The collection instance
        /// does not change, so nothing would otherwise re-evaluate the computed members that gate
        /// the row's disclosure control.</summary>
        public void RaiseFeaturesChanged()
        {
            RaisePropertyChanged(nameof(HasFeatures));
            RaisePropertyChanged(nameof(Features));
        }

        private bool _featuresExpanded;
        /// <summary>Per-row disclosure state for the feature list.</summary>
        public bool FeaturesExpanded
        {
            get => _featuresExpanded;
            set { _featuresExpanded = value; RaisePropertyChanged(); }
        }

        private string _itemId;
        /// <summary>The ArcGIS portal item this layer came from, when known.
        ///
        /// <para>Needed because an operator who styles a hosted layer on the item's
        /// <b>Visualization</b> tab does NOT modify the feature service. ArcGIS saves that
        /// renderer as an item-level override at
        /// <c>/sharing/rest/content/items/{itemId}/data</c>, so the service keeps reporting its
        /// original — often <c>simple</c> — renderer, and the layer renders as one repeated
        /// marker. Without the item id there is no way to find the symbology the operator can
        /// actually see.</para>
        ///
        /// <para>Empty for a layer added by URL, where no portal item is known; the service
        /// renderer is then all there is.</para></summary>
        public string ItemId
        {
            get => _itemId;
            set { _itemId = value; RaisePropertyChanged(); }
        }

        private bool _largeDownloadAccepted;
        /// <summary>The operator has already agreed to sync this layer despite its size, so the
        /// "large layer" prompt must not appear again for it.
        ///
        /// <para>Persisted deliberately. The prompt fires on feature count, and a layer over the
        /// threshold is over it on every single sync — including every recurrence tick — so
        /// without a remembered answer a 15-second recurrence turns one reasonable question into
        /// a modal dialog that reappears forever. The decision is the operator's and it is made
        /// once.</para></summary>
        public bool LargeDownloadAccepted
        {
            get => _largeDownloadAccepted;
            set { _largeDownloadAccepted = value; RaisePropertyChanged(); }
        }

        private bool _hasDisplayConfig;
        /// <summary>Whether a display config (icons/colors/labels/popups) is attached — set from
        /// a received share's sym/lbl/popup/cm keys. Drives the Config/No Config badge.</summary>
        public bool HasDisplayConfig { get => _hasDisplayConfig; set { _hasDisplayConfig = value; RaisePropertyChanged(); } }

        private string _symJson;
        /// <summary>Raw compact "sym" JSON from a received share (DisplayConfig.java's schema —
        /// see CONFIG-FORMAT.md), kept as-is rather than parsed into a model class. Resolved
        /// per-feature at download time by DisplayStyleResolver.ResolveIconsetPath()/
        /// ResolveColor(). Null when the share carried no icon/color styling.</summary>
        public string SymJson { get => _symJson; set { _symJson = value; RaisePropertyChanged(); } }

        private string _lblJson;
        /// <summary>Raw compact "lbl" JSON (field/size/color/bold/italic — only "f" is currently
        /// resolved, see DisplayStyleResolver.ResolveLabel()). Null when absent.</summary>
        public string LblJson { get => _lblJson; set { _lblJson = value; RaisePropertyChanged(); } }

        private string _popupJson;
        /// <summary>Raw compact "popup" JSON (titleField + flds — see
        /// DisplayStyleResolver.BuildRemarks()). Null when absent.</summary>
        public string PopupJson { get => _popupJson; set { _popupJson = value; RaisePropertyChanged(); } }

        private string _shpJson;
        /// <summary>Raw compact "shp" JSON — polyline/polygon stroke+fill styling
        /// (<c>{"f":field,"s":{…},"bv":{value:{…}}}</c>), resolved per feature by
        /// <see cref="Services.DisplayStyleResolver.ResolveShapeStyle"/>.
        ///
        /// C-07 / Appendix F §5: this had no representation at all on WinTAK, so a Portal-authored
        /// config carrying shape styling was silently and completely dropped here. Null when the
        /// share carried none.</summary>
        public string ShpJson { get => _shpJson; set { _shpJson = value; RaisePropertyChanged(); } }

        /// <summary>Symbology derived on this device from the layer's own
        /// <c>drawingInfo.renderer</c> when no explicit display config is attached. Not persisted:
        /// it is re-derived from live metadata on every download so it can never go stale against
        /// a renderer the layer owner edited.</summary>
        public string AutoSymJson { get; set; }

        /// <summary>Shape styling derived from the layer's own renderer — the auto counterpart of
        /// <see cref="ShpJson"/>. Not persisted, for the same reason.</summary>
        public string AutoShpJson { get; set; }

        /// <summary>
        /// Iconset UIDs this device has generated and installed for this layer, semicolon-separated.
        ///
        /// <para>Persisted, unlike <see cref="AutoSymJson"/>, and for the opposite reason. The
        /// symbology config must go stale-proof by being re-derived every download; this is a record
        /// of what is on <b>disk</b>, and the zips outlive the session. Without it a data package
        /// built after a restart could only bundle icons for layers already re-synced in that
        /// session, which is precisely when an operator packaging data for an offline peer is least
        /// likely to have re-synced anything.</para>
        /// </summary>
        public string IconsetUids { get => _iconsetUids; set { _iconsetUids = value; RaisePropertyChanged(); } }
        private string _iconsetUids;

        /// <summary>Records an iconset UID against this layer, keeping the list unique and ordered.
        /// Returns true when the record changed and settings should be saved.</summary>
        public bool RecordIconsetUid(string uid)
        {
            if (string.IsNullOrWhiteSpace(uid)) return false;

            var current = new System.Collections.Generic.SortedSet<string>(
                (IconsetUids ?? string.Empty).Split(new[] { ';' },
                    StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

            if (!current.Add(uid.Trim())) return false;
            IconsetUids = string.Join(";", current);
            return true;
        }

        private long _featureCount;
        /// <summary>Features in the source layer. Assigning also marks the count KNOWN — see
        /// <see cref="FeatureCountText"/> for why the distinction matters.</summary>
        public long FeatureCount
        {
            get => _featureCount;
            set
            {
                _featureCount = value;
                FeatureCountKnown = true;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(FeatureCountText));
            }
        }

        private bool _featureCountKnown;
        /// <summary>Whether a count has ever been obtained for this layer.</summary>
        public bool FeatureCountKnown
        {
            get => _featureCountKnown;
            set
            {
                _featureCountKnown = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(FeatureCountText));
            }
        }

        /// <summary>What the layer row shows.
        ///
        /// <para>"0 features" used to appear for two completely different situations: a layer that
        /// genuinely holds nothing, and a layer nobody has counted yet. The second is by far the
        /// common one — a browse-list row that has never been downloaded — and reporting it as
        /// zero states something false about the source data. An unknown count now reads as a
        /// dash.</para></summary>
        public string FeatureCountText => FeatureCountKnown
            ? FeatureCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) + " features"
            : "— features";

        private long _lastSyncTicks;
        /// <summary>Last successful download time (UTC ticks), 0 = never synced.</summary>
        public long LastSyncTicks
        {
            get => _lastSyncTicks;
            set
            {
                _lastSyncTicks = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(ActionGlyph));
                RaisePropertyChanged(nameof(CanToggleVisibility));
            }
        }

        /// <summary>Whether this row's visibility toggle does anything.
        ///
        /// <para>False until the layer has actually plotted markers. A "My ArcGIS Layers" browse
        /// row is a catalogue entry, not a synced layer — there is nothing on the map to show or
        /// hide, so offering an eye there is a control that silently does nothing. The same is
        /// true of any layer that has not completed a sync yet, which is why this keys on
        /// <see cref="LastSyncTicks"/> rather than on which list the row happens to sit in.</para></summary>
        public bool CanToggleVisibility => LastSyncTicks > 0;

        /// <summary>Text-glyph stand-in for item_layer.xml's ic_download / ic_refresh_circle
        /// drawable (down-arrow until first sync, then a refresh glyph) — private ("My ArcGIS
        /// Layers") rows only; public layers keep the static refresh glyph they always had.</summary>
        public string ActionGlyph => IsPrivate && LastSyncTicks <= 0 ? "⬇" : "↻"; // ⬇ / ↻

        private bool _downloadEnabled;
        public bool DownloadEnabled { get => _downloadEnabled; set { _downloadEnabled = value; RaisePropertyChanged(); } }

        /// <summary>Single source of truth for the refresh unit. The field initialiser, the XML
        /// writer, the XML reader and <see cref="RecurrenceTimeSpan"/> previously disagreed
        /// ("s"/"s"/"min"/"min"), so a layer whose element was missing silently changed its
        /// refresh period by 60×.</summary>
        public const string DefaultRecurrenceUnit = "s";

        /// <summary>Floor for an auto-refresh interval. A received share could set
        /// <c>{"freq":{"iv":1}}</c> and drive a full layer re-download every second, forever, on a
        /// network peer's say-so — remote resource exhaustion and ArcGIS credit burn.</summary>
        public const int MinRecurrenceSeconds = 30;

        private int _recurrenceInterval = 180;
        /// <summary>0 = auto-refresh disabled; &gt;0 = refresh every RecurrenceInterval RecurrenceUnit.
        /// Clamped on set: negatives become 0 (disabled) and anything under
        /// <see cref="MinRecurrenceSeconds"/> seconds is raised to it.</summary>
        public int RecurrenceInterval
        {
            get => _recurrenceInterval;
            set { _recurrenceInterval = ClampInterval(value, _recurrenceUnit); RaisePropertyChanged(); }
        }

        private string _recurrenceUnit = DefaultRecurrenceUnit;
        /// <summary>"s", "min", or "hr" — mirrors the Android side's three supported units.</summary>
        public string RecurrenceUnit
        {
            get => _recurrenceUnit;
            set
            {
                string v = value;
                if (v != "s" && v != "min" && v != "hr") v = DefaultRecurrenceUnit;
                _recurrenceUnit = v;
                _recurrenceInterval = ClampInterval(_recurrenceInterval, v);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(RecurrenceInterval));
            }
        }

        private static int ClampInterval(int value, string unit)
        {
            if (value <= 0) return 0;
            if (unit == "s" && value < MinRecurrenceSeconds) return MinRecurrenceSeconds;
            return value > 1_000_000 ? 1_000_000 : value;
        }

        private bool _isPliLayer;
        public bool IsPliLayer { get => _isPliLayer; set { _isPliLayer = value; RaisePropertyChanged(); } }

        private bool _visible = true;
        /// <summary>Whether this layer's markers should currently be shown on the map.</summary>
        public bool Visible
        {
            get => _visible;
            set { _visible = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(EyeIconSource)); }
        }

        /// <summary>Real icon (ported from item_layer.xml's ic_eye_open/ic_eye_closed) rather than
        /// the Unicode-glyph placeholder this port started with — see README "Where the WinTAK UI
        /// diverges from ATAK".</summary>
        public string EyeIconSource => Visible
            ? "pack://application:,,,/FeatureLink;component/Assets/ic_eye_open.png"
            : "pack://application:,,,/FeatureLink;component/Assets/ic_eye_closed.png";

        public ArcGisLayer() { }

        public ArcGisLayer(string name, string url, string type)
        {
            Name = name;
            Url = url;
            Type = type;
        }

        public DateTime LastSync
        {
            // DateTimeKind.Unspecified would be treated as local by ToUniversalTime() — a silent
            // timezone shift. Callers pass UtcNow today; this makes that a contract rather than luck.
            get => LastSyncTicks > 0 ? new DateTime(LastSyncTicks, DateTimeKind.Utc) : DateTime.MinValue;
            set => LastSyncTicks = (value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
                : value.ToUniversalTime()).Ticks;
        }

        /// <summary>Returns the auto-refresh period, or <see cref="TimeSpan.Zero"/> if disabled.</summary>
        public TimeSpan RecurrenceTimeSpan()
        {
            if (RecurrenceInterval <= 0) return TimeSpan.Zero;
            switch (RecurrenceUnit ?? DefaultRecurrenceUnit)
            {
                case "min": return TimeSpan.FromMinutes(RecurrenceInterval);
                case "hr": return TimeSpan.FromHours(RecurrenceInterval);
                default: return TimeSpan.FromSeconds(RecurrenceInterval);
            }
        }

        public XElement ToXElement()
        {
            return new XElement("Layer",
                new XElement("Name", Name ?? string.Empty),
                new XElement("Url", Url ?? string.Empty),
                new XElement("Type", Type ?? "public"),
                new XElement("Access", Access ?? "org"),
                // Empty element for an unbound layer, read back as null by NullIfEmpty, so a
                // round-trip of a public/legacy layer is stable rather than turning null into "".
                new XElement("OwnerAccountKey", OwnerAccountKey ?? string.Empty),
                new XElement("HasDisplayConfig", HasDisplayConfig),
                new XElement("LargeDownloadAccepted", LargeDownloadAccepted),
                new XElement("ItemId", ItemId ?? string.Empty),
                new XElement("IconsetUids", IconsetUids ?? string.Empty),
                Extent?.ToXElement(),
                new XElement("SymJson", SymJson ?? string.Empty),
                new XElement("LblJson", LblJson ?? string.Empty),
                new XElement("PopupJson", PopupJson ?? string.Empty),
                new XElement("ShpJson", ShpJson ?? string.Empty),
                new XElement("FeatureCount", FeatureCount),
                new XElement("FeatureCountKnown", FeatureCountKnown),
                new XElement("LastSyncTicks", LastSyncTicks),
                new XElement("DownloadEnabled", DownloadEnabled),
                new XElement("RecurrenceInterval", RecurrenceInterval),
                new XElement("RecurrenceUnit", RecurrenceUnit ?? DefaultRecurrenceUnit),
                new XElement("IsPliLayer", IsPliLayer),
                new XElement("Visible", Visible));
        }

        /// <summary>
        /// Per-element defensive parse. Every read used to be an explicit <c>XElement</c>
        /// conversion, so a single malformed element (<c>&lt;RecurrenceInterval&gt;abc&lt;/&gt;</c>)
        /// threw <c>FormatException</c> into <c>SettingsStore.Load</c>'s bare catch and discarded
        /// the operator's <b>entire</b> configuration silently. Now one bad element costs one
        /// field, and it is logged.
        /// </summary>
        public static ArcGisLayer FromXElement(XElement el)
        {
            var layer = new ArcGisLayer
            {
                Name = Text(el, "Name") ?? "Unknown",
                Url = Text(el, "Url") ?? string.Empty,
                Type = Text(el, "Type") ?? "public",
                Access = Text(el, "Access") ?? "org",
                // Absent (a settings.xml written before multi-account) and empty both mean unbound.
                OwnerAccountKey = NullIfEmpty(Text(el, "OwnerAccountKey")),
                HasDisplayConfig = Bool(el, "HasDisplayConfig", false),
                LargeDownloadAccepted = Bool(el, "LargeDownloadAccepted", false),
                ItemId = NullIfEmpty(Text(el, "ItemId")),
                IconsetUids = NullIfEmpty(Text(el, "IconsetUids")),
                Extent = LayerExtent.FromXElement(el.Element("Extent")),
                SymJson = NullIfEmpty(Text(el, "SymJson")),
                LblJson = NullIfEmpty(Text(el, "LblJson")),
                PopupJson = NullIfEmpty(Text(el, "PopupJson")),
                ShpJson = NullIfEmpty(Text(el, "ShpJson")),
                FeatureCount = Long(el, "FeatureCount", 0),
                FeatureCountKnown = Bool(el, "FeatureCountKnown", false),
                LastSyncTicks = Long(el, "LastSyncTicks", 0),
                DownloadEnabled = Bool(el, "DownloadEnabled", false),
                // Unit first: the interval clamp depends on it.
                RecurrenceUnit = Text(el, "RecurrenceUnit") ?? DefaultRecurrenceUnit,
                IsPliLayer = Bool(el, "IsPliLayer", false),
                Visible = Bool(el, "Visible", true),
            };
            layer.RecurrenceInterval = Int(el, "RecurrenceInterval", 0);
            return layer;
        }

        private static string NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

        private static string Text(XElement el, string name) => el.Element(name)?.Value;

        private static bool Bool(XElement el, string name, bool fallback)
        {
            bool v;
            string raw = Text(el, name);
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            if (bool.TryParse(raw.Trim(), out v)) return v;
            Warn(name, raw);
            return fallback;
        }

        private static int Int(XElement el, string name, int fallback)
        {
            int v;
            string raw = Text(el, name);
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            if (int.TryParse(raw.Trim(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out v)) return v;
            Warn(name, raw);
            return fallback;
        }

        private static long Long(XElement el, string name, long fallback)
        {
            long v;
            string raw = Text(el, name);
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            if (long.TryParse(raw.Trim(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out v)) return v;
            Warn(name, raw);
            return fallback;
        }

        private static void Warn(string element, string raw) =>
            Services.Log.Warn($"settings.xml <{element}> value \"{raw}\" is not valid — using the default.");
    }
}
