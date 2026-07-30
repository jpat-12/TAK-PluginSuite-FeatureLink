using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
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

namespace FeatureLink.ViewModels
{
    /// <summary>
    /// Dock-pane view model for FeatureLink — ported from FeatureLinkDropDownReceiver.java's
    /// Home / Layers / PLI 3-tab state machine, including its "pushed full-panel overlay" pattern
    /// for the Account and Add Layer pages (main_layout.xml's overlay_container) and each card's
    /// collapse/expand chevron (page_home.xml's stats_content, page_layers.xml's
    /// private/public_layers_content, page_pli.xml's pli_layer_content).
    ///
    /// QR code sharing/scanning, the radial-menu "send to layer" injection, deep-link import from
    /// TAK Portal, and DisplayConfig symbology mapping are explicitly out of scope for this pass
    /// — see the project README for where those cards are still present in the UI (disabled) vs.
    /// omitted entirely.
    /// </summary>
    [DockPane(ID, "FeatureLink", Content = typeof(FeatureLinkView))]
    [Export(typeof(IDockPane))]
    public class FeatureLinkDockPane : DockPane, IDisposable
    {
        internal const string ID = "FeatureLink_FeatureLinkDockPane";

        // How often the PLI auto-send ticks — matches ATAK's
        // pliScheduler.scheduleAtFixedRate(this::sendPliUpdate, 0, 30, TimeUnit.SECONDS).
        private static readonly TimeSpan PliSendInterval = TimeSpan.FromSeconds(30);

        // How often the layer-recurrence check runs. ATAK re-evaluates every layer's
        // recurrenceMillis() once at plugin start (checkLayerRecurrence()); polling here keeps
        // long-running WinTAK sessions honoring each layer's configured refresh interval without
        // requiring a plugin reload.
        private static readonly TimeSpan RecurrenceCheckInterval = TimeSpan.FromSeconds(15);

        // ── Injected services ────────────────────────────────────────────────────

        private readonly ICotMessageSender _cotSender;
        private readonly ILocationService _locationService;
        private readonly ICommunicationService _communicationService;
        private readonly IMapItemFinderService _mapItemFinder;
        private readonly WinTak.Net.Contacts.IContactService _contactService;
        private readonly ArcGisAuthService _authService = new ArcGisAuthService();
        private readonly ArcGisFeatureService _restClient = new ArcGisFeatureService();

        private Timer _pliTimer;
        private Timer _recurrenceTimer;
        private FileSystemWatcher _shareFileWatcher;

        /// <summary>UIDs of markers this pane has injected onto the map for a given layer URL,
        /// so a re-download can be tracked. WinTAK's CoT pipeline treats a repeated Process()
        /// call with the same uid as an update-in-place; there is no documented
        /// "remove marker by uid" call in this SDK's samples, so shrinking feature sets are not
        /// pruned from the map automatically — see README "Known limitations".</summary>
        private readonly Dictionary<string, List<string>> _layerMarkerUids =
            new Dictionary<string, List<string>>();

        // -------------------------------------------------------------------------
        // Tab navigation (main_layout.xml tab_home / tab_layers / tab_pli + page_container)
        // -------------------------------------------------------------------------

        private int _currentTabIndex;
        /// <summary>0 = Home, 1 = Layers, 2 = PLI — mirrors currentPage in FeatureLinkDropDownReceiver.</summary>
        public int CurrentTabIndex
        {
            get => _currentTabIndex;
            private set { _currentTabIndex = value; RaisePropertyChanged(nameof(CurrentTabIndex)); }
        }

        public ICommand NavigateHomeCommand { get; }
        public ICommand NavigateLayersCommand { get; }
        public ICommand NavigatePliCommand { get; }

        private void NavigatePage(int page)
        {
            if (page < 0 || page > 2) return;
            CurrentTabIndex = page;
            if (page == 1) RefreshLayerCollapsedHints();
        }

        // -------------------------------------------------------------------------
        // Overlay pages (Account, Add Layer) — main_layout.xml's overlay_container
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
        private void HideOverlay() => IsOverlayVisible = false;

        // -------------------------------------------------------------------------
        // Home tab — status card + collapsible feature-statistics card
        // -------------------------------------------------------------------------

        public bool IsAuthenticated => _authService.IsAuthenticated;
        public string AuthStatusText => IsAuthenticated
            ? $"Signed in as: {_authService.Username}"
            : "Not signed in";

        /// <summary>page_home.xml's status_account_text.</summary>
        public string StatusAccountText => IsAuthenticated ? "Signed In" : "Not Signed In";

        /// <summary>A PLI destination is "connected" when configured and this session can send to
        /// it — mirrors isPliConnected() in FeatureLinkDropDownReceiver.</summary>
        public bool IsPliConnected => IsPliConfigured && IsAuthenticated;
        public string StatusPliText => IsPliConnected ? "Connected" : "Not Configured";
        public string StatusAutoSendText => PliAutoSendEnabled ? "On" : "Off";
        /// <summary>On-device counts only — PrivateLayers is now a pure "My ArcGIS Layers"
        /// browse list (see MoveMyArcGisLayerOnDownload), not itself on-device content.</summary>
        public string StatusLayersText => $"{SharedPrivateLayers.Count} private, {PublicLayers.Count} public";

        /// <summary>page_home.xml's set_pli_endpoint_btn — visible only until a PLI layer is configured.</summary>
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

        /// <summary>Formatted "name [type] — N features" rows — mirrors buildStatStrings().</summary>
        public ObservableCollection<string> LayerStats { get; } = new ObservableCollection<string>();

        /// <summary>FeatureLink's own substitute for ATAK's radial-menu "Send to Feature Layer"
        /// — see RecentCotItem's summary for why. Populated from
        /// ICommunicationService.PreviewCotBroadcast, capped at RecentCotItemsCap.</summary>
        public ObservableCollection<RecentCotItem> RecentCotItems { get; } = new ObservableCollection<RecentCotItem>();
        public ICommand SendItemToLayerCommand { get; }
        private const int RecentCotItemsCap = 30;

        // -------------------------------------------------------------------------
        // Layers tab — page_layers.xml
        // -------------------------------------------------------------------------

