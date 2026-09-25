using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Prism.Commands;
using FeatureLink.Models;
using FeatureLink.Services;

namespace FeatureLink.ViewModels
{
    /// <summary>
    /// The data-package workflow: name it, pick the points, review them, choose who gets it.
    ///
    /// <para>Its own view model rather than another six hundred lines of
    /// <c>FeatureLinkDockPane</c>, which is already the largest file in the port. Everything it
    /// needs from the host arrives as a delegate, so the workflow itself depends on no WinTAK type
    /// — <see cref="MapAreaSelector"/> holds all of that — and the selection arithmetic lives in
    /// <see cref="FeatureSelection"/>, where it is tested.</para>
    /// </summary>
    public sealed class DataPackageWorkflow : INotifyPropertyChanged
    {
        /// <summary>Where the operator is in the workflow.</summary>
        public enum Step
        {
            /// <summary>Not started — the tab shows the intro.</summary>
            Idle = 0,

            /// <summary>Name the package.</summary>
            Name = 1,

            /// <summary>Pick features on the map.</summary>
            Select = 2,

            /// <summary>Review what is selected; add or remove.</summary>
            Review = 3,

            /// <summary>Choose contacts and send.</summary>
            Send = 4,
        }

        /// <summary>What the workflow needs from the dock pane. Delegates rather than an interface
        /// on the pane, so this class can be exercised without one.</summary>
        public sealed class Host
        {
            /// <summary>Layers that have been downloaded and so have features to pick from.</summary>
            public Func<IReadOnlyList<ArcGisLayer>> PackageableLayers { get; set; }

            /// <summary>Contacts currently reachable, as (uid, name).</summary>
            public Func<IReadOnlyList<Tuple<string, string>>> Contacts { get; set; }

            /// <summary>Serializes one feature to CoT XML, or null when it cannot be built.</summary>
            public Func<ArcGisLayer, LayerFeature, string> BuildFeatureCot { get; set; }

            /// <summary>The layer's <c>.featurelinkshare</c> config.</summary>
            public Func<ArcGisLayer, string> BuildShareConfig { get; set; }

            /// <summary>Writes the package, and sends it when asked to.</summary>
            public Func<DataPackageBuilder.PackagePlan, string, IReadOnlyList<string>, bool, DeliveryResult> Deliver { get; set; }

            /// <summary>Puts a line in the panel's status strip.</summary>
            public Action<string> SetStatus { get; set; }

            /// <summary>Centres the map on a feature, so a review-list row can be located.</summary>
            public Action<double, double> LookAt { get; set; }
        }

        /// <summary>What came of a delivery. An explicit flag rather than the caller sniffing the
        /// message for a prefix: whether the workflow closes is a real decision, and tying it to
        /// the first word of an operator-facing sentence meant rewording that sentence could
        /// silently stop the workflow closing.</summary>
        public sealed class DeliveryResult
        {
            /// <summary>True when the package was written, and sent if sending was asked for.</summary>
            public bool Succeeded { get; set; }

            /// <summary>Line for the status strip. Null when the operator cancelled a dialog, in
            /// which case nothing is reported and nothing changes.</summary>
            public string Message { get; set; }

            public static DeliveryResult Ok(string message)
            {
                return new DeliveryResult { Succeeded = true, Message = message };
            }

            public static DeliveryResult Failed(string message)
            {
                return new DeliveryResult { Succeeded = false, Message = message };
            }

            /// <summary>The operator backed out of a file dialog — not a failure to report.</summary>
            public static DeliveryResult Cancelled()
            {
                return new DeliveryResult { Succeeded = false, Message = null };
            }
        }

        private readonly Host _host;
        private readonly MapAreaSelector _selector;
        private readonly FeatureSelection _selection = new FeatureSelection();

        public DataPackageWorkflow(Host host, MapAreaSelector selector)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _selector = selector;

