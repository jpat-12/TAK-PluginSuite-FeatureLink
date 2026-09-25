using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace FeatureLink.Views
{
    /// <summary>
    /// Picks one or more contacts to send an existing package to.
    ///
    /// <para>Separate from <see cref="ContactPickerWindow"/>, which is single-select and belongs to
    /// the older single-layer Share button. A package is usually going to a team, and
    /// <c>SendMissionPackage</c> already takes a list of contact UIDs, so restricting this to one
    /// would be an invented limit.</para>
    /// </summary>
    public partial class ContactMultiPickerWindow : Window
    {
        private readonly ObservableCollection<DataPackageWindow.SelectableRow> _contacts =
            new ObservableCollection<DataPackageWindow.SelectableRow>();

        /// <param name="packageName">Named in the heading, so the operator can see which package
        /// they are about to send when several are listed.</param>
        /// <param name="contacts">Contacts that can actually receive — the caller filters.</param>
        /// <param name="hiddenCount">How many contacts were left out for having no route, so the
        /// absence of a familiar name is explained rather than mysterious.</param>
        public ContactMultiPickerWindow(string packageName,
            IEnumerable<DataPackageWindow.SelectableRow> contacts, int hiddenCount = 0)
        {
            InitializeComponent();

            HeadingText.Text = string.IsNullOrEmpty(packageName)
                ? "Send package" : "Send “" + packageName + "”";

            foreach (var row in contacts ?? Enumerable.Empty<DataPackageWindow.SelectableRow>())
                _contacts.Add(row);

            ContactList.ItemsSource = _contacts;

            HintText.Text = hiddenCount > 0
                ? hiddenCount + (hiddenCount == 1
                    ? " contact is hidden because there is no route to it."
                    : " contacts are hidden because there is no route to them.")
                : string.Empty;

            UpdateSendButton();
        }

        /// <summary>UIDs of the checked contacts.</summary>
        public List<string> SelectedUids
        {
            get { return _contacts.Where(c => c.IsSelected).Select(c => c.Key).ToList(); }
        }

        private void UpdateSendButton()
        {
            SendButton.IsEnabled = _contacts.Any(c => c.IsSelected);
        }

        private void Selection_Changed(object sender, RoutedEventArgs e)
        {
            UpdateSendButton();
        }

        private void SetAll(bool selected)
        {
            foreach (var row in _contacts) row.IsSelected = selected;
            UpdateSendButton();
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e) { SetAll(true); }
        private void SelectNone_Click(object sender, RoutedEventArgs e) { SetAll(false); }

        private void Send_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedUids.Count == 0) return;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