        /// <summary>"My ArcGIS Layers" — a pure browse list of the signed-in user's own ArcGIS
        /// content. Items here are NOT on-device; downloading one moves it into SharedPrivateLayers
        /// or PublicLayers via MoveMyArcGisLayerOnDownload() and removes it from this list.</summary>
        public ObservableCollection<ArcGisLayer> PrivateLayers { get; } = new ObservableCollection<ArcGisLayer>();
        public ObservableCollection<ArcGisLayer> PublicLayers { get; } = new ObservableCollection<ArcGisLayer>();
        /// <summary>"Private Layers" — on-device layers not shared to Everyone: either received
        /// via a share from another user, or moved here from "My ArcGIS Layers" on first
        /// download. Card is hidden entirely when this is empty (see ShowSharedPrivateLayersCard).</summary>
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

        /// <summary>Whole "Private Layers" card hidden when empty — mirrors
        /// refreshSharedPrivateLayers()'s sharedPrivateLayersCard visibility toggle.</summary>
        public bool ShowSharedPrivateLayersCard => SharedPrivateLayers.Count > 0;

        private bool _publicLayersExpanded = true;
        public bool PublicLayersExpanded
        {
            get => _publicLayersExpanded;
            private set { _publicLayersExpanded = value; RaisePropertyChanged(nameof(PublicLayersExpanded)); }
        }
        public ICommand TogglePublicLayersCommand { get; }
        private void TogglePublicLayers() => PublicLayersExpanded = !PublicLayersExpanded;

        /// <summary>Gear menu (top-right header) — "Clear All Layers".</summary>
        public ICommand ClearAllLayersCommand { get; }

        private void RefreshLayerCollapsedHints() { /* placeholder parity hook — ATAK re-renders lists on tab entry */ }

        private string _newPublicLayerUrl = string.Empty;
        public string NewPublicLayerUrl
        {
            get => _newPublicLayerUrl;
            set { _newPublicLayerUrl = value; RaisePropertyChanged(nameof(NewPublicLayerUrl)); }
        }

        private readonly HashSet<string> _excludedPrivateLayerUrls = new HashSet<string>();

        // -------------------------------------------------------------------------
        // PLI tab — page_pli.xml
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

        /// <summary>Collapses the "PLI Feature Layer" card the first time it's found connected —
        /// mirrors maybeAutoCollapsePliLayerSection() in FeatureLinkDropDownReceiver.</summary>
        private void MaybeAutoCollapsePliLayerSection()
        {
            if (_pliAutoCollapseDone || !IsPliConnected) return;
            _pliAutoCollapseDone = true;
            PliLayerExpanded = false;
        }

        private bool _isPliJoinMode;
        /// <summary>True = "Join Existing Layer" radio selected, false = "Create New Layer" — mirrors
        /// pli_radio_group's create_layer_rb / join_layer_rb.</summary>
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
        /// <summary>Read/write inverse of <see cref="IsPliJoinMode"/> so both RadioButtons in the
        /// pair can each be bound TwoWay (a plain computed getter-only property can't be a
        /// RadioButton's IsChecked target).</summary>
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

        // ── Commands ─────────────────────────────────────────────────────────────

        public ICommand SignInCommand { get; }
        public ICommand SignOutCommand { get; }
        public ICommand RefreshLayersCommand { get; }
        public ICommand AddPublicLayerCommand { get; }
        public ICommand UploadDisplayPrefsCommand { get; }
        public ICommand DownloadLayerCommand { get; }
        public ICommand RemoveLayerCommand { get; }
        public ICommand ShareLayerCommand { get; }
        public ICommand ToggleLayerVisibilityCommand { get; }
        public ICommand PliActionCommand { get; }

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
            StartShareFileWatcher();
            _communicationService.PreviewCotBroadcast += OnPreviewCotBroadcast;

            NavigateHomeCommand = new DelegateCommand(() => NavigatePage(0));
            NavigateLayersCommand = new DelegateCommand(() => NavigatePage(1));
            NavigatePliCommand = new DelegateCommand(() => NavigatePage(2));

            ShowAccountPageCommand = new DelegateCommand(ShowAccountPage);
            ShowAddLayerPageCommand = new DelegateCommand(ShowAddLayerPage);
            HideOverlayCommand = new DelegateCommand(HideOverlay);
            SetPliEndpointCommand = new DelegateCommand(() => NavigatePage(2));

            ToggleStatsCommand = new DelegateCommand(ToggleStats);
            TogglePrivateLayersCommand = new DelegateCommand(TogglePrivateLayers);
            ToggleSharedPrivateLayersCommand = new DelegateCommand(ToggleSharedPrivateLayers);
            TogglePublicLayersCommand = new DelegateCommand(TogglePublicLayers);
            ClearAllLayersCommand = new DelegateCommand(OnClearAllLayers);
            TogglePliLayerCommand = new DelegateCommand(TogglePliLayer);

            SignInCommand = new DelegateCommand(async () => await OnSignInAsync(), () => !IsAuthenticated);
            SignOutCommand = new DelegateCommand(OnSignOut, () => IsAuthenticated);
            RefreshLayersCommand = new DelegateCommand(async () => await OnRefreshLayersAsync());
            AddPublicLayerCommand = new DelegateCommand(async () => await OnAddPublicLayerAsync());
            UploadDisplayPrefsCommand = new DelegateCommand(OnUploadDisplayPrefs);
            SendItemToLayerCommand = new DelegateCommand<RecentCotItem>(async item => await OnSendItemToLayerAsync(item));
            DownloadLayerCommand = new DelegateCommand<ArcGisLayer>(async layer => await OnDownloadLayerAsync(layer));
            RemoveLayerCommand = new DelegateCommand<ArcGisLayer>(OnRemoveLayer);
            ShareLayerCommand = new DelegateCommand<ArcGisLayer>(OnShareLayer);
            ToggleLayerVisibilityCommand = new DelegateCommand<ArcGisLayer>(OnToggleLayerVisibility);
            PliActionCommand = new DelegateCommand(async () =>
            {
                if (IsPliJoinMode) OnJoinPliLayer(); else await OnCreatePliLayerAsync();
            });

            LoadSettings();
            StartRecurrenceTimer();
            if (_pliAutoSendEnabled) StartPliTimer();

            if (IsAuthenticated)
            {
                // Fire-and-forget refresh of the private layer list at startup, same as ATAK's
                // fetchUserLayers() call from loadSavedData() when a saved session is present.
                _ = OnRefreshLayersAsync();
            }
        }