            if (_selector != null)
            {
                _selector.FeatureClicked += OnFeatureClicked;
                _selector.ShapeDrawn += OnShapeDrawn;
                _selector.Stopped += OnSelectorStopped;
            }

            StartCommand = new DelegateCommand<ArcGisLayer>(Start);
            CancelCommand = new DelegateCommand(Cancel);
            NextCommand = new DelegateCommand(Next);
            BackCommand = new DelegateCommand(Back);

            SelectPointsCommand = new DelegateCommand(() => SetMode(MapAreaSelector.Mode.Points));
            SelectBoxCommand = new DelegateCommand(() => SetMode(MapAreaSelector.Mode.Box));
            SelectRadiusCommand = new DelegateCommand(() => SetMode(MapAreaSelector.Mode.Radius));
            StopSelectingCommand = new DelegateCommand(() => SetMode(MapAreaSelector.Mode.None));

            SelectAllInLayerCommand = new DelegateCommand<ArcGisLayer>(SelectWholeLayer);
            RemoveFeatureCommand = new DelegateCommand<SelectableFeature>(RemoveFeature);
            LocateFeatureCommand = new DelegateCommand<SelectableFeature>(LocateFeature);
            ClearSelectionCommand = new DelegateCommand(ClearSelection);

            SendCommand = new DelegateCommand(() => Deliver(send: true));
            SaveCommand = new DelegateCommand(() => Deliver(send: false));
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Commands
        // ─────────────────────────────────────────────────────────────────────────

        public ICommand StartCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand NextCommand { get; }
        public ICommand BackCommand { get; }
        public ICommand SelectPointsCommand { get; }
        public ICommand SelectBoxCommand { get; }
        public ICommand SelectRadiusCommand { get; }
        public ICommand StopSelectingCommand { get; }
        public ICommand SelectAllInLayerCommand { get; }
        public ICommand RemoveFeatureCommand { get; }
        public ICommand LocateFeatureCommand { get; }
        public ICommand ClearSelectionCommand { get; }
        public ICommand SendCommand { get; }
        public ICommand SaveCommand { get; }

        // ─────────────────────────────────────────────────────────────────────────
        // State
        // ─────────────────────────────────────────────────────────────────────────

        private Step _currentStep = Step.Idle;
        public Step CurrentStep
        {
            get { return _currentStep; }
            private set
            {
                if (_currentStep == value) return;
                _currentStep = value;
                Raise();
                Raise(nameof(IsIdle));
                Raise(nameof(IsNaming));
                Raise(nameof(IsSelecting));
                Raise(nameof(IsReviewing));
                Raise(nameof(IsSending));
                Raise(nameof(StepTitle));
                Raise(nameof(CanGoNext));
                Raise(nameof(CanGoBack));
                Raise(nameof(NextLabel));
            }
        }

        public bool IsIdle { get { return CurrentStep == Step.Idle; } }
        public bool IsNaming { get { return CurrentStep == Step.Name; } }
        public bool IsSelecting { get { return CurrentStep == Step.Select; } }
        public bool IsReviewing { get { return CurrentStep == Step.Review; } }
        public bool IsSending { get { return CurrentStep == Step.Send; } }

        public string StepTitle
        {
            get
            {
                switch (CurrentStep)
                {
                    case Step.Name: return "Step 1 of 4 — Name the package";
                    case Step.Select: return "Step 2 of 4 — Select data";
                    case Step.Review: return "Step 3 of 4 — Review data";
                    case Step.Send: return "Step 4 of 4 — Send or save";
                    default: return "Data package";
                }
            }
        }

        private string _packageName = string.Empty;
        public string PackageName
        {
            get { return _packageName; }
            set
            {
                if (_packageName == value) return;
                _packageName = value;
                _nameEdited = true;
                Raise();
                Raise(nameof(CanGoNext));
            }
        }

        /// <summary>Once the operator types a name, the automatic one stops overwriting it —
        /// otherwise selecting one more feature would silently discard what they just typed.</summary>
        private bool _nameEdited;

