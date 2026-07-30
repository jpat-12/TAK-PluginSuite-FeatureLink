using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using WinTak.Common.CoT;

namespace FeatureLink.Models
{
    /// <summary>
    /// One map item recently observed going out over the network, captured via
    /// ICommunicationService.PreviewCotBroadcast — this is FeatureLink's own substitute for
    /// ATAK's radial-menu "Send to Feature Layer" (SEND_TO_LAYER intent/handleSendToLayer()),
    /// since no context-menu extension point or scene-wide "item selected" service exists
    /// anywhere in the reviewed WinTAK SDK assemblies (see README). Instead of picking a map
    /// item by right-clicking it, the user picks it from this recently-seen list.
    /// </summary>
    public sealed class RecentCotItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void RaisePropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public string Uid { get; set; }

        private string _callsign;
        public string Callsign { get => _callsign; set { _callsign = value; RaisePropertyChanged(); } }

        private string _cotType;
        public string CotType { get => _cotType; set { _cotType = value; RaisePropertyChanged(); } }

        private DateTime _lastSeen;
        public DateTime LastSeen
        {
            get => _lastSeen;
            set { _lastSeen = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(LastSeenLabel)); }
        }

        /// <summary>Full CoT event as last observed — carries point/detail (contact, remarks,
        /// usericon, __group) needed to build the applyEdits payload on send.</summary>
        public CotEvent LastEvent { get; set; }

        public string LastSeenLabel
        {
            get
            {
                var age = DateTime.Now - LastSeen;
                if (age.TotalSeconds < 60) return "just now";
                if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m ago";
                return $"{(int)age.TotalHours}h ago";
            }
        }
    }
}