        // -------------------------------------------------------------------------
        // Sign-in / sign-out
        // -------------------------------------------------------------------------

        private async Task OnSignInAsync()
        {
            try
            {
                StatusText = "Signing in…";
                await _authService.SignInAsync("https://www.arcgis.com").ConfigureAwait(false);
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
                RunOnUi(() => StatusText = $"Sign-in failed: {ex.Message}");
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
        }

        // -------------------------------------------------------------------------
        // Layers
        // -------------------------------------------------------------------------

        private async Task OnRefreshLayersAsync()
        {
            if (!IsAuthenticated) return;
            string token = await _authService.GetTokenAsync().ConfigureAwait(false);
            if (token == null)
            {
                RunOnUi(() => StatusText = "Session expired — please sign in again.");
                return;
            }

            StatusText = "Loading layers…";
            var layers = await _restClient.SearchUserLayersAsync(_authService.PortalUrl, token, _authService.Username)
                .ConfigureAwait(false);

            RunOnUi(() =>
            {
                var preserved = PrivateLayers.ToDictionary(l => l.Url, l => l);
                // Already downloaded onto this device (see MoveMyArcGisLayerOnDownload) — it now
                // lives in SharedPrivateLayers or PublicLayers, not the browse list.
                var onDevice = new HashSet<string>(
                    SharedPrivateLayers.Select(l => l.Url).Concat(PublicLayers.Select(l => l.Url)));
                PrivateLayers.Clear();
                foreach (var layer in layers)
                {
                    if (_excludedPrivateLayerUrls.Contains(layer.Url) || onDevice.Contains(layer.Url)) continue;
                    layer.Type = "private";
                    if (preserved.TryGetValue(layer.Url, out var old))
                    {
                        layer.DownloadEnabled = old.DownloadEnabled;
                        layer.RecurrenceInterval = old.RecurrenceInterval;
                        layer.RecurrenceUnit = old.RecurrenceUnit;
                        layer.LastSyncTicks = old.LastSyncTicks;
                        layer.Visible = old.Visible;
                    }
                    PrivateLayers.Add(layer);
                }
                StatusText = $"Loaded {PrivateLayers.Count} private layer(s).";
                RaisePropertyChanged(nameof(StatusLayersText));
                SaveSettings();
            });

            await RefreshFeatureCountsAsync().ConfigureAwait(false);
        }

        private async Task RefreshFeatureCountsAsync()
        {
            string token = await _authService.GetTokenAsync().ConfigureAwait(false);
            long total = 0;
            var stats = new List<string>();
            // PrivateLayers ("My ArcGIS Layers") is excluded — it's a pure browse list of items
            // not yet downloaded onto this device (see MoveMyArcGisLayerOnDownload), so querying
            // and reporting a feature count for something the user hasn't asked to sync yet would
            // be misleading in the Home tab's stats. Only on-device layers count here.
            foreach (var layer in SharedPrivateLayers.Concat(PublicLayers).ToList())
            {
                try
                {
                    long count = await _restClient.QueryFeatureCountAsync(layer.Url,
                        layer.Type == "private" ? token : null).ConfigureAwait(false);
                    layer.FeatureCount = count;
                    total += count;
                    stats.Add($"{layer.Name}  [{layer.Type}]  —  {count} features");
                }
                catch
                {
                    stats.Add($"{layer.Name}  [{layer.Type}]  —  error");
                }
            }
            RunOnUi(() =>
            {
                TotalFeatureCount = total;
                LayerStats.Clear();
                foreach (var s in stats) LayerStats.Add(s);
            });
        }

        private async Task OnAddPublicLayerAsync()
        {
            string url = (NewPublicLayerUrl ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(url))
            {
                StatusText = "Enter a Feature Service URL.";
                return;
            }

            var layer = await _restClient.FetchLayerInfoAsync(url).ConfigureAwait(false);
            if (layer == null)
            {
                RunOnUi(() => StatusText = "Could not load layer from URL.");
                return;
            }

            layer.Type = "public";
            RunOnUi(() =>
            {
                PublicLayers.Add(layer);
                NewPublicLayerUrl = string.Empty;
                RaisePropertyChanged(nameof(StatusLayersText));
                HideOverlay();
                NavigatePage(1);
                SaveSettings();
            });
            await DownloadLayerAsync(layer).ConfigureAwait(false);
        }

        /// <summary>Add Layer page's "Upload Display Prefs (JSON)" — manual counterpart to the
        /// Mission Package folder watcher (StartShareFileWatcher/ImportFeatureLinkShareAsync):
        /// same file shape, just picked from disk instead of arriving via a Mission Package.</summary>
        private void OnUploadDisplayPrefs()
        {
            var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Filter = "FeatureLink share (*.featurelinkshare;*.json)|*.featurelinkshare;*.json|All files (*.*)|*.*",
                Title = "Upload Display Prefs",
            };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            _ = ImportFeatureLinkShareAsync(dialog.FileName);
        }

