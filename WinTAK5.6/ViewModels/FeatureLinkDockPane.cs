using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
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
        public ICommand DownloadLayerCommand { get; }
        public ICommand RemoveLayerCommand { get; }
        public ICommand ToggleLayerVisibilityCommand { get; }
        public ICommand PliActionCommand { get; }

        // ── Constructor ──────────────────────────────────────────────────────────

        [ImportingConstructor]
        public FeatureLinkDockPane(ICotMessageSender cotSender, ILocationService locationService,
            ICommunicationService communicationService)
        {
            _cotSender = cotSender;
            _locationService = locationService;
            _communicationService = communicationService;
            StartShareFileWatcher();

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
            TogglePliLayerCommand = new DelegateCommand(TogglePliLayer);

            SignInCommand = new DelegateCommand(async () => await OnSignInAsync(), () => !IsAuthenticated);
            SignOutCommand = new DelegateCommand(OnSignOut, () => IsAuthenticated);
            RefreshLayersCommand = new DelegateCommand(async () => await OnRefreshLayersAsync());
            AddPublicLayerCommand = new DelegateCommand(async () => await OnAddPublicLayerAsync());
            DownloadLayerCommand = new DelegateCommand<ArcGisLayer>(async layer => await OnDownloadLayerAsync(layer));
            RemoveLayerCommand = new DelegateCommand<ArcGisLayer>(OnRemoveLayer);
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
            foreach (var layer in PrivateLayers.Concat(SharedPrivateLayers).Concat(PublicLayers).ToList())
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

        /// <summary>
        /// Watches WinTAK's own Mission Package extraction folder for an incoming
        /// ".featurelink.json" share (ATAK's sendLayerShare() naming). WinTAK's core handles the
        /// accept-prompt/extraction/GDAL-import-attempt chain itself — there's no plugin hook for
        /// that step the way ATAK's FeatureLinkMarshal/FeatureLinkImporter register into ATAK's
        /// own import pipeline, and ICommunicationService.FileTransferred (tried first) turned
        /// out not to fire for server-relayed Mission Package content in a live test — WinTAK
        /// just logs a "GDAL: not recognized as a supported file format" warning and moves on.
        /// Watching the real extraction folder directly sidesteps needing that event at all.
        /// </summary>
        private void StartShareFileWatcher()
        {
            try
            {
                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "WinTAK", "attachments");
                Directory.CreateDirectory(root);

                _shareFileWatcher = new FileSystemWatcher(root, "*.featurelink.json")
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
                bool hasDisplayConfig = o["sym"] != null || o["lbl"] != null
                    || o["popup"] != null || o["cm"] != null;

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

        /// <summary>item_layer.xml's layer_eye_icon toggle — mirrors toggleLayerVisibility().</summary>
        private void OnToggleLayerVisibility(ArcGisLayer layer)
        {
            if (layer == null) return;
            layer.Visible = !layer.Visible;
            SaveSettings();
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

                var uids = new List<string>(features.Count);
                foreach (var f in features)
                {
                    PostFeatureAsCot(f, layer.Visible);
                    uids.Add(f.Uid);
                }
                _layerMarkerUids[layer.Url] = uids;

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

        /// <summary>Converts one downloaded ArcGIS feature into a CoT event and injects it onto
        /// the WinTAK map — the CoT-building/Process() pattern here mirrors
        /// ImageSyncDockPane.PostCotMarker in the ImageFolderSync sample.</summary>
        private void PostFeatureAsCot(DownloadedFeature f, bool visible)
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
            contact.SetAttribute("callsign", f.Callsign);
            detail.AddChild(contact);
            if (!string.IsNullOrEmpty(f.Remarks))
            {
                var remarks = new CotItem("remarks") { InnerText = f.Remarks };
                detail.AddChild(remarks);
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

            // NOTE: unlike ATAK's Marker.setVisible(), there is no documented per-item visibility
            // toggle for a CoT-sourced map item in this SDK's samples — "Visible" here only
            // controls whether the item is (re-)posted at all on the next download/refresh.
            // TODO: revisit if/when a WinTAK item-visibility API is confirmed.
            if (visible) _cotSender.Process(cotEvent);
        }

        /// <summary>
        /// Removes a layer's markers from the map. There's no documented "remove map item by
        /// uid" call in any reviewed SDK sample (see README "Known limitations" — this is the
        /// same gap noted for shrinking feature sets), so instead of removing the item directly
        /// this re-posts each of the layer's known uids with an already-past stale time — the
        /// standard CoT-protocol expiry convention, which any compliant CoT consumer (including
        /// WinTAK's own map engine, which ingests self-posted events through the same pipeline as
        /// ATAK/WinTAK/CloudTAK
        /// devices' events) treats as "expire this immediately." Avoids needing an unconfirmed
        /// item-removal API at all.
        /// </summary>
        private void RemoveLayerMarkers(ArcGisLayer layer)
        {
            if (layer == null) return;
            if (_layerMarkerUids.TryGetValue(layer.Url, out var uids))
            {
                var now = DateTime.UtcNow;
                var expired = new CoordinatedTime(now.AddMinutes(-1));
                foreach (var uid in uids)
                {
                    var cotEvent = new CotEvent(
                        uid: uid,
                        type: "a-f-G",
                        vers: CotEvent.VERSION_2_0,
                        point: new CotPoint(new GeoPoint(0, 0)),
                        time: new CoordinatedTime(now),
                        start: new CoordinatedTime(now),
                        stale: expired,
                        how: CotEvent.HOW_MACHINE_GENERATED,
                        detail: null,
                        opex: null,
                        qos: null,
                        access: null);
                    _cotSender.Process(cotEvent);
                }
            }
            _layerMarkerUids.Remove(layer.Url);
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
                // TODO(confirmed-but-partial): ILocationService (WinTak.Common.Services) is the
                // real WinTAK GPS/self-position service — its shape here (PositionChanged event,
                // GetGpsPosition(), HasConnections, GetPositionDocument()) is inferred from the
                // VideoStream SDK sample (VideoStreamDockPane.cs), which is the only sample that
                // touches self-location. No sample exposes team/group-color or "how" the way
                // ATAK's self marker meta strings do, so those PLI fields are left blank below
                // rather than guessed at.
                var pos = _locationService.GetGpsPosition();
                if (pos == null) return;

                var doc = _locationService.GetPositionDocument();
                string uid = doc?.DocumentElement?.GetAttribute("uid");
                var contactNode = doc?.SelectSingleNode("//contact") as System.Xml.XmlElement;
                string callsign = contactNode?.GetAttribute("callsign");
                if (string.IsNullOrEmpty(uid)) return;
                if (string.IsNullOrEmpty(callsign)) callsign = uid;

                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string username = _authService.Username;

                if (_pliObjectId < 0)
                {
                    long objectId = await _restClient.AddPliFeatureAsync(
                        PliLayerUrl, token,
                        uid, "a-f-G-U-C", callsign,
                        string.Empty, string.Empty, "m-g", username,
                        string.Empty, string.Empty,
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
                        string.Empty, string.Empty, "m-g", username,
                        string.Empty, string.Empty,
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
        }
    }
}
