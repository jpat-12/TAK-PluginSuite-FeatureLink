using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Prism.Commands;
using TAKEngine.Core;
using WinTak.Common.CoT;
using WinTak.Common.Location;
using WinTak.Common.Services;
using WinTak.Common.Time;
using WinTak.CursorOnTarget.Services;
using WinTak.Net.Contacts;
using WinTak.Framework.Docking;
using WinTak.Framework.Docking.Attributes;
using FeatureLink.Models;
using FeatureLink.Services;
using FeatureLink.Views;
using Newtonsoft.Json.Linq;

namespace FeatureLink.ViewModels
{
    /// <summary>
    /// Dock-pane view model for FeatureLink — ported from FeatureLinkDropDownReceiver.java's
    /// Home / Layers / PLI 3-tab state machine, including its "pushed full-panel overlay" pattern
    /// for the Account and Add Layer pages (main_layout.xml's overlay_container) and each card's
    /// collapse/expand chevron (page_home.xml's stats_content, page_layers.xml's
    /// private/public_layers_content, page_pli.xml's pli_layer_content).
    ///
    /// Display-config symbology (icons, colours, labels, popups <b>and</b> polyline/polygon shape
    /// styling), Mission Package layer share, and "send item to PLI layer" are all implemented —
    /// the class comment that used to claim they were "explicitly out of scope" has been false
    /// since commit 94ef70b, and shape styling landed with the C-07 remediation. QR scanning and
    /// the ATAK radial-menu injection remain out of scope (there is no WinTAK equivalent); see the
    /// project README.
    /// </summary>
    [DockPane(ID, "FeatureLink", Content = typeof(FeatureLinkView))]
    [Export(typeof(IDockPane))]
    public class FeatureLinkDockPane : DockPane, IDisposable
    {
        internal const string ID = "FeatureLink_FeatureLinkDockPane";

        // How often the PLI auto-send ticks — matches ATAK's
        // pliScheduler.scheduleAtFixedRate(this::sendPliUpdate, 0, 30, TimeUnit.SECONDS).
        private static readonly TimeSpan PliSendInterval = TimeSpan.FromSeconds(30);

        // How often the layer-recurrence check runs.
        private static readonly TimeSpan RecurrenceCheckInterval = TimeSpan.FromSeconds(15);

        /// <summary>Hard ceiling on a single layer download or PLI send (C-26). Without one, a
        /// black-holed endpoint left an operation "in flight" forever and its re-entrancy guard
        /// permanently closed.</summary>
        private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan PliOperationTimeout = TimeSpan.FromSeconds(60);

        /// <summary>Staleness multiplier for a synced feature. The old value was a flat 365 days:
        /// if the plugin was uninstalled, or a layer removed while WinTAK was closed, those
        /// markers persisted on every peer's map for a year.</summary>
        private const int StaleIntervalMultiplier = 3;
        private static readonly TimeSpan DefaultFeatureStaleness = TimeSpan.FromHours(24);
        private static readonly TimeSpan SentItemStaleness = TimeSpan.FromDays(7);

        /// <summary>CoT type grammar. An unvalidated ArcGIS string used to be emitted straight onto
        /// the network. The fallback is <c>a-u-G</c> (<b>unknown</b>) rather than the previous
        /// <c>a-f-G</c> — asserting a <i>friendly</i> affiliation the source data never claimed is
        /// an operational-safety problem in its own right.</summary>
        private static readonly System.Text.RegularExpressions.Regex CotTypePattern =
            new System.Text.RegularExpressions.Regex(@"^[a-z](-[a-zA-Z0-9]+)+$",
                System.Text.RegularExpressions.RegexOptions.Compiled);
        private const string UnknownCotType = "a-u-G";
        private const int MaxCotTypeLength = 64;

        /// <summary>Schema version this build writes and is willing to read.</summary>
        private const int ShareSchemaVersion = 2;

        /// <summary>Feature count above which the operator is asked to confirm a download, and the
        /// count above which it is refused outright.
        ///
        /// <para><b>SME-UNCONFIRMED.</b> These two numbers are engineering estimates, not values
        /// anyone has validated against a fielded workstation or a CAP mission profile. They exist
        /// because the previous behaviour — no ceiling at all, up to the client's own
        /// 5,000-page × 1,000-row limit — froze the application with no warning. A wrong-but-stated
        /// limit is recoverable; no limit is not. Confirm both with an operator who has run a large
        /// layer on target hardware, then delete this paragraph.</para></summary>
        private const long LargeDownloadPromptThreshold = 5000;
        private const long MaxDownloadableFeatures = 50000;

        // ── Injected services ────────────────────────────────────────────────────

        private readonly ICotMessageSender _cotSender;
        private readonly ILocationService _locationService;
        private readonly ICommunicationService _communicationService;
        private readonly IMapItemFinderService _mapItemFinder;
        private readonly WinTak.Net.Contacts.IContactService _contactService;
        /// <summary>WinTAK's map camera, used by "zoom to layer".
        ///
        /// <para>A PROPERTY import with <c>AllowDefault</c>, deliberately, rather than a
        /// constructor parameter: an unsatisfied constructor import fails MEF composition and the
        /// whole dock pane silently never loads. Zooming is a convenience, and losing the entire
        /// plugin because the host did not export this contract would be a wildly
        /// disproportionate failure. Null simply means the affordance does nothing, and says so
        /// in the log.</para></summary>
        /// <summary>WinTAK's package list, so a package FeatureLink creates is findable in the
        /// host afterwards rather than being only a file on disk.
        ///
        /// <para>A property import with <c>AllowDefault</c>, for the same reason as
        /// <see cref="MapViewController"/> below: an unsatisfied constructor import fails MEF
        /// composition and the whole dock pane never loads. Listing is an enhancement on top of a
        /// package that is written and sent either way, so its absence must cost only the listing.
        /// </para></summary>
        [Import(AllowDefault = true)]
        public WinTak.MissionPackages.IMissionPackageService MissionPackageService { get; set; }

        [Import(AllowDefault = true)]
        public WinTak.Display.IMapViewController MapViewController
        {
            get { return _mapViewController; }
            set
            {
                _mapViewController = value;
                // The concrete MapViewControl also implements TAKEngine.Core.RenderContext, which
                // is IconsetInstaller's only route onto the render thread. Handed over here
                // because MEF sets this after construction, so the installer cannot ask for it.
                IconsetInstaller.RenderHost = value;
            }
        }
        private WinTak.Display.IMapViewController _mapViewController;

        private readonly ArcGisAuthService _authService = new ArcGisAuthService();
        private readonly ArcGisFeatureService _restClient = new ArcGisFeatureService();

        private Timer _pliTimer;
        private Timer _recurrenceTimer;
        private FileSystemWatcher _shareFileWatcher;

        /// <summary>Captured at construction. <c>Application.Current</c> can legitimately be null
        /// in a plugin host, and the old fallback ran UI mutations on whatever background thread
        /// happened to be current — an <c>ObservableCollection</c> mutation off the dispatcher.</summary>
        private readonly Dispatcher _dispatcher;

        /// <summary>Cancelled on <see cref="Dispose"/> so in-flight downloads/PLI sends stop
        /// instead of completing against a detached pane.</summary>
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private bool _disposed;

        /// <summary>UIDs of markers this pane has injected onto the map for a given layer URL.
        /// Mutated from the recurrence timer thread, read from WinTAK's CoT pipeline thread and
        /// from the UI thread — previously with <b>no</b> synchronisation of any kind, which is a
        /// realistic "collection modified during enumeration" crash given the 30 s PLI timer, the
        /// 15 s recurrence timer and continuous CoT traffic. Every access now takes
        /// <see cref="_markerLock"/>, and the flattened uid set is maintained alongside so the
        /// per-broadcast membership test is O(1) instead of O(total markers).</summary>
        private readonly Dictionary<string, List<string>> _layerMarkerUids =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private readonly HashSet<string> _allMarkerUids = new HashSet<string>(StringComparer.Ordinal);
        private readonly object _markerLock = new object();

        /// <summary>Per-layer in-flight guard (C-26). Double-clicking "Sync now", or a recurrence
        /// tick landing on a layer whose previous download is still running, used to start two
        /// concurrent downloads that both wrote <c>_layerMarkerUids[url]</c>.</summary>
        private readonly HashSet<string> _downloadsInFlight = new HashSet<string>(StringComparer.Ordinal);
        private readonly object _downloadLock = new object();

        /// <summary>Re-entrancy guards for the two timers (C-26/C-27).</summary>
        private int _pliSendInFlight;
        private int _recurrenceInFlight;

        // -------------------------------------------------------------------------
        // Tab navigation
        // -------------------------------------------------------------------------

        private int _currentTabIndex;
        /// <summary>0 = Home, 1 = Layers, 2 = PLI.</summary>
        public int CurrentTabIndex
        {
            get => _currentTabIndex;
            private set { _currentTabIndex = value; RaisePropertyChanged(nameof(CurrentTabIndex)); }
        }

        public ICommand NavigateHomeCommand { get; }
        public ICommand NavigateLayersCommand { get; }
        public ICommand NavigatePackageCommand { get; }
        public ICommand NavigatePliCommand { get; }

        private void NavigatePage(int page)
        {
            if (page < 0 || page > 3) return;   // HOME, LAYERS, PACKAGES, PLI
            CurrentTabIndex = page;

            // Read the folder when the tab is opened rather than on a timer: packages can be
            // deleted from WinTAK's own list or from Explorer, and a stale listing offering a Send
            // button for a file that is gone is worse than a moment's work here.
            if (page == 2) Guard(() => PackageLibrary.Refresh(), "list built packages");
        }

        // -------------------------------------------------------------------------
        // Overlay pages (Account, Add Layer)
        // -------------------------------------------------------------------------

        private bool _isOverlayVisible;
        public bool IsOverlayVisible
        {
            get => _isOverlayVisible;
            private set { _isOverlayVisible = value; RaisePropertyChanged(nameof(IsOverlayVisible)); }
        }

        private int _overlayPageIndex; // 0 = Account, 1 = Add Layer
        public int OverlayPageIndex
        {
            get => _overlayPageIndex;
            private set { _overlayPageIndex = value; RaisePropertyChanged(nameof(OverlayPageIndex)); }
        }

        public ICommand ShowAccountPageCommand { get; }
        public ICommand ShowAddLayerPageCommand { get; }
        public ICommand HideOverlayCommand { get; }
        public ICommand SetPliEndpointCommand { get; }

        private void ShowAccountPage() { OverlayPageIndex = 0; IsOverlayVisible = true; }
        private void ShowAddLayerPage() { OverlayPageIndex = 1; IsOverlayVisible = true; }
        private void HideOverlay() { IsOverlayVisible = false; OverlayPageIndex = 0; }

        // -------------------------------------------------------------------------
        // Home tab
        // -------------------------------------------------------------------------

        public bool IsAuthenticated => _authService.IsAuthenticated;
        public string AuthStatusText => IsAuthenticated
            ? $"Signed in as: {_authService.Username}"
            : "Not signed in";

        public string StatusAccountText => IsAuthenticated ? "Signed In" : "Not Signed In";

        /// <summary>A PLI destination is "connected" once a send has actually succeeded recently —
        /// not merely because a URL was typed. The old definition
        /// (<c>IsPliConfigured &amp;&amp; IsAuthenticated</c>) showed a green "Connected" badge
        /// indefinitely for a dead, deleted or permission-denied layer.</summary>
        public bool IsPliConnected =>
            IsPliConfigured && IsAuthenticated && _consecutivePliFailures == 0 && _lastPliSuccessUtc != DateTime.MinValue;
        public string StatusPliText
        {
            get
            {
                if (!IsPliConfigured) return "Not Configured";
                if (!IsAuthenticated) return "Signed Out";
                if (_lastPliSuccessUtc == DateTime.MinValue) return "Not Yet Sent";
                if (_consecutivePliFailures > 0) return $"Failing ({_consecutivePliFailures})";
                return "Connected";
            }
        }
        public string StatusAutoSendText => PliAutoSendEnabled ? "On" : "Off";
        public string StatusLayersText => $"{SharedPrivateLayers.Count} private, {PublicLayers.Count} public";

        public bool ShowSetPliEndpointButton => !IsPliConfigured;

        private bool _statsExpanded = true;
        public bool StatsExpanded
        {
            get => _statsExpanded;
            private set { _statsExpanded = value; RaisePropertyChanged(nameof(StatsExpanded)); }
        }
        public ICommand ToggleStatsCommand { get; }
        private void ToggleStats() => StatsExpanded = !StatsExpanded;

        private string _statusText = "Ready.";
        /// <summary>The plugin's only user-facing error/progress channel. <b>C-16</b>: this was
        /// assigned in 20+ places and bound by <b>nothing</b> in FeatureLinkView.xaml — the only
        /// "StatusText" in the XAML was a Style resource key of the same name, so every network,
        /// auth, import, share and download failure was silently swallowed. It is now bound to a
        /// persistent status strip in the root grid (visible on all three tabs), and
        /// <c>FeatureLink.Tests</c>' binding smoke test asserts every public string property on
        /// this type is referenced by at least one binding so the class of defect cannot recur.</summary>
        public string StatusText
        {
            get => _statusText;
            private set { _statusText = value; RaisePropertyChanged(nameof(StatusText)); }
        }

        private long _totalFeatureCount;
        public long TotalFeatureCount
        {
            get => _totalFeatureCount;
            private set { _totalFeatureCount = value; RaisePropertyChanged(nameof(TotalFeatureCount)); }
        }

        public ObservableCollection<string> LayerStats { get; } = new ObservableCollection<string>();

        public ObservableCollection<RecentCotItem> RecentCotItems { get; } = new ObservableCollection<RecentCotItem>();
        public ICommand SendItemToLayerCommand { get; }
        private const int RecentCotItemsCap = 30;

        // -------------------------------------------------------------------------
        // Layers tab
        // -------------------------------------------------------------------------

        public ObservableCollection<ArcGisLayer> PrivateLayers { get; } = new ObservableCollection<ArcGisLayer>();
        public ObservableCollection<ArcGisLayer> PublicLayers { get; } = new ObservableCollection<ArcGisLayer>();
        public ObservableCollection<ArcGisLayer> SharedPrivateLayers { get; } = new ObservableCollection<ArcGisLayer>();

        private bool _privateLayersExpanded = true;
        public bool PrivateLayersExpanded
        {
            get => _privateLayersExpanded;
            private set { _privateLayersExpanded = value; RaisePropertyChanged(nameof(PrivateLayersExpanded)); }
        }
        public ICommand TogglePrivateLayersCommand { get; }
        private void TogglePrivateLayers() => PrivateLayersExpanded = !PrivateLayersExpanded;

        private bool _sharedPrivateLayersExpanded = true;
        public bool SharedPrivateLayersExpanded
        {
            get => _sharedPrivateLayersExpanded;
            private set { _sharedPrivateLayersExpanded = value; RaisePropertyChanged(nameof(SharedPrivateLayersExpanded)); }
        }
        public ICommand ToggleSharedPrivateLayersCommand { get; }
        private void ToggleSharedPrivateLayers() => SharedPrivateLayersExpanded = !SharedPrivateLayersExpanded;

        public bool ShowSharedPrivateLayersCard => SharedPrivateLayers.Count > 0;

        private bool _publicLayersExpanded = true;
        public bool PublicLayersExpanded
        {
            get => _publicLayersExpanded;
            private set { _publicLayersExpanded = value; RaisePropertyChanged(nameof(PublicLayersExpanded)); }
        }
        public ICommand TogglePublicLayersCommand { get; }
        private void TogglePublicLayers() => PublicLayersExpanded = !PublicLayersExpanded;

        public ICommand ClearAllLayersCommand { get; }

        private string _newPublicLayerUrl = string.Empty;
        public string NewPublicLayerUrl
        {
            get => _newPublicLayerUrl;
            set { _newPublicLayerUrl = value; RaisePropertyChanged(nameof(NewPublicLayerUrl)); }
        }

        private readonly HashSet<string> _excludedPrivateLayerUrls = new HashSet<string>(StringComparer.Ordinal);

        // -------------------------------------------------------------------------
        // PLI tab
        // -------------------------------------------------------------------------

        private string _pliLayerUrl;
        public string PliLayerUrl
        {
            get => _pliLayerUrl;
            private set
            {
                _pliLayerUrl = value;
                RaisePropertyChanged(nameof(PliLayerUrl));
                RaisePropertyChanged(nameof(IsPliConfigured));
                RaisePropertyChanged(nameof(IsPliConnected));
                RaisePropertyChanged(nameof(StatusPliText));
                RaisePropertyChanged(nameof(ShowSetPliEndpointButton));
            }
        }

        public bool IsPliConfigured => !string.IsNullOrEmpty(PliLayerUrl);

        private long _pliObjectId = -1;
        private readonly object _pliObjectIdLock = new object();
        private int _consecutivePliFailures;
        private DateTime _lastPliSuccessUtc = DateTime.MinValue;

