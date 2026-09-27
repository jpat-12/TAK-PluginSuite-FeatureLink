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
    /// Asks which sublayers of a multi-layer FeatureServer to add.
    ///
    /// <para>Shown only when a service actually has more than one queryable layer. One layer is
    /// added without asking — a dialog with a single checkbox is a dialog that teaches the
    /// operator to dismiss dialogs.</para>
    /// </summary>
    public partial class SubLayerPickerWindow : Window
    {
        /// <summary>A sublayer with a tick box.</summary>
        public sealed class Row : INotifyPropertyChanged
        {
            private bool _isSelected = true;

            public Row(ArcGisFeatureService.SubLayerRef layer)
            {
                Layer = layer;
            }

            public ArcGisFeatureService.SubLayerRef Layer { get; }

            public string Display { get { return Layer.Display; } }
            public string Url { get { return Layer.Url; } }

            /// <summary>Ticked by default: the operator pasted the service because they want what
            /// is in it, and the previous behaviour silently gave them layer 0 alone.</summary>
            public bool IsSelected
            {
                get { return _isSelected; }
                set
                {
                    if (_isSelected == value) return;
                    _isSelected = value;
                    var handler = PropertyChanged;
                    if (handler != null) handler(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }

        private readonly ObservableCollection<Row> _rows = new ObservableCollection<Row>();

        public SubLayerPickerWindow(string serviceName,
            IEnumerable<ArcGisFeatureService.SubLayerRef> layers)
        {
            InitializeComponent();

            foreach (var layer in layers ?? Enumerable.Empty<ArcGisFeatureService.SubLayerRef>())
                _rows.Add(new Row(layer));

            HeadingText.Text = string.IsNullOrWhiteSpace(serviceName)
                ? _rows.Count + " layers in this service"
                : _rows.Count + " layers in “" + serviceName + "”";

            LayerList.ItemsSource = _rows;
            UpdateAddButton();
        }

        /// <summary>The sublayers the operator ticked.</summary>
        public List<ArcGisFeatureService.SubLayerRef> Selected
        {
            get { return _rows.Where(r => r.IsSelected).Select(r => r.Layer).ToList(); }
        }

        private void UpdateAddButton()
        {
            int count = _rows.Count(r => r.IsSelected);
            AddButton.IsEnabled = count > 0;
            AddButton.Content = count <= 1 ? "Add" : "Add " + count;
        }

        private void Selection_Changed(object sender, RoutedEventArgs e) { UpdateAddButton(); }

        private void SetAll(bool selected)
        {
            foreach (var row in _rows) row.IsSelected = selected;
            UpdateAddButton();
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e) { SetAll(true); }
        private void SelectNone_Click(object sender, RoutedEventArgs e) { SetAll(false); }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            if (Selected.Count == 0) return;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
