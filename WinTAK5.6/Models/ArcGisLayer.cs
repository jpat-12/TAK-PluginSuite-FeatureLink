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

        private int _recurrenceInterval = 180;
        /// <summary>0 = auto-refresh disabled; &gt;0 = refresh every RecurrenceInterval RecurrenceUnit.</summary>
        public int RecurrenceInterval { get => _recurrenceInterval; set { _recurrenceInterval = value; RaisePropertyChanged(); } }

        private string _recurrenceUnit = "s";
        /// <summary>"s", "min", or "hr" — mirrors the Android side's three supported units.</summary>
        public string RecurrenceUnit { get => _recurrenceUnit; set { _recurrenceUnit = value; RaisePropertyChanged(); } }

        private bool _isPliLayer;
        public bool IsPliLayer { get => _isPliLayer; set { _isPliLayer = value; RaisePropertyChanged(); } }

        private bool _visible = true;
        /// <summary>Whether this layer's markers should currently be shown on the map.</summary>
        public bool Visible
        {
            get => _visible;
            set { _visible = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(EyeGlyph)); }
        }

        /// <summary>Text-glyph stand-in for item_layer.xml's ic_eye / ic_eye_off drawable — see
        /// README "Where the WinTAK UI diverges from ATAK" (no bundled icon set in this port).</summary>
        public string EyeGlyph => Visible ? "◉" : "○"; // ◉ / ○

        public ArcGisLayer() { }

        public ArcGisLayer(string name, string url, string type)
        {
            Name = name;
            Url = url;
            Type = type;
        }

        public DateTime LastSync
        {
            get => LastSyncTicks > 0 ? new DateTime(LastSyncTicks, DateTimeKind.Utc) : DateTime.MinValue;
            set => LastSyncTicks = value.ToUniversalTime().Ticks;
        }

        /// <summary>Returns the auto-refresh period, or <see cref="TimeSpan.Zero"/> if disabled.</summary>
        public TimeSpan RecurrenceTimeSpan()
        {
            if (RecurrenceInterval <= 0) return TimeSpan.Zero;
            switch (RecurrenceUnit ?? "min")
            {
                case "s": return TimeSpan.FromSeconds(RecurrenceInterval);
                case "hr": return TimeSpan.FromHours(RecurrenceInterval);
                default: return TimeSpan.FromMinutes(RecurrenceInterval);
            }
        }

        public XElement ToXElement()
        {
            return new XElement("Layer",
                new XElement("Name", Name ?? string.Empty),
                new XElement("Url", Url ?? string.Empty),
                new XElement("Type", Type ?? "public"),
                new XElement("FeatureCount", FeatureCount),
                new XElement("LastSyncTicks", LastSyncTicks),
                new XElement("DownloadEnabled", DownloadEnabled),
                new XElement("RecurrenceInterval", RecurrenceInterval),
                new XElement("RecurrenceUnit", RecurrenceUnit ?? "s"),
                new XElement("IsPliLayer", IsPliLayer),
                new XElement("Visible", Visible));
        }

        public static ArcGisLayer FromXElement(XElement el)
        {
            var layer = new ArcGisLayer
            {
                Name = (string)el.Element("Name") ?? "Unknown",
                Url = (string)el.Element("Url") ?? string.Empty,
                Type = (string)el.Element("Type") ?? "public",
                FeatureCount = (long?)el.Element("FeatureCount") ?? 0,
                LastSyncTicks = (long?)el.Element("LastSyncTicks") ?? 0,
                DownloadEnabled = (bool?)el.Element("DownloadEnabled") ?? false,
                RecurrenceInterval = (int?)el.Element("RecurrenceInterval") ?? 0,
                RecurrenceUnit = (string)el.Element("RecurrenceUnit") ?? "min",
                IsPliLayer = (bool?)el.Element("IsPliLayer") ?? false,
                Visible = (bool?)el.Element("Visible") ?? true,
            };
            return layer;
        }
    }
}