        private bool _pliAutoSendEnabled;
        public bool PliAutoSendEnabled
        {
            get => _pliAutoSendEnabled;
            set
            {
                if (_pliAutoSendEnabled == value) return;
                _pliAutoSendEnabled = value;
                RaisePropertyChanged(nameof(PliAutoSendEnabled));
                RaisePropertyChanged(nameof(StatusAutoSendText));
                if (value) StartPliTimer(); else StopPliTimer();
                SaveSettings();
            }
        }

        private bool _pliLayerExpanded = true;
        public bool PliLayerExpanded
        {
            get => _pliLayerExpanded;
            private set { _pliLayerExpanded = value; RaisePropertyChanged(nameof(PliLayerExpanded)); }
        }
        public ICommand TogglePliLayerCommand { get; }
        private void TogglePliLayer() => PliLayerExpanded = !PliLayerExpanded;
        private bool _pliAutoCollapseDone;

        private void MaybeAutoCollapsePliLayerSection()
        {
            if (_pliAutoCollapseDone || !IsPliConfigured) return;
            _pliAutoCollapseDone = true;
            PliLayerExpanded = false;
        }

        private bool _isPliJoinMode;
        public bool IsPliJoinMode
        {
            get => _isPliJoinMode;
            set
            {
                if (_isPliJoinMode == value) return;
                _isPliJoinMode = value;
                RaisePropertyChanged(nameof(IsPliJoinMode));
                RaisePropertyChanged(nameof(IsPliCreateMode));
                RaisePropertyChanged(nameof(PliActionButtonText));
            }
        }
        public bool IsPliCreateMode
        {
            get => !IsPliJoinMode;
            set => IsPliJoinMode = !value;
        }
        public string PliActionButtonText => IsPliJoinMode ? "Join Layer" : "Create Layer";

        private string _newPliLayerName = string.Empty;
        public string NewPliLayerName
        {
            get => _newPliLayerName;
            set { _newPliLayerName = value; RaisePropertyChanged(nameof(NewPliLayerName)); }
        }

        private string _newPliJoinUrl = string.Empty;
        public string NewPliJoinUrl
        {
            get => _newPliJoinUrl;
            set { _newPliJoinUrl = value; RaisePropertyChanged(nameof(NewPliJoinUrl)); }
        }

        private string _pliStatusText = string.Empty;
        public string PliStatusText
        {
            get => _pliStatusText;
            private set { _pliStatusText = value; RaisePropertyChanged(nameof(PliStatusText)); }
        }

        /// <summary>Label for the auto-send row, derived from <see cref="PliSendInterval"/> rather
        /// than a hardcoded "30 seconds" literal in the XAML that silently lied whenever the
        /// constant changed.</summary>
        public string PliAutoSendLabel => string.Format(CultureInfo.CurrentCulture,
            "Auto-send PLI every {0:0} seconds", PliSendInterval.TotalSeconds);

        // ── Commands ─────────────────────────────────────────────────────────────

        /// <summary>Frames a layer's plotted features on the map.</summary>
        public ICommand ZoomToLayerCommand { get; }

        /// <summary>Centres the map on one feature from a layer's list.</summary>
        public ICommand ZoomToFeatureCommand { get; }

        /// <summary>Expands or collapses a layer row's feature list.</summary>
        public ICommand ToggleFeaturesCommand { get; }

        public ICommand SignInCommand { get; }
        public ICommand SignOutCommand { get; }
        public ICommand RefreshLayersCommand { get; }
        public ICommand AddPublicLayerCommand { get; }
        public ICommand UploadDisplayPrefsCommand { get; }
        public ICommand DownloadLayerCommand { get; }
        public ICommand RemoveLayerCommand { get; }
        public ICommand ShareLayerCommand { get; }

        /// <summary>Opens the data-package dialog: pick layers, pick contacts, send.</summary>
        public ICommand CreateDataPackageCommand { get; }

        /// <summary>Opens the PACKAGE tab and begins the workflow. A non-null layer pre-selects
        /// everything in it, which is what the per-layer button does.</summary>
        public ICommand StartPackageWorkflowCommand { get; }
        public ICommand ToggleLayerVisibilityCommand { get; }
        public ICommand PliActionCommand { get; }

        private readonly DelegateCommand _signInCommand;
        private readonly DelegateCommand _signOutCommand;

        // ── Constructor ──────────────────────────────────────────────────────────

        [ImportingConstructor]
        public FeatureLinkDockPane(ICotMessageSender cotSender, ILocationService locationService,
            ICommunicationService communicationService, IMapItemFinderService mapItemFinder,
            WinTak.Net.Contacts.IContactService contactService)
        {
            _cotSender = cotSender;
            _locationService = locationService;
            _communicationService = communicationService;
            _mapItemFinder = mapItemFinder;
            _contactService = contactService;
            _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

            // Resolves MapViewController lazily: MEF sets that import after this constructor.
            _mapSelector = new MapAreaSelector(() => MapViewController);
            Package = CreatePackageWorkflow();
            PackageLibrary = CreatePackageLibrary();

            NavigateHomeCommand = new DelegateCommand(() => NavigatePage(0));
            NavigateLayersCommand = new DelegateCommand(() => NavigatePage(1));
            NavigatePackageCommand = new DelegateCommand(() => NavigatePage(2));
            NavigatePliCommand = new DelegateCommand(() => NavigatePage(3));

            ShowAccountPageCommand = new DelegateCommand(ShowAccountPage);
            ShowAddLayerPageCommand = new DelegateCommand(ShowAddLayerPage);
            HideOverlayCommand = new DelegateCommand(HideOverlay);
            // Also closes an open overlay: the button lives on the Home tab, and switching tabs
            // behind a visible overlay looked like the button did nothing at all.
            SetPliEndpointCommand = new DelegateCommand(() => { HideOverlay(); NavigatePage(2); });

            ToggleStatsCommand = new DelegateCommand(ToggleStats);
            TogglePrivateLayersCommand = new DelegateCommand(TogglePrivateLayers);
            ToggleSharedPrivateLayersCommand = new DelegateCommand(ToggleSharedPrivateLayers);
            TogglePublicLayersCommand = new DelegateCommand(TogglePublicLayers);
            ClearAllLayersCommand = new DelegateCommand(() => Guard(OnClearAllLayers, "clear all layers"));
            TogglePliLayerCommand = new DelegateCommand(TogglePliLayer);

            // C-27: Prism's DelegateCommand takes an Action, so `async () => await X()` is an
            // `async void` in disguise — an exception escaping the awaited method is thrown on the
            // dispatcher with no handler, i.e. a WinTAK crash from a button click. Every async
            // handler is now launched through RunGuarded, which observes the task and reports the
            // failure to the (now visible) status channel.
            _signInCommand = new DelegateCommand(
                () => RunGuarded(OnSignInAsync(), "sign-in"), () => !IsAuthenticated);
            _signOutCommand = new DelegateCommand(
                () => Guard(OnSignOut, "sign-out"), () => IsAuthenticated);
            ZoomToLayerCommand = new DelegateCommand<ArcGisLayer>(
                layer => Guard(() => OnZoomToLayer(layer), "zoom to layer"));
            ZoomToFeatureCommand = new DelegateCommand<LayerFeature>(
                feature => Guard(() => OnZoomToFeature(feature), "zoom to feature"));
            ToggleFeaturesCommand = new DelegateCommand<ArcGisLayer>(
                layer => Guard(() => { if (layer != null) layer.FeaturesExpanded = !layer.FeaturesExpanded; },
                    "toggle feature list"));

            SignInCommand = _signInCommand;
            SignOutCommand = _signOutCommand;

            RefreshLayersCommand = new DelegateCommand(
                () => RunGuarded(OnRefreshLayersAsync(), "layer refresh"));
            AddPublicLayerCommand = new DelegateCommand(
                () => RunGuarded(OnAddPublicLayerAsync(), "add layer"));
            UploadDisplayPrefsCommand = new DelegateCommand(
                () => Guard(OnUploadDisplayPrefs, "upload display prefs"));
            SendItemToLayerCommand = new DelegateCommand<RecentCotItem>(
                item => RunGuarded(OnSendItemToLayerAsync(item), "send item to layer"));
            DownloadLayerCommand = new DelegateCommand<ArcGisLayer>(
                layer => RunGuarded(OnDownloadLayerAsync(layer), "layer download"));
            RemoveLayerCommand = new DelegateCommand<ArcGisLayer>(
                layer => Guard(() => OnRemoveLayer(layer), "remove layer"));
            ShareLayerCommand = new DelegateCommand<ArcGisLayer>(
                layer => Guard(() => OnShareLayer(layer), "share layer"));
            CreateDataPackageCommand = new DelegateCommand(
                () => Guard(OnCreateDataPackage, "create data package"));
            ToggleLayerVisibilityCommand = new DelegateCommand<ArcGisLayer>(
                layer => Guard(() => OnToggleLayerVisibility(layer), "toggle layer visibility"));
            PliActionCommand = new DelegateCommand(() =>
            {
                if (IsPliJoinMode) Guard(OnJoinPliLayer, "join PLI layer");
                else RunGuarded(OnCreatePliLayerAsync(), "create PLI layer");
            });

            StartPackageWorkflowCommand = new DelegateCommand<ArcGisLayer>(
                layer => Guard(() => { NavigatePage(2); Package.Start(layer); }, "start data package"));

            LoadSettings();
            StartRecurrenceTimer();
            if (_pliAutoSendEnabled) StartPliTimer();

            // Subscribe last: a CoT message arriving mid-construction previously reached
            // OnPreviewCotBroadcast before the commands and settings existed.
            StartShareFileWatcher();
            _communicationService.PreviewCotBroadcast += OnPreviewCotBroadcast;

            if (IsAuthenticated)
                RunGuarded(OnRefreshLayersAsync(), "startup layer refresh");

            Log.Info("FeatureLink dock pane constructed.");
        }

        // -------------------------------------------------------------------------
        // Failure plumbing (C-27)
        // -------------------------------------------------------------------------

        /// <summary>Observes a fire-and-forget task so a fault reaches the status channel and the
        /// log instead of <c>TaskScheduler.UnobservedTaskException</c>.</summary>
        private void RunGuarded(Task task, string operation)
        {
            if (task == null) return;
            task.ContinueWith(t => OnFaulted(t, operation),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }

        private void OnFaulted(Task task, string operation)
        {
            var ex = task.Exception?.GetBaseException();
            if (ex is OperationCanceledException) return;
            Log.Error($"Unhandled failure during {operation}.", ex);
            SetStatus(ErrorText.WithContext(Capitalize(operation) + " failed", ex, IsAuthenticated));
        }

        /// <summary>Synchronous counterpart of <see cref="RunGuarded"/> for command handlers that
        /// are not async but can still throw (MessageBox, file I/O, SDK calls).</summary>
        private void Guard(Action action, string operation)
        {
            try { action(); }
            catch (Exception ex)
            {
                Log.Error($"Unhandled failure during {operation}.", ex);
                SetStatus(ErrorText.WithContext(Capitalize(operation) + " failed", ex, IsAuthenticated));
            }
        }

        private static string Capitalize(string s) =>
            string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        /// <summary>Turns an exception into something an operator can act on.
        ///
        /// <para>This used to hand the raw ArcGIS wording straight to the status bar, plus a bare
        /// "(sign in again)" for every auth failure — advice that is actively wrong when the
        /// operator is already signed in and the layer simply is not shared with them. All of the
        /// mapping now lives in <see cref="ErrorText"/>, which is pure logic and unit-tested;
        /// this method only supplies the one piece of context ErrorText cannot know, namely
        /// whether there is a live ArcGIS session.</para></summary>
        private string Describe(Exception ex) => ErrorText.ForOperator(ex, IsAuthenticated);

        /// <summary>Every status assignment goes through here so it is always marshalled to the
        /// dispatcher and always logged. Assignments used to be made directly from background
        /// continuations in several places (`:475`, `:546`, `:1116`, `:1308`) while their
        /// neighbours were wrapped in RunOnUi — inconsistent and unsafe by construction.</summary>
        private void SetStatus(string text)
        {
            Log.Info("Status: " + text);
            RunOnUi(() => StatusText = text);
        }

        private void SetPliStatus(string text)
        {
            Log.Info("PLI status: " + text);
            RunOnUi(() => PliStatusText = text);
        }

        private CancellationToken LinkedToken(TimeSpan timeout, out CancellationTokenSource cts)
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            cts.CancelAfter(timeout);
            return cts.Token;
        }

        // -------------------------------------------------------------------------
        // Sign-in / sign-out
        // -------------------------------------------------------------------------

        private async Task OnSignInAsync()
        {
            try
            {
                SetStatus("Signing in…");
                await _authService.SignInAsync(ArcGisFeatureService.DefaultPortalUrl, _lifetime.Token)
                    .ConfigureAwait(false);
                RunOnUi(() =>
                {
                    RaiseAuthDependentPropertiesChanged();
                    StatusText = $"Signed in as {_authService.Username}";
                    SaveSettings();
                });
                await OnRefreshLayersAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("Sign-in failed.", ex);
                SetStatus($"Sign-in failed: {Describe(ex)}");
                RunOnUi(RaiseAuthDependentPropertiesChanged);
            }
        }

        private void OnSignOut()
        {
            _authService.Logout();
            PrivateLayers.Clear();
            RaiseAuthDependentPropertiesChanged();
            StatusText = "Signed out.";
        }

        private void RaiseAuthDependentPropertiesChanged()
        {
            RaisePropertyChanged(nameof(IsAuthenticated));
            RaisePropertyChanged(nameof(AuthStatusText));
            RaisePropertyChanged(nameof(StatusAccountText));
            RaisePropertyChanged(nameof(IsPliConnected));
            RaisePropertyChanged(nameof(StatusPliText));
            RaisePropertyChanged(nameof(StatusLayersText));
            // Without this the Sign In button stayed enabled after signing in (and Sign Out stayed
            // disabled) until an unrelated CommandManager requery, so a user could start a second
            // HttpListener and a second browser tab by double-clicking.
            _signInCommand?.RaiseCanExecuteChanged();
            _signOutCommand?.RaiseCanExecuteChanged();
        }

        // -------------------------------------------------------------------------
        // Layers
        // -------------------------------------------------------------------------