        private string _selectionSummary = "No features selected.";
        public string SelectionSummary
        {
            get { return _selectionSummary; }
            private set { _selectionSummary = value; Raise(); }
        }

        private string _modeHint = string.Empty;
        public string ModeHint
        {
            get { return _modeHint; }
            private set { _modeHint = value; Raise(); }
        }

        private MapAreaSelector.Mode _mode = MapAreaSelector.Mode.None;
        public MapAreaSelector.Mode ActiveMode
        {
            get { return _mode; }
            private set
            {
                if (_mode == value) return;
                _mode = value;
                Raise();
                Raise(nameof(IsPickingPoints));
                Raise(nameof(IsDrawingBox));
                Raise(nameof(IsDrawingRadius));
                Raise(nameof(IsMapModeActive));
            }
        }

        public bool IsPickingPoints { get { return ActiveMode == MapAreaSelector.Mode.Points; } }
        public bool IsDrawingBox { get { return ActiveMode == MapAreaSelector.Mode.Box; } }
        public bool IsDrawingRadius { get { return ActiveMode == MapAreaSelector.Mode.Radius; } }
        public bool IsMapModeActive { get { return ActiveMode != MapAreaSelector.Mode.None; } }

        /// <summary>The review list.</summary>
        public ObservableCollection<SelectableFeature> SelectedFeatures { get; }
            = new ObservableCollection<SelectableFeature>();

        /// <summary>Layers offered for "select everything in this layer".</summary>
        public ObservableCollection<ArcGisLayer> AvailableLayers { get; }
            = new ObservableCollection<ArcGisLayer>();

        /// <summary>Contacts, with a checkbox each.</summary>
        public ObservableCollection<ContactChoice> Contacts { get; }
            = new ObservableCollection<ContactChoice>();

        public bool HasContacts { get { return Contacts.Count > 0; } }

        public int SelectedCount { get { return _selection.Count; } }

        public bool CanGoNext
        {
            get
            {
                switch (CurrentStep)
                {
                    case Step.Name: return !string.IsNullOrWhiteSpace(PackageName);
                    case Step.Select: return _selection.Count > 0;
                    case Step.Review: return _selection.Count > 0;
                    default: return false;
                }
            }
        }

        public string NextLabel { get { return CurrentStep == Step.Review ? "Send or save" : "Next"; } }

        /// <summary>Back exists from the second step onwards. Explicit rather than "not the first
        /// step", because Idle is not a step the operator can go back into.</summary>
        public bool CanGoBack
        {
            get
            {
                return CurrentStep == Step.Select || CurrentStep == Step.Review
                    || CurrentStep == Step.Send;
            }
        }

        /// <summary>Sending needs both a selection and a recipient.</summary>
        public bool CanSend
        {
            get { return _selection.Count > 0 && Contacts.Any(c => c.IsSelected); }
        }

        /// <summary>Saving needs only a selection. Deliberately independent of
        /// <see cref="CanSend"/>: creating the package without picking anybody is a supported
        /// outcome, not a consolation prize for when there is nobody to send to.</summary>
        public bool CanSave { get { return _selection.Count > 0; } }

        /// <summary>Shown under the contact list, so an empty tick-list never reads as a dead
        /// end.</summary>
        public string ContactHint
        {
            get
            {
                if (!HasContacts)
                    return "No contacts are reachable. You can still save the package to a file.";
                return Contacts.Any(c => c.IsSelected)
                    ? "You can also save a copy to a file."
                    : "Choosing contacts is optional — you can just save the package to a file.";
            }
        }

        /// <summary>A contact row with a checkbox.</summary>
        public sealed class ContactChoice : INotifyPropertyChanged
        {
            private readonly Action _changed;
            private bool _isSelected;

            public ContactChoice(string uid, string name, Action changed)
            {
                Uid = uid; Name = name; _changed = changed;
            }

            public string Uid { get; }
            public string Name { get; }

