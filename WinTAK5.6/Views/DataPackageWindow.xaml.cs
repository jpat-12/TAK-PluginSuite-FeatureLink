using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using FeatureLink.Services;

namespace FeatureLink.Views
{
    /// <summary>
    /// Picks the layers that go into a data package, names it, and picks who receives it.
    ///
    /// <para>The dialog only gathers a choice — it neither builds nor sends the package, so nothing
    /// here touches the WinTAK SDK and the window can be opened from a designer. The caller reads
    /// <see cref="SelectedLayerKeys"/>, <see cref="SelectedContactUids"/>, <see cref="PackageName"/>
    /// and <see cref="SaveToFileRequested"/> after a true dialog result.</para>
    /// </summary>
    public partial class DataPackageWindow : Window
    {
        /// <summary>A selectable row. <see cref="Key"/> is opaque to the dialog: the caller uses it
        /// to map a row back to whatever it came from (a layer URL, a contact UID).</summary>
        public sealed class SelectableRow : INotifyPropertyChanged
        {
            private bool _isSelected;

            public SelectableRow(string key, string name, string detail = null)
            {
                Key = key;
                Name = name;
                Detail = detail;
            }

            public string Key { get; }
            public string Name { get; }
            public string Detail { get; }

            public bool IsSelected
            {
                get { return _isSelected; }
                set { if (_isSelected != value) { _isSelected = value; Raise(); } }
            }

            public event PropertyChangedEventHandler PropertyChanged;

            private void Raise([CallerMemberName] string name = null)
            {
                var handler = PropertyChanged;
                if (handler != null) handler(this, new PropertyChangedEventArgs(name));
            }
        }

        private readonly ObservableCollection<SelectableRow> _layers =
            new ObservableCollection<SelectableRow>();
        private readonly ObservableCollection<SelectableRow> _contacts =
            new ObservableCollection<SelectableRow>();

        /// <summary>Set once the operator types in the name box, after which the automatic name
        /// stops overwriting their text. Without this, checking one more layer would silently
        /// discard a name they had just typed.</summary>
        private bool _nameEdited;

        /// <summary>Guards the programmatic name update from being mistaken for an edit.</summary>
        private bool _updatingName;

        /// <summary>Summarises the current selection, so the dialog can report what will be
        /// packaged before anything is written. Supplied by the caller because building a plan
        /// needs the layers' configs, which the dialog does not hold.</summary>
        private readonly Func<IReadOnlyList<string>, string> _summarize;

        public DataPackageWindow(
            IEnumerable<SelectableRow> layers,
            IEnumerable<SelectableRow> contacts,
            Func<IReadOnlyList<string>, string> summarize = null)
        {
            InitializeComponent();

            _summarize = summarize;

            foreach (var row in layers ?? Enumerable.Empty<SelectableRow>()) _layers.Add(row);
            foreach (var row in contacts ?? Enumerable.Empty<SelectableRow>()) _contacts.Add(row);

            LayerList.ItemsSource = _layers;
            ContactList.ItemsSource = _contacts;

            if (_contacts.Count == 0)
            {
                ContactList.Visibility = Visibility.Collapsed;
                NoContactsNote.Visibility = Visibility.Visible;
            }

            // Opening with everything selected would make an accidental Send broadcast every layer
            // on the machine. The operator opts in.
            RefreshName();
            RefreshSummary();
        }

        // ---------------------------------------------------------------------
        // Results
        // ---------------------------------------------------------------------

        /// <summary>Keys of the checked layers, in display order.</summary>
        public IReadOnlyList<string> SelectedLayerKeys
        {
            get { return _layers.Where(r => r.IsSelected).Select(r => r.Key).ToList(); }
        }

        /// <summary>UIDs of the checked contacts. Empty when the operator chose to save instead.</summary>
        public IReadOnlyList<string> SelectedContactUids
        {
            get { return _contacts.Where(r => r.IsSelected).Select(r => r.Key).ToList(); }
        }

        /// <summary>The package name, automatic unless the operator edited it.</summary>
        public string PackageName { get; private set; }

        /// <summary>True when the operator pressed "Save to file…" rather than "Send".</summary>
        public bool SaveToFileRequested { get; private set; }

        // ---------------------------------------------------------------------
        // Name and summary
        // ---------------------------------------------------------------------

        private void RefreshName()
        {
            if (_nameEdited) return;

            var names = _layers.Where(r => r.IsSelected).Select(r => r.Name).ToList();
            _updatingName = true;
            try { NameBox.Text = DataPackageBuilder.AutoName(names, DateTime.Now); }
            finally { _updatingName = false; }
        }

        private void RefreshSummary()
        {
            var keys = SelectedLayerKeys;

            if (keys.Count == 0)
            {
                SummaryText.Text = "Nothing selected — choose at least one layer to include.";
                WarningText.Visibility = Visibility.Collapsed;
                SendButton.IsEnabled = false;
                SaveButton.IsEnabled = false;
                return;
            }

            SummaryText.Text = _summarize != null
                ? "Package contains: " + _summarize(keys)
                : "Package contains: " + keys.Count + (keys.Count == 1 ? " layer" : " layers");

            SaveButton.IsEnabled = true;
            bool anyContact = _contacts.Any(r => r.IsSelected);
            SendButton.IsEnabled = anyContact;

            if (!anyContact && _contacts.Count > 0)
            {
                WarningText.Text = "Choose at least one contact to send to, or save the package to a file.";
                WarningText.Visibility = Visibility.Visible;
            }
            else
            {
                WarningText.Visibility = Visibility.Collapsed;
            }
        }

        private void NameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_updatingName) return;
            _nameEdited = true;
        }

        private void Selection_Changed(object sender, RoutedEventArgs e)
        {
            RefreshName();
            RefreshSummary();
        }

        // ---------------------------------------------------------------------
        // Buttons
        // ---------------------------------------------------------------------

        private void SetAll(ObservableCollection<SelectableRow> rows, bool selected)
        {
            foreach (var row in rows) row.IsSelected = selected;
            RefreshName();
            RefreshSummary();
        }

        private void SelectAllLayers_Click(object sender, RoutedEventArgs e) { SetAll(_layers, true); }
        private void SelectNoLayers_Click(object sender, RoutedEventArgs e) { SetAll(_layers, false); }
        private void SelectAllContacts_Click(object sender, RoutedEventArgs e) { SetAll(_contacts, true); }
        private void SelectNoContacts_Click(object sender, RoutedEventArgs e) { SetAll(_contacts, false); }

        private bool Commit()
        {
            if (SelectedLayerKeys.Count == 0) return false;

            string typed = (NameBox.Text ?? string.Empty).Trim();
            // An emptied name box must not produce an unnamed package; fall back to the automatic
            // name rather than refusing the send.
            PackageName = typed.Length > 0
                ? typed
                : DataPackageBuilder.AutoName(
                    _layers.Where(r => r.IsSelected).Select(r => r.Name), DateTime.Now);
            return true;
        }

        private void Send_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedContactUids.Count == 0 || !Commit()) return;
            SaveToFileRequested = false;
            DialogResult = true;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (!Commit()) return;
            SaveToFileRequested = true;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
