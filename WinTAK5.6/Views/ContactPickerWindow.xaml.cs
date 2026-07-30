using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace FeatureLink.Views
{
    /// <summary>
    /// Minimal contact picker for the Layers-tab Share button — WinTAK has no built-in
    /// list-selection dialog equivalent to ATAK's AlertDialog.setItems(), so this is a small
    /// dedicated Window rather than pulling in a new UI-toolkit dependency for one picker.
    /// </summary>
    public partial class ContactPickerWindow : Window
    {
        public sealed class ContactRow
        {
            public string Name { get; }
            public string Uid { get; }
            public ContactRow(string name, string uid) { Name = name; Uid = uid; }
        }

        public string SelectedUid { get; private set; }

        public ContactPickerWindow(IEnumerable<ContactRow> contacts)
        {
            InitializeComponent();
            ContactList.ItemsSource = contacts;
        }

        private void Share_Click(object sender, RoutedEventArgs e) => TryAccept();

        private void ContactList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => TryAccept();

        private void TryAccept()
        {
            if (ContactList.SelectedItem is ContactRow row)
            {
                SelectedUid = row.Uid;
                DialogResult = true;
                Close();
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