            public bool IsSelected
            {
                get { return _isSelected; }
                set
                {
                    if (_isSelected == value) return;
                    _isSelected = value;
                    var h = PropertyChanged;
                    if (h != null) h(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                    if (_changed != null) _changed();
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Flow
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>Begins the workflow. A non-null layer pre-selects everything in it, which is
        /// what the per-layer button does.</summary>
        public void Start(ArcGisLayer seed)
        {
            _selection.Clear();
            SelectedFeatures.Clear();
            _nameEdited = false;
            ActiveMode = MapAreaSelector.Mode.None;

            RefreshLayers();
            RefreshContacts();

            if (seed != null) SelectWholeLayer(seed);

            AutoName();
            CurrentStep = Step.Name;
            RefreshSelectionState();
        }

        public void Cancel()
        {
            StopMapMode();
            _selection.Clear();
            SelectedFeatures.Clear();
            CurrentStep = Step.Idle;
            RefreshSelectionState();
        }

        private void Next()
        {
            if (!CanGoNext) return;

            switch (CurrentStep)
            {
                case Step.Name:
                    CurrentStep = Step.Select;
                    ModeHint = "Choose how to pick features, then draw on the map.";
                    break;

                case Step.Select:
                    StopMapMode();
                    CurrentStep = Step.Review;
                    break;

                case Step.Review:
                    RefreshContacts();
                    CurrentStep = Step.Send;
                    break;
            }
        }

        private void Back()
        {
            switch (CurrentStep)
            {
                case Step.Select:
                    StopMapMode();
                    CurrentStep = Step.Name;
                    break;

                case Step.Review:
                    // "Add more" is the same selection step with everything already chosen still
                    // chosen — the selection is keyed by UID, so re-drawing over the same ground
                    // adds only what is new instead of toggling existing picks back off.
                    CurrentStep = Step.Select;
                    ModeHint = _selection.Count + " already selected — drawing again adds to them.";
                    break;

                case Step.Send:
                    CurrentStep = Step.Review;
                    break;
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Selection
        // ─────────────────────────────────────────────────────────────────────────

        private void SetMode(MapAreaSelector.Mode mode)
        {
            if (_selector == null || !_selector.IsAvailable)
            {
                Status("Map selection is unavailable — WinTAK did not export a map view controller. "
                       + "Use \"Add whole layer\" instead.");
                return;
            }

            if (mode == MapAreaSelector.Mode.None || mode == ActiveMode)
            {
                StopMapMode();
                return;
            }

            if (!_selector.Start(mode))
            {
                Status("Could not start map selection.");
                return;
            }

            ActiveMode = mode;
            switch (mode)
            {
                case MapAreaSelector.Mode.Points:
                    ModeHint = "Click features on the map to add or remove them. Click the button again to stop.";
                    break;
                case MapAreaSelector.Mode.Box:
                    ModeHint = "Drag a rectangle on the map. Everything inside it is added.";
                    break;
                case MapAreaSelector.Mode.Radius:
                    ModeHint = "Drag out from a centre point. Everything inside the radius is added.";
                    break;
            }
        }

        private void StopMapMode()
        {
            if (_selector != null) _selector.Stop();
            ActiveMode = MapAreaSelector.Mode.None;
        }

        private void OnSelectorStopped()
        {
            ActiveMode = MapAreaSelector.Mode.None;
        }

        private void OnFeatureClicked(string uid)
        {
            var found = FindFeature(uid);
            if (found == null)
            {
                // Clicking someone else's marker, or a host item, is not an error — just say why
                // nothing happened rather than appearing to ignore the click.
                Status("That map item is not part of a downloaded FeatureLink layer.");
                return;
            }

            bool nowSelected = _selection.Toggle(found);
            Status((nowSelected ? "Added " : "Removed ") + found.DisplayName
                   + " — " + _selection.Count + " selected.");
            RefreshSelectionState();
        }

        private void OnShapeDrawn(ISelectionShape shape)
        {
            if (shape == null) return;

            int added = _selection.AddWithin(shape, AllCandidates());
            ActiveMode = MapAreaSelector.Mode.None;

            Status(added == 0
                ? "Nothing inside that " + shape.Describe() + " — " + _selection.Count + " still selected."
                : "Added " + added + " from that " + shape.Describe() + " — "
                  + _selection.Count + " selected.");

            RefreshSelectionState();
        }

        private void SelectWholeLayer(ArcGisLayer layer)
        {
            if (layer == null) return;

            int added = 0;
            foreach (var feature in CandidatesFrom(layer))
                if (_selection.Add(feature)) added++;

            Status(added == 0
                ? "\"" + layer.Name + "\" has no plotted features to add."
                : "Added " + added + " from \"" + layer.Name + "\" — " + _selection.Count + " selected.");

            RefreshSelectionState();
        }

        private void RemoveFeature(SelectableFeature feature)
        {
            if (feature == null) return;
            if (_selection.Remove(feature.Uid)) RefreshSelectionState();
        }

        private void LocateFeature(SelectableFeature feature)
        {
            if (feature == null || _host.LookAt == null) return;
            _host.LookAt(feature.Lat, feature.Lon);
        }

        private void ClearSelection()
        {
            _selection.Clear();
            RefreshSelectionState();
            Status("Selection cleared.");
        }

        /// <summary>Every plotted feature across every downloaded layer.</summary>
        private IEnumerable<SelectableFeature> AllCandidates()
        {
            return AvailableLayers.SelectMany(CandidatesFrom);
        }

        private static IEnumerable<SelectableFeature> CandidatesFrom(ArcGisLayer layer)
        {
            if (layer == null || layer.AllFeatures == null) yield break;

            foreach (var f in layer.AllFeatures)
            {
                if (f == null || string.IsNullOrEmpty(f.Uid)) continue;
                yield return new SelectableFeature
                {
                    LayerUrl = layer.Url,
                    LayerName = layer.Name,
                    Uid = f.Uid,
                    Callsign = f.Callsign,
                    Lat = f.Lat,
                    Lon = f.Lon,
                };
            }
        }

        private SelectableFeature FindFeature(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;
            return AllCandidates().FirstOrDefault(
                f => string.Equals(f.Uid, uid, StringComparison.Ordinal));
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Refresh
        // ─────────────────────────────────────────────────────────────────────────

        private void RefreshLayers()
        {
            AvailableLayers.Clear();
            var layers = _host.PackageableLayers != null ? _host.PackageableLayers() : null;
            foreach (var layer in layers ?? (IReadOnlyList<ArcGisLayer>)new List<ArcGisLayer>())
                AvailableLayers.Add(layer);
        }

        private void RefreshContacts()
        {
            var selected = new HashSet<string>(
                Contacts.Where(c => c.IsSelected).Select(c => c.Uid), StringComparer.Ordinal);

            Contacts.Clear();
            var rows = _host.Contacts != null ? _host.Contacts() : null;
            foreach (var row in rows ?? (IReadOnlyList<Tuple<string, string>>)new List<Tuple<string, string>>())
            {
                if (row == null || string.IsNullOrEmpty(row.Item1)) continue;
                var choice = new ContactChoice(row.Item1, row.Item2 ?? row.Item1, OnContactToggled);
                // A contact already ticked stays ticked across a refresh.
                if (selected.Contains(row.Item1)) choice.IsSelected = true;
                Contacts.Add(choice);
            }

            Raise(nameof(HasContacts));
            Raise(nameof(CanSend));
            Raise(nameof(CanSave));
            Raise(nameof(ContactHint));
        }

        private void OnContactToggled()
        {
            Raise(nameof(CanSend));
            Raise(nameof(ContactHint));
        }

        private void RefreshSelectionState()
        {
            SelectedFeatures.Clear();
            foreach (var f in _selection.Items) SelectedFeatures.Add(f);

            SelectionSummary = _selection.IsEmpty
                ? "No features selected."
                : _selection.Describe() + " selected.";

            if (!_nameEdited) AutoName();

            Raise(nameof(SelectedCount));
            Raise(nameof(CanGoNext));
            Raise(nameof(CanSend));
            Raise(nameof(CanSave));
            Raise(nameof(ContactHint));
        }

        private void AutoName()
        {
            var names = _selection.ByLayer()
                .Select(g => g.LayerName)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();

            _packageName = DataPackageBuilder.AutoName(names, DateTime.Now);
            Raise(nameof(PackageName));
            Raise(nameof(CanGoNext));
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Delivery
        // ─────────────────────────────────────────────────────────────────────────

        private void Deliver(bool send)
        {
            if (_selection.IsEmpty)
            {
                Status("Nothing selected.");
                return;
            }

            var recipients = Contacts.Where(c => c.IsSelected).Select(c => c.Uid).ToList();

            // Sending needs a recipient; SAVING DOES NOT. Choosing nobody is a legitimate way to
            // finish — the operator gets the .zip and moves it by whatever means they have, which
            // on a disconnected network is often the only means there is.
            if (send && recipients.Count == 0)
            {
                Status("Choose at least one contact, or save the package to a file instead.");
                return;
            }

            var plan = BuildPlan();
            if (plan == null || plan.Entries.Count == 0)
            {
                Status("Nothing could be packaged from that selection.");
                return;
            }

            var result = _host.Deliver != null
                ? _host.Deliver(plan, PackageName, recipients, send)
                : DeliveryResult.Failed("Packaging is unavailable.");

            if (result == null) return;
            if (!string.IsNullOrEmpty(result.Message)) Status(result.Message);

            // Saving is as much a completion as sending, so both close the workflow. A FAILURE
            // leaves the selection intact so the operator can retry without rebuilding it by hand
            // — which is the case that matters, since a hand-picked selection of points is the
            // expensive thing to recreate.
            if (result.Succeeded) Cancel();
        }

        /// <summary>Turns the selection into a package plan: one config plus its iconsets per
        /// contributing layer, and the selected features as CoT.</summary>
        public DataPackageBuilder.PackagePlan BuildPlan()
        {
            var byUrl = AvailableLayers
                .Where(l => l != null && !string.IsNullOrEmpty(l.Url))
                .GroupBy(l => l.Url, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var inputs = new List<DataPackageBuilder.LayerPlanInput>();

            foreach (var group in _selection.ByLayer())
            {
                ArcGisLayer layer;
                if (!byUrl.TryGetValue(group.LayerUrl ?? string.Empty, out layer)) continue;

                var features = new List<DataPackageBuilder.FeatureContent>();
                foreach (var selected in group.Features)
                {
                    var source = layer.AllFeatures?.FirstOrDefault(
                        f => f != null && string.Equals(f.Uid, selected.Uid, StringComparison.Ordinal));
                    if (source == null) continue;

                    string cot = _host.BuildFeatureCot != null
                        ? _host.BuildFeatureCot(layer, source) : null;
                    if (string.IsNullOrWhiteSpace(cot)) continue;

                    features.Add(new DataPackageBuilder.FeatureContent
                    {
                        Uid = selected.Uid,
                        CotXml = cot,
                    });
                }

                inputs.Add(new DataPackageBuilder.LayerPlanInput
                {
                    Name = layer.Name,
                    Url = layer.Url,
                    ConfigJson = _host.BuildShareConfig != null ? _host.BuildShareConfig(layer) : null,
                    IconsetUids = (layer.IconsetUids ?? string.Empty)
                        .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries),
                    Features = features,
                });
            }

            return DataPackageBuilder.Plan(inputs, null);
        }

        private void Status(string text)
        {
            if (_host.SetStatus != null) _host.SetStatus(text);
        }

        // ─────────────────────────────────────────────────────────────────────────

        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise([CallerMemberName] string name = null)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }
}
