using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Prism.Commands;
using FeatureLink.Services;

namespace FeatureLink.ViewModels
{
    /// <summary>
    /// The "Built packages" half of the PACKAGES tab: what this plugin has created, and what can
    /// be done with each one.
    ///
    /// <para>Its own view model, separate from <see cref="DataPackageWorkflow"/>, because the two
    /// halves of the tab share a screen and nothing else — one is a four-step wizard over live map
    /// state, the other is a directory listing. Folding the listing into the workflow would have
    /// been the easy move and the wrong one.</para>
    ///
    /// <para>The list comes from <see cref="PackageLibrary.Scan"/> every time it is refreshed
    /// rather than being accumulated as packages are built, so a package deleted from WinTAK's own
    /// Data Packages list, or moved, or left over from a previous run, all produce the right
    /// answer.</para>
    /// </summary>
    public sealed class PackageLibraryViewModel : INotifyPropertyChanged
    {
        /// <summary>What the listing needs from the dock pane.</summary>
        public sealed class Host
        {
            /// <summary>Where to look for packages.</summary>
            public Func<string> PackagesFolder { get; set; }

            /// <summary>Sends an existing package to contacts the operator picks.</summary>
            public Action<PackageLibrary.BuiltPackage> Send { get; set; }

            /// <summary>Shows the package in Windows Explorer.</summary>
            public Action<PackageLibrary.BuiltPackage> Reveal { get; set; }

            /// <summary>Deletes it, after confirming. Returns true when it went.</summary>
            public Func<PackageLibrary.BuiltPackage, bool> Delete { get; set; }

            public Action<string> SetStatus { get; set; }
        }

        private readonly Host _host;

        public PackageLibraryViewModel(Host host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));

            RefreshCommand = new DelegateCommand(Refresh);
            SendCommand = new DelegateCommand<PackageLibrary.BuiltPackage>(Send);
            RevealCommand = new DelegateCommand<PackageLibrary.BuiltPackage>(Reveal);
            DeleteCommand = new DelegateCommand<PackageLibrary.BuiltPackage>(Delete);
        }

        public ICommand RefreshCommand { get; }
        public ICommand SendCommand { get; }
        public ICommand RevealCommand { get; }
        public ICommand DeleteCommand { get; }

        public ObservableCollection<PackageLibrary.BuiltPackage> Packages { get; }
            = new ObservableCollection<PackageLibrary.BuiltPackage>();

        public bool HasPackages { get { return Packages.Count > 0; } }

        public bool IsEmpty { get { return Packages.Count == 0; } }

        private string _summary = "No packages built yet.";
        public string Summary
        {
            get { return _summary; }
            private set { _summary = value; Raise(); }
        }

        /// <summary>Shown when the list is empty, so an empty section explains itself rather than
        /// looking broken.</summary>
        public string EmptyText
        {
            get
            {
                return "Nothing here yet. Packages you create above appear in this list and in "
                     + "WinTAK's own Data Packages list.";
            }
        }

        /// <summary>Where the listing is reading from, shown under the heading — the single most
        /// common question about this feature is where the files actually are.</summary>
        public string FolderText
        {
            get
            {
                string folder = CurrentFolder();
                return string.IsNullOrEmpty(folder) ? string.Empty : folder;
            }
        }

        private string CurrentFolder()
        {
            try { return _host.PackagesFolder != null ? _host.PackagesFolder() : null; }
            catch (Exception ex)
            {
                Log.Warn("Could not resolve the packages folder: " + ex.Message);
                return null;
            }
        }

        /// <summary>Re-reads the folder. Safe to call often; it is a directory listing plus a few
        /// kilobytes of manifest per file.</summary>
        public void Refresh()
        {
            List<PackageLibrary.BuiltPackage> found;
            try
            {
                found = PackageLibrary.Scan(CurrentFolder());
            }
            catch (Exception ex)
            {
                // Scan is already defensive per file; this catches the folder itself vanishing.
                Log.Warn("Could not list built packages: " + ex.Message);
                found = new List<PackageLibrary.BuiltPackage>();
            }

            Packages.Clear();
            foreach (var package in found) Packages.Add(package);

            Summary = found.Count == 0
                ? "No packages built yet."
                : found.Count == 1 ? "1 package" : found.Count + " packages";

            Raise(nameof(HasPackages));
            Raise(nameof(IsEmpty));
            Raise(nameof(FolderText));
        }

        private void Send(PackageLibrary.BuiltPackage package)
        {
            if (package == null) return;
            if (_host.Send == null) return;

            try { _host.Send(package); }
            catch (Exception ex)
            {
                Log.Error("Could not send an existing package.", ex);
                Status("Could not send that package: " + ex.Message);
            }
        }

        private void Reveal(PackageLibrary.BuiltPackage package)
        {
            if (package == null || _host.Reveal == null) return;

            try { _host.Reveal(package); }
            catch (Exception ex)
            {
                Log.Warn("Could not show the package in Explorer: " + ex.Message);
                Status("Could not open that folder: " + ex.Message);
            }
        }

        private void Delete(PackageLibrary.BuiltPackage package)
        {
            if (package == null || _host.Delete == null) return;

            try
            {
                if (_host.Delete(package)) Refresh();
            }
            catch (Exception ex)
            {
                Log.Error("Could not delete a package.", ex);
                Status("Could not delete that package: " + ex.Message);
            }
        }

        private void Status(string text)
        {
            if (_host.SetStatus != null) _host.SetStatus(text);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise([CallerMemberName] string name = null)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }
}