        private async Task OnRefreshLayersAsync()
        {
            if (!IsAuthenticated) return;
            CancellationTokenSource cts = null;
            try
            {
                var ct = LinkedToken(OperationTimeout, out cts);

                string token = await _authService.GetTokenAsync(ct).ConfigureAwait(false);
                if (token == null)
                {
                    SetStatus("Session expired — please sign in again.");
                    return;
                }

                SetStatus("Loading layers…");
                var search = await _restClient
                    .SearchUserLayersAsync(_authService.PortalUrl, token, _authService.Username, ct)
                    .ConfigureAwait(false);

                RunOnUi(() =>
                {
                    // C-27: ToDictionary throws ArgumentException on a duplicate Url — two ArcGIS
                    // items sharing a service URL (a Feature Service published twice, or a hosted
                    // view) crashed the refresh from inside a Dispatcher.Invoke on a fire-and-forget
                    // task. GroupBy keeps the first and never throws.
                    var preserved = PrivateLayers
                        .Where(l => l.Url != null)
                        .GroupBy(l => l.Url, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

                    var onDevice = new HashSet<string>(
                        SharedPrivateLayers.Select(l => l.Url).Concat(PublicLayers.Select(l => l.Url)),
                        StringComparer.Ordinal);

                    PrivateLayers.Clear();
                    foreach (var layer in search.Layers)
                    {
                        if (_excludedPrivateLayerUrls.Contains(layer.Url) || onDevice.Contains(layer.Url)) continue;
                        layer.Type = "private";
                        ArcGisLayer old;
                        if (preserved.TryGetValue(layer.Url, out old))
                            MergePreservedFields(old, layer);
                        PrivateLayers.Add(layer);
                    }
                    StatusText = search.Truncated
                        ? $"Loaded {PrivateLayers.Count} private layer(s) — truncated, more exist in your account."
                        : $"Loaded {PrivateLayers.Count} private layer(s).";
                    RaisePropertyChanged(nameof(StatusLayersText));
                    SaveSettings();
                });

                await RefreshFeatureCountsAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* pane disposed or timed out — nothing to report */ }
            catch (Exception ex)
            {
                Log.Error("Layer refresh failed.", ex);
                SetStatus("Could not refresh layers: " + Describe(ex));
            }
            finally { cts?.Dispose(); }
        }

        /// <summary>Carries every locally-owned field across a browse-list rebuild. The old code
        /// preserved 5 of 13 fields, silently dropping display-config styling, feature counts and
        /// the PLI flag on every refresh.</summary>
        private static void MergePreservedFields(ArcGisLayer from, ArcGisLayer to)
        {
            to.DownloadEnabled = from.DownloadEnabled;
            to.RecurrenceUnit = from.RecurrenceUnit;
            to.RecurrenceInterval = from.RecurrenceInterval;
            to.LastSyncTicks = from.LastSyncTicks;
            to.Visible = from.Visible;
            to.HasDisplayConfig = from.HasDisplayConfig;
            to.LargeDownloadAccepted = from.LargeDownloadAccepted;
            to.SymJson = from.SymJson;
            to.LblJson = from.LblJson;
            to.PopupJson = from.PopupJson;
            to.ShpJson = from.ShpJson;
            to.AutoSymJson = from.AutoSymJson;
            to.AutoShpJson = from.AutoShpJson;
            to.FeatureCount = from.FeatureCount;
            to.IsPliLayer = from.IsPliLayer;
        }

        private async Task RefreshFeatureCountsAsync(CancellationToken cancellationToken)
        {
            string token = await _authService.GetTokenAsync(cancellationToken).ConfigureAwait(false);
            long total = 0;
            var stats = new List<string>();
            var counts = new List<Tuple<ArcGisLayer, long>>();

            var layers = await RunOnUiAsync(() => SharedPrivateLayers.Concat(PublicLayers).ToList())
                .ConfigureAwait(false);

            foreach (var layer in layers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (layer.IsPrivate && token == null)
                {
                    stats.Add($"{layer.Name}  [{layer.Type}]  —  signed out");
                    continue;
                }
                try
                {
                    long count = await _restClient
                        .QueryFeatureCountAsync(layer.Url, layer.IsPrivate ? token : null, cancellationToken)
                        .ConfigureAwait(false);
                    counts.Add(Tuple.Create(layer, count));
                    total += count;
                    stats.Add(string.Format(CultureInfo.InvariantCulture, "{0}  [{1}]  —  {2} features",
                        layer.Name, layer.Type, count));
                }
                catch (OperationCanceledException) { throw; }
                catch (ArcGisServiceException ex)
                {
                    // A 401, a 404 and a DNS failure used to be indistinguishable "— error" rows.
                    Log.Warn($"Feature count failed for {layer.Name}: {ex.Message}");
                    stats.Add($"{layer.Name}  [{layer.Type}]  —  {ex.Message}");
                }
                catch (Exception ex)
                {
                    Log.Warn($"Feature count failed for {layer.Name}: {ex.Message}");
                    stats.Add($"{layer.Name}  [{layer.Type}]  —  {Describe(ex)}");
                }
            }

            RunOnUi(() =>
            {
                // Model mutation is marshalled: layer.FeatureCount raises PropertyChanged into a
                // live WPF binding and was previously written from a thread-pool continuation.
                foreach (var pair in counts) pair.Item1.FeatureCount = pair.Item2;
                TotalFeatureCount = total;
                LayerStats.Clear();
                foreach (var s in stats) LayerStats.Add(s);
            });
        }

        private async Task OnAddPublicLayerAsync()
        {
            string url = (NewPublicLayerUrl ?? string.Empty).Trim();
            var check = UrlGuard.ValidateServiceUrl(url);
            if (!check.Ok)
            {
                SetStatus("Cannot add layer: " + check.Reason);
                return;
            }

            CancellationTokenSource cts = null;
            try
            {
                var ct = LinkedToken(OperationTimeout, out cts);

                // Try anonymously FIRST, because that is what actually determines whether the
                // layer is public — not what the operator believes about it.
                //
                // This used to pass a null token unconditionally, so "Add Layer" was permanently
                // anonymous even while the operator was signed in. Pasting the URL of a layer in
                // your OWN ArcGIS organisation therefore failed with ArcGIS 499 "Token Required",
                // reported as the bare "Could not load layer from URL: Token Required" — which
                // reads as a broken URL, not as "you need to be signed in for this one". The
                // operator's only recourse was the browse list, and nothing said so.
                ArcGisLayer layer;
                bool neededAuth = false;
                try
                {
                    layer = await _restClient.FetchLayerInfoAsync(url, null, ct).ConfigureAwait(false);
                }
                catch (ArcGisServiceException ex) when (ex.IsAuthFailure)
                {
                    if (!IsAuthenticated)
                    {
                        // Not signed in and the service wants a token: say precisely that, rather
                        // than letting the raw ArcGIS wording stand.
                        SetStatus("That layer is not public — sign in to ArcGIS first, then add it.");
                        return;
                    }

                    string token = await _authService.GetTokenAsync(ct).ConfigureAwait(false);
                    if (token == null)
                    {
                        SetStatus("That layer requires sign-in, and the ArcGIS session has expired — sign in again.");
                        return;
                    }

                    Log.Info("Add Layer: anonymous fetch was refused; retrying with the signed-in token.");
                    layer = await _restClient.FetchLayerInfoAsync(url, token, ct).ConfigureAwait(false);
                    neededAuth = true;
                }

                // Classify by what the service actually required. A layer that only loads with a
                // token is not a public layer, and filing it as one would make every later sync
                // fetch it anonymously and fail exactly the same way. (IsPrivate is computed from
                // Type, and it is what DownloadLayerAsync consults to decide whether to attach a
                // token — so this assignment is what makes recurring syncs keep working.)
                layer.Type = neededAuth ? "private" : "public";

                await DownloadLayerAsync(layer).ConfigureAwait(false);

                // Persist only after the first download succeeded, so a broken URL does not leave
                // a permanently marker-less layer in the operator's list.
                RunOnUi(() =>
                {
                    // A layer that needed a token belongs with the other on-device non-public
                    // layers, not in the Public list where it would be fetched anonymously.
                    if (neededAuth) SharedPrivateLayers.Add(layer);
                    else PublicLayers.Add(layer);
                    NewPublicLayerUrl = string.Empty;
                    RaisePropertyChanged(nameof(StatusLayersText));
                    HideOverlay();
                    NavigatePage(1);
                    SaveSettings();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Error("Add layer failed for " + Log.Redact(url), ex);
                SetStatus(ErrorText.WithContext("Could not add that layer", ex, IsAuthenticated));
            }
            finally { cts?.Dispose(); }
        }

        private void OnUploadDisplayPrefs()
        {
            using (var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Filter = "FeatureLink share (*.featurelinkshare;*.json)|*.featurelinkshare;*.json|All files (*.*)|*.*",
                Title = "Upload Display Prefs",
            })
            {
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                // A file the operator picked themselves is trusted enough to skip the consent
                // prompt a Mission Package gets, but it is still bounded and validated.
                RunGuarded(ImportFeatureLinkShareAsync(dialog.FileName, requireConsent: false),
                    "display prefs import");
            }
        }

        private void StartShareFileWatcher()
        {
            try
            {
                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "WinTAK", "attachments");
                Directory.CreateDirectory(root);
                Log.Info("Watching for shared layers under: " + root);

                _shareFileWatcher = new FileSystemWatcher(root, "*.featurelinkshare")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                    // The default 8 KB buffer silently drops events under load; shares arriving in
                    // a burst were lost with no record at all.
                    InternalBufferSize = 64 * 1024,
                };
                _shareFileWatcher.Created += OnShareFileCreated;
                _shareFileWatcher.Error += OnShareWatcherError;
                _shareFileWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Log.Error("Could not start the share-file watcher.", ex);
                SetStatus($"Could not watch for shared layers: {ex.Message}");
            }
        }

        private void OnShareWatcherError(object sender, ErrorEventArgs e)
        {
            Log.Error("Share-file watcher error — incoming shares may have been missed.",
                e.GetException());
            SetStatus("Shared-layer watcher error — incoming shares may have been missed.");
        }

        private void OnShareFileCreated(object sender, FileSystemEventArgs e)
        {
            RunGuarded(ImportFeatureLinkShareAsync(e.FullPath, requireConsent: true), "shared layer import");
        }

