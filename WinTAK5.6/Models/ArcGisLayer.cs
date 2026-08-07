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

        private string _access = "org";
        /// <summary>ArcGIS portal sharing scope for a "My ArcGIS Layers" browse item: "public"
        /// (shared to Everyone), "org", or "private". Only meaningful for items returned by
        /// ArcGisFeatureService.SearchUserLayersAsync() — decides whether
        /// MoveMyArcGisLayerOnDownload() files a downloaded item into PublicLayers or
        /// SharedPrivateLayers. Layers added via URL or received via a share default to "org"
        /// since there's no portal item to ask.</summary>
        public string Access { get => _access; set { _access = value; RaisePropertyChanged(); } }

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

        private long _featureCount;
        public long FeatureCount { get => _featureCount; set { _featureCount = value; RaisePropertyChanged(); } }

        private long _lastSyncTicks;
        /// <summary>Last successful download time (UTC ticks), 0 = never synced.</summary>
        public long LastSyncTicks
        {
            get => _lastSyncTicks;
            set { _lastSyncTicks = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(ActionGlyph)); }
        }

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
                new XElement("HasDisplayConfig", HasDisplayConfig),
                new XElement("SymJson", SymJson ?? string.Empty),
                new XElement("LblJson", LblJson ?? string.Empty),
                new XElement("PopupJson", PopupJson ?? string.Empty),
                new XElement("ShpJson", ShpJson ?? string.Empty),
                new XElement("FeatureCount", FeatureCount),
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
                HasDisplayConfig = Bool(el, "HasDisplayConfig", false),
                SymJson = NullIfEmpty(Text(el, "SymJson")),
                LblJson = NullIfEmpty(Text(el, "LblJson")),
                PopupJson = NullIfEmpty(Text(el, "PopupJson")),
                ShpJson = NullIfEmpty(Text(el, "ShpJson")),
                FeatureCount = Long(el, "FeatureCount", 0),
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