        /// <summary>
        /// Watches WinTAK's own Mission Package extraction folder for an incoming
        /// ".featurelinkshare" share (ATAK's sendLayerShare() naming). WinTAK's core handles the
        /// accept-prompt/extraction/auto-import-attempt chain itself — there's no plugin hook for
        /// that step the way ATAK's FeatureLinkMarshal/FeatureLinkImporter register into ATAK's
        /// own import pipeline, and ICommunicationService.FileTransferred (tried first) turned
        /// out not to fire for server-relayed Mission Package content in a live test. Watching the
        /// real extraction folder directly sidesteps needing that event at all.
        ///
        /// Not ".featurelink.json" (the original naming, still plain JSON either way) — WinTAK's
        /// own auto-import chain tries a GRG (Gridded Reference Graphic) importer against any
        /// unrecognized ".json" attachment inside an accepted package, which throws an unhandled
        /// NullReferenceException in WinTak.Common.Coords.MGRSPoint.decodeString on our payload
        /// instead of just skipping it. A non-geo-looking extension keeps that importer away.
        /// </summary>
        private void StartShareFileWatcher()
        {
            try
            {
                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "WinTAK", "attachments");
                Directory.CreateDirectory(root);

                _shareFileWatcher = new FileSystemWatcher(root, "*.featurelinkshare")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                };
                _shareFileWatcher.Created += OnShareFileCreated;
                _shareFileWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                RunOnUi(() => StatusText = $"Could not watch for shared layers: {ex.Message}");
            }
        }

        private void OnShareFileCreated(object sender, FileSystemEventArgs e)
        {
            _ = ImportFeatureLinkShareAsync(e.FullPath);
        }

        /// <summary>The Created event can fire while WinTAK's own extraction/GDAL-import-attempt
        /// still holds the file open — retry briefly instead of failing on the first IOException.</summary>
        private static async Task<string> ReadFileWithRetryAsync(string path)
        {
            const int maxAttempts = 6;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    return File.ReadAllText(path);
                }
                catch (IOException) when (attempt < maxAttempts)
                {
                    await Task.Delay(250).ConfigureAwait(false);
                }
            }
            return File.ReadAllText(path);
        }

        /// <summary>
        /// Applies a received layer-share file — the WinTAK counterpart of ATAK's
        /// importFeatureLinkShareUri()/applyScannedDisplayConfig() url branch. Only the bare
        /// url/name/private/freq fields are used; sym/lbl/popup styling in the payload is
        /// ignored, matching this port's existing DisplayConfig-symbology-mapping scope cut (see
        /// README "What's deferred") — the layer still downloads and renders as CoT markers with
        /// default styling.
        /// </summary>
        private async Task ImportFeatureLinkShareAsync(string path)
        {
            try
            {
                string json = await ReadFileWithRetryAsync(path).ConfigureAwait(false);
                var o = Newtonsoft.Json.Linq.JObject.Parse(json);

                string url = (string)o["url"];
                if (string.IsNullOrEmpty(url))
                {
                    RunOnUi(() => StatusText = "Received a FeatureLink file with no layer URL.");
                    return;
                }

                bool isPrivate = (bool?)o["private"] ?? false;
                string name = (string)o["layer"]?["name"];
                int freqInterval = (int?)o["freq"]?["iv"] ?? 0;
                string freqUnit = (string)o["freq"]?["u"] ?? "s";
                var symObj = o["sym"] as Newtonsoft.Json.Linq.JObject;
                var lblObj = o["lbl"] as Newtonsoft.Json.Linq.JObject;
                var popupObj = o["popup"] as Newtonsoft.Json.Linq.JObject;
                bool hasDisplayConfig = symObj != null || lblObj != null
                    || popupObj != null || o["cm"] != null;

                if (SharedPrivateLayers.Any(l => l.Url == url) || PublicLayers.Any(l => l.Url == url))
                {
                    RunOnUi(() => StatusText = $"Layer already in your list: {name ?? url}");
                    return;
                }

                RunOnUi(() => StatusText = $"Receiving shared layer: {name ?? url}…");

                var layer = await _restClient.FetchLayerInfoAsync(url).ConfigureAwait(false);
                if (layer == null)
                {
                    RunOnUi(() => StatusText = "Could not load the shared layer.");
                    return;
                }

                if (!string.IsNullOrEmpty(name)) layer.Name = name;
                layer.Type = isPrivate ? "private" : "public";
                layer.RecurrenceInterval = freqInterval;
                layer.RecurrenceUnit = freqUnit;
                layer.HasDisplayConfig = hasDisplayConfig;
                layer.SymJson = symObj?.ToString();
                layer.LblJson = lblObj?.ToString();
                layer.PopupJson = popupObj?.ToString();

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
            catch (Exception ex)
            {
                RunOnUi(() => StatusText = $"Failed to import shared layer: {ex.Message}");
            }
        }

        /// <summary>Share button — mirrors ATAK's onLayerShare()/sendLayerShare(). Picks a
        /// contact via ContactPickerWindow, builds the same ".featurelinkshare" JSON shape
        /// LayerShareHelper.java produces (reusing this layer's own SymJson/LblJson/PopupJson
        /// verbatim, so styling round-trips the same way ATAK's share does), and sends it as a
        /// Mission Package via ICommunicationService.SendMissionPackage — found reflecting the
        /// SDK assemblies (no reviewed sample exercises it, same as the receive-side folder
        /// watcher this mirrors).</summary>
        private void OnShareLayer(ArcGisLayer layer)
        {
            if (layer == null || _contactService == null) return;

            var contacts = new List<ContactPickerWindow.ContactRow>();
            foreach (var c in _contactService.AllContacts ?? Enumerable.Empty<Contact>())
            {
                if (c == null || string.IsNullOrEmpty(c.Uid)) continue;
                contacts.Add(new ContactPickerWindow.ContactRow(c.Name ?? c.Uid, c.Uid));
            }
            if (contacts.Count == 0)
            {
                StatusText = "No contacts available to share with.";
                return;
            }

            var picker = new ContactPickerWindow(contacts) { Owner = Application.Current?.MainWindow };
            if (picker.ShowDialog() != true || string.IsNullOrEmpty(picker.SelectedUid)) return;

            try
            {
                string json = BuildShareConfigJson(layer);
                string safeName = System.Text.RegularExpressions.Regex.Replace(layer.Name ?? "layer", "[^a-zA-Z0-9 _-]", "_");

                string dir = Path.Combine(Path.GetTempPath(), "FeatureLinkShare");
                Directory.CreateDirectory(dir);
                string filePath = Path.Combine(dir, safeName + ".featurelinkshare");
                File.WriteAllText(filePath, json);

                _communicationService.SendMissionPackage(
                    new List<string> { picker.SelectedUid },
                    new FileInfo(filePath),
                    "FeatureLink - " + safeName,
                    false);

                StatusText = $"Sent \"{layer.Name}\" to contact.";
            }
            catch (Exception ex)
            {
                StatusText = $"Failed to share layer: {ex.Message}";
            }
        }

        /// <summary>Same compact ".featurelinkshare" shape LayerShareHelper.java produces —
        /// v/url/layer{name,opacity,visible}/private/freq, plus this layer's own sym/lbl/popup
        /// JSON merged in verbatim if present (already in the exact wire shape, since it either
        /// came from a received share or — not yet possible on this port — a config source).</summary>
        private string BuildShareConfigJson(ArcGisLayer layer)
        {
            var layerObj = new Newtonsoft.Json.Linq.JObject
            {
                ["name"] = layer.Name,
                ["opacity"] = 1.0,
                ["visible"] = true,
            };
            var o = new Newtonsoft.Json.Linq.JObject
            {
                ["v"] = 2,
                ["url"] = layer.Url,
                ["layer"] = layerObj,
                ["private"] = layer.Type == "private",
            };
            if (layer.RecurrenceInterval > 0)
            {
                o["freq"] = new Newtonsoft.Json.Linq.JObject
                {
                    ["iv"] = layer.RecurrenceInterval,
                    ["u"] = layer.RecurrenceUnit,
                };
            }
            var sym = ParseJsonOrNull(layer.SymJson, "sym");
            if (sym != null) o["sym"] = sym;
            var lbl = ParseJsonOrNull(layer.LblJson, "lbl");
            if (lbl != null) o["lbl"] = lbl;
            var popup = ParseJsonOrNull(layer.PopupJson, "popup");
            if (popup != null) o["popup"] = popup;
            return o.ToString();
        }

        /// <summary>Mirrors confirmRemovePublicLayer()/onLayerDelete()'s AlertDialog confirmation —
        /// this previously removed the layer immediately on click with no confirmation at all.</summary>
        private void OnRemoveLayer(ArcGisLayer layer)
        {
            if (layer == null) return;

            bool isMyArcGis = PrivateLayers.Contains(layer);
            bool isSharedPrivate = SharedPrivateLayers.Contains(layer);
            string message = (isMyArcGis || isSharedPrivate)
                ? $"Remove \"{layer.Name}\" from your layer list here? It stays in your ArcGIS account — this only hides it on this device. Any markers it added to the map will also be removed."
                : $"Remove \"{layer.Name}\" from your layer list? Any markers it added to the map will also be removed.";
            var result = MessageBox.Show(message, "Remove Layer?", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            if (isMyArcGis)
            {
                PrivateLayers.Remove(layer);
                _excludedPrivateLayerUrls.Add(layer.Url);
            }
            else if (isSharedPrivate)
            {
                SharedPrivateLayers.Remove(layer);
            }
            else
            {
                PublicLayers.Remove(layer);
            }
            RemoveLayerMarkers(layer);
            RaisePropertyChanged(nameof(StatusLayersText));
            RaisePropertyChanged(nameof(ShowSharedPrivateLayersCard));
            SaveSettings();
        }

        /// <summary>Gear menu's "Clear All Layers" — wipes all three sections (My ArcGIS Layers
        /// browse list, Private Layers, Public Layers), removes every layer's markers from the
        /// map, and clears the excluded-URL list so a subsequent sign-in/Refresh repopulates "My
        /// ArcGIS Layers" from scratch rather than still hiding previously-removed items.</summary>
        private void OnClearAllLayers()
        {
            var result = MessageBox.Show(
                "Remove every layer from My ArcGIS Layers, Private Layers, and Public Layers, and "
                    + "clear their markers from the map? This only affects this device — nothing "
                    + "in your ArcGIS account is deleted.",
                "Clear All Layers?", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            foreach (var url in _layerMarkerUids.Keys.ToList()) RemoveLayerMarkers(url);

            PrivateLayers.Clear();
            SharedPrivateLayers.Clear();
            PublicLayers.Clear();
            _excludedPrivateLayerUrls.Clear();

            RaisePropertyChanged(nameof(StatusLayersText));
            RaisePropertyChanged(nameof(ShowSharedPrivateLayersCard));
            StatusText = "Cleared all layers.";
            SaveSettings();
        }

        /// <summary>item_layer.xml's layer_eye_icon toggle — mirrors toggleLayerVisibility().</summary>
        /// <summary>item_layer.xml's layer_eye_icon toggle — mirrors toggleLayerVisibility().
        /// Now flips already-posted markers live via WinTak.Graphics.MapItem.Visible (found
        /// reflecting the SDK assemblies for the marker-removal fix — see RemoveLayerMarkers()),
        /// instead of only gating whether a feature gets (re-)posted on the next download.</summary>
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
            if (!_layerMarkerUids.TryGetValue(url, out var uids)) return;
            foreach (var uid in uids)
            {
                try
                {
                    var item = _mapItemFinder?.GetMapItem(uid);
                    if (item != null && !item.IsDisposed) item.Visible = visible;
                }
                catch (Exception ex)
                {
                    RunOnUi(() => StatusText = $"Could not set visibility for {uid}: {ex.Message}");
                }
            }
        }

        /// <summary>Download/sync button handler — mirrors onLayerAction()'s private branch.
        /// Downloading a "My ArcGIS Layers" browse item for the first time moves it onto the
        /// device before downloading it.</summary>
        private async Task OnDownloadLayerAsync(ArcGisLayer layer)
        {
            if (layer == null) return;
            if (PrivateLayers.Contains(layer)) MoveMyArcGisLayerOnDownload(layer);
            await DownloadLayerAsync(layer).ConfigureAwait(false);
        }

        /// <summary>Moves a "My ArcGIS Layers" browse-list item onto the device once its
        /// download/sync button is tapped, landing it in SharedPrivateLayers or PublicLayers
        /// depending on whether the ArcGIS item is shared to Everyone (ArcGisLayer.Access) —
        /// from then on it behaves like any other on-device layer in that section instead of
        /// staying in the browse list.</summary>
        private void MoveMyArcGisLayerOnDownload(ArcGisLayer layer)
        {
            PrivateLayers.Remove(layer);
            if (layer.Access == "public")
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

        private async Task DownloadLayerAsync(ArcGisLayer layer)
        {
            if (layer == null) return;
            string token = layer.Type == "private" ? await _authService.GetTokenAsync().ConfigureAwait(false) : null;

            try
            {
                var features = await _restClient.DownloadLayerAsCotAsync(layer.Url, token).ConfigureAwait(false);
                layer.LastSync = DateTime.UtcNow;

                Newtonsoft.Json.Linq.JObject sym = ParseJsonOrNull(layer.SymJson, "sym");
                Newtonsoft.Json.Linq.JObject lbl = ParseJsonOrNull(layer.LblJson, "lbl");
                Newtonsoft.Json.Linq.JObject popup = ParseJsonOrNull(layer.PopupJson, "popup");

                // Uids present last download but not this one — the source feature disappeared
                // server-side. Diffed here (rather than just overwriting _layerMarkerUids) so
                // their markers get removed instead of lingering on the map forever.
                var previousUids = _layerMarkerUids.TryGetValue(layer.Url, out var prev)
                    ? new HashSet<string>(prev) : new HashSet<string>();

                var uids = new List<string>(features.Count);
                foreach (var f in features)
                {
                    string iconsetPath = sym != null ? DisplayStyleResolver.ResolveIconsetPath(sym, f.Attributes) : null;
                    Color? color = sym != null ? DisplayStyleResolver.ResolveColor(sym, f.Attributes) : (Color?)null;
                    string label = lbl != null ? DisplayStyleResolver.ResolveLabel(lbl, f.Attributes, f.Callsign) : f.Callsign;
                    string remarks = popup != null ? DisplayStyleResolver.BuildRemarks(popup, f.Attributes) : f.Remarks;
                    PostFeatureAsCot(f, layer.Visible, iconsetPath, color, label, remarks);
                    uids.Add(f.Uid);
                    previousUids.Remove(f.Uid);
                }
                _layerMarkerUids[layer.Url] = uids;

                foreach (var vanishedUid in previousUids)
                {
                    try
                    {
                        var item = _mapItemFinder?.GetMapItem(vanishedUid);
                        if (item != null && !item.IsDisposed) item.Dispose();
                    }
                    catch { /* best-effort cleanup */ }
                }

                RunOnUi(() =>
                {
                    StatusText = $"Downloaded: {layer.Name} ({features.Count} features)";
                    SaveSettings();
                });
            }
            catch (Exception ex)
            {
                RunOnUi(() => StatusText = $"Download failed for {layer.Name}: {ex.Message}");
            }
        }

        private static Newtonsoft.Json.Linq.JObject ParseJsonOrNull(string json, string label)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return Newtonsoft.Json.Linq.JObject.Parse(json); }
            catch { return null; } // corrupt/old persisted JSON — fall back to no styling for this piece
        }

        /// <summary>Converts one downloaded ArcGIS feature into a CoT event and injects it onto
        /// the WinTAK map — the CoT-building/Process() pattern here mirrors
        /// ImageSyncDockPane.PostCotMarker in the ImageFolderSync sample. label/remarks override
        /// f.Callsign/f.Remarks when a DisplayStyleResolver.ResolveLabel()/BuildRemarks() result
        /// is available; color is applied post-creation via WinTak.Graphics.MapMarker.Color
        /// (found reflecting the SDK for the marker-removal/visibility fixes — there's no CoT
        /// wire-detail equivalent to ATAK's Marker.setColor(), so this mutates the live map item
        /// directly instead, the same pattern as RemoveLayerMarkers()/SetLayerMarkersVisible()).</summary>
        private void PostFeatureAsCot(DownloadedFeature f, bool visible, string iconsetPath = null,
            Color? color = null, string label = null, string remarks = null)
        {
            var now = DateTime.UtcNow;
            // Long staleness — these are periodically re-synced/re-posted on the layer's own
            // recurrence interval (see CheckLayerRecurrence), not meant to visibly go "stale"
            // between refreshes the way a live PLI track would.
            var stale = now.AddDays(365);

            var geoPoint = double.IsNaN(f.Hae)
                ? new GeoPoint(f.Lat, f.Lon)
                : new GeoPoint(f.Lat, f.Lon, f.Hae, AltitudeReference.HAE);
            var cotPoint = new CotPoint(geoPoint);

            var detail = new CotDetail();
            var contact = new CotItem("contact");
            contact.SetAttribute("callsign", label ?? f.Callsign);
            detail.AddChild(contact);
            string remarksText = remarks ?? f.Remarks;
            if (!string.IsNullOrEmpty(remarksText))
            {
                var remarksItem = new CotItem("remarks") { InnerText = remarksText };
                detail.AddChild(remarksItem);
            }
            if (!string.IsNullOrEmpty(iconsetPath))
            {
                // Standard ATAK/WinTAK custom-icon CoT detail — matches
                // com.atakmap.android.icons.UserIcon.IconsetPath's serialized shape on the ATAK
                // side (FeatureLinkDropDownReceiver.downloadLayer()). Requires the referenced
                // iconset to already be installed on this device (e.g. bundled alongside the
                // share Mission Package, or via AUTO-ICONSET-SPEC.md's federated generation) —
                // if it isn't, WinTAK falls back to the default CoT-type icon, same as ATAK would.
                var usericon = new CotItem("usericon");
                usericon.SetAttribute("iconsetpath", iconsetPath);
                detail.AddChild(usericon);
            }

            var cotEvent = new CotEvent(
                uid: f.Uid,
                type: string.IsNullOrEmpty(f.CotType) ? "a-f-G" : f.CotType,
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

            // Always post (create) the marker, then set its live Visible via
            // IMapItemFinderService — previously this skipped Process() entirely when hidden,
            // which meant a layer downloaded while hidden had nothing for a later "show" toggle
            // to reveal. WinTak.Graphics.MapItem.Visible (found for the marker-removal fix) lets
            // us create-then-hide instead.
            _cotSender.Process(cotEvent);
            if (!visible || color.HasValue)
            {
                try
                {
                    var item = _mapItemFinder?.GetMapItem(f.Uid);
                    if (item != null && !item.IsDisposed)
                    {
                        if (!visible) item.Visible = false;
                        if (color.HasValue && item is WinTak.Graphics.MapMarker marker) marker.Color = color.Value;
                    }
                }
                catch { /* best-effort — marker still exists, just may keep default visibility/color */ }
            }
        }

        /// <summary>
        /// Removes a layer's markers from the map, via WinTak.Common.Services.
        /// IMapItemFinderService.GetMapItem(uid) → WinTak.Graphics.MapItem.Dispose() — the actual
        /// "remove this item" call (found by reflecting the SDK's assemblies; no reviewed SDK
        /// sample exercises it, but the type shape is unambiguous: MapItem implements IDisposable
        /// specifically for this). Replaces an earlier attempt that re-posted each uid with an
        /// already-past stale time hoping WinTAK's own staleness sweep would clean it up — that
        /// didn't reliably remove markers in a live test, so this calls the direct API instead.
        /// </summary>
        private void RemoveLayerMarkers(ArcGisLayer layer)
        {
            if (layer == null) return;
            RemoveLayerMarkers(layer.Url);
        }

        private void RemoveLayerMarkers(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            if (_layerMarkerUids.TryGetValue(url, out var uids))
            {
                foreach (var uid in uids)
                {
                    try
                    {
                        var item = _mapItemFinder?.GetMapItem(uid);
                        if (item != null && !item.IsDisposed) item.Dispose();
                    }
                    catch (Exception ex)
                    {
                        RunOnUi(() => StatusText = $"Could not remove marker {uid}: {ex.Message}");
                    }
                }
            }
            _layerMarkerUids.Remove(url);
        }

        // -------------------------------------------------------------------------
        // PLI layer setup
        // -------------------------------------------------------------------------

        private async Task OnCreatePliLayerAsync()
        {
            string token = await _authService.GetTokenAsync().ConfigureAwait(false);
            if (token == null) return;

            PliStatusText = "Creating feature service…";
            string serviceUrl = await _restClient.CreatePliFeatureServiceAsync(
                _authService.PortalUrl, _authService.Username, token,
                string.IsNullOrWhiteSpace(NewPliLayerName) ? null : NewPliLayerName.Trim()).ConfigureAwait(false);

            RunOnUi(() =>
            {
                if (serviceUrl != null)
                {
                    SetPliLayerUrl(serviceUrl);
                    NewPliJoinUrl = serviceUrl;
                    PliStatusText = $"Created: {serviceUrl}";
                }
                else
                {
                    PliStatusText = "Failed to create service.";
                }
            });
        }

        private void OnJoinPliLayer()
        {
            string url = (NewPliJoinUrl ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(url))
            {
                PliStatusText = "Enter a Feature Layer URL.";
                return;
            }
            SetPliLayerUrl(url);
            PliStatusText = $"Joined: {url}";
        }

        private void SetPliLayerUrl(string url)
        {
            PliLayerUrl = url;
            _pliObjectId = -1;
            MaybeAutoCollapsePliLayerSection();
            SaveSettings();
        }

        // -------------------------------------------------------------------------
        // PLI auto-send
        // -------------------------------------------------------------------------

        private void StartPliTimer()
        {
            StopPliTimer();
            _pliTimer = new Timer(_ => SendPliUpdate(), null, TimeSpan.Zero, PliSendInterval);
        }

        private void StopPliTimer()
        {
            _pliTimer?.Dispose();
            _pliTimer = null;
        }

        private async void SendPliUpdate()
        {
            if (string.IsNullOrEmpty(PliLayerUrl)) return;
            string token = await _authService.GetTokenAsync().ConfigureAwait(false);
            if (token == null) return;

            try
            {
                // ILocationService (WinTak.Common.Services) is the real WinTAK GPS/self-position
                // service — its shape here (PositionChanged event, GetGpsPosition(),
                // HasConnections, GetPositionDocument()) is inferred from the VideoStream SDK
                // sample (VideoStreamDockPane.cs), which is the only sample that touches
                // self-location. It exposes no team/group-color or "how" directly, but
                // GetSelfCotEvent() returns WinTAK's own actual self CoT event — its <__group>
                // detail and How carry the same information ATAK's self marker meta strings do,
                // so those are pulled from there instead of being left blank.
                var pos = _locationService.GetGpsPosition();
                if (pos == null) return;

                var doc = _locationService.GetPositionDocument();
                string uid = doc?.DocumentElement?.GetAttribute("uid");
                var contactNode = doc?.SelectSingleNode("//contact") as System.Xml.XmlElement;
                string callsign = contactNode?.GetAttribute("callsign");
                if (string.IsNullOrEmpty(uid)) return;
                if (string.IsNullOrEmpty(callsign)) callsign = uid;

                var selfEvent = _locationService.GetSelfCotEvent();
                string how = !string.IsNullOrEmpty(selfEvent?.How) ? selfEvent.How : "m-g";
                string groupName = selfEvent?.Detail?.GetDetailAttribute("__group", "name") ?? string.Empty;
                string groupRole = selfEvent?.Detail?.GetDetailAttribute("__group", "role") ?? string.Empty;

                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string username = _authService.Username;

                if (_pliObjectId < 0)
                {
                    long objectId = await _restClient.AddPliFeatureAsync(
                        PliLayerUrl, token,
                        uid, "a-f-G-U-C", callsign,
                        string.Empty, string.Empty, how, username,
                        groupName, groupRole,
                        pos.Latitude, pos.Longitude, pos.Altitude, 0, 0,
                        now, now, now + 30_000L, string.Empty).ConfigureAwait(false);
                    if (objectId >= 0)
                    {
                        _pliObjectId = objectId;
                        RunOnUi(SaveSettings);
                    }
                }
                else
                {
                    bool updated = await _restClient.UpdatePliFeatureAsync(
                        PliLayerUrl, token, _pliObjectId,
                        uid, "a-f-G-U-C", callsign,
                        string.Empty, string.Empty, how, username,
                        groupName, groupRole,
                        pos.Latitude, pos.Longitude, pos.Altitude, 0, 0,
                        now, now, now + 30_000L, string.Empty).ConfigureAwait(false);
                    if (!updated)
                    {
                        _pliObjectId = -1;
                        RunOnUi(SaveSettings);
                    }
                }
            }
            catch
            {
                // Non-fatal — same tolerance as ATAK's sendPliUpdate() catch-and-log; next tick
                // retries on its own.
            }
        }

        // -------------------------------------------------------------------------
        // "Send Item to Layer" — own substitute for ATAK's radial-menu SEND_TO_LAYER
        // -------------------------------------------------------------------------

        /// <summary>Observes every CoT message this WinTAK instance is about to broadcast — self
        /// position updates, user-placed markers, anything — and keeps a capped recent-items list
        /// so the user can pick one to send to the PLI layer from our own panel instead of a
        /// native radial menu (see RecentCotItem's summary for why this exists).</summary>
        private void OnPreviewCotBroadcast(object sender, CoTMessageArgument e)
        {
            var evt = e?.CotEvent;
            if (evt == null || string.IsNullOrEmpty(evt.Uid)) return;
            // Skip our own layer-downloaded markers — sending one of those back to the PLI layer
            // would just be a noisy, pointless round-trip of data that already came from ArcGIS.
            if (_layerMarkerUids.Values.Any(list => list.Contains(evt.Uid))) return;
            // Skip this device's own self-position track — it re-broadcasts frequently and would
            // dominate/clutter this list, and it already has its own dedicated path (PLI
            // Auto-Send, straight to the layer every 30s with no user action needed). This list
            // is for arbitrary plotted points, mirroring what ATAK's radial-menu "Send to Feature
            // Layer" is actually for.
            string selfUid = null;
            try { selfUid = _locationService.GetSelfCotEvent()?.Uid; } catch { /* GPS not ready yet */ }
            if (!string.IsNullOrEmpty(selfUid) && evt.Uid == selfUid) return;

            string callsign = evt.Detail?.GetFirstChildByName("contact")?.GetAttribute("callsign");
            if (string.IsNullOrEmpty(callsign)) callsign = evt.Uid;

            RunOnUi(() =>
            {
                var existing = RecentCotItems.FirstOrDefault(i => i.Uid == evt.Uid);
                if (existing != null)
                {
                    existing.Callsign = callsign;
                    existing.CotType = evt.Type;
                    existing.LastSeen = DateTime.Now;
                    existing.LastEvent = evt;
                    // Bump to the top so the most recently active items surface first.
                    RecentCotItems.Move(RecentCotItems.IndexOf(existing), 0);
                }
                else
                {
                    RecentCotItems.Insert(0, new RecentCotItem
                    {
                        Uid = evt.Uid,
                        Callsign = callsign,
                        CotType = evt.Type,
                        LastSeen = DateTime.Now,
                        LastEvent = evt,
                    });
                    while (RecentCotItems.Count > RecentCotItemsCap)
                        RecentCotItems.RemoveAt(RecentCotItems.Count - 1);
                }
            });
        }

        /// <summary>Sends a recently-observed item's current position/attributes to the
        /// configured PLI layer — mirrors handleSendToLayer()'s addPliFeature() call exactly
        /// (always a plain add, never tracked for update, since this is a one-time snapshot of
        /// someone else's item rather than this device's own continuously-updated position).</summary>
        private async Task OnSendItemToLayerAsync(RecentCotItem item)
        {
            if (item?.LastEvent == null) return;
            if (string.IsNullOrEmpty(PliLayerUrl))
            {
                StatusText = "Configure a PLI Feature Layer on the Home tab first.";
                return;
            }

            string token = await _authService.GetTokenAsync().ConfigureAwait(false);
            var evt = item.LastEvent;
            var pt = evt.Point;
            if (pt == null) return;

            string icon = evt.Detail?.GetDetailAttribute("usericon", "iconsetpath") ?? "";
            string remarks = evt.Detail?.GetFirstChildByName("remarks")?.InnerText ?? "";
            string how = !string.IsNullOrEmpty(evt.How) ? evt.How : "h-g-i-g-o";
            string groupName = evt.Detail?.GetDetailAttribute("__group", "name") ?? "";
            string groupRole = evt.Detail?.GetDetailAttribute("__group", "role") ?? "";
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            try
            {
                long objectId = await _restClient.AddPliFeatureAsync(
                    PliLayerUrl, token,
                    evt.Uid, evt.Type, item.Callsign,
                    icon, remarks, how, _authService.Username,
                    groupName, groupRole,
                    pt.Latitude, pt.Longitude, pt.Altitude, pt.CE90, pt.LE90,
                    now, now, now + 7 * 24 * 60 * 60 * 1000L, string.Empty).ConfigureAwait(false);
                RunOnUi(() => StatusText = objectId >= 0
                    ? $"Sent to Feature Layer: {item.Callsign}"
                    : "Failed to send to Feature Layer.");
            }
            catch (Exception ex)
            {
                RunOnUi(() => StatusText = $"Failed to send to Feature Layer: {ex.Message}");
            }
        }

        // -------------------------------------------------------------------------
        // Layer recurrence (auto-refresh)
        // -------------------------------------------------------------------------

        private void StartRecurrenceTimer()
        {
            _recurrenceTimer = new Timer(_ => CheckLayerRecurrence(), null,
                RecurrenceCheckInterval, RecurrenceCheckInterval);
        }

        private async void CheckLayerRecurrence()
        {
            var now = DateTime.UtcNow;
            // PrivateLayers ("My ArcGIS Layers") is excluded entirely — it's a pure browse list
            // now, not on-device content (see MoveMyArcGisLayerOnDownload). LastSyncTicks == 0
            // means never manually downloaded yet — skip it here rather than treating "never
            // synced" as "infinitely overdue" and auto-downloading every layer right after
            // sign-in, before the user has asked for any of them (same fix already made in
            // CloudTAK's scheduler.ts). Auto-refresh only kicks in once a layer's had its first
            // manual download via the sync/download button.
            var due = SharedPrivateLayers.Concat(PublicLayers)
                .Where(l => l.LastSyncTicks > 0
                    && l.RecurrenceTimeSpan() > TimeSpan.Zero
                    && (now - l.LastSync) >= l.RecurrenceTimeSpan())
                .ToList();
            foreach (var layer in due)
                await DownloadLayerAsync(layer).ConfigureAwait(false);
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
                    PliObjectId = _pliObjectId,
                    AutoSendEnabled = PliAutoSendEnabled,
                },
            };
            SettingsStore.Save(settings);
        }

        // -------------------------------------------------------------------------
        // Misc helpers
        // -------------------------------------------------------------------------

        private static void RunOnUi(Action action)
        {
            if (Application.Current?.Dispatcher == null) { action(); return; }
            if (Application.Current.Dispatcher.CheckAccess()) action();
            else Application.Current.Dispatcher.Invoke(action);
        }

        private void RaisePropertyChanged(string propertyName) =>
            OnPropertyChanged(new PropertyChangedEventArgs(propertyName));

        // ── IDisposable ──────────────────────────────────────────────────────────

        public void Dispose()
        {
            StopPliTimer();
            _recurrenceTimer?.Dispose();
            _recurrenceTimer = null;
            if (_shareFileWatcher != null)
            {
                _shareFileWatcher.EnableRaisingEvents = false;
                _shareFileWatcher.Created -= OnShareFileCreated;
                _shareFileWatcher.Dispose();
                _shareFileWatcher = null;
            }
            _communicationService.PreviewCotBroadcast -= OnPreviewCotBroadcast;
        }
    }
}