        private static async Task<string> ReadFileWithRetryAsync(string path, CancellationToken cancellationToken)
        {
            // Exponential backoff to ~30 s: WinTAK's own extraction/import attempt can hold the
            // file well past the old fixed 1.5 s window, after which the share was lost forever.
            var delay = TimeSpan.FromMilliseconds(250);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return SafeJson.ReadTextCapped(path);
                }
                catch (IOException) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 4000));
                }
            }
        }

        /// <summary>
        /// Applies a received layer-share file — the WinTAK counterpart of ATAK's
        /// importFeatureLinkShareUri()/applyScannedDisplayConfig() url branch.
        ///
        /// C-02: a share arriving from any network peer used to be auto-added and auto-downloaded
        /// with <b>no consent prompt at all</b>, so anyone who could send a Mission Package could
        /// inject arbitrary markers into another operator's COP without interaction. An explicit
        /// accept dialog showing the source file and the target URL is now mandatory.
        /// C-19: the payload is parsed with a depth and size limit.
        /// S3: the peer-supplied URL is validated before any request is made with it.
        /// </summary>
        private async Task ImportFeatureLinkShareAsync(string path, bool requireConsent)
        {
            CancellationTokenSource cts = null;
            try
            {
                var ct = LinkedToken(OperationTimeout, out cts);

                string json = await ReadFileWithRetryAsync(path, ct).ConfigureAwait(false);
                if (json == null)
                {
                    SetStatus("Ignored an oversized FeatureLink share file.");
                    return;
                }

                JObject o = SafeJson.ParseObject(json);

                int version = (int?)ToInt(o["v"]) ?? ShareSchemaVersion;
                if (version > ShareSchemaVersion)
                {
                    SetStatus($"This FeatureLink share uses schema v{version}, which this build cannot read.");
                    return;
                }

                string url = (string)o["url"];
                if (string.IsNullOrEmpty(url))
                {
                    SetStatus("Received a FeatureLink file with no layer URL.");
                    return;
                }

                var check = UrlGuard.ValidateServiceUrl(url);
                if (!check.Ok)
                {
                    Log.Warn($"Rejected a shared layer URL: {check.Reason} ({Log.Redact(url)})");
                    SetStatus("Rejected a shared layer: " + check.Reason);
                    return;
                }

                bool isPrivate = ToBool(o["private"]) ?? false;
                string name = UrlGuard.SanitizeDisplayName((string)o["layer"]?["name"]);
                int freqInterval = ToInt(o["freq"]?["iv"]) ?? 0;
                string freqUnit = (string)o["freq"]?["u"] ?? ArcGisLayer.DefaultRecurrenceUnit;

                var symObj = o["sym"] as JObject;
                var lblObj = o["lbl"] as JObject;
                var popupObj = o["popup"] as JObject;
                var shpObj = o["shp"] as JObject;
                bool hasDisplayConfig = symObj != null || lblObj != null
                    || popupObj != null || shpObj != null || o["cm"] != null;

                bool duplicate = await RunOnUiAsync(() =>
                    SharedPrivateLayers.Any(l => string.Equals(l.Url, url, StringComparison.Ordinal))
                    || PublicLayers.Any(l => string.Equals(l.Url, url, StringComparison.Ordinal)))
                    .ConfigureAwait(false);
                if (duplicate)
                {
                    SetStatus($"Layer already in your list: {name ?? url}");
                    return;
                }

                if (requireConsent)
                {
                    bool accepted = await RunOnUiAsync(() => DialogService.ConfirmShareImport(
                        Path.GetFileName(path), name, url, isPrivate, hasDisplayConfig)).ConfigureAwait(false);
                    if (!accepted)
                    {
                        Log.Info("Operator declined a shared layer: " + Log.Redact(url));
                        SetStatus("Declined the shared layer.");
                        return;
                    }
                }

                SetStatus($"Receiving shared layer: {name ?? url}…");

                var layer = await _restClient.FetchLayerInfoAsync(url, null, ct).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(name)) layer.Name = name;
                layer.Type = isPrivate ? "private" : "public";
                layer.RecurrenceUnit = freqUnit;
                layer.RecurrenceInterval = freqInterval; // clamped by the model (min 30 s)
                layer.HasDisplayConfig = hasDisplayConfig;
                layer.SymJson = symObj?.ToString(Newtonsoft.Json.Formatting.None);
                layer.LblJson = lblObj?.ToString(Newtonsoft.Json.Formatting.None);
                layer.PopupJson = popupObj?.ToString(Newtonsoft.Json.Formatting.None);
                layer.ShpJson = shpObj?.ToString(Newtonsoft.Json.Formatting.None);

                RunOnUi(() =>
                {
                    if (isPrivate) SharedPrivateLayers.Add(layer); else PublicLayers.Add(layer);
                    StatusText = $"Received shared layer: {layer.Name}";
                    RaisePropertyChanged(nameof(StatusLayersText));
                    RaisePropertyChanged(nameof(ShowSharedPrivateLayersCard));
                    NavigatePage(1);
                    SaveSettings();
                });

                await DownloadLayerAsync(layer).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Error("Failed to import a shared layer from " + path, ex);
                SetStatus($"Failed to import shared layer: {Describe(ex)}");
            }
            finally { cts?.Dispose(); }
        }

        /// <summary>Type-tolerant reads of attacker-controlled JSON: <c>"private":"yes"</c> or
        /// <c>"iv":99999999999999</c> used to throw <c>FormatException</c>/<c>OverflowException</c>
        /// out of an implicit <c>JToken</c> conversion.</summary>
        private static bool? ToBool(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.Boolean) return (bool)t;
            bool parsed;
            return bool.TryParse(t.ToString(), out parsed) ? parsed : (bool?)null;
        }

        private static int? ToInt(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float)
            {
                double d = (double)t;
                if (double.IsNaN(d) || d < int.MinValue || d > int.MaxValue) return null;
                return (int)d;
            }
            int parsed;
            return int.TryParse(t.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                ? parsed : (int?)null;
        }

        // -------------------------------------------------------------------------
        // Share (Mission Package send)
        // -------------------------------------------------------------------------

        private void OnShareLayer(ArcGisLayer layer)
        {
            if (layer == null) return;
            if (_contactService == null)
            {
                SetStatus("Sharing is unavailable — WinTAK's contact service did not load.");
                return;
            }

            var contacts = new List<ContactPickerWindow.ContactRow>();
            foreach (var row in ContactRows())
                contacts.Add(new ContactPickerWindow.ContactRow(row.Item2, row.Item1));

            if (contacts.Count == 0)
            {
                SetStatus(_unreachableContactCount > 0
                    ? $"No contacts can be reached right now ({_unreachableContactCount} known but "
                      + "with no route). Check the server connection."
                    : "No contacts available to share with.");
                return;
            }

            var picker = new ContactPickerWindow(contacts);
            var owner = Application.Current?.MainWindow;
            if (owner != null) picker.Owner = owner;
            if (picker.ShowDialog() != true || string.IsNullOrEmpty(picker.SelectedUid)) return;

            string filePath = null;
            try
            {
                string json = BuildShareConfigJson(layer);
                string safeName = SafeShareFileName(layer.Name, layer.Url);

                string dir = Path.Combine(Path.GetTempPath(), "FeatureLinkShare");
                Directory.CreateDirectory(dir);
                filePath = Path.Combine(dir, safeName + ".featurelinkshare");
                File.WriteAllText(filePath, json);

                _communicationService.SendMissionPackage(
                    new List<string> { picker.SelectedUid },
                    new FileInfo(filePath),
                    "FeatureLink - " + safeName,
                    false);

                SetStatus($"Sent \"{layer.Name}\" to contact.");
            }
            catch (Exception ex)
            {
                Log.Error("Layer share failed.", ex);
                SetStatus($"Failed to share layer: {Describe(ex)}");
            }
            finally
            {
                // The share file holds the layer URL and full display config; it used to be left
                // in %TEMP% forever on a shared workstation.
                TryDeleteLater(filePath);
            }
        }

        // -------------------------------------------------------------------------
        // Data package (multi-layer, multi-contact)
        // -------------------------------------------------------------------------

        /// <summary>Every layer the operator could put in a package, in the order the panel shows
        /// them. Browse-list entries are excluded: a layer that has never been downloaded has no
        /// symbology derived and no icons generated, so packaging it would ship a bare URL and a
        /// recipient who cannot reach ArcGIS would get nothing at all.</summary>
        private List<ArcGisLayer> PackageableLayers()
        {
            return SharedPrivateLayers.Concat(PublicLayers)
                .Where(l => l != null && !string.IsNullOrEmpty(l.Url) && l.LastSyncTicks > 0)
                .ToList();
        }

        /// <summary>Turns a layer into the planner's view of it.</summary>
        private DataPackageBuilder.LayerPlanInput ToPlanInput(ArcGisLayer layer)
        {
            // Both the explicit and the auto-derived configs can carry icon references, and a layer
            // can have either. They are wrapped in an array so one parse covers both.
            var configs = new JArray();
            foreach (string json in new[] { layer.SymJson, layer.AutoSymJson })
            {
                if (string.IsNullOrWhiteSpace(json)) continue;
                try { configs.Add(JToken.Parse(json)); }
                catch (Exception ex)
                {
                    Log.Warn($"Ignoring unparseable display config on \"{layer.Name}\": {ex.Message}");
                }
            }

            return new DataPackageBuilder.LayerPlanInput
            {
                Name = layer.Name,
                Url = layer.Url,
                ConfigJson = BuildShareConfigJson(layer),
                DisplayConfigJson = configs.Count > 0
                    ? configs.ToString(Newtonsoft.Json.Formatting.None) : null,
                IconsetUids = (layer.IconsetUids ?? string.Empty)
                    .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries),
            };
        }

        private DataPackageBuilder.PackagePlan PlanFor(IEnumerable<string> layerUrls)
        {
            var byUrl = new Dictionary<string, ArcGisLayer>(StringComparer.Ordinal);
            foreach (var layer in PackageableLayers()) byUrl[layer.Url] = layer;

            var inputs = new List<DataPackageBuilder.LayerPlanInput>();
            foreach (string url in layerUrls ?? Enumerable.Empty<string>())
            {
                ArcGisLayer layer;
                if (byUrl.TryGetValue(url, out layer)) inputs.Add(ToPlanInput(layer));
            }
            return DataPackageBuilder.Plan(inputs, null);
        }

        private void OnCreateDataPackage()
        {
            var layers = PackageableLayers();
            if (layers.Count == 0)
            {
                SetStatus("No downloaded layers to package — sync a layer first.");
                return;
            }

            var layerRows = layers
                .Select(l => new DataPackageWindow.SelectableRow(l.Url, l.Name, l.FeatureCountText))
                .ToList();

            var contactRows = new List<DataPackageWindow.SelectableRow>();
            foreach (var row in ContactRows())
                contactRows.Add(new DataPackageWindow.SelectableRow(row.Item1, row.Item2));

            var dialog = new DataPackageWindow(layerRows, contactRows,
                urls => DataPackageBuilder.Describe(PlanFor(urls)));
            var owner = Application.Current?.MainWindow;
            if (owner != null) dialog.Owner = owner;

            if (dialog.ShowDialog() != true) return;

            var plan = PlanFor(dialog.SelectedLayerKeys);
            if (plan.Entries.Count == 0)
            {
                SetStatus("Nothing could be packaged from that selection.");
                return;
            }

            // Same delivery path as the PACKAGE tab, so both write to WinTAK's Data Packages
            // folder, both register with the host, and neither can drift from the other in where
            // a package ends up or what the operator is told.
            var result = DeliverPackage(plan, dialog.PackageName,
                dialog.SelectedContactUids, !dialog.SaveToFileRequested);

            if (result != null && !string.IsNullOrEmpty(result.Message)) SetStatus(result.Message);
        }

        /// <summary>Deletes a staged package immediately, for the path where no send was started.</summary>
        private static void TryDelete(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { Log.Warn("Could not delete the unsent package file: " + ex.Message); }
        }

        /// <summary>This machine's callsign, for the package record's "who made it" field.
        /// Falls back to the self UID and then to a literal, because a package with a blank
        /// creator reads as corrupt in the host's list.</summary>
        private string SelfCallsign()
        {
            try
            {
                var self = _locationService?.GetSelfCotEvent();
                string callsign = self?.Detail?.GetDetailAttribute("contact", "callsign");
                if (!string.IsNullOrWhiteSpace(callsign)) return callsign;
                if (!string.IsNullOrWhiteSpace(self?.Uid)) return self.Uid;
            }
            catch (Exception ex)
            {
                Log.Warn("Could not read this machine's callsign: " + ex.Message);
            }
            return "FeatureLink";
        }

        /// <summary>Asks where to save a package. Returns null when the operator cancels.</summary>
        private static string AskWhereToSave(string packageName)
        {
            string folder = MissionPackageRegistrar.PackagesFolder;
            try { if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder); }
            catch (Exception ex) { Log.Warn("Could not prepare the packages folder: " + ex.Message); }

            var save = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save data package",
                // Opens where WinTAK keeps packages, so the default action also produces a listed
                // one; the operator can still navigate anywhere.
                InitialDirectory = folder,
                FileName = DataPackageBuilder.Sanitize(packageName, 96) + ".zip",
                DefaultExt = ".zip",
                Filter = "TAK data package (*.zip)|*.zip|All files (*.*)|*.*",
                OverwritePrompt = true,
            };
            return save.ShowDialog() == true ? save.FileName : null;
        }

        // -------------------------------------------------------------------------
        // Data package workflow (select features on the map, then send)
        // -------------------------------------------------------------------------

        /// <summary>Drives the PACKAGE tab. Constructed here rather than injected because its
        /// whole dependency set is delegates onto this pane.</summary>
        public DataPackageWorkflow Package { get; }

        /// <summary>The "Built packages" listing on the PACKAGES tab.</summary>
        public PackageLibraryViewModel PackageLibrary { get; }

        private readonly MapAreaSelector _mapSelector;

        private DataPackageWorkflow CreatePackageWorkflow()
        {
            var host = new DataPackageWorkflow.Host
            {
                PackageableLayers = () => PackageableLayers(),
                Contacts = () => ContactRows(),
                BuildFeatureCot = BuildFeatureCotXml,
                BuildShareConfig = BuildShareConfigJson,
                Deliver = DeliverPackage,
                SetStatus = SetStatus,
                LookAt = (lat, lon) => LookAt(lat, lon, DefaultFeatureResolution, "feature"),
            };
            return new DataPackageWorkflow(host, _mapSelector);
        }

        private PackageLibraryViewModel CreatePackageLibrary()
        {
            return new PackageLibraryViewModel(new PackageLibraryViewModel.Host
            {
                PackagesFolder = () => MissionPackageRegistrar.PackagesFolder,
                Send = SendExistingPackage,
                Reveal = RevealPackage,
                Delete = DeletePackage,
                SetStatus = SetStatus,
            });
        }

        /// <summary>Sends a package that already exists, without rebuilding it — the reason to keep
        /// a list of them at all.</summary>
        private void SendExistingPackage(Services.PackageLibrary.BuiltPackage package)
        {
            if (package == null) return;

            if (!File.Exists(package.Path))
            {
                SetStatus($"\"{package.Name}\" is no longer on disk.");
                PackageLibrary.Refresh();
                return;
            }

            var contacts = new List<DataPackageWindow.SelectableRow>();
            foreach (var row in ContactRows())
                contacts.Add(new DataPackageWindow.SelectableRow(row.Item1, row.Item2));

            if (contacts.Count == 0)
            {
                SetStatus(_unreachableContactCount > 0
                    ? $"No contacts can be reached right now ({_unreachableContactCount} known but "
                      + "with no route). Check the server connection."
                    : "No contacts are available to send that package to.");
                return;
            }

            var dialog = new ContactMultiPickerWindow(
                package.Name, contacts, _unreachableContactCount);
            var owner = Application.Current?.MainWindow;
            if (owner != null) dialog.Owner = owner;

            if (dialog.ShowDialog() != true) return;

            var picked = dialog.SelectedUids;
            if (picked.Count == 0) return;

            try
            {
                _communicationService.SendMissionPackage(
                    picked, new FileInfo(package.Path), package.Name, false);

                string who = picked.Count == 1 ? "1 contact" : picked.Count + " contacts";
                SetStatus($"Sent \"{package.Name}\" to {who}.");
            }
            catch (Exception ex)
            {
                Log.Error("Could not send an existing data package.", ex);
                SetStatus("Could not send that package: " + Describe(ex));
            }
        }

        private void RevealPackage(Services.PackageLibrary.BuiltPackage package)
        {
            if (package == null || string.IsNullOrEmpty(package.Path)) return;

            if (!File.Exists(package.Path))
            {
                SetStatus($"\"{package.Name}\" is no longer on disk.");
                PackageLibrary.Refresh();
                return;
            }

            try
            {
                // /select opens Explorer with the file highlighted rather than just the folder.
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + package.Path + "\"");
            }
            catch (Exception ex)
            {
                Log.Warn("Could not open Explorer: " + ex.Message);
                SetStatus("Could not open that folder: " + Describe(ex));
            }
        }

        private bool DeletePackage(Services.PackageLibrary.BuiltPackage package)
        {
            if (package == null) return false;

            // Deleting removes the file AND the only record of what went into it, so it is
            // confirmed rather than instant.
            bool confirmed = DialogService.Confirm(
                $"Delete the data package \"{package.Name}\"?\n\n{package.Contents}\n{package.Path}"
                + "\n\nThis cannot be undone.",
                "Delete data package");
            if (!confirmed) return false;

            try
            {
                if (File.Exists(package.Path)) File.Delete(package.Path);
                SetStatus($"Deleted \"{package.Name}\".");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"Could not delete the data package \"{package.Name}\".", ex);
                SetStatus("Could not delete that package: " + Describe(ex));
                return false;
            }
        }

        /// <summary>Metres per pixel used when centring on a single reviewed feature — close
        /// enough to see it in context without guessing at a layer-wide extent.</summary>
        private const double DefaultFeatureResolution = 20.0;

        /// <summary>How many contacts were left out of the last <see cref="ContactRows"/> call for
        /// having no route. Reported to the operator so a missing name is explained.</summary>
        private int _unreachableContactCount;

        /// <summary>
        /// Contacts that can actually receive a package.
        ///
        /// <para>This used to list <c>AllContacts</c>, which is every contact the client has ever
        /// seen — including ones long gone. Offering those produced a send that failed with
        /// <c>CommoIllegalArgument</c> and a bare "Failed to send Data Package", because Commo
        /// refuses the whole transfer when it cannot find a usable endpoint for ANY of the
        /// destinations: <c>MP Send init failed to find usable endpoints for any of the given
        /// destination contacts</c>. One stale contact in the selection was enough.</para>
        ///
        /// <para>Two filters, both needed. <c>Contacts</c> rather than <c>AllContacts</c> is the
        /// host's own live set. <c>CurrentConnector</c> is then the literal expression of the thing
        /// Commo was looking for — a contact with no connector has no endpoint to send to, whatever
        /// its presence says.</para>
        /// </summary>
        private IReadOnlyList<Tuple<string, string>> ContactRows()
        {
            var rows = new List<Tuple<string, string>>();
            int unreachable = 0;

            var candidates = _contactService?.Contacts
                ?? _contactService?.AllContacts
                ?? Enumerable.Empty<Contact>();

            foreach (var c in candidates)
            {
                if (c == null || string.IsNullOrEmpty(c.Uid)) continue;

                if (!CanReceive(c)) { unreachable++; continue; }
                rows.Add(Tuple.Create(c.Uid, c.Name ?? c.Uid));
            }

            _unreachableContactCount = unreachable;
            if (unreachable > 0)
                Log.Info($"{unreachable} contact(s) omitted from the send list: no usable endpoint.");

            return rows;
        }

        /// <summary>Whether a package can be sent to this contact at all.</summary>
        private static bool CanReceive(Contact contact)
        {
            if (contact == null) return false;

            try
            {
                // Gone and Offline are exactly the states with no route.
                if (contact.PresenceLevel == ContactStatus.Gone
                    || contact.PresenceLevel == ContactStatus.Offline) return false;

                // The endpoint itself. Checked last and treated as decisive: presence describes
                // whether we have heard from them, this describes whether we can reach them.
                if (contact.CurrentConnector != null) return true;
                return contact.Connectors != null && contact.Connectors.Count > 0;
            }
            catch (Exception ex)
            {
                // A contact mid-teardown can throw from these. Excluding it is the safe answer:
                // including it risks failing the whole multi-recipient transfer.
                Log.Info("Treating a contact as unreachable after an error reading it: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Serializes one plotted feature to CoT XML for a package.
        ///
        /// <para>Rebuilt through <see cref="BuildFeatureCotEvent"/> — the same call the map is
        /// drawn from — using the style captured when the feature was plotted. The recipient
        /// therefore gets the marker the sender is looking at, not an approximation of it.</para>
        /// </summary>
        private string BuildFeatureCotXml(ArcGisLayer layer, LayerFeature feature)
        {
            if (layer == null || feature == null) return null;

            try
            {
                var downloaded = new DownloadedFeature(
                    uid: feature.Uid,
                    cotType: feature.CotType,
                    callsign: feature.Callsign,
                    remarks: feature.Remarks,
                    lat: feature.Lat,
                    lon: feature.Lon,
                    hae: feature.Hae,
                    attributes: null);

                Color? color = feature.ColorArgb.HasValue
                    ? Color.FromArgb(feature.ColorArgb.Value)
                    : (Color?)null;

                bool iconApplied;
                var cot = BuildFeatureCotEvent(
                    downloaded,
                    StalenessFor(layer),
                    out iconApplied,
                    feature.IconsetPath,
                    color,
                    feature.Label,
                    feature.Remarks);

                if (cot == null) return null;

                var xml = cot.ToXml();
                return xml?.OuterXml;
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not build CoT for feature {feature.Uid}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Writes the planned package and, when asked, sends it.
        /// </summary>
        /// <param name="send">False to only write the package to a location the operator picks.
        /// This is a first-class outcome, not a fallback: on a disconnected network, handing over
        /// a file is often the only way to move data at all.</param>
        private DataPackageWorkflow.DeliveryResult DeliverPackage(
            DataPackageBuilder.PackagePlan plan, string packageName,
            IReadOnlyList<string> recipients, bool send)
        {
            if (plan == null || plan.Entries.Count == 0)
                return DataPackageWorkflow.DeliveryResult.Failed("Nothing to package.");

            // Both paths now default to WinTAK's Data Packages folder. A package has to live
            // somewhere durable to be listed in the host, and that is where every other package on
            // the machine already lives.
            string destination = send
                ? DataPackageWriter.PathIn(packageName, MissionPackageRegistrar.PackagesFolder)
                : AskWhereToSave(packageName);

            // Backing out of the file dialog is not a failure — say nothing and change nothing,
            // so the selection is still there when they try again.
            if (string.IsNullOrEmpty(destination))
                return DataPackageWorkflow.DeliveryResult.Cancelled();

            DataPackageWriter.WriteResult written;
            try
            {
                written = DataPackageWriter.Write(plan, packageName, destination);
            }
            catch (Exception ex)
            {
                Log.Error("Could not build the data package.", ex);
                return DataPackageWorkflow.DeliveryResult.Failed(
                    "Could not build the data package: " + Describe(ex));
            }

            string detail = DataPackageBuilder.Describe(plan);
            if (written.Skipped.Count > 0)
                detail += $"; {written.Skipped.Count} file(s) could not be read and were left out";

            bool listed = MissionPackageRegistrar.Register(
                MissionPackageService, written.Path, packageName, written.Uid, SelfCallsign());

            // The new package should appear in the tab's own listing immediately, not on the next
            // manual refresh.
            RunOnUi(() => PackageLibrary.Refresh());

            if (!send)
            {
                string where = listed
                    ? $"Saved \"{packageName}\" ({detail}) to {written.Path} and listed it in Data Packages."
                    : $"Saved \"{packageName}\" ({detail}) to {written.Path}.";
                return DataPackageWorkflow.DeliveryResult.Ok(where);
            }

            try
            {
                _communicationService.SendMissionPackage(
                    recipients.ToList(), new FileInfo(written.Path), packageName, false);

                // Deliberately NOT deleted after sending any more. It used to go to %TEMP% and be
                // removed two minutes later, which kept a file holding layer URLs and display
                // configs off a shared workstation. It now lives in WinTAK's Data Packages folder
                // and is listed there, which is the behaviour asked for and the behaviour every
                // other package on the machine already has — a sent package the sender cannot find
                // again is the thing that made this feel broken. Removing it is the operator's call
                // now, from the same list.
                string who = recipients.Count == 1 ? "1 contact" : recipients.Count + " contacts";
                return DataPackageWorkflow.DeliveryResult.Ok(
                    $"Sent \"{packageName}\" ({detail}) to {who}.");
            }
            catch (Exception ex)
            {
                Log.Error("Could not send the data package.", ex);
                TryDelete(written.Path);
                return DataPackageWorkflow.DeliveryResult.Failed(
                    "Could not send the data package: " + Describe(ex));
            }
        }

        /// <summary>The staleness a layer's markers were plotted with, so a packaged CoT carries
        /// the same lifetime as the marker it was built from. Same expression as the download
        /// path — a package whose markers expired on a different schedule to the sender's would
        /// be a confusing thing to receive.</summary>
        private static TimeSpan StalenessFor(ArcGisLayer layer)
        {
            if (layer == null) return DefaultFeatureStaleness;
            return layer.RecurrenceTimeSpan() > TimeSpan.Zero
                ? TimeSpan.FromTicks(layer.RecurrenceTimeSpan().Ticks * StaleIntervalMultiplier)
                : DefaultFeatureStaleness;
        }

        private static void TryDeleteLater(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ =>
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception ex) { Log.Warn("Could not delete the temporary share file: " + ex.Message); }
            }, TaskScheduler.Default);
        }

        private static readonly HashSet<string> ReservedDeviceNames = new HashSet<string>(
            new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6",
                    "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6",
                    "LPT7", "LPT8", "LPT9" }, StringComparer.OrdinalIgnoreCase);

        /// <summary>Builds a share file name that is length-capped, free of Windows reserved
        /// device names, and unique per layer (an all-invalid name used to collapse to "____" and
        /// silently overwrite a different layer's pending share).</summary>
        internal static string SafeShareFileName(string layerName, string layerUrl)
        {
            string cleaned = System.Text.RegularExpressions.Regex.Replace(
                layerName ?? "layer", "[^a-zA-Z0-9 _-]", "_").Trim();
            if (cleaned.Length > 48) cleaned = cleaned.Substring(0, 48).Trim();
            if (cleaned.Length == 0 || ReservedDeviceNames.Contains(cleaned)) cleaned = "layer";
            return cleaned + "-" + ArcGisFeatureService.ShortHash(layerUrl ?? cleaned);
        }

        private string BuildShareConfigJson(ArcGisLayer layer)
        {
            var layerObj = new JObject
            {
                ["name"] = layer.Name,
                ["opacity"] = 1.0,
                ["visible"] = layer.Visible,   // was hardcoded true: a layer shared while hidden arrived visible
            };
            var o = new JObject
            {
                ["v"] = ShareSchemaVersion,
                ["url"] = layer.Url,
                ["layer"] = layerObj,
                ["private"] = layer.IsPrivate,
            };
            if (layer.RecurrenceInterval > 0)
            {
                o["freq"] = new JObject
                {
                    ["iv"] = layer.RecurrenceInterval,
                    ["u"] = layer.RecurrenceUnit,
                };
            }
            AddIfPresent(o, "sym", layer.SymJson ?? layer.AutoSymJson);
            AddIfPresent(o, "lbl", layer.LblJson);
            AddIfPresent(o, "popup", layer.PopupJson);
            // C-23's WinTAK half: shape styling is carried in the outgoing share instead of being
            // stripped in transit. Sharing an auto-derived style is deliberate — the receiving
            // device may not be able to reach the layer's metadata endpoint.
            AddIfPresent(o, "shp", layer.ShpJson ?? layer.AutoShpJson);
            return o.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static void AddIfPresent(JObject target, string key, string rawJson)
        {
            var parsed = SafeJson.ParseObjectOrNull(rawJson);
            if (parsed != null) target[key] = parsed;
        }

        // -------------------------------------------------------------------------
        // Remove / clear
        // -------------------------------------------------------------------------

        private void OnRemoveLayer(ArcGisLayer layer)
        {
            if (layer == null) return;

            bool isMyArcGis = PrivateLayers.Contains(layer);
            bool isSharedPrivate = SharedPrivateLayers.Contains(layer);

            // A layer that CAME from the operator's own ArcGIS account still exists there after
            // it is removed from the map, so deleting the row outright loses the one thing that
            // made it easy to get back. Those return to "My ArcGIS Layers" instead, where they can
            // be re-downloaded. The portal item id is what proves the layer came from the browse
            // list; a layer added by URL never appeared there and has nowhere to return to.
            bool returnsToBrowseList = !isMyArcGis && !string.IsNullOrEmpty(layer.ItemId);

            string displayName = UrlGuard.SanitizeDisplayName(layer.Name, 80);
            string message;
            if (returnsToBrowseList)
                message = $"Remove \"{displayName}\" from the map? Its markers will be removed and it "
                        + "will move back to My ArcGIS Layers, so you can download it again later.";
            else if (isMyArcGis || isSharedPrivate)
                message = $"Remove \"{displayName}\" from your layer list here? It stays in your ArcGIS "
                        + "account — this only hides it on this device. Any markers it added to the map "
                        + "will also be removed.";
            else
                message = $"Remove \"{displayName}\" from your layer list? Any markers it added to the "
                        + "map will also be removed.";

            if (!DialogService.Confirm(message, "Remove Layer?")) return;

            if (isMyArcGis)
            {
                PrivateLayers.Remove(layer);
                _excludedPrivateLayerUrls.Add(layer.Url);
            }
            else if (returnsToBrowseList)
            {
                if (isSharedPrivate) SharedPrivateLayers.Remove(layer);
                else PublicLayers.Remove(layer);

                // Back to the state a never-downloaded browse row is in, so the list is honest
                // about what is on the map: no extent to zoom to, no feature list, no sync time,
                // and the row's action glyph returns to the download arrow. The feature COUNT is
                // kept — it is still true of the source layer and is useful before re-downloading.
                layer.Type = "private";
                layer.LastSyncTicks = 0;
                layer.Extent = null;
                layer.Features.Clear();
                layer.RaiseFeaturesChanged();
                layer.Visible = true;

                // It is deliberately NOT added to the exclusion list — that list exists to keep a
                // layer OUT of the browse list on the next sign-in, which is the opposite of what
                // is wanted here.
                _excludedPrivateLayerUrls.Remove(layer.Url);
                PrivateLayers.Add(layer);

                SetStatus($"\"{layer.Name}\" moved back to My ArcGIS Layers.");
            }
            else if (isSharedPrivate)
            {
                SharedPrivateLayers.Remove(layer);
            }
            else
            {
                PublicLayers.Remove(layer);
            }

            RemoveLayerMarkers(layer.Url);
            RaisePropertyChanged(nameof(StatusLayersText));
            RaisePropertyChanged(nameof(ShowSharedPrivateLayersCard));
            SaveSettings();
        }

        private void OnClearAllLayers()
        {
            if (!DialogService.Confirm(
                "Remove every layer from My ArcGIS Layers, Private Layers, and Public Layers, and "
                    + "clear their markers from the map? This only affects this device — nothing "
                    + "in your ArcGIS account is deleted.",
                "Clear All Layers?")) return;

            List<string> urls;
            lock (_markerLock) urls = _layerMarkerUids.Keys.ToList();
            foreach (var url in urls) RemoveLayerMarkers(url);

            PrivateLayers.Clear();
            SharedPrivateLayers.Clear();
            PublicLayers.Clear();
            _excludedPrivateLayerUrls.Clear();

            RaisePropertyChanged(nameof(StatusLayersText));
            RaisePropertyChanged(nameof(ShowSharedPrivateLayersCard));
            StatusText = "Cleared all layers.";
            SaveSettings();
        }

        // -------------------------------------------------------------------------
        // Visibility
        // -------------------------------------------------------------------------

        /// <summary>item_layer.xml's layer_eye_icon toggle. Flips already-posted markers live via
        /// WinTak.Graphics.MapItem.Visible (found reflecting the SDK assemblies for the
        /// marker-removal fix — see RemoveLayerMarkers), rather than only gating whether a feature
        /// gets re-posted on the next download.</summary>
        private void OnToggleLayerVisibility(ArcGisLayer layer)
        {
            if (layer == null) return;
            layer.Visible = !layer.Visible;
            SetLayerMarkersVisible(layer.Url, layer.Visible);
            SaveSettings();
        }

        private void SetLayerMarkersVisible(string url, bool visible)
        {
            if (string.IsNullOrEmpty(url)) return;
            List<string> uids;
            lock (_markerLock)
            {
                if (!_layerMarkerUids.TryGetValue(url, out var stored)) return;
                uids = new List<string>(stored);
            }

            int failures = 0;
            Exception first = null;
            foreach (var uid in uids)
            {
                try
                {
                    var item = _mapItemFinder?.GetMapItem(uid);
                    if (item != null && !item.IsDisposed) item.Visible = visible;
                }
                catch (Exception ex)
                {
                    failures++;
                    if (first == null) first = ex;
                }
            }
            if (failures > 0)
            {
                // Aggregate: the old code reported once per uid into a status channel nobody could
                // see, and left the layer half-visible with no summary.
                Log.Error($"Could not set visibility on {failures} of {uids.Count} markers.", first);
                SetStatus($"Could not change visibility for {failures} of {uids.Count} markers: {first?.Message}");
            }
        }

        // -------------------------------------------------------------------------
        // Download
        // -------------------------------------------------------------------------

        private async Task OnDownloadLayerAsync(ArcGisLayer layer)
        {
            if (layer == null) return;
            if (PrivateLayers.Contains(layer)) MoveMyArcGisLayerOnDownload(layer);
            await DownloadLayerAsync(layer).ConfigureAwait(false);
        }

        private void MoveMyArcGisLayerOnDownload(ArcGisLayer layer)
        {
            PrivateLayers.Remove(layer);
            // ArcGIS also returns "org" and "shared"; only "public" (shared to Everyone) is
            // reachable without a token, so everything else stays private.
            if (string.Equals(layer.Access, "public", StringComparison.OrdinalIgnoreCase))
            {
                layer.Type = "public";
                PublicLayers.Add(layer);
            }
            else
            {
                layer.Type = "private";
                SharedPrivateLayers.Add(layer);
            }
            RaisePropertyChanged(nameof(StatusLayersText));
            RaisePropertyChanged(nameof(ShowSharedPrivateLayersCard));
            SaveSettings();
        }

        private async Task DownloadLayerAsync(ArcGisLayer layer, bool interactive = true)
        {
            if (layer == null || string.IsNullOrEmpty(layer.Url)) return;

            // C-26: per-layer in-flight guard. A recurrence tick every 15 s against a download
            // that can legitimately take longer used to run two concurrent downloads of the same
            // layer, double-posting markers and racing _layerMarkerUids[url].
            lock (_downloadLock)
            {
                if (!_downloadsInFlight.Add(layer.Url))
                {
                    Log.Info("Skipping a download for " + Log.Redact(layer.Url) + " — one is already running.");
                    return;
                }
            }

            CancellationTokenSource cts = null;
            try
            {
                var ct = LinkedToken(OperationTimeout, out cts);
                string token = layer.IsPrivate
                    ? await _authService.GetTokenAsync(ct).ConfigureAwait(false) : null;
                if (layer.IsPrivate && token == null)
                {
                    SetStatus($"Cannot sync \"{layer.Name}\" — session expired, please sign in again.");
                    return;
                }

                LayerInfo meta = await ResolveSymbologyAsync(layer, token, ct).ConfigureAwait(false);

                if (!await ConfirmDownloadSizeAsync(layer, token, ct, interactive).ConfigureAwait(false))
                    return;

                // meta.DisplayField is the attribute ArcGIS itself labels features with, so a
                // marker gets the name the operator already recognises instead of "Feature-7".
                var download = await _restClient
                    .DownloadLayerAsCotAsync(layer.Url, token, null, ct, meta?.DisplayField)
                    .ConfigureAwait(false);

                JObject sym = SafeJson.ParseObjectOrNull(layer.SymJson) ?? SafeJson.ParseObjectOrNull(layer.AutoSymJson);
                JObject lbl = SafeJson.ParseObjectOrNull(layer.LblJson);
                JObject popup = SafeJson.ParseObjectOrNull(layer.PopupJson);
                JObject shp = SafeJson.ParseObjectOrNull(layer.ShpJson) ?? SafeJson.ParseObjectOrNull(layer.AutoShpJson);

                HashSet<string> previousUids;
                lock (_markerLock)
                {
                    previousUids = _layerMarkerUids.TryGetValue(layer.Url, out var prev)
                        ? new HashSet<string>(prev, StringComparer.Ordinal)
                        : new HashSet<string>(StringComparer.Ordinal);
                }

                var staleness = layer.RecurrenceTimeSpan() > TimeSpan.Zero
                    ? TimeSpan.FromTicks(layer.RecurrenceTimeSpan().Ticks * StaleIntervalMultiplier)
                    : DefaultFeatureStaleness;

                // An existing marker keeps the icon it resolved when it was created: re-posting
                // the same uid UPDATES it rather than recreating it, so a changed symbol never
                // reaches the map until WinTAK restarts. Observed directly — a re-styled layer
                // kept its previous icons for the whole session. Disposing them first forces the
                // host to build each marker fresh against the newly installed iconset.
                if (IconsetWasReplaced)
                {
                    Log.Info($"Icons changed for \"{layer.Name}\"; recreating its markers so the new "
                             + "symbols are drawn.");
                    RemoveLayerMarkers(layer.Url);
                }

                var uids = new List<string>(download.Features.Count);
                int shapeStyled = 0;
                var tally = new PostTally();

                // Resolved style per feature, captured as it is applied, so a data package can
                // rebuild exactly the CoT the map was drawn from. See LayerFeature's remarks for
                // why this is retained instead of the raw attributes.
                var styleByUid = new Dictionary<string, PlottedStyle>(
                    download.Features.Count, StringComparer.Ordinal);
                foreach (var f in download.Features)
                {
                    ct.ThrowIfCancellationRequested();
                    string iconsetPath = sym != null ? DisplayStyleResolver.ResolveIconsetPath(sym, f.Attributes) : null;
                    Color? color = sym != null ? DisplayStyleResolver.ResolveColor(sym, f.Attributes) : null;
                    string label = lbl != null ? DisplayStyleResolver.ResolveLabel(lbl, f.Attributes, f.Callsign) : f.Callsign;
                    string remarks = popup != null ? DisplayStyleResolver.BuildRemarks(popup, f.Attributes) : f.Remarks;

                    // C-07: shape styling, which did not exist on this platform at all.
                    ShapeStyle shape = null;
                    if (shp != null && !string.Equals(f.GeometryKind, "point", StringComparison.Ordinal))
                    {
                        shape = DisplayStyleResolver.ResolveShapeStyle(shp, f.Attributes);
                        if (shape != null)
                        {
                            shapeStyled++;
                            // WinTAK's plugin SDK exposes no verified polyline/polygon map-item
                            // API, so a line/area feature is still plotted as a marker at its
                            // first vertex (the same simplification ATAK's downloader makes) and
                            // the resolved stroke colour is applied to that marker. This is a
                            // deliberate, documented partial: what it is NOT is the previous
                            // behaviour, where the styling was discarded with no warning and no
                            // log line anywhere (Appendix F §5).
                            if (!color.HasValue) color = shape.StrokeColor;
                        }
                    }

                    styleByUid[f.Uid] = new PlottedStyle
                    {
                        Hae = f.Hae,
                        CotType = f.CotType,
                        IconsetPath = iconsetPath,
                        ColorArgb = color.HasValue ? ToArgbSigned(color.Value) : (int?)null,
                        Label = label,
                        Remarks = remarks,
                    };

                    await PostFeatureAsCotAsync(f, layer.Visible, staleness, iconsetPath, color, label,
                        remarks, tally).ConfigureAwait(false);
                    uids.Add(f.Uid);
                    previousUids.Remove(f.Uid);
                }

                lock (_markerLock)
                {
                    if (_layerMarkerUids.TryGetValue(layer.Url, out var old))
                        foreach (var u in old) _allMarkerUids.Remove(u);
                    _layerMarkerUids[layer.Url] = uids;
                    foreach (var u in uids) _allMarkerUids.Add(u);
                }

                // Uids present last download but not this one — the source feature disappeared
                // server-side; their markers are removed instead of lingering on the map forever.
                foreach (var vanishedUid in previousUids)
                {
                    try
                    {
                        var item = _mapItemFinder?.GetMapItem(vanishedUid);
                        if (item != null && !item.IsDisposed) item.Dispose();
                    }
                    catch (Exception ex) { Log.Warn($"Could not dispose stale marker {vanishedUid}: {ex.Message}"); }
                }

                // LastSync only advances on a completed download, so a partial failure no longer
                // makes the recurrence scheduler treat it as a successful sync.
                layer.LastSync = DateTime.UtcNow;

                // The download is the authoritative count and it was never recorded: FeatureCount
                // was written in exactly one place, the Home-tab stats refresh, which only runs on
                // an explicit layer refresh and skips the browse list entirely. So a layer could
                // plot 29 markers and still show "0 features" in its own row indefinitely.
                RunOnUi(() => layer.FeatureCount = download.Features.Count);

                // What was actually plotted, for "zoom to layer" and the row's feature list.
                // Built from the downloaded features rather than the service's published extent:
                // these are already WGS84 (the query asks outSR=4326) and they describe what is on
                // the operator's map, which is what zooming to the layer should frame.
                var plotted = download.Features
                    .Where(f => ArcGisFeatureService.IsPlottable(f.Lat, f.Lon))
                    .ToList();

                var extent = LayerExtent.FromPoints(
                    plotted.Select(f => Tuple.Create(f.Lat, f.Lon)));

                Func<DownloadedFeature, LayerFeature> toLayerFeature = f =>
                {
                    var lf = new LayerFeature
                    {
                        Uid = f.Uid,
                        Callsign = f.Callsign,
                        Lat = f.Lat,
                        Lon = f.Lon,
                        Hae = f.Hae,
                        CotType = f.CotType,
                    };
                    PlottedStyle style;
                    if (styleByUid.TryGetValue(f.Uid ?? string.Empty, out style) && style != null)
                    {
                        lf.Hae = style.Hae;
                        lf.CotType = style.CotType ?? lf.CotType;
                        lf.IconsetPath = style.IconsetPath;
                        lf.ColorArgb = style.ColorArgb;
                        lf.Label = style.Label;
                        lf.Remarks = style.Remarks;
                    }
                    return lf;
                };

                var listed = plotted
                    .Take(ArcGisLayer.MaxListedFeatures)
                    .Select(toLayerFeature)
                    .ToList();

                // Uncapped pool for package selection; the bound list stays capped.
                var selectable = plotted.Select(toLayerFeature).ToList();

                RunOnUi(() =>
                {
                    layer.Extent = extent;
                    layer.AllFeatures = selectable;
                    layer.Features.Clear();
                    foreach (var f in listed) layer.Features.Add(f);
                    layer.RaiseFeaturesChanged();
                });

                string summary = $"Downloaded: {layer.Name} ({download.Features.Count} features";
                if (shapeStyled > 0) summary += $", {shapeStyled} shape-styled";
                if (download.SkippedFeatures > 0) summary += $", {download.SkippedFeatures} skipped";
                if (download.OutOfRangeCoordinates > 0)
                    summary += $", {download.OutOfRangeCoordinates} out of range";
                summary += ")";
                if (download.Truncated) summary += " — TRUNCATED, not all features were downloaded.";

                Log.Info(summary);

                // One line for the whole layer instead of one per affected feature.
                if (tally.Any)
                    Log.Warn($"\"{layer.Name}\" sync had problem features: {tally.Describe()}.");

                RunOnUi(() =>
                {
                    StatusText = summary;
                    SaveSettings();
                });
            }
            catch (OperationCanceledException)
            {
                SetStatus($"Sync of \"{layer.Name}\" was cancelled or timed out.");
            }
            catch (Exception ex)
            {
                Log.Error($"Download failed for {layer.Name}.", ex);
                SetStatus($"Download failed for {layer.Name}: {Describe(ex)}");
            }
            finally
            {
                cts?.Dispose();
                lock (_downloadLock) _downloadsInFlight.Remove(layer.Url);
            }
        }

        /// <summary>
        /// C-07, the core of it: when a layer carries no explicit display config, its own
        /// published <c>drawingInfo.renderer</c> is read and translated into compact
        /// <c>sym</c>/<c>shp</c> blocks — exactly what
        /// <c>TAKPortal/services/featurelinkArcgisIconset.service.js:236-244</c> does server-side
        /// and what <c>DisplayConfig.forAutoIcons</c> does on ATAK. WinTAK previously discarded
        /// <c>drawingInfo</c> outright in <c>FetchLayerInfoAsync</c>, so no symbology of any kind
        /// was resolved on the download path.
        /// </summary>
        private async Task<LayerInfo> ResolveSymbologyAsync(ArcGisLayer layer, string token, CancellationToken ct)
        {
            // An explicitly configured layer keeps its config: a Portal/share-authored config
            // always wins over whatever the layer's own renderer says. The metadata is still
            // fetched in that case, because the caller needs its displayField to name markers.
            bool configured = !string.IsNullOrEmpty(layer.SymJson) && !string.IsNullOrEmpty(layer.ShpJson);
            LayerInfo resolvedMeta = null;

            try
            {
                var meta = await _restClient.FetchLayerMetadataAsync(layer.Url, token, ct).ConfigureAwait(false);
                resolvedMeta = meta;

                // An item-level override is what the operator actually SEES in ArcGIS Online, so
                // it wins over the service's published renderer. Styling saved on the item's
                // Visualization tab never touches the feature service, so without this a layer
                // styled by unique value still reports a single-symbol 'simple' renderer here and
                // renders as one repeated marker — the exact field symptom this closes.
                if (!string.IsNullOrEmpty(layer.ItemId))
                {
                    var overrideRenderer = await _restClient.FetchItemRendererAsync(
                        _authService.PortalUrl, layer.ItemId,
                        ArcGisFeatureService.LayerIndexOf(layer.Url), token, ct).ConfigureAwait(false);

                    if (overrideRenderer != null)
                    {
                        Log.Info($"Layer \"{layer.Name}\": using the item-level renderer from item "
                                 + $"{layer.ItemId} (the service renderer was "
                                 + $"'{(string)meta.Renderer?["type"] ?? "absent"}').");
                        meta.Renderer = overrideRenderer;
                    }
                }

                if (configured || meta.Renderer == null)
                {
                    if (!configured)
                        Log.Info($"Layer \"{layer.Name}\" publishes no renderer — features keep WinTAK's default styling.");
                    return meta;
                }

                // Picture-marker (esriPMS) symbols carry embedded bitmaps and need a real iconset
                // installed before a marker can reference one. That is what used to dead-end here
                // with "this build cannot translate" — the renderer was understood perfectly well,
                // there was simply nothing on WinTAK that could create the icons. Try that first,
                // because an icon beats a coloured dot whenever the layer publishes one.
                // The two extractors are COMPLEMENTS over the same renderer, not alternatives:
                // AutoIconset takes the esriPMS picture symbols, AutoSymbology takes the
                // esriSMS/SLS/SFS colour and shape symbols. A normal ArcGIS renderer mixes both —
                // a few badge icons among a dozen coloured circles — so treating them as
                // either/or produced a single icon stamped on every feature and no colours at all
                // while the source map showed twelve distinct symbols. Both are resolved, then
                // merged per value.
                IconsetResult icons = TryResolveIconset(layer, meta);
                IconsetWasReplaced = icons != null && icons.ReplacedExisting;

                // Remember which iconsets exist on disk for this layer, so a data package built in
                // a later session can still bundle them (AutoSymJson is deliberately not persisted).
                if (icons != null && layer.RecordIconsetUid(icons.Uid)) RunOnUi(SaveSettings);
                var extracted = AutoSymbology.Extract(meta.Renderer);

                if (icons == null && extracted.IsEmpty)
                {
                    Log.Info($"Layer \"{layer.Name}\" publishes a renderer this build cannot "
                             + "translate into either icons or colours — default styling applies.");
                    return meta;
                }

                if (string.IsNullOrEmpty(layer.SymJson))
                {
                    JObject colors = extracted.IsEmpty
                        ? null
                        : DisplayStyleResolver.BuildMarkerSymConfigJson(extracted);

                    JObject sym = icons == null
                        ? colors
                        : AutoIconset.MergeIconAndColorConfig(icons.Uid, icons.Group, icons.Extraction, colors);

                    layer.AutoSymJson = sym?.ToString(Newtonsoft.Json.Formatting.None);
                }

                // The Config/No Config badge binds to HasDisplayConfig, which was only ever set on
                // the share-import path — so a layer whose styling we derived from its OWN
                // renderer still reported "No Config". The badge is the operator's only signal
                // that symbology resolved, so it has to reflect auto-derived config too.
                if (!string.IsNullOrEmpty(layer.AutoSymJson))
                    RunOnUi(() => layer.HasDisplayConfig = true);

                if (string.IsNullOrEmpty(layer.ShpJson))
                {
                    var shp = DisplayStyleResolver.BuildShapeConfigJson(extracted);
                    layer.AutoShpJson = shp?.ToString(Newtonsoft.Json.Formatting.None);
                    if (shp != null)
                        Log.Info($"Layer \"{layer.Name}\" carries polyline/polygon shape styling "
                                 + "(esriSLS/esriSFS); applying it to first-vertex markers.");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Symbology is an enhancement — never fail the download over it, but never fail
                // it silently either, which is exactly what the old code did by not looking at all.
                Log.Warn($"Could not resolve symbology for \"{layer.Name}\": {ex.Message}");
                return null;
            }

            return resolvedMeta;
        }

        /// <summary>
        /// Generates and installs an iconset from the layer's own picture-marker renderer, and
        /// points the layer's symbology config at it. Returns true when icons are available.
        ///
        /// <para>This is the step WinTAK never had. The resolver and the CoT emitter could already
        /// carry an <c>iconsetpath</c>; nothing ever created the set it named, so every
        /// <c>esriPMS</c> layer fell back to default markers and said so in the log once per
        /// sync.</para>
        ///
        /// <para>Never throws and never fails a download — symbology is an enhancement. A failure
        /// leaves <c>AutoSymJson</c> alone so the colour/shape path can still apply.</para>
        /// </summary>
        /// <summary>An installed iconset plus the extraction it came from, so the caller can merge
        /// the icons with the renderer's colour symbols rather than choosing between them.</summary>
        private sealed class IconsetResult
        {
            public string Uid;
            public string Group;
            public AutoIconset.Extraction Extraction;

            /// <summary>True when this replaced a set the host already held — i.e. the layer's
            /// symbols changed. The markers already on the map still hold the OLD rendering, so
            /// they have to be recreated rather than updated in place.</summary>
            public bool ReplacedExisting;
        }

        /// <summary>Set by the most recent symbology resolve: the layer's icons changed, so its
        /// existing markers are showing stale renderings and must be recreated.</summary>
        private bool IconsetWasReplaced { get; set; }

        private IconsetResult TryResolveIconset(ArcGisLayer layer, LayerInfo meta)
        {
            try
            {
                string canonical = AutoIconset.Canonicalize(layer.Url);
                var icons = AutoIconset.ExtractPictureSymbols(meta.Renderer);

                // Always report WHAT the renderer declared versus what came out. A layer that is
                // richly styled in ArcGIS but yields one icon here is not visible any other way —
                // no exception is thrown and the output is perfectly valid for what was parsed.
                string explanation = AutoIconset.ExplainExtraction(icons);
                if (explanation != null) Log.Info($"Layer \"{layer.Name}\" {explanation}");

                if (icons.IsEmpty) return null;

                // The naming source is the FeatureServer layer's own name, never the operator's
                // local label for it — every platform must derive the same group string.
                string group = AutoIconset.GroupFor(meta.Name ?? layer.Name);
                string uid = AutoIconset.Uid(canonical, icons.Field ?? string.Empty);

                byte[] zip = AutoIconset.BuildZipBytes(uid, group, icons.Symbols);

                string zipPath;
                bool replaced;
                if (!IconsetInstaller.Install(uid, group, zip, out zipPath, out replaced)) return null;

                SetStatus($"Icons ready for \"{layer.Name}\" — {icons.Symbols.Count} symbol(s).");
                return new IconsetResult
                {
                    Uid = uid,
                    Group = group,
                    Extraction = icons,
                    ReplacedExisting = replaced,
                };
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not build an iconset for \"{layer.Name}\": {ErrorText.ForOperator(ex)}");
                return null;
            }
        }

        // -------------------------------------------------------------------------
        // Zoom to data
        // -------------------------------------------------------------------------

        /// <summary>
        /// Frames a layer's features: centre on their midpoint, zoom to fit their span.
        ///
        /// <para>The extent comes from what was actually plotted, so a layer whose features are a
        /// hundred miles apart frames all of them, and a layer clustered in one town frames that
        /// town rather than the county.</para>
        /// </summary>
        private void OnZoomToLayer(ArcGisLayer layer)
        {
            if (layer == null) return;

            var extent = layer.Extent;
            if (extent == null || !extent.IsValid)
            {
                SetStatus($"\"{layer.Name}\" has no plotted features to zoom to — sync it first.");
                return;
            }

            LookAt(extent.CenterLat, extent.CenterLon, extent.ResolutionMetresPerPixel(),
                $"\"{layer.Name}\" ({extent.LatSpan:0.###}° x {extent.LonSpan:0.###}°)");
        }

        private void OnZoomToFeature(LayerFeature feature)
        {
            if (feature == null) return;

            // A single point has no span, so pick a resolution that shows its surroundings
            // rather than filling the screen with one marker.
            const double SingleFeatureMetresPerPixel = 4.0;
            LookAt(feature.Lat, feature.Lon, SingleFeatureMetresPerPixel, feature.Callsign);
        }

        /// <summary>Moves the map camera, or explains why it could not.</summary>
        private void LookAt(double lat, double lon, double metresPerPixel, string what)
        {
            var controller = MapViewController;
            if (controller == null)
            {
                // See the MapViewController import: absence is tolerated by design.
                Log.Warn("WinTAK did not provide a map controller; zoom-to-layer is unavailable.");
                SetStatus("This WinTAK build does not expose map control to plugins — cannot zoom.");
                return;
            }

            try
            {
                var point = new GeoPoint(lat, lon);
                RunOnUi(() => controller.LookAt(point, metresPerPixel, true));
                SetStatus($"Zoomed to {what}.");
                Log.Info($"Zoom to {what}: {lat:0.#####}, {lon:0.#####} at {metresPerPixel:0.#} m/px.");
            }
            catch (Exception ex)
            {
                Log.Error("Could not move the map camera.", ex);
                SetStatus("Could not move the map: " + ErrorText.ForOperator(ex, IsAuthenticated));
            }
        }

        /// <summary>Layers already asked about in this session, by URL. A large layer is large on
        /// every single sync, so without this the dialog would reappear on every recurrence tick —
        /// the exact behaviour reported from the field. A "yes" is persisted on the layer and
        /// outlives the session; a "no" lives only here, so restarting WinTAK offers again rather
        /// than permanently hiding a layer behind a decision made once in a hurry.</summary>
        private readonly HashSet<string> _largeLayerAsked = new HashSet<string>(StringComparer.Ordinal);
        private readonly object _largeLayerAskedLock = new object();

        /// <summary>
        /// True when the size dialog should be raised for this layer now.
        ///
        /// <para>An OPERATOR-INITIATED sync always asks, every time: they just pressed the button,
        /// so the dialog is the answer to a question they actually posed, and suppressing it would
        /// make Sync look broken for any layer they had previously declined. Only the TIMER-driven
        /// path is rate-limited, to once per layer per session.</para>
        /// </summary>
        private bool ShouldRaiseLargeLayerDialog(ArcGisLayer layer, bool interactive)
        {
            if (layer == null || string.IsNullOrEmpty(layer.Url)) return false;
            lock (_largeLayerAskedLock)
            {
                bool first = _largeLayerAsked.Add(layer.Url);
                return interactive || first;
            }
        }

        /// <summary>
        /// Pre-flight size guard for a layer download.
        ///
        /// <para><c>ArcGisFeatureService</c> pages correctly, but its own ceiling is
        /// <c>MaxPages</c> × <c>DefaultPageSize</c> — millions of features, every one of which
        /// becomes an individual CoT marker on the map. Nothing consulted the layer's feature
        /// count before starting, even though <c>QueryFeatureCountAsync</c> already existed and
        /// was already being called to populate the Home tab's statistics. Pointing Add Layer at a
        /// county parcel or national hydrography service therefore froze the workstation with no
        /// warning and no way out short of the operation timeout.</para>
        ///
        /// <para>Returns false when the download must not proceed. A count that cannot be obtained
        /// is NOT treated as a refusal — some valid services do not answer
        /// <c>returnCountOnly</c>, and failing closed there would break working layers. In that
        /// case the download proceeds and the service's own
        /// <c>exceededTransferLimit</c>/<c>Truncated</c> path remains the backstop.</para>
        /// </summary>
        private async Task<bool> ConfirmDownloadSizeAsync(ArcGisLayer layer, string token, CancellationToken ct,
            bool interactive)
        {
            long count;
            try
            {
                count = await _restClient.QueryFeatureCountAsync(layer.Url, token, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Fail OPEN, loudly. See the remarks above.
                Log.Warn($"Could not pre-check the feature count for \"{layer.Name}\" "
                         + $"({Describe(ex)}); proceeding with the download.");
                return true;
            }

            // Free and accurate — record it whatever happens next, so a layer the operator
            // declines still reports its real size rather than a dash.
            RunOnUi(() => layer.FeatureCount = count);

            if (count > MaxDownloadableFeatures)
            {
                // Once per layer per session — an over-cap layer is over the cap on EVERY
                // recurrence tick, so an ungated notice here would pop every 15 seconds forever.
                if (!ShouldRaiseLargeLayerDialog(layer, interactive)) return false;

                string refusal =
                    $"\"{layer.Name}\" contains {count:N0} features, over this plugin's limit of "
                    + $"{MaxDownloadableFeatures:N0}.\n\nPlotting that many markers would make the map "
                    + "unusable. Filter the layer at the source, or publish a smaller view of it.";
                Log.Warn($"Refused to download \"{layer.Name}\": {count} features exceeds the "
                         + $"{MaxDownloadableFeatures} hard cap.");
                SetStatus($"\"{layer.Name}\" has {count:N0} features — too many to plot (limit {MaxDownloadableFeatures:N0}).");
                await RunOnUiAsync(() => { DialogService.Inform(refusal, "Layer too large"); return true; })
                    .ConfigureAwait(false);
                return false;
            }

            if (count > LargeDownloadPromptThreshold)
            {
                // Already approved on a previous run — the answer is persisted per layer.
                if (layer.LargeDownloadAccepted) return true;

                // The operator IS asked, even when the sync was started by the recurrence timer
                // rather than by a click. An earlier version only wrote to the status strip here,
                // on the reasoning that a background task should not steal focus — but a status
                // line nobody is looking at is indistinguishable from the layer silently never
                // syncing, which is the worse failure on a tactical display. Asking is right; the
                // defect was asking REPEATEDLY, which is what the once-per-session gate fixes.
                if (!ShouldRaiseLargeLayerDialog(layer, interactive))
                {
                    // Already asked and declined this session. Say nothing — no dialog, no status
                    // churn, no log line every tick.
                    return false;
                }

                string question =
                    $"\"{layer.Name}\" contains {count:N0} features.\n\n"
                    + "Each one is plotted as a separate marker, so a layer this size will take a "
                    + "while to sync and may slow the map down.\n\n"
                    + (interactive
                        ? "Download it anyway?"
                        : "This layer is due for its scheduled refresh. Download it now?")
                    + "\n\nYes is remembered for this layer. No will not be asked again until "
                    + "WinTAK restarts.";

                bool proceed = await RunOnUiAsync(() => DialogService.Confirm(question, "Large layer"))
                    .ConfigureAwait(false);
                if (!proceed)
                {
                    Log.Info($"Operator declined the {count}-feature download of \"{layer.Name}\"; "
                             + "not asking again this session.");
                    SetStatus($"Sync of \"{layer.Name}\" ({count:N0} features) was cancelled.");
                    return false;
                }

                Log.Info($"Operator accepted the {count}-feature download of \"{layer.Name}\"; "
                         + "not asking again for this layer.");
                RunOnUi(() => { layer.LargeDownloadAccepted = true; SaveSettings(); });
            }

            return true;
        }

        /// <summary>Converts one downloaded ArcGIS feature into a CoT event and injects it onto the
        /// WinTAK map. Colour is applied post-creation via WinTak.Graphics.MapMarker.Color.
        ///
        /// <para>Awaits <c>ICotMessageSender.ProcessAsync</c> rather than calling the <c>void</c>
        /// <c>Process</c> overload. The void form gives no completion signal and no success
        /// signal, so the immediately-following <c>GetMapItem(uid)</c> raced marker creation:
        /// when it lost, <c>Visible</c> and <c>Color</c> were silently never applied and the
        /// operator saw markers for a layer they had toggled hidden, or default-coloured markers
        /// for a styled layer — intermittently, which is the worst way for a tactical display to
        /// be wrong. <c>ProcessAsync</c> returns <c>Task&lt;bool&gt;</c>; both the ordering and
        /// the discarded failure signal are now honoured.</para>
        ///
        /// <para>These SDK types do NOT require the dispatcher: <c>MapItem</c> derives from
        /// <c>WinTak.Framework.PropertyChangedBase</c> (→ <c>object</c>), with no
        /// <c>DispatcherObject</c>/<c>DependencyObject</c> anywhere in the chain, so there is no
        /// <c>VerifyAccess()</c> affinity to violate by mutating them from the download's
        /// thread-pool continuation. Verified against the 5.6.0.151 assemblies.</para></summary>
        /// <summary>
        /// Per-download tally of the conditions that used to emit one log line per feature.
        ///
        /// A systematic fault — a layer whose geometry is all out of range, a display config whose
        /// iconset paths are all malformed — produced one identical warning for every feature in
        /// the layer. That buries the rest of the log, and it is strictly less useful to a support
        /// engineer than a single line saying how many. Counted here, reported once by
        /// <see cref="DownloadLayerAsync"/>.
        /// </summary>
        /// <summary>The style actually applied to one plotted feature, kept so a data package can
        /// rebuild its CoT without re-resolving against a config that may since have changed.</summary>
        private sealed class PlottedStyle
        {
            public double Hae;
            public string CotType;
            public string IconsetPath;
            public int? ColorArgb;
            public string Label;
            public string Remarks;
        }

        private sealed class PostTally
        {
            public int OutOfRange;
            public int Rejected;
            public int NotMaterialised;
            public int StyleFailed;
            public int UnsafeIconset;

            public bool Any => OutOfRange + Rejected + NotMaterialised + StyleFailed + UnsafeIconset > 0;

            public string Describe()
            {
                var parts = new List<string>(5);
                if (OutOfRange > 0) parts.Add($"{OutOfRange} with out-of-range coordinates");
                if (Rejected > 0) parts.Add($"{Rejected} rejected by WinTAK");
                if (NotMaterialised > 0) parts.Add($"{NotMaterialised} with no map item after ProcessAsync");
                if (StyleFailed > 0) parts.Add($"{StyleFailed} whose visibility/colour could not be applied");
                if (UnsafeIconset > 0) parts.Add($"{UnsafeIconset} with an unsafe iconset path dropped");
                return string.Join(", ", parts);
            }
        }

        /// <summary>
        /// Builds the CoT event for a downloaded feature — everything the marker is, short of
        /// sending it.
        ///
        /// <para>Split out of <see cref="PostFeatureAsCotAsync"/> so a data package can carry the
        /// SAME event that plotting would create. Rebuilding an equivalent-looking event for the
        /// package would be two implementations of the styling rules, and they would drift: the
        /// icon-forces-white-colour rule and the iconset-path safety check both live here, and a
        /// package built from a second copy would be the one that quietly stopped matching.</para>
        ///
        /// <para>Returns null when the feature cannot be plotted at all.</para>
        /// </summary>
        private CotEvent BuildFeatureCotEvent(DownloadedFeature f, TimeSpan staleness,
            out bool iconApplied,
            string iconsetPath = null, Color? color = null, string label = null, string remarks = null,
            PostTally tally = null)
        {
            iconApplied = false;
            if (!ArcGisFeatureService.IsPlottable(f.Lat, f.Lon))
            {
                if (tally != null) tally.OutOfRange++;
                else Log.Warn($"Refusing to plot feature {f.Uid}: coordinates out of range.");
                return null;
            }

            var now = DateTime.UtcNow;
            var stale = now.Add(staleness);

            var geoPoint = double.IsNaN(f.Hae)
                ? new GeoPoint(f.Lat, f.Lon)
                : new GeoPoint(f.Lat, f.Lon, f.Hae, AltitudeReference.HAE);
            var cotPoint = new CotPoint(geoPoint);

            var detail = new CotDetail();
            var contact = new CotItem("contact");
            contact.SetAttribute("callsign", SanitizeForCot(label ?? f.Callsign, 255));
            detail.AddChild(contact);

            string remarksText = remarks ?? f.Remarks;
            if (!string.IsNullOrEmpty(remarksText))
            {
                var remarksItem = new CotItem("remarks")
                {
                    InnerText = SanitizeForCot(remarksText, DisplayStyleResolver.MaxRemarksLength)
                };
                detail.AddChild(remarksItem);
            }

            if (!string.IsNullOrEmpty(iconsetPath))
            {
                // A peer controls this string via a received share and it is broadcast to the whole
                // TAK network, so the format is validated before it becomes a CoT attribute.
                if (UrlGuard.IsSafeIconsetPath(iconsetPath))
                {
                    var usericon = new CotItem("usericon");
                    usericon.SetAttribute("iconsetpath", iconsetPath);
                    detail.AddChild(usericon);
                    iconApplied = true;
                }
                else
                {
                    if (tally != null) tally.UnsafeIconset++;
                    else Log.Warn("Dropped an unsafe iconsetpath from a display config.");
                }
            }

            // Colour travels IN the CoT, as a <color argb="…"/> detail.
            //
            // Setting WinTak.Graphics.MapMarker.Color after the fact does not stick: the marker is
            // created by the host's CoT pipeline, which derives its style from the event, and a
            // later host-side refresh re-derives it and discards anything the plugin poked in.
            // The observed symptom was every simple-marker category rendering as the same yellow
            // 2525 "unknown affiliation" clover — the default for the a-u-G type — while ArcGIS
            // showed five distinct colours.
            //
            // When a custom ICON applies, the colour is forced to white instead. A marker colour
            // tints the icon bitmap, so a coloured fill would wash an ACP or EOC badge in that
            // colour; white means no tint and the icon shows its own. ATAK does exactly this and
            // documents the same reason.
            if (iconApplied || color.HasValue)
            {
                Color applied = iconApplied ? Color.White : color.Value;
                var colorItem = new CotItem("color");
                colorItem.SetAttribute("argb",
                    ToArgbSigned(applied).ToString(CultureInfo.InvariantCulture));
                detail.AddChild(colorItem);
            }

            var cotEvent = new CotEvent(
                uid: f.Uid,
                type: ValidCotType(f.CotType),
                vers: CotEvent.VERSION_2_0,
                point: cotPoint,
                time: new CoordinatedTime(now),
                start: new CoordinatedTime(now),
                stale: new CoordinatedTime(stale),
                how: CotEvent.HOW_MACHINE_GENERATED,
                detail: detail,
                opex: null,
                qos: null,
                access: null);

            return cotEvent;
        }

        private async Task PostFeatureAsCotAsync(DownloadedFeature f, bool visible, TimeSpan staleness,
            string iconsetPath = null, Color? color = null, string label = null, string remarks = null,
            PostTally tally = null)
        {
            bool iconApplied;
            var cotEvent = BuildFeatureCotEvent(
                f, staleness, out iconApplied, iconsetPath, color, label, remarks, tally);
            if (cotEvent == null) return;

            // Post (create) the marker and WAIT for the host to have processed it, then set its
            // live Visible/Color via IMapItemFinderService.
            bool accepted = await _cotSender.ProcessAsync(cotEvent).ConfigureAwait(false);
            if (!accepted)
            {
                // The void Process() overload discarded this entirely, so a marker the host
                // refused simply never appeared and nothing anywhere said so.
                if (tally != null) tally.Rejected++;
                else Log.Warn($"WinTAK did not accept the CoT event for feature {f.Uid}; no marker was created.");
                return;
            }

            if (!visible || color.HasValue)
            {
                try
                {
                    var item = _mapItemFinder?.GetMapItem(f.Uid);
                    if (item == null)
                    {
                        // Should now be unreachable for an accepted event — ProcessAsync has
                        // completed. Kept as a real signal: if it fires, marker creation is not
                        // synchronous with ProcessAsync's completion after all, and the fix is to
                        // apply styling from IMapItemFinderService.Created instead.
                        if (tally != null) tally.NotMaterialised++;
                        else Log.Warn($"Map item {f.Uid} was still not available after ProcessAsync completed; "
                                      + "visibility/colour were not applied.");
                    }
                    else if (!item.IsDisposed)
                    {
                        if (!visible) item.Visible = false;

                        // Belt and braces alongside the <color> detail above — harmless if the
                        // host already applied it, and the same white-when-icon rule applies so
                        // the two can never disagree.
                        if ((iconApplied || color.HasValue) && item is WinTak.Graphics.MapMarker marker)
                            marker.Color = iconApplied ? Color.White : color.Value;
                    }
                }
                catch (Exception ex)
                {
                    if (tally != null) tally.StyleFailed++;
                    else Log.Warn($"Could not apply visibility/colour to marker {f.Uid}: {ex.Message}");
                }
            }
        }

        /// <summary>Packs a colour into the signed 32-bit ARGB integer CoT's <c>color</c> detail
        /// carries. Unchecked because a fully-opaque colour sets the high bit and overflows a
        /// signed int, which is the representation TAK expects.</summary>
        private static int ToArgbSigned(Color c)
        {
            unchecked
            {
                return (int)(((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B);
            }
        }

        /// <summary>Validates an ArcGIS-sourced CoT type against the CoT grammar. An invalid value
        /// ("hello", or a 4 KB blob) used to be emitted straight onto the network.</summary>
        internal static string ValidCotType(string cotType)
        {
            if (string.IsNullOrEmpty(cotType)) return UnknownCotType;
            if (cotType.Length > MaxCotTypeLength || !CotTypePattern.IsMatch(cotType))
            {
                Log.Warn("Replacing an invalid CoT type from layer data with " + UnknownCotType + ".");
                return UnknownCotType;
            }
            return cotType;
        }

        /// <summary>Caps length and strips characters that have no business in a CoT attribute.
        /// WinTAK's CotItem is believed to XML-escape its input, but that is undocumented and
        /// unverified in the SDK; this makes the guarantee at our own boundary rather than
        /// depending on it (§3.8, the one finding that must be settled before delivery).</summary>
        internal static string SanitizeForCot(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
            var sb = new System.Text.StringBuilder(Math.Min(value.Length, maxLength));
            foreach (char c in value)
            {
                if (sb.Length >= maxLength) break;
                if (c == '\t' || c == '\n' || c == '\r') { sb.Append(' '); continue; }
                if (char.IsControl(c)) continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Removes a layer's markers from the map, via
        /// IMapItemFinderService.GetMapItem(uid) → WinTak.Graphics.MapItem.Dispose().</summary>
        private void RemoveLayerMarkers(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            List<string> uids;
            lock (_markerLock)
            {
                _layerMarkerUids.TryGetValue(url, out var stored);
                uids = stored != null ? new List<string>(stored) : new List<string>();
                _layerMarkerUids.Remove(url);
                foreach (var u in uids) _allMarkerUids.Remove(u);
            }

            int failures = 0;
            Exception first = null;
            foreach (var uid in uids)
            {
                try
                {
                    var item = _mapItemFinder?.GetMapItem(uid);
                    if (item != null && !item.IsDisposed) item.Dispose();
                }
                catch (Exception ex) { failures++; if (first == null) first = ex; }
            }
            if (failures > 0)
            {
                Log.Error($"Could not remove {failures} of {uids.Count} markers.", first);
                SetStatus($"Could not remove {failures} of {uids.Count} markers: {first?.Message}");
            }
        }

        // -------------------------------------------------------------------------
        // PLI layer setup
        // -------------------------------------------------------------------------

        private async Task OnCreatePliLayerAsync()
        {
            CancellationTokenSource cts = null;
            try
            {
                var ct = LinkedToken(OperationTimeout, out cts);
                string token = await _authService.GetTokenAsync(ct).ConfigureAwait(false);
                if (token == null)
                {
                    SetPliStatus("Sign in to your ArcGIS account before creating a PLI layer.");
                    return;
                }

                string name = (NewPliLayerName ?? string.Empty).Trim();
                if (name.Length > 200)
                {
                    SetPliStatus("Layer name is too long (200 characters maximum).");
                    return;
                }

                SetPliStatus("Creating feature service…");
                string serviceUrl = await _restClient.CreatePliFeatureServiceAsync(
                    _authService.PortalUrl, _authService.Username, token,
                    string.IsNullOrWhiteSpace(name) ? null : name, ct).ConfigureAwait(false);

                RunOnUi(() =>
                {
                    SetPliLayerUrl(serviceUrl);
                    NewPliJoinUrl = serviceUrl;
                    PliStatusText = $"Created: {serviceUrl}";
                });
            }
            catch (OperationCanceledException) { SetPliStatus("Creating the feature service timed out."); }
            catch (Exception ex)
            {
                Log.Error("PLI feature-service creation failed.", ex);
                SetPliStatus("Failed to create service: " + Describe(ex));
            }
            finally { cts?.Dispose(); }
        }

        private void OnJoinPliLayer()
        {
            string url = (NewPliJoinUrl ?? string.Empty).Trim();
            var check = UrlGuard.ValidateServiceUrl(url);
            if (!check.Ok)
            {
                PliStatusText = "Cannot join: " + check.Reason;
                return;
            }
            SetPliLayerUrl(url);
            PliStatusText = $"Joined: {url}";
        }

        private void SetPliLayerUrl(string url)
        {
            bool changed = !string.Equals(_pliLayerUrl, url, StringComparison.Ordinal);
            PliLayerUrl = url;
            lock (_pliObjectIdLock) _pliObjectId = -1;
            _consecutivePliFailures = 0;
            _lastPliSuccessUtc = DateTime.MinValue;
            RaisePropertyChanged(nameof(IsPliConnected));
            RaisePropertyChanged(nameof(StatusPliText));
            MaybeAutoCollapsePliLayerSection();
            SaveSettings();
            // Restart the timer so a switched layer starts reporting immediately rather than
            // waiting out the remainder of the previous tick against the old objectId.
            if (changed && PliAutoSendEnabled) StartPliTimer();
        }

        // -------------------------------------------------------------------------
        // PLI auto-send
        // -------------------------------------------------------------------------

        private void StartPliTimer()
        {
            StopPliTimer();
            _pliTimer = new Timer(_ => RunGuarded(SendPliUpdateAsync(), "PLI send"),
                null, TimeSpan.Zero, PliSendInterval);
        }

        private void StopPliTimer()
        {
            _pliTimer?.Dispose();
            _pliTimer = null;
        }

        /// <summary>
        /// C-27: this was <c>async void</c> fired from a <c>System.Threading.Timer</c>, with the
        /// first <c>await</c> sitting <b>outside</b> its own try block — any escaping exception
        /// was thrown on a thread-pool thread with no handler and terminated the WinTAK process.
        /// C-26: it had no re-entrancy guard, so on a slow link overlapping ticks raced
        /// <c>_pliObjectId</c> and produced duplicate PLI rows in the operator's feature layer.
        /// §3.5: the whole path ended in an empty catch, so the plugin could stop reporting
        /// position indefinitely with no indication anywhere — a mission-safety defect.
        /// </summary>
        private async Task SendPliUpdateAsync()
        {
            if (Interlocked.CompareExchange(ref _pliSendInFlight, 1, 0) != 0)
            {
                Log.Warn("Skipping a PLI tick — the previous send is still in flight.");
                return;
            }

            CancellationTokenSource cts = null;
            try
            {
                if (string.IsNullOrEmpty(PliLayerUrl)) return;
                var ct = LinkedToken(PliOperationTimeout, out cts);

                string token = await _authService.GetTokenAsync(ct).ConfigureAwait(false);
                if (token == null)
                {
                    RecordPliFailure("not signed in");
                    return;
                }

                var pos = _locationService.GetGpsPosition();
                if (pos == null) { Log.Info("PLI tick skipped — no GPS position yet."); return; }
                if (!ArcGisFeatureService.IsPlottable(pos.Latitude, pos.Longitude))
                {
                    RecordPliFailure("no valid GPS fix");
                    return;
                }

                var doc = _locationService.GetPositionDocument();
                string uid = doc?.DocumentElement?.GetAttribute("uid");
                var contactNode = doc?.SelectSingleNode("//contact") as System.Xml.XmlElement;
                string callsign = contactNode?.GetAttribute("callsign");
                if (string.IsNullOrEmpty(uid)) { Log.Info("PLI tick skipped — no self uid yet."); return; }
                if (string.IsNullOrEmpty(callsign)) callsign = uid;

                var selfEvent = _locationService.GetSelfCotEvent();
                string how = !string.IsNullOrEmpty(selfEvent?.How) ? selfEvent.How : "m-g";
                // Use the real self CoT type rather than a hardcoded ground-unit-combat: an
                // aircraft or vehicle self-marker was being reported as a-f-G-U-C.
                string cotType = ValidCotType(selfEvent?.Type);
                string groupName = selfEvent?.Detail?.GetDetailAttribute("__group", "name") ?? string.Empty;
                string groupRole = selfEvent?.Detail?.GetDetailAttribute("__group", "role") ?? string.Empty;

                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                // Staleness at 3× the send interval: a flat 30 s meant any jitter or missed tick
                // left the row stale before its replacement arrived.
                long stale = now + (long)(PliSendInterval.TotalMilliseconds * StaleIntervalMultiplier);
                string username = _authService.Username;
                // Real CE/LE instead of the hardcoded 0,0 the old code sent (a fabricated 0 m
                // error is worse than a null for a downstream consumer assessing position
                // quality). Sourced from WinTAK's own self CoT point — the same CotPoint type the
                // send-item path already reads CE90/LE90 from — because ILocationService's GPS
                // position struct exposes no accuracy fields in any reviewed SDK sample. NaN when
                // unavailable, which BuildPliAttributes serialises as JSON null.
                double ce = selfEvent?.Point?.CE90 ?? double.NaN;
                double le = selfEvent?.Point?.LE90 ?? double.NaN;

                long currentObjectId;
                lock (_pliObjectIdLock) currentObjectId = _pliObjectId;

                if (currentObjectId < 0)
                {
                    long objectId = await _restClient.AddPliFeatureAsync(
                        PliLayerUrl, token,
                        uid, cotType, callsign,
                        string.Empty, string.Empty, how, username,
                        groupName, groupRole,
                        pos.Latitude, pos.Longitude, pos.Altitude, ce, le,
                        now, now, stale, string.Empty, ct).ConfigureAwait(false);
                    if (objectId >= 0)
                    {
                        lock (_pliObjectIdLock) _pliObjectId = objectId;
                        RecordPliSuccess();
                        RunOnUi(SaveSettings);
                    }
                    else
                    {
                        RecordPliFailure("the service rejected the position row");
                    }
                }
                else
                {
                    bool updated = await _restClient.UpdatePliFeatureAsync(
                        PliLayerUrl, token, currentObjectId,
                        uid, cotType, callsign,
                        string.Empty, string.Empty, how, username,
                        groupName, groupRole,
                        pos.Latitude, pos.Longitude, pos.Altitude, ce, le,
                        now, now, stale, string.Empty, ct).ConfigureAwait(false);
                    if (updated)
                    {
                        RecordPliSuccess();
                    }
                    else
                    {
                        // The row is gone; forget the objectId so the next tick re-adds it.
                        lock (_pliObjectIdLock) _pliObjectId = -1;
                        RecordPliFailure("the position row no longer exists — it will be recreated");
                        RunOnUi(SaveSettings);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                RecordPliFailure("the send timed out");
            }
            catch (Exception ex)
            {
                Log.Error("PLI send failed.", ex);
                RecordPliFailure(Describe(ex));
            }
            finally
            {
                cts?.Dispose();
                Interlocked.Exchange(ref _pliSendInFlight, 0);
            }
        }

        private void RecordPliSuccess()
        {
            _consecutivePliFailures = 0;
            _lastPliSuccessUtc = DateTime.UtcNow;
            RunOnUi(() =>
            {
                PliStatusText = string.Format(CultureInfo.CurrentCulture,
                    "Last position sent {0:HH:mm:ss} UTC", _lastPliSuccessUtc);
                RaisePropertyChanged(nameof(IsPliConnected));
                RaisePropertyChanged(nameof(StatusPliText));
            });
        }

        private void RecordPliFailure(string reason)
        {
            int failures = Interlocked.Increment(ref _consecutivePliFailures);
            Log.Warn($"PLI send failed ({failures} consecutive): {reason}");
            RunOnUi(() =>
            {
                PliStatusText = $"Position not sent ({failures} consecutive failures): {reason}";
                RaisePropertyChanged(nameof(IsPliConnected));
                RaisePropertyChanged(nameof(StatusPliText));
            });
        }

        // -------------------------------------------------------------------------
        // "Send Item to Layer"
        // -------------------------------------------------------------------------

        private void OnPreviewCotBroadcast(object sender, CoTMessageArgument e)
        {
            try
            {
                var evt = e?.CotEvent;
                if (evt == null || string.IsNullOrEmpty(evt.Uid)) return;

                // O(1) membership test on the flattened uid set; this used to be a linear scan
                // across every marker on WinTAK's CoT pipeline thread for every outgoing message.
                lock (_markerLock) { if (_allMarkerUids.Contains(evt.Uid)) return; }

                string selfUid = CachedSelfUid();
                if (!string.IsNullOrEmpty(selfUid) && string.Equals(evt.Uid, selfUid, StringComparison.Ordinal)) return;

                string callsign = evt.Detail?.GetFirstChildByName("contact")?.GetAttribute("callsign");
                if (string.IsNullOrEmpty(callsign)) callsign = evt.Uid;
                string cotType = evt.Type;

                RunOnUi(() =>
                {
                    var existing = RecentCotItems.FirstOrDefault(i =>
                        string.Equals(i.Uid, evt.Uid, StringComparison.Ordinal));
                    if (existing != null)
                    {
                        existing.Callsign = callsign;
                        existing.CotType = cotType;
                        existing.LastSeen = DateTime.Now;
                        existing.LastEvent = evt;
                        int index = RecentCotItems.IndexOf(existing);
                        if (index > 0) RecentCotItems.Move(index, 0);
                    }
                    else
                    {
                        RecentCotItems.Insert(0, new RecentCotItem
                        {
                            Uid = evt.Uid,
                            Callsign = callsign,
                            CotType = cotType,
                            LastSeen = DateTime.Now,
                            LastEvent = evt,
                        });
                        while (RecentCotItems.Count > RecentCotItemsCap)
                            RecentCotItems.RemoveAt(RecentCotItems.Count - 1);
                    }
                });
            }
            catch (Exception ex)
            {
                // This runs on WinTAK's CoT pipeline thread; an escaping exception there is not
                // ours to throw.
                Log.Warn("CoT preview handler failed: " + ex.Message);
            }
        }

        private string _cachedSelfUid;
        private DateTime _cachedSelfUidAtUtc = DateTime.MinValue;

        /// <summary>The self uid was fetched from the SDK on every single broadcast, inside a bare
        /// catch, on a latency-sensitive thread. Cached for 30 s instead.</summary>
        private string CachedSelfUid()
        {
            if (DateTime.UtcNow - _cachedSelfUidAtUtc < TimeSpan.FromSeconds(30)) return _cachedSelfUid;
            try { _cachedSelfUid = _locationService.GetSelfCotEvent()?.Uid; }
            catch { _cachedSelfUid = null; }
            _cachedSelfUidAtUtc = DateTime.UtcNow;
            return _cachedSelfUid;
        }

        private async Task OnSendItemToLayerAsync(RecentCotItem item)
        {
            if (item?.LastEvent == null) return;
            if (string.IsNullOrEmpty(PliLayerUrl))
            {
                SetStatus("Configure a PLI Feature Layer on the Home tab first.");
                return;
            }

            CancellationTokenSource cts = null;
            try
            {
                var ct = LinkedToken(OperationTimeout, out cts);
                string token = await _authService.GetTokenAsync(ct).ConfigureAwait(false);
                var evt = item.LastEvent;
                var pt = evt.Point;
                if (pt == null) { SetStatus("That item has no position to send."); return; }

                string icon = evt.Detail?.GetDetailAttribute("usericon", "iconsetpath") ?? string.Empty;
                string remarks = evt.Detail?.GetFirstChildByName("remarks")?.InnerText ?? string.Empty;
                string how = !string.IsNullOrEmpty(evt.How) ? evt.How : "h-g-i-g-o";
                string groupName = evt.Detail?.GetDetailAttribute("__group", "name") ?? string.Empty;
                string groupRole = evt.Detail?.GetDetailAttribute("__group", "role") ?? string.Empty;
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long stale = now + (long)SentItemStaleness.TotalMilliseconds;

                long objectId = await _restClient.AddPliFeatureAsync(
                    PliLayerUrl, token,
                    evt.Uid, ValidCotType(evt.Type), item.Callsign,
                    icon, remarks, how, _authService.Username,
                    groupName, groupRole,
                    pt.Latitude, pt.Longitude, pt.Altitude, pt.CE90, pt.LE90,
                    now, now, stale, string.Empty, ct).ConfigureAwait(false);

                SetStatus(objectId >= 0
                    ? $"Sent to Feature Layer: {item.Callsign}"
                    : "The feature layer rejected that item.");
            }
            catch (OperationCanceledException) { SetStatus("Sending to the feature layer timed out."); }
            catch (Exception ex)
            {
                Log.Error("Send-to-layer failed.", ex);
                SetStatus($"Failed to send to Feature Layer: {Describe(ex)}");
            }
            finally { cts?.Dispose(); }
        }

        // -------------------------------------------------------------------------
        // Layer recurrence (auto-refresh)
        // -------------------------------------------------------------------------

        private void StartRecurrenceTimer()
        {
            _recurrenceTimer = new Timer(_ => RunGuarded(CheckLayerRecurrenceAsync(), "auto-refresh"),
                null, RecurrenceCheckInterval, RecurrenceCheckInterval);
        }

        /// <summary>C-26/C-27: was <c>async void</c> from a timer with no re-entrancy guard, and it
        /// enumerated two <c>ObservableCollection</c>s on the timer thread while the UI thread
        /// could be mutating them — an <c>InvalidOperationException</c> on a thread-pool thread
        /// from an <c>async void</c>, i.e. a process crash.</summary>
        private async Task CheckLayerRecurrenceAsync()
        {
            if (Interlocked.CompareExchange(ref _recurrenceInFlight, 1, 0) != 0) return;
            try
            {
                var now = DateTime.UtcNow;
                // Snapshot on the dispatcher; ObservableCollection is not thread-safe.
                var due = await RunOnUiAsync(() => SharedPrivateLayers.Concat(PublicLayers)
                    .Where(l => l.LastSyncTicks > 0
                        && l.RecurrenceTimeSpan() > TimeSpan.Zero
                        && (now - l.LastSync) >= l.RecurrenceTimeSpan())
                    .ToList()).ConfigureAwait(false);

                foreach (var layer in due)
                {
                    if (_lifetime.IsCancellationRequested) return;
                    // interactive:false — a scheduled refresh must not raise a modal dialog.
                    await DownloadLayerAsync(layer, interactive: false).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Error("Auto-refresh pass failed.", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _recurrenceInFlight, 0);
            }
        }

        // -------------------------------------------------------------------------
        // Settings persistence
        // -------------------------------------------------------------------------

        private void LoadSettings()
        {
            var settings = SettingsStore.Load();
            PrivateLayers.Clear();
            foreach (var l in settings.PrivateLayers) PrivateLayers.Add(l);
            PublicLayers.Clear();
            foreach (var l in settings.PublicLayers) PublicLayers.Add(l);
            SharedPrivateLayers.Clear();
            foreach (var l in settings.SharedPrivateLayers) SharedPrivateLayers.Add(l);

            _excludedPrivateLayerUrls.Clear();
            foreach (var u in settings.ExcludedPrivateLayerUrls) _excludedPrivateLayerUrls.Add(u);

            _pliLayerUrl = settings.Pli.PliLayerUrl;
            _pliObjectId = settings.Pli.PliObjectId;
            _pliAutoSendEnabled = settings.Pli.AutoSendEnabled;
            if (!string.IsNullOrEmpty(_pliLayerUrl)) NewPliJoinUrl = _pliLayerUrl;

            // Also collapse when the layer came from persisted settings rather than only from a
            // fresh Join/Create, which is what ATAK's parity target does.
            MaybeAutoCollapsePliLayerSection();

            RaisePropertyChanged(nameof(PliLayerUrl));
            RaisePropertyChanged(nameof(IsPliConfigured));
            RaisePropertyChanged(nameof(IsPliConnected));
            RaisePropertyChanged(nameof(PliAutoSendEnabled));
            RaisePropertyChanged(nameof(StatusPliText));
            RaisePropertyChanged(nameof(StatusAutoSendText));
            RaisePropertyChanged(nameof(StatusLayersText));
            RaisePropertyChanged(nameof(ShowSharedPrivateLayersCard));
            RaisePropertyChanged(nameof(ShowSetPliEndpointButton));
        }

        private void SaveSettings()
        {
            long objectId;
            lock (_pliObjectIdLock) objectId = _pliObjectId;

            var settings = new SettingsStore.FeatureLinkSettings
            {
                PrivateLayers = PrivateLayers.ToList(),
                PublicLayers = PublicLayers.ToList(),
                SharedPrivateLayers = SharedPrivateLayers.ToList(),
                ExcludedPrivateLayerUrls = _excludedPrivateLayerUrls.ToList(),
                PortalUrl = _authService.PortalUrl,
                Pli = new PliSettings
                {
                    PliLayerUrl = PliLayerUrl,
                    PliObjectId = objectId,
                    AutoSendEnabled = PliAutoSendEnabled,
                },
            };

            // SettingsStore.Save had no try/catch at all, and is called from WPF property setters,
            // command handlers and the PLI timer — a read-only profile or an AV lock produced a
            // WinTAK crash dialog or an unobserved timer exception.
            if (!SettingsStore.TrySave(settings, out var error))
            {
                Log.Error("Could not save settings.", error);
                RunOnUi(() => StatusText = "Could not save settings: " + error.Message);
            }
        }

        // -------------------------------------------------------------------------
        // Misc helpers
        // -------------------------------------------------------------------------

        private void RunOnUi(Action action)
        {
            var dispatcher = _dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted) { return; }
            if (dispatcher.CheckAccess()) action();
            else dispatcher.Invoke(action);
        }

        private Task<T> RunOnUiAsync<T>(Func<T> func)
        {
            var dispatcher = _dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) return Task.FromResult(func());
            return dispatcher.InvokeAsync(func).Task;
        }

        private void RaisePropertyChanged(string propertyName) =>
            OnPropertyChanged(new PropertyChangedEventArgs(propertyName));

        // ── IDisposable ──────────────────────────────────────────────────────────

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (!disposing) return;

            try { _lifetime.Cancel(); } catch { }

            StopPliTimer();
            _recurrenceTimer?.Dispose();
            _recurrenceTimer = null;

            if (_shareFileWatcher != null)
            {
                _shareFileWatcher.EnableRaisingEvents = false;
                _shareFileWatcher.Created -= OnShareFileCreated;
                _shareFileWatcher.Error -= OnShareWatcherError;
                _shareFileWatcher.Dispose();
                _shareFileWatcher = null;
            }

            if (_communicationService != null)
                _communicationService.PreviewCotBroadcast -= OnPreviewCotBroadcast;

            _lifetime.Dispose();
            Log.Info("FeatureLink dock pane disposed.");

            // The log writer is held open for the life of the pane (see Log.AppendToFile).
            // Release it here so a disabled/reloaded plugin does not keep a handle on the file.
            Log.Shutdown();
        }
    }
}
