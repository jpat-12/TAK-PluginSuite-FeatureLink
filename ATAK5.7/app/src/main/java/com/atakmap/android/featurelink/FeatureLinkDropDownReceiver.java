package com.atakmap.android.featurelink;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.BroadcastReceiver;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.graphics.Bitmap;
import android.graphics.Color;
import android.net.Uri;
import android.os.Handler;
import android.os.Looper;
import android.view.GestureDetector;
import android.view.MotionEvent;
import android.view.View;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ListView;
import android.widget.RadioButton;
import android.widget.RadioGroup;
import android.widget.TextView;
import android.widget.Toast;
import android.webkit.WebView;
import android.webkit.WebViewClient;

import android.view.LayoutInflater;
import com.atakmap.android.contact.Contact;
import com.atakmap.android.contact.Contacts;
import com.atakmap.android.contact.IndividualContact;
import com.atakmap.android.dropdown.DropDown;
import com.atakmap.android.dropdown.DropDownReceiver;
import com.atakmap.android.featurelink.arcgis.ArcGISAuthManager;
import com.atakmap.android.featurelink.arcgis.ArcGISLayer;
import com.atakmap.android.featurelink.arcgis.ArcGISRestClient;
import com.atakmap.android.featurelink.arcgis.AutoIconset;
import com.atakmap.android.featurelink.arcgis.AutoSymbology;
import com.atakmap.android.featurelink.plugin.R;
import com.atakmap.android.ipc.AtakBroadcast;
import com.atakmap.android.maps.MapGroup;
import com.atakmap.android.maps.MapItem;
import com.atakmap.android.maps.MapView;
import com.atakmap.android.maps.Marker;
import com.atakmap.android.maps.PointMapItem;
import com.atakmap.android.maps.Polyline;
import com.atakmap.android.missionpackage.api.MissionPackageApi;
import com.atakmap.android.missionpackage.api.ToastSaveCallback;
import com.atakmap.android.missionpackage.file.MissionPackageManifest;
import android.util.Log;
import com.atakmap.coremap.maps.coords.GeoPoint;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.File;
import java.io.FileWriter;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;

public class FeatureLinkDropDownReceiver extends DropDownReceiver
        implements DropDown.OnStateListener {

    public static final String TAG = "FeatureLinkDropDown";

    public static final String SHOW_PLUGIN    = "com.atakmap.android.featurelink.SHOW_PLUGIN";
    public static final String SEND_TO_LAYER  = "com.atakmap.android.featurelink.SEND_TO_LAYER";
    /** From ImportConfigActivity's featurelink://import deep link — carries "config" (JSON string) extra. */
    public static final String IMPORT_CONFIG  = "com.atakmap.android.featurelink.IMPORT_CONFIG";

    private static final String PREFS_NAME              = "featurelink_prefs";
    private static final String PREF_PLI_LAYER_URL      = "pli_layer_url";
    private static final String PREF_PLI_OBJECT_ID      = "pli_object_id";
    private static final String PREF_PLI_AUTO_SEND      = "pli_auto_send";
    private static final String PREF_PORTAL_URL         = "portal_url";
    private static final String PREF_LAYERS_JSON        = "layers_json";
    private static final String PREF_EXCLUDED_PRIVATE_URLS = "excluded_private_layer_urls";
    private static final String PREF_DISPLAY_CONFIGS_JSON = "display_configs_json";
    private static final String PREF_PUBLIC_LAYERS_JSON = "public_layers_json";
    private static final String PREF_SHARED_PRIVATE_LAYERS_JSON = "shared_private_layers_json";
    private static final String PREF_SHARED_WITH_ME_LAYERS_JSON = "shared_with_me_layers_json";
    private static final String PREF_SECTION_PRIVATE_EXPANDED = "section_private_expanded";
    private static final String PREF_SECTION_SHARED_PRIVATE_EXPANDED = "section_shared_private_expanded";
    private static final String PREF_SECTION_PUBLIC_EXPANDED = "section_public_expanded";

    /** Request code for the "Upload Pref File" system file picker — matched against the
     * "requestCode" extra on ATAK's "com.atakmap.android.ACTIVITY_FINISHED" broadcast, which is
     * how a plugin gets an Activity result back (see prefFileResultReceiver). */
    private static final int PREF_FILE_PICK_REQUEST_CODE = 41217;

    private final Context pluginContext;
    private final ArcGISAuthManager authManager;
    private final ArcGISRestClient restClient;
    private final SharedPreferences prefs;
    private final ExecutorService executor = Executors.newFixedThreadPool(2);
    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private ScheduledExecutorService pliScheduler;
    private BroadcastReceiver prefFileResultReceiver;

    // Main view and page navigation (0=HOME, 1=LAYERS, 2=PLI)
    private View mainView;
    private FrameLayout pageContainer;
    private View homePageView, layersPageView, pliPageView;
    private Button tabHome, tabLayers, tabPli;
    private int currentPage = 0;

    // Home page — statistics
    private TextView totalCountText;
    private ListView layerStatsList;
    private Button setPliEndpointBtn;
    private LinearLayout statsContent;
    private android.widget.ImageButton collapseStatsBtn;
    private boolean statsExpanded = true;

    // Home page — quick-glance status card
    private TextView statusAccountText, statusPliText, statusAutoSendText, statusLayersText;

    // Overlay — full-screen pushed pages (Account, Add Layer)
    private FrameLayout overlayContainer;
    private View accountPageView, addLayerPageView;
    private android.widget.ImageButton accountBtn;
    private android.widget.ImageButton settingsBtn;

    // Account page — credentials
    private Button ssoLoginBtn, logoutBtn;
    private TextView authStatusText;

    // Add Layer page
    private Button scanLayerQrBtn, addPublicLayerBtn, uploadPrefBtn;

    // OAuth WebView overlay (lives in main_layout, covers everything during sign-in)
    private FrameLayout oauthWebViewContainer;
    private View oauthKeyboardListenerTarget;
    private android.view.ViewTreeObserver.OnGlobalLayoutListener oauthKeyboardListener;


    // PLI page
    private TextView pliFeatureLayerTitle;
    private LinearLayout pliLayerContent;
    private android.widget.ImageButton collapsePliLayerBtn;
    private boolean pliLayerExpanded = true;
    private boolean pliAutoCollapseDone = false;
    private LinearLayout pliContent;
    private TextView pliSignInHint;
    private RadioGroup pliRadioGroup;
    private RadioButton createLayerRb, joinLayerRb;
    private EditText pliLayerNameEdit, pliLayerUrlEdit;
    private LinearLayout pliLayerUrlRow;
    private Button pliActionBtn;
    private TextView pliStatusText;
    private CheckBox pliAutoSendCheckbox;
    private Button pliShareQrBtn, pliScanQrBtn;

    // Layers page — public layers section (plain containers, not ListViews, so the whole
    // Layers page can be one scrollable unit with each section sized to its content)
    private LinearLayout publicLayersList;
    private TextView publicLayersCountBadge;
    private EditText publicLayerUrlEdit;

    // Layers page — "My ArcGIS Layers" section (browse list of the signed-in user's own ArcGIS
    // content); the whole card is hidden while signed out, not just its content.
    private LinearLayout privateLayersList;
    private View myArcGisLayersCard;

    // Layers page — "Shared with me" subsection, nested inside "My ArcGIS Layers": items shared
    // with the signed-in user via ArcGIS group membership (see ArcGISRestClient.
    // searchSharedWithMeLayers()), as opposed to owned items. Shares the same collapse control as
    // the owned-layers list above it (no separate toggle).
    private LinearLayout sharedWithMeList;
    private TextView sharedWithMeCountBadge;

    // Layers page — "Private Layers" section: on-device layers not shared to Everyone, either
    // shared to you by another user, or downloaded from "My ArcGIS Layers" and not public — see
    // moveMyArcGisLayerOnDownload(). Whole card hidden when this list is empty.
    private LinearLayout sharedPrivateLayersList;
    private View sharedPrivateLayersCard;
    private TextView sharedPrivateLayersCountBadge;
    private LinearLayout sharedPrivateLayersContent;
    private android.widget.ImageButton collapseSharedPrivateBtn;
    private boolean sharedPrivateLayersExpanded = false;

    // Layers page — collapsible section state
    private LinearLayout privateLayersContent, publicLayersContent;
    private android.widget.ImageButton collapsePrivateBtn, collapsePublicBtn;
    private boolean privateLayersExpanded = false;
    private boolean publicLayersExpanded  = false;

    // Data
    private final List<ArcGISLayer> privateLayers = new ArrayList<>();
    private final List<ArcGISLayer> publicLayers  = new ArrayList<>();
    private final List<ArcGISLayer> sharedPrivateLayers = new ArrayList<>();
    private final List<ArcGISLayer> sharedWithMeLayers = new ArrayList<>();
    private String pliLayerUrl = null;

    /** Tracks ATAK map items (Markers for points, Polylines for lines/polygons) added per layer
     * URL so they can be refreshed or removed — MapItem is the common base both extend. */
    private final Map<String, List<MapItem>> layerItems = new HashMap<>();

    /** Display configs keyed by layer URL, populated when a v:2 (or v:1 display) QR is scanned. */
    private final Map<String, DisplayConfig> layerDisplayConfigs = new HashMap<>();

    private final PliHistoryOverlay pliHistoryOverlay;

    public FeatureLinkDropDownReceiver(MapView mapView, Context context,
            PliHistoryOverlay pliHistoryOverlay) {
        super(mapView);
        this.pluginContext     = context;
        this.pliHistoryOverlay = pliHistoryOverlay;
        this.prefs             = mapView.getContext().getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
        this.authManager       = new ArcGISAuthManager(prefs);
        this.restClient        = new ArcGISRestClient();

        mainView         = LayoutInflater.from(context).inflate(R.layout.main_layout,    null);
        homePageView     = LayoutInflater.from(context).inflate(R.layout.page_home,      null);
        layersPageView   = LayoutInflater.from(context).inflate(R.layout.page_layers,    null);
        pliPageView      = LayoutInflater.from(context).inflate(R.layout.page_pli,       null);
        accountPageView  = LayoutInflater.from(context).inflate(R.layout.page_account,   null);
        addLayerPageView = LayoutInflater.from(context).inflate(R.layout.page_add_layer, null);

        pageContainer        = mainView.findViewById(R.id.page_container);
        overlayContainer      = mainView.findViewById(R.id.overlay_container);
        oauthWebViewContainer = mainView.findViewById(R.id.oauth_webview_container);
        tabHome   = mainView.findViewById(R.id.tab_home);
        tabLayers = mainView.findViewById(R.id.tab_layers);
        tabPli    = mainView.findViewById(R.id.tab_pli);
        accountBtn = mainView.findViewById(R.id.account_btn);
        settingsBtn = mainView.findViewById(R.id.settings_btn);

        tabHome.setOnClickListener(v   -> navigatePage(0));
        tabLayers.setOnClickListener(v -> navigatePage(1));
        tabPli.setOnClickListener(v    -> navigatePage(2));
        accountBtn.setOnClickListener(v -> showAccountPage());
        settingsBtn.setOnClickListener(this::showSettingsMenu);

        setupSwipeGesture(pageContainer);
        wireHomePageViews();
        wireLayersPageViews();
        wirePliPageViews();
        wireAccountPageView();
        wireAddLayerPageView();

        loadSavedData();
        navigatePage(0);

        registerPrefFileResultReceiver();
    }

    // -------------------------------------------------------------------------
    // Overlay pages (Account, Add Layer) — full-screen, pushed over the tabs
    // -------------------------------------------------------------------------

    private void showAccountPage() {
        overlayContainer.removeAllViews();
        overlayContainer.addView(accountPageView);
        overlayContainer.setVisibility(View.VISIBLE);
        syncHomeAuthState();
    }

    private void showAddLayerPage() {
        overlayContainer.removeAllViews();
        overlayContainer.addView(addLayerPageView);
        overlayContainer.setVisibility(View.VISIBLE);
    }

    private boolean isOverlayShowing() {
        return overlayContainer.getVisibility() == View.VISIBLE;
    }

    private void hideOverlay() {
        overlayContainer.removeAllViews();
        overlayContainer.setVisibility(View.GONE);
    }

    // -------------------------------------------------------------------------
    // Settings menu (gear icon, top right)
    // -------------------------------------------------------------------------

    private void showSettingsMenu(View anchor) {
        android.widget.PopupMenu menu = new android.widget.PopupMenu(getMapView().getContext(), anchor);
        menu.getMenu().add("Clear All Layers");
        menu.setOnMenuItemClickListener(item -> {
            confirmClearAllLayers();
            return true;
        });
        menu.show();
    }

    private void confirmClearAllLayers() {
        new AlertDialog.Builder(getMapView().getContext())
                .setTitle("Clear All Layers?")
                .setMessage("Removes every layer from My ArcGIS Layers, Private Layers, and "
                        + "Public Layers on this device, along with any markers and display "
                        + "configs they added. Layers stay in your ArcGIS account — this only "
                        + "clears this device's list.")
                .setPositiveButton("Clear All", (d, w) -> clearAllLayers())
                .setNegativeButton("Cancel", null)
                .show();
    }

    /** Wipes every layer from all three Layers-page sections — the browse list ("My ArcGIS
     * Layers") repopulates on the next Refresh/sign-in since nothing here is excluded, but
     * downloaded private/public layers and their markers/display configs are gone for good on
     * this device (they still exist server-side). */
    private void clearAllLayers() {
        List<ArcGISLayer> all = new ArrayList<>();
        all.addAll(privateLayers);
        all.addAll(sharedPrivateLayers);
        all.addAll(publicLayers);
        for (ArcGISLayer layer : all) removeLayerItems(layer);

        privateLayers.clear();
        sharedPrivateLayers.clear();
        publicLayers.clear();
        sharedWithMeLayers.clear();
        layerDisplayConfigs.clear();

        savePrivateLayers();
        saveSharedPrivateLayers();
        savePublicLayers();
        saveSharedWithMeLayers();
        saveDisplayConfigs();

        refreshHomeStats();
        refreshHomeStatusCard();
        if (currentPage == 1) refreshLayersList();
        Toast.makeText(pluginContext, "All layers cleared", Toast.LENGTH_SHORT).show();
    }

    // -------------------------------------------------------------------------
    // Page navigation
    // -------------------------------------------------------------------------

    private void navigatePage(int page) {
        if (page < 0 || page > 2) return;
        currentPage = page;
        pageContainer.removeAllViews();
        switch (page) {
            case 0:
                pageContainer.addView(homePageView);
                refreshHomeStats();
                syncHomeAuthState();
                updateSetPliEndpointBtn();
                break;
            case 1:
                pageContainer.addView(layersPageView);
                refreshLayersList();
                break;
            case 2:
                pageContainer.addView(pliPageView);
                syncPliPageAuthState();
                break;
        }
        tabHome.setSelected(page == 0);
        tabLayers.setSelected(page == 1);
        tabPli.setSelected(page == 2);
    }

    private void setupSwipeGesture(View target) {
        GestureDetector gd = new GestureDetector(pluginContext,
                new GestureDetector.SimpleOnGestureListener() {
                    @Override
                    public boolean onFling(MotionEvent e1, MotionEvent e2,
                            float vX, float vY) {
                        if (e1 == null || e2 == null) return false;
                        float dx = e2.getX() - e1.getX();
                        if (Math.abs(dx) > 150 && Math.abs(vX) > 100) {
                            navigatePage(dx < 0 ? currentPage + 1 : currentPage - 1);
                            return true;
                        }
                        return false;
                    }
                });
        target.setOnTouchListener((v, event) -> gd.onTouchEvent(event));
    }

    // -------------------------------------------------------------------------
    // Home page — wire
    // -------------------------------------------------------------------------

    private void wireHomePageViews() {
        totalCountText    = homePageView.findViewById(R.id.total_count_text);
        layerStatsList    = homePageView.findViewById(R.id.layer_stats_list);
        setPliEndpointBtn = homePageView.findViewById(R.id.set_pli_endpoint_btn);
        statsContent      = homePageView.findViewById(R.id.stats_content);
        collapseStatsBtn  = homePageView.findViewById(R.id.collapse_stats_btn);

        statusAccountText  = homePageView.findViewById(R.id.status_account_text);
        statusPliText      = homePageView.findViewById(R.id.status_pli_text);
        statusAutoSendText = homePageView.findViewById(R.id.status_autosend_text);
        statusLayersText   = homePageView.findViewById(R.id.status_layers_text);

        Button refreshBtn = homePageView.findViewById(R.id.home_refresh_btn);
        refreshBtn.setOnClickListener(v -> refreshHomeStats());

        collapseStatsBtn.setOnClickListener(v -> toggleStatsSection());

        setPliEndpointBtn.setOnClickListener(v -> navigatePage(2));
        updateSetPliEndpointBtn();
    }

    /** Updates the Home page's quick-glance Status card (account, PLI, auto-send, layer counts). */
    private void refreshHomeStatusCard() {
        if (statusAccountText == null) return;

        boolean authed = authManager.isAuthenticated();
        statusAccountText.setText(authed ? "Signed In" : "Not Signed In");
        statusAccountText.setTextColor(authed ? 0xFF4CAF50 : 0xFFFF5722);

        boolean pliConnected = isPliConnected();
        statusPliText.setText(pliConnected ? "Connected" : "Not Configured");
        statusPliText.setTextColor(pliConnected ? 0xFF4CAF50 : 0xFFFF5722);

        boolean autoSend = prefs.getBoolean(PREF_PLI_AUTO_SEND, false);
        statusAutoSendText.setText(autoSend ? "On" : "Off");
        statusAutoSendText.setTextColor(autoSend ? 0xFF4CAF50 : 0xFF7A7A7A);

        int totalPrivate = privateLayers.size() + sharedPrivateLayers.size();
        statusLayersText.setText(totalPrivate + " private, " + publicLayers.size() + " public");
    }

    /** A PLI destination is "connected" when it's configured and the session can actually send to it. */
    private boolean isPliConnected() {
        return pliLayerUrl != null && !pliLayerUrl.isEmpty() && authManager.isAuthenticated();
    }

    private void toggleStatsSection() {
        statsExpanded = !statsExpanded;
        statsContent.setVisibility(statsExpanded ? View.VISIBLE : View.GONE);
        collapseStatsBtn.setImageResource(statsExpanded
                ? R.drawable.ic_chevron_up : R.drawable.ic_chevron_down);
    }

    private void wireAccountPageView() {
        ssoLoginBtn    = accountPageView.findViewById(R.id.sso_login_btn);
        logoutBtn      = accountPageView.findViewById(R.id.logout_btn);
        authStatusText = accountPageView.findViewById(R.id.auth_status_text);

        ssoLoginBtn.setOnClickListener(v -> performSsoLogin());
        logoutBtn.setOnClickListener(v   -> performLogout());

        android.widget.ImageButton backBtn = accountPageView.findViewById(R.id.account_back_btn);
        backBtn.setOnClickListener(v -> hideOverlay());
    }

    private void wireAddLayerPageView() {
        publicLayerUrlEdit = addLayerPageView.findViewById(R.id.public_layer_url_edit);
        addPublicLayerBtn  = addLayerPageView.findViewById(R.id.add_public_layer_btn);
        scanLayerQrBtn     = addLayerPageView.findViewById(R.id.scan_layer_qr_btn);
        uploadPrefBtn      = addLayerPageView.findViewById(R.id.upload_pref_btn);

        addPublicLayerBtn.setOnClickListener(v -> addPublicLayer());
        scanLayerQrBtn.setOnClickListener(v -> startQrScan());
        uploadPrefBtn.setOnClickListener(v -> showPrefFileDialog());

        android.widget.ImageButton backBtn = addLayerPageView.findViewById(R.id.add_layer_back_btn);
        backBtn.setOnClickListener(v -> hideOverlay());
    }

    private void updateSetPliEndpointBtn() {
        if (setPliEndpointBtn == null) return;
        boolean hasPli = pliLayerUrl != null && !pliLayerUrl.isEmpty();
        setPliEndpointBtn.setVisibility(hasPli ? View.GONE : View.VISIBLE);
    }

    private void wirePliPageViews() {
        pliFeatureLayerTitle = pliPageView.findViewById(R.id.pli_feature_layer_title);
        pliLayerContent      = pliPageView.findViewById(R.id.pli_layer_content);
        collapsePliLayerBtn  = pliPageView.findViewById(R.id.collapse_pli_layer_btn);
        collapsePliLayerBtn.setOnClickListener(v -> togglePliLayerSection());
        pliSignInHint      = pliPageView.findViewById(R.id.pli_sign_in_hint);
        pliContent         = pliPageView.findViewById(R.id.pli_content);
        pliRadioGroup      = pliPageView.findViewById(R.id.pli_radio_group);
        createLayerRb      = pliPageView.findViewById(R.id.create_layer_rb);
        joinLayerRb        = pliPageView.findViewById(R.id.join_layer_rb);
        pliLayerNameEdit   = pliPageView.findViewById(R.id.pli_layer_name_edit);
        pliLayerUrlRow     = pliPageView.findViewById(R.id.pli_layer_url_row);
        pliLayerUrlEdit    = pliPageView.findViewById(R.id.pli_layer_url_edit);
        pliActionBtn       = pliPageView.findViewById(R.id.pli_action_btn);

        android.widget.ImageButton pliLayerUrlQrBtn = pliPageView.findViewById(R.id.pli_layer_url_qr_btn);
        pliLayerUrlQrBtn.setOnClickListener(v -> startPliUrlQrScan());
        pliStatusText      = pliPageView.findViewById(R.id.pli_status_text);
        pliAutoSendCheckbox = pliPageView.findViewById(R.id.pli_auto_send_checkbox);
        pliShareQrBtn      = pliPageView.findViewById(R.id.pli_share_qr_btn);
        pliScanQrBtn       = pliPageView.findViewById(R.id.pli_scan_qr_btn);

        pliShareQrBtn.setOnClickListener(v -> showQrCodeDialog());
        pliScanQrBtn.setOnClickListener(v  -> startQrScan());

        pliRadioGroup.setOnCheckedChangeListener((group, checkedId) -> {
            boolean isJoin = checkedId == R.id.join_layer_rb;
            pliLayerNameEdit.setVisibility(isJoin ? View.GONE    : View.VISIBLE);
            pliLayerUrlRow.setVisibility(  isJoin ? View.VISIBLE : View.GONE);
            pliActionBtn.setText(          isJoin ? "Join Layer" : "Create Layer");
        });

        pliActionBtn.setOnClickListener(v -> {
            if (joinLayerRb.isChecked()) joinPliLayer();
            else                         createPliLayer();
        });

        pliAutoSendCheckbox.setOnCheckedChangeListener((btn, checked) -> {
            prefs.edit().putBoolean(PREF_PLI_AUTO_SEND, checked).apply();
            if (checked) startPliScheduler();
            else         stopPliScheduler();
            refreshHomeStatusCard();
        });

    }

    // -------------------------------------------------------------------------
    // Home page — auth state sync
    // -------------------------------------------------------------------------

    private void syncHomeAuthState() {
        boolean authed = authManager.isAuthenticated();
        ssoLoginBtn.setVisibility(authed ? View.GONE    : View.VISIBLE);
        logoutBtn.setVisibility(  authed ? View.VISIBLE : View.GONE);

        if (authed) {
            authStatusText.setText("Signed in as: " + authManager.getUsername());
            authStatusText.setTextColor(0xFF4CAF50);
        } else {
            authStatusText.setText("Not signed in");
            authStatusText.setTextColor(0xFFFF5722);
        }
        updateAccountButtonColor(authed);
        refreshHomeStatusCard();
    }

    private void updateAccountButtonColor(boolean authed) {
        accountBtn.setColorFilter(authed ? 0xFF4CAF50 : 0xFFFF5722,
                android.graphics.PorterDuff.Mode.SRC_IN);
    }

    private void syncPliPageAuthState() {
        boolean authed = authManager.isAuthenticated();
        pliSignInHint.setVisibility(authed ? View.GONE    : View.VISIBLE);
        pliContent.setVisibility(   authed ? View.VISIBLE : View.GONE);

        if (authed) {
            pliLayerUrl = prefs.getString(PREF_PLI_LAYER_URL, null);
            if (pliLayerUrl != null) {
                pliLayerUrlEdit.setText(pliLayerUrl);
                pliStatusText.setText("PLI layer: " + pliLayerUrl);
            }
            pliAutoSendCheckbox.setChecked(prefs.getBoolean(PREF_PLI_AUTO_SEND, false));
        }
        updateQrShareButton();
    }

    private void updateQrShareButton() {
        pliShareQrBtn.setEnabled(pliLayerUrl != null && !pliLayerUrl.isEmpty()
                && authManager.isAuthenticated());
        updatePliConnectionIndicator();
        refreshHomeStatusCard();
    }

    private void togglePliLayerSection() {
        pliLayerExpanded = !pliLayerExpanded;
        applyPliLayerSectionVisibility();
    }

    private void applyPliLayerSectionVisibility() {
        pliLayerContent.setVisibility(pliLayerExpanded ? View.VISIBLE : View.GONE);
        collapsePliLayerBtn.setImageResource(pliLayerExpanded
                ? R.drawable.ic_chevron_up : R.drawable.ic_chevron_down);
    }

    /** Collapses the "PLI Feature Layer" section the first time it's found connected — once
     * it's set up, there's nothing left to look at there, so this saves a scroll/tap on every
     * later visit. Only fires once; a user who manually re-expands it isn't fought afterward. */
    private void maybeAutoCollapsePliLayerSection() {
        if (pliAutoCollapseDone || !isPliConnected()) return;
        pliAutoCollapseDone = true;
        pliLayerExpanded = false;
        applyPliLayerSectionVisibility();
    }

    /** Colors the "PLI Feature Layer" section header red/green based on connection status. */
    private void updatePliConnectionIndicator() {
        if (pliFeatureLayerTitle == null) return;
        pliFeatureLayerTitle.setTextColor(isPliConnected() ? 0xFF4CAF50 : 0xFFFF5722);
        maybeAutoCollapsePliLayerSection();
    }

    // -------------------------------------------------------------------------
    // Home page — feature statistics
    // -------------------------------------------------------------------------

    private void refreshHomeStats() {
        final List<ArcGISLayer> privateSnap = new ArrayList<>(privateLayers);
        final List<ArcGISLayer> sharedWithMeSnap = new ArrayList<>(sharedWithMeLayers);
        final List<ArcGISLayer> sharedPrivateSnap = new ArrayList<>(sharedPrivateLayers);
        final List<ArcGISLayer> publicSnap  = new ArrayList<>(publicLayers);
        executor.submit(() -> {
            final List<String[]> stats = new ArrayList<>();
            int total = 0;
            String token = authManager.getToken();

            for (ArcGISLayer layer : privateSnap) {
                long count = cachedCount(layer.url, token);
                layer.featureCount = count;
                total += count;
                stats.add(new String[]{layer.name, String.valueOf(count), "private"});
                Log.d(TAG, "refreshHomeStats: [myArcGis] " + layer.name + " count=" + count);
            }
            for (ArcGISLayer layer : sharedWithMeSnap) {
                long count = cachedCount(layer.url, token);
                layer.featureCount = count;
                total += count;
                stats.add(new String[]{layer.name, String.valueOf(count), "private"});
                Log.d(TAG, "refreshHomeStats: [sharedWithMe] " + layer.name + " count=" + count);
            }
            for (ArcGISLayer layer : sharedPrivateSnap) {
                long count = cachedCount(layer.url, token);
                layer.featureCount = count;
                total += count;
                stats.add(new String[]{layer.name, String.valueOf(count), "private"});
                Log.d(TAG, "refreshHomeStats: [privateOnDevice] " + layer.name + " count=" + count);
            }
            for (ArcGISLayer layer : publicSnap) {
                long count = cachedCount(layer.url, null);
                layer.featureCount = count;
                total += count;
                stats.add(new String[]{layer.name, String.valueOf(count), "public"});
                Log.d(TAG, "refreshHomeStats: [publicOnDevice] " + layer.name + " count=" + count);
            }

            final int totalFinal = total;
            mainHandler.post(() -> {
                totalCountText.setText("Total Features: " + totalFinal);
                ArrayAdapter<String> adapter = new ArrayAdapter<>(pluginContext,
                        android.R.layout.simple_list_item_1,
                        buildStatStrings(stats));
                layerStatsList.setAdapter(adapter);
            });
        });
    }

    private long cachedCount(String url, String token) {
        if (url == null || url.isEmpty()) return 0;
        try {
            return restClient.queryFeatureCount(url, token);
        } catch (Exception e) {
            Log.e(TAG, "Count query failed for " + url, e);
            return -1;
        }
    }

    private List<String> buildStatStrings(List<String[]> stats) {
        List<String> out = new ArrayList<>();
        for (String[] s : stats) {
            out.add(s[0] + "  [" + s[2] + "]  —  "
                    + (Long.parseLong(s[1]) < 0 ? "error" : s[1] + " features"));
        }
        return out;
    }

    // -------------------------------------------------------------------------
    // Authentication
    // -------------------------------------------------------------------------

    private void performSsoLogin() {
        launchOAuthWebView("https://www.arcgis.com");
    }

    private void launchOAuthWebView(String portal) {
        String authUrl = authManager.startOAuthFlow(portal);
        if (authUrl == null) {
            Toast.makeText(pluginContext, "Failed to start sign-in", Toast.LENGTH_SHORT).show();
            return;
        }
        authStatusText.setText("Signing in…");

        Context ctx = getMapView().getContext();

        // WebView must use mapView context — ATAK plugin requirement
        WebView webView = new WebView(ctx);
        webView.getSettings().setJavaScriptEnabled(true);
        webView.getSettings().setDomStorageEnabled(true);

        webView.setWebViewClient(new WebViewClient() {
            @Override
            public boolean shouldOverrideUrlLoading(WebView view, String url) {
                if (url.startsWith("featurelink://auth")) {
                    Uri uri  = Uri.parse(url);
                    String code  = uri.getQueryParameter("code");
                    String error = uri.getQueryParameter("error");
                    String desc  = uri.getQueryParameter("error_description");
                    hideOAuthWebView();
                    mainHandler.post(() -> handlePendingOAuthCode(code,
                            error != null ? (desc != null ? desc : error) : null));
                    return true;
                }
                return false;
            }
        });

        // Header bar with Cancel button
        LinearLayout header = new LinearLayout(ctx);
        header.setOrientation(LinearLayout.HORIZONTAL);
        header.setBackgroundColor(0xFF1A1A1A);
        header.setGravity(android.view.Gravity.CENTER_VERTICAL);
        int hPad = dpToPx(12);
        header.setPadding(hPad, dpToPx(6), hPad, dpToPx(6));

        TextView titleView = new TextView(ctx);
        titleView.setText("Sign in to ArcGIS");
        titleView.setTextColor(0xFFFFFFFF);
        titleView.setTextSize(15);
        titleView.setLayoutParams(new LinearLayout.LayoutParams(
                0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));
        header.addView(titleView);

        Button cancelBtn = new Button(ctx);
        cancelBtn.setText("Cancel");
        cancelBtn.setTextSize(12);
        cancelBtn.setOnClickListener(v -> {
            hideOAuthWebView();
            authStatusText.setText("Sign-in cancelled");
            authStatusText.setTextColor(0xFFFF5722);
        });
        header.addView(cancelBtn);

        // Stack: header on top, WebView fills the rest
        LinearLayout overlay = new LinearLayout(ctx);
        overlay.setOrientation(LinearLayout.VERTICAL);
        overlay.addView(header, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT));
        overlay.addView(webView, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f));

        oauthWebViewContainer.removeAllViews();
        oauthWebViewContainer.addView(overlay);
        oauthWebViewContainer.setVisibility(View.VISIBLE);
        webView.loadUrl(authUrl);

        // Hide the "Sign in to ArcGIS" header while the keyboard is up so the field the
        // user is typing into isn't pushed out of view on a small drop-down panel.
        watchKeyboardToToggle(ctx, header);
    }

    /**
     * Hides {@code toHide} whenever the on-screen keyboard is visible and restores it once
     * the keyboard is dismissed. Cleaned up automatically in {@link #hideOAuthWebView()}.
     */
    private void watchKeyboardToToggle(Context ctx, View toHide) {
        if (!(ctx instanceof android.app.Activity)) return;
        View decorView = ((android.app.Activity) ctx).getWindow().getDecorView();
        oauthKeyboardListenerTarget = decorView;
        oauthKeyboardListener = () -> {
            android.graphics.Rect visibleFrame = new android.graphics.Rect();
            decorView.getWindowVisibleDisplayFrame(visibleFrame);
            int screenHeight = decorView.getRootView().getHeight();
            boolean keyboardVisible = screenHeight > 0
                    && (screenHeight - visibleFrame.bottom) > screenHeight * 0.15;
            toHide.setVisibility(keyboardVisible ? View.GONE : View.VISIBLE);
        };
        decorView.getViewTreeObserver().addOnGlobalLayoutListener(oauthKeyboardListener);
    }

    private void hideOAuthWebView() {
        if (oauthKeyboardListenerTarget != null && oauthKeyboardListener != null) {
            oauthKeyboardListenerTarget.getViewTreeObserver()
                    .removeOnGlobalLayoutListener(oauthKeyboardListener);
            oauthKeyboardListenerTarget = null;
            oauthKeyboardListener = null;
        }
        oauthWebViewContainer.removeAllViews();
        oauthWebViewContainer.setVisibility(View.GONE);
    }

    private void handlePendingOAuthCode(String code, String error) {
        if (error != null) {
            authStatusText.setText("Sign-in failed: " + error);
            authStatusText.setTextColor(0xFFFF5722);
            return;
        }
        if (code == null) return;

        authStatusText.setText("Completing sign-in…");
        authManager.handleAuthCode(code, executor, mainHandler,
                () -> {
                    prefs.edit().putString(PREF_PORTAL_URL, "https://www.arcgis.com").apply();
                    syncHomeAuthState();
                    fetchUserLayers();
                    if (currentPage == 2) syncPliPageAuthState();
                },
                message -> {
                    authStatusText.setText("Sign-in failed: " + message);
                    authStatusText.setTextColor(0xFFFF5722);
                }
        );
    }

    private void performLogout() {
        authManager.logout();
        privateLayers.clear();
        sharedWithMeLayers.clear();
        prefs.edit().remove(PREF_PORTAL_URL).apply();
        syncHomeAuthState();
        if (currentPage == 1) refreshLayersList();
        if (currentPage == 2) syncPliPageAuthState();
    }

    private void fetchUserLayers() {
        String token    = authManager.getToken();
        String username = authManager.getUsername();
        String portal   = prefs.getString(PREF_PORTAL_URL, "https://www.arcgis.com");
        if (token == null || username == null) return;

        authStatusText.setText("Loading layers…");
        executor.submit(() -> {
            List<ArcGISLayer> owned  = restClient.searchUserLayers(portal, token, username);
            List<ArcGISLayer> shared = restClient.searchSharedWithMeLayers(portal, token, username);
            Log.d(TAG, "fetchUserLayers: username=" + username + " owned=" + owned.size()
                    + " shared=" + shared.size());
            for (ArcGISLayer l : shared) {
                Log.d(TAG, "fetchUserLayers: shared item name=" + l.name + " owner=" + l.sharedBy);
            }
            mainHandler.post(() -> {
                Set<String> excluded = getExcludedPrivateUrls();
                Set<String> onDevice = new HashSet<>();
                for (ArcGISLayer l : sharedPrivateLayers) onDevice.add(l.url);
                for (ArcGISLayer l : publicLayers)        onDevice.add(l.url);

                privateLayers.clear();
                for (ArcGISLayer l : owned) {
                    // Already downloaded onto this device (see moveMyArcGisLayerOnDownload) —
                    // it now lives in Private or Public Layers, not the browse list.
                    if (excluded.contains(l.url) || onDevice.contains(l.url)) continue;
                    l.type = "private";
                    privateLayers.add(l);
                }
                restoreLayerPreferences(privateLayers, PREF_LAYERS_JSON);
                savePrivateLayers();

                // Cross-check against the owned-layers result by URL rather than trusting only
                // searchSharedWithMeLayers()'s owner-string comparison — ArcGIS Online can format
                // a username differently between the group-search "owner" field and the OAuth
                // username, so an owned item shared into the user's own group could otherwise
                // slip past that check and land here too.
                Set<String> ownedUrls = new HashSet<>();
                for (ArcGISLayer l : owned) ownedUrls.add(l.url);

                sharedWithMeLayers.clear();
                for (ArcGISLayer l : shared) {
                    if (excluded.contains(l.url) || onDevice.contains(l.url) || ownedUrls.contains(l.url)) continue;
                    l.type = "private";
                    sharedWithMeLayers.add(l);
                }
                restoreLayerPreferences(sharedWithMeLayers, PREF_SHARED_WITH_ME_LAYERS_JSON);
                saveSharedWithMeLayers();

                authStatusText.setText("Signed in as: " + authManager.getUsername());
                refreshHomeStats();
                refreshHomeStatusCard();
                if (currentPage == 1) refreshLayersList();
                if (currentPage == 2) syncPliPageAuthState();
            });
        });
    }

    // -------------------------------------------------------------------------
    // Layers page
    // -------------------------------------------------------------------------

    private void wireLayersPageViews() {
        // Public layers section
        publicLayersList       = layersPageView.findViewById(R.id.public_layers_list);
        publicLayersContent    = layersPageView.findViewById(R.id.public_layers_content);
        publicLayersCountBadge = layersPageView.findViewById(R.id.public_layers_count_badge);
        collapsePublicBtn      = layersPageView.findViewById(R.id.collapse_public_btn);
        Button openAddLayerBtn = layersPageView.findViewById(R.id.open_add_layer_btn);
        openAddLayerBtn.setOnClickListener(v -> showAddLayerPage());
        collapsePublicBtn.setOnClickListener(v -> togglePublicLayersSection());

        // "My ArcGIS Layers" section — browse list, only shown while signed in
        privateLayersList    = layersPageView.findViewById(R.id.private_layers_list);
        privateLayersContent = layersPageView.findViewById(R.id.private_layers_content);
        myArcGisLayersCard   = layersPageView.findViewById(R.id.my_arcgis_layers_card);
        collapsePrivateBtn   = layersPageView.findViewById(R.id.collapse_private_btn);

        // "Shared with me" subsection, nested inside the same card/collapse control above
        sharedWithMeList       = layersPageView.findViewById(R.id.shared_with_me_list);
        sharedWithMeCountBadge = layersPageView.findViewById(R.id.shared_with_me_count_badge);
        Button refreshPrivateBtn = layersPageView.findViewById(R.id.refresh_private_layers_btn);
        refreshPrivateBtn.setOnClickListener(v -> {
            if (authManager.isAuthenticated()) fetchUserLayers();
            else refreshPrivateLayers();
        });
        collapsePrivateBtn.setOnClickListener(v -> togglePrivateLayersSection());

        // "Private Layers" section — on-device layers not shared to Everyone; hidden when empty
        sharedPrivateLayersList       = layersPageView.findViewById(R.id.shared_private_layers_list);
        sharedPrivateLayersContent    = layersPageView.findViewById(R.id.shared_private_layers_content);
        sharedPrivateLayersCard       = layersPageView.findViewById(R.id.private_layers_card);
        sharedPrivateLayersCountBadge = layersPageView.findViewById(R.id.private_layers_count_badge);
        collapseSharedPrivateBtn      = layersPageView.findViewById(R.id.collapse_shared_private_btn);
        collapseSharedPrivateBtn.setOnClickListener(v -> toggleSharedPrivateLayersSection());

        // Apply persisted collapse/expand state (all three default collapsed) now that the
        // views exist — loadSavedData() (called right after this) sets the booleans from prefs.
        applyLayersSectionVisibility();
    }

    private void applyLayersSectionVisibility() {
        privateLayersContent.setVisibility(privateLayersExpanded ? View.VISIBLE : View.GONE);
        collapsePrivateBtn.setImageResource(privateLayersExpanded
                ? R.drawable.ic_chevron_up : R.drawable.ic_chevron_down);
        sharedPrivateLayersContent.setVisibility(sharedPrivateLayersExpanded ? View.VISIBLE : View.GONE);
        collapseSharedPrivateBtn.setImageResource(sharedPrivateLayersExpanded
                ? R.drawable.ic_chevron_up : R.drawable.ic_chevron_down);
        publicLayersContent.setVisibility(publicLayersExpanded ? View.VISIBLE : View.GONE);
        collapsePublicBtn.setImageResource(publicLayersExpanded
                ? R.drawable.ic_chevron_up : R.drawable.ic_chevron_down);
    }

    private void togglePrivateLayersSection() {
        privateLayersExpanded = !privateLayersExpanded;
        privateLayersContent.setVisibility(privateLayersExpanded ? View.VISIBLE : View.GONE);
        collapsePrivateBtn.setImageResource(privateLayersExpanded
                ? R.drawable.ic_chevron_up : R.drawable.ic_chevron_down);
        prefs.edit().putBoolean(PREF_SECTION_PRIVATE_EXPANDED, privateLayersExpanded).apply();
    }

    private void toggleSharedPrivateLayersSection() {
        sharedPrivateLayersExpanded = !sharedPrivateLayersExpanded;
        sharedPrivateLayersContent.setVisibility(sharedPrivateLayersExpanded ? View.VISIBLE : View.GONE);
        collapseSharedPrivateBtn.setImageResource(sharedPrivateLayersExpanded
                ? R.drawable.ic_chevron_up : R.drawable.ic_chevron_down);
        prefs.edit().putBoolean(PREF_SECTION_SHARED_PRIVATE_EXPANDED, sharedPrivateLayersExpanded).apply();
    }

    private void togglePublicLayersSection() {
        publicLayersExpanded = !publicLayersExpanded;
        publicLayersContent.setVisibility(publicLayersExpanded ? View.VISIBLE : View.GONE);
        collapsePublicBtn.setImageResource(publicLayersExpanded
                ? R.drawable.ic_chevron_up : R.drawable.ic_chevron_down);
        prefs.edit().putBoolean(PREF_SECTION_PUBLIC_EXPANDED, publicLayersExpanded).apply();
    }

    private void refreshLayersList() {
        Log.d(TAG, "refreshLayersList: myArcGisBrowse=" + privateLayers.size()
                + " sharedWithMeBrowse=" + sharedWithMeLayers.size()
                + " privateOnDevice=" + sharedPrivateLayers.size()
                + " publicOnDevice=" + publicLayers.size());
        refreshPublicLayers();
        refreshPrivateLayers();
        refreshSharedPrivateLayers();
        refreshHomeStatusCard();
    }

    private void refreshPublicLayers() {
        publicLayersCountBadge.setText(String.valueOf(publicLayers.size()));
        populateLayerList(publicLayersList, publicLayers);
    }

    private void refreshPrivateLayers() {
        boolean authed = authManager.isAuthenticated();
        myArcGisLayersCard.setVisibility(authed ? View.VISIBLE : View.GONE);
        if (!authed) return;
        populateLayerList(privateLayersList, privateLayers);
        sharedWithMeCountBadge.setText(String.valueOf(sharedWithMeLayers.size()));
        populateLayerList(sharedWithMeList, sharedWithMeLayers);
    }

    private void refreshSharedPrivateLayers() {
        sharedPrivateLayersCard.setVisibility(
                sharedPrivateLayers.isEmpty() ? View.GONE : View.VISIBLE);
        sharedPrivateLayersCountBadge.setText(String.valueOf(sharedPrivateLayers.size()));
        populateLayerList(sharedPrivateLayersList, sharedPrivateLayers);
    }

    /**
     * Populates a plain vertical container (rather than a ListView) so each list can grow to
     * its natural height and the whole Layers page scrolls as one unit.
     */
    private void populateLayerList(LinearLayout container, List<ArcGISLayer> layers) {
        container.removeAllViews();
        Set<String> styledLayerUrls = layerDisplayConfigs.keySet();
        LayerListAdapter adapter = new LayerListAdapter(
                pluginContext, layers, this::onLayerAction, this::toggleLayerVisibility,
                this::onLayerIntervalChanged, this::onLayerShare, this::onLayerDelete,
                styledLayerUrls);
        for (int i = 0; i < layers.size(); i++) {
            container.addView(adapter.getView(i, null, container));
            if (i < layers.size() - 1) {
                View divider = new View(pluginContext);
                divider.setLayoutParams(new LinearLayout.LayoutParams(
                        LinearLayout.LayoutParams.MATCH_PARENT, dpToPx(1)));
                divider.setBackgroundColor(0xFF242424);
                container.addView(divider);
            }
        }
    }

    /** Which of the three Layers-page sections a given layer instance currently lives in.
     * Distinct from ArcGISLayer.type (which only tracks "private" vs "public" for auth-token
     * purposes — a shared-private layer still needs the recipient's own ArcGIS token to
     * download, so it keeps type="private" — list membership is what actually tells the three
     * sections apart). */
    private void saveLayerOfSection(ArcGISLayer layer) {
        if (sharedPrivateLayers.contains(layer)) saveSharedPrivateLayers();
        else if (privateLayers.contains(layer)) savePrivateLayers();
        else savePublicLayers();
    }

    private void refreshLayerOfSection(ArcGISLayer layer) {
        if (sharedPrivateLayers.contains(layer)) refreshSharedPrivateLayers();
        else if (privateLayers.contains(layer)) refreshPrivateLayers();
        else refreshPublicLayers();
    }

    private void toggleLayerVisibility(ArcGISLayer layer) {
        layer.visible = !layer.visible;
        List<MapItem> items = layerItems.get(layer.url);
        if (items != null) {
            for (MapItem item : items) item.setVisible(layer.visible);
        }
        saveLayerOfSection(layer);
        refreshLayerOfSection(layer);
    }

    private void onLayerAction(ArcGISLayer layer) {
        if ("public".equals(layer.type)) {
            // Action button on a public layer = remove it — confirm first, it also removes
            // any markers already placed on the map
            if (publicLayers.contains(layer)) {
                confirmRemovePublicLayer(layer);
            }
        } else {
            // Downloading a "My ArcGIS Layers"/"Shared with me" item for the first time moves it
            // onto the device, into Private or Public Layers depending on its ArcGIS sharing scope.
            if (privateLayers.contains(layer) || sharedWithMeLayers.contains(layer)) {
                moveMyArcGisLayerOnDownload(layer);
            }
            saveLayerOfSection(layer);
            downloadLayer(layer);
        }
    }

    /** Moves a "My ArcGIS Layers"/"Shared with me" browse-list item onto the device once its
     * download/refresh button is tapped, landing it in Private Layers or Public Layers depending
     * on whether the ArcGIS item is shared to Everyone ({@link ArcGISLayer#access}) — from then on
     * it behaves like any other on-device layer in that section instead of staying in the browse
     * list. */
    private void moveMyArcGisLayerOnDownload(ArcGISLayer layer) {
        if (sharedWithMeLayers.remove(layer)) {
            saveSharedWithMeLayers();
        } else {
            privateLayers.remove(layer);
            savePrivateLayers();
        }
        if ("public".equals(layer.access)) {
            layer.type = "public";
            publicLayers.add(layer);
            savePublicLayers();
            refreshPublicLayers();
        } else {
            layer.type = "private";
            sharedPrivateLayers.add(layer);
            saveSharedPrivateLayers();
            refreshSharedPrivateLayers();
        }
        refreshPrivateLayers();
    }

    /** Public-layer refresh interval/unit spinner changed — save only, this device only. The
     * recurrence scheduler (checkLayerRecurrence()) picks the new value up on its own next tick;
     * no need to force an immediate re-download just from adjusting the interval. */
    private void onLayerIntervalChanged(ArcGISLayer layer) {
        savePublicLayers();
    }

    // -------------------------------------------------------------------------
    // Share a public layer with a contact
    // -------------------------------------------------------------------------

    /** Share button tapped — pick a contact, then send them this layer as an ATAK Mission
     * Package (data package). ATAK's own native "X wants to send you a file" accept/decline
     * flow handles the rest; the recipient picks the received file up via the Add Layer page's
     * "Upload Pref File" button once they accept it. */
    private void onLayerShare(ArcGISLayer layer) {
        List<String> uuids = Contacts.getInstance().getAllIndividualContactUuids();
        IndividualContact[] contacts = Contacts.getInstance().getIndividualContactsByUuid(uuids);

        List<Contact> targets = new ArrayList<>();
        List<String> names = new ArrayList<>();
        for (IndividualContact c : contacts) {
            if (c == null) continue;
            targets.add(c);
            names.add(c.getName());
        }
        if (targets.isEmpty()) {
            Toast.makeText(pluginContext, "No contacts available to share with", Toast.LENGTH_SHORT).show();
            return;
        }

        new AlertDialog.Builder(getMapView().getContext())
                .setTitle("Share \"" + layer.name + "\" with…")
                .setItems(names.toArray(new String[0]), (d, which) ->
                        sendLayerShare(layer, targets.get(which)))
                .setNegativeButton("Cancel", null)
                .show();
    }

    private void sendLayerShare(ArcGISLayer layer, Contact recipient) {
        DisplayConfig displayConfig = layerDisplayConfigs.get(layer.url);
        String configJson = LayerShareHelper.buildShareConfigJson(layer, displayConfig);
        if (configJson == null) {
            Toast.makeText(pluginContext, "Could not build share file", Toast.LENGTH_SHORT).show();
            return;
        }
        // Bundle this device's already-installed custom iconset(s) (see AutoIconset) alongside
        // the config JSON, so the recipient gets the actual icons in the same package instead of
        // just a usericonPath reference they may not have installed — avoids the "missing icon
        // set" prompt entirely for layers styled via the auto-iconset feature.
        List<File> iconsetZips = new ArrayList<>();
        if (displayConfig != null) {
            File iconsetsDir = new File(android.os.Environment.getExternalStorageDirectory(), "atak/iconsets");
            for (String group : displayConfig.referencedIconsetGroups()) {
                File zip = new File(iconsetsDir, group + ".zip");
                if (zip.exists()) iconsetZips.add(zip);
            }
        }
        executor.submit(() -> {
            try {
                // pluginContext.getCacheDir() doesn't resolve to a real, writable directory —
                // ATAK plugin Context objects are largely for resource resolution (assets/
                // strings/themes), not a genuine backing filesystem the way a normally-installed
                // app's Context is. The host ATAK app's context always has a real one, and
                // MissionPackageApi.Send() below already runs against that same host context.
                File dir = new File(getMapView().getContext().getCacheDir(), "featurelink_share");
                dir.mkdirs();
                String safeName = layer.name.replaceAll("[^a-zA-Z0-9 _-]", "_");
                // Deliberately NOT ".featurelink.json" — WinTAK's own Mission-Package content
                // auto-import chain tries a GRG (Gridded Reference Graphic) importer against any
                // unrecognized ".json" attachment, which throws an unhandled
                // NullReferenceException in WinTak.Common.Coords.MGRSPoint.decodeString on our
                // payload instead of just skipping it (GDAL's own probe fails gracefully first).
                // A non-geo-looking extension avoids that importer picking the file up at all.
                File file = new File(dir, safeName + ".featurelinkshare");
                try (FileWriter w = new FileWriter(file)) {
                    w.write(configJson);
                }

                // ATAK derives the on-disk filename it writes the incoming package to (under
                // .../atak/tools/datapackage/incoming/) from this manifest name — a colon (as in
                // the earlier "FeatureLink: <name>") isn't valid there (external storage is
                // FAT-based) and fails with EPERM. Reuse the already-filesystem-safe name.
                MissionPackageManifest manifest = MissionPackageApi.CreateTempManifest(
                        "FeatureLink - " + safeName, true, true, null);
                manifest.addFile(file, "FeatureLink Layer Config");
                // Iconset zip(s) ride along in the same package — ATAK's own built-in iconset
                // importer already recognizes this exact format (iconset.xml at the zip root,
                // the same shape a manual Import Manager iconset install uses), so
                // setImportInstructions below picks these up automatically too, no
                // FeatureLink-specific handling needed for them.
                for (File iconsetZip : iconsetZips) {
                    manifest.addFile(iconsetZip, "FeatureLink Iconset: " + iconsetZip.getName());
                }
                // Tells ATAK to automatically run the received file(s) through its normal import
                // system (FeatureLinkMarshal/FeatureLinkImporter, registered in
                // FeatureLinkMapComponent, plus ATAK's own iconset importer for the zip(s) above)
                // once the recipient accepts the package, instead of just extracting it and
                // leaving it for a manual "Upload Pref File" pick.
                manifest.getConfiguration().setImportInstructions(true, false, null);

                boolean started = MissionPackageApi.Send(
                        getMapView().getContext(), manifest,
                        ToastSaveCallback.class,
                        new Contact[]{recipient});

                String iconsetNote = iconsetZips.isEmpty() ? ""
                        : " (+ " + iconsetZips.size() + " iconset" + (iconsetZips.size() > 1 ? "s" : "") + ")";
                mainHandler.post(() -> Toast.makeText(pluginContext,
                        started ? "Sending \"" + layer.name + "\"" + iconsetNote + " to " + recipient.getName() + "…"
                                : "Could not start send to " + recipient.getName(),
                        Toast.LENGTH_SHORT).show());
            } catch (Exception e) {
                Log.e(TAG, "sendLayerShare failed", e);
                mainHandler.post(() -> Toast.makeText(pluginContext,
                        "Failed to share layer", Toast.LENGTH_SHORT).show());
            }
        });
    }

    private void confirmRemovePublicLayer(ArcGISLayer layer) {
        new AlertDialog.Builder(getMapView().getContext())
                .setTitle("Remove Layer?")
                .setMessage("Remove \"" + layer.name + "\" from your layer list? "
                        + "Any markers it added to the map will also be removed.")
                .setPositiveButton("Remove", (d, w) -> {
                    publicLayers.remove(layer);
                    savePublicLayers();
                    removeLayerItems(layer);
                    layerDisplayConfigs.remove(layer.url);
                    saveDisplayConfigs();
                    refreshPublicLayers();
                })
                .setNegativeButton("Cancel", null)
                .show();
    }

    /** Trash-can button on a private (signed-in ArcGIS) or shared-private layer. Unlike a
     * public layer, this layer still exists in someone's ArcGIS account — there's nothing here
     * to actually delete server-side, so this just hides it from this device's list. "My
     * ArcGIS Layers" additionally remembers the exclusion so the next sign-in/"Refresh" doesn't
     * silently bring it back — a shared-private layer only ever gets (re)added by another share,
     * so it doesn't need that same tracking. */
    private void onLayerDelete(ArcGISLayer layer) {
        boolean isMyArcGis = privateLayers.contains(layer) || sharedWithMeLayers.contains(layer);
        new AlertDialog.Builder(getMapView().getContext())
                .setTitle("Remove Layer?")
                .setMessage("Remove \"" + layer.name + "\" from your layer list here? "
                        + (isMyArcGis ? "It stays in your ArcGIS account — this only hides it on this device. "
                                      : "")
                        + "Any markers it added to the map will also be removed.")
                .setPositiveButton("Remove", (d, w) -> {
                    if (sharedWithMeLayers.remove(layer)) {
                        saveSharedWithMeLayers();
                        addExcludedPrivateUrl(layer.url);
                    } else if (isMyArcGis) {
                        privateLayers.remove(layer);
                        savePrivateLayers();
                        addExcludedPrivateUrl(layer.url);
                    } else {
                        sharedPrivateLayers.remove(layer);
                        saveSharedPrivateLayers();
                    }
                    removeLayerItems(layer);
                    layerDisplayConfigs.remove(layer.url);
                    saveDisplayConfigs();
                    if (isMyArcGis) refreshPrivateLayers(); else refreshSharedPrivateLayers();
                })
                .setNegativeButton("Cancel", null)
                .show();
    }

    private Set<String> getExcludedPrivateUrls() {
        Set<String> out = new HashSet<>();
        try {
            JSONArray arr = new JSONArray(prefs.getString(PREF_EXCLUDED_PRIVATE_URLS, "[]"));
            for (int i = 0; i < arr.length(); i++) out.add(arr.getString(i));
        } catch (Exception ignored) {}
        return out;
    }

    private void addExcludedPrivateUrl(String url) {
        if (url == null) return;
        Set<String> excluded = getExcludedPrivateUrls();
        excluded.add(url);
        prefs.edit().putString(PREF_EXCLUDED_PRIVATE_URLS, new JSONArray(excluded).toString()).apply();
    }

    private void addPublicLayer() {
        String url = publicLayerUrlEdit.getText().toString().trim();
        if (url.isEmpty()) {
            Toast.makeText(pluginContext, "Enter a service URL", Toast.LENGTH_SHORT).show();
            return;
        }
        addPublicLayerBtn.setEnabled(false);
        executor.submit(() -> {
            ArcGISLayer layer = restClient.fetchLayerInfo(url);
            // One-link, no-server (method 5): generate + install this layer's iconset on-device
            // per AUTO-ICONSET-SPEC.md, so a shared CoT that references it renders here without a
            // TAK Portal round trip. Federated — the UID/group/filenames match what every other
            // platform independently produces for the same layer. Non-fatal: on failure the layer
            // still loads, markers just keep default styling until the set exists. Runs on this
            // background thread (AutoIconset.generate does its own blocking renderer fetch).
            AutoIconset.Result iconset = null;
            AutoSymbology.Result shapeResult = null;
            if (layer != null) {
                // Fetch the renderer once and feed it into both extractors — AutoIconset only
                // handles esriPMS (picture-marker) symbols, AutoSymbology handles esriSMS/esriSLS/
                // esriSFS (marker color/shape, line stroke, polygon fill) — avoids a duplicate
                // renderer round trip for the same layer.
                JSONObject renderer = null;
                String canonicalUrl = AutoIconset.canonicalize(url);
                if (canonicalUrl != null) {
                    try {
                        JSONObject meta = restClient.fetchJson(canonicalUrl, null);
                        if (meta != null && !meta.has("error")) {
                            JSONObject drawingInfo = meta.optJSONObject("drawingInfo");
                            renderer = drawingInfo != null ? drawingInfo.optJSONObject("renderer") : null;
                        }
                    } catch (Exception e) {
                        Log.w(TAG, "renderer fetch failed for " + url, e);
                    }
                }
                try {
                    iconset = AutoIconset.generate(pluginContext, restClient, url, null, null,
                            renderer, null, null);
                } catch (Exception e) {
                    Log.w(TAG, "auto-iconset generation failed for " + url, e);
                }
                shapeResult = AutoSymbology.extract(renderer, null);
            }
            final AutoIconset.Result iconsetResult = iconset;
            final AutoSymbology.Result finalShapeResult = shapeResult;
            mainHandler.post(() -> {
                addPublicLayerBtn.setEnabled(true);
                if (layer != null) {
                    layer.type = "public";
                    publicLayers.add(layer);
                    savePublicLayers();
                    publicLayerUrlEdit.setText("");
                    hideOverlay();
                    navigatePage(1);
                    if (iconsetResult != null) {
                        Toast.makeText(pluginContext, "Icons ready: " + iconsetResult.group
                                + " (" + iconsetResult.iconCount + ")", Toast.LENGTH_SHORT).show();
                    }
                    boolean hasShapeStyle = finalShapeResult != null && !finalShapeResult.isEmpty();
                    // Self-render: if there's no styling config for this layer yet, synthesize one
                    // from the just-generated icons and/or extracted shape styling so the markers/
                    // shapes show their real styling on THIS device too (not only on devices that
                    // receive its CoT). We don't overwrite an existing config (e.g. one from a
                    // scanned QR / TAK Portal).
                    if ((iconsetResult != null || hasShapeStyle) && layerDisplayConfigs.get(url) == null) {
                        String field = iconsetResult != null ? iconsetResult.field
                                : (finalShapeResult != null ? finalShapeResult.field : "");
                        String singleIconPath = iconsetResult != null ? iconsetResult.singleIconPath : null;
                        Map<String, String> pathByValue = iconsetResult != null ? iconsetResult.pathByValue : null;
                        layerDisplayConfigs.put(url, DisplayConfig.forAutoIcons(url, field,
                                singleIconPath, pathByValue, finalShapeResult));
                        saveDisplayConfigs();
                    }
                    downloadLayer(layer);
                } else {
                    Toast.makeText(pluginContext, "Could not load layer from URL",
                            Toast.LENGTH_SHORT).show();
                }
            });
        });
    }

    /** Removes any ATAK map items (markers/shapes) previously placed on the map for this layer. */
    private void removeLayerItems(ArcGISLayer layer) {
        List<MapItem> old = layerItems.remove(layer.url);
        if (old != null) {
            MapGroup root = getMapView().getRootGroup();
            for (MapItem item : old) root.removeItem(item);
        }
    }

    private void downloadLayer(ArcGISLayer layer) {
        String token = "private".equals(layer.type) ? authManager.getToken() : null;
        DisplayConfig displayConfig = layerDisplayConfigs.get(layer.url);
        Log.d(TAG, "downloadLayer: layer.url=" + layer.url + " displayConfig=" + (displayConfig != null)
                + " layerDisplayConfigs.keys=" + layerDisplayConfigs.keySet());
        if (displayConfig != null && displayConfig.sym != null) {
            DisplayConfig.SymConfig sym = displayConfig.sym;
            StringBuilder vsDump = new StringBuilder();
            if (sym.advValues != null) {
                for (DisplayConfig.UvEntry e : sym.advValues) {
                    vsDump.append("[v=").append(e.value)
                          .append(" isIcon=").append(e.isIcon)
                          .append(" iconset=").append(e.iconset)
                          .append(" iconFile=").append(e.iconFile)
                          .append(" usericonPath=").append(e.usericonPath)
                          .append("] ");
                }
            }
            Log.d(TAG, "downloadLayer sym-debug: type=" + sym.type + " fieldName=" + sym.fieldName
                    + " advValues=" + vsDump);
        }
        ArcGISRestClient.CotFieldMapping cotMapping = (displayConfig != null && displayConfig.cotMapping != null)
                ? new ArcGISRestClient.CotFieldMapping(
                        displayConfig.cotMapping.uidFields,
                        displayConfig.cotMapping.typeFields,
                        displayConfig.cotMapping.callsignFields,
                        displayConfig.cotMapping.remarksFields)
                : null;
        executor.submit(() -> {
            try {
                // One-time lazy backfill for layers added before geometryType existed, or whose
                // browse-list search result (a portal item, not layer metadata) never carried it.
                if (layer.geometryType == null || layer.geometryType.isEmpty()) {
                    String canonicalUrl = AutoIconset.canonicalize(layer.url);
                    if (canonicalUrl != null) {
                        try {
                            JSONObject meta = restClient.fetchJson(canonicalUrl, token);
                            if (meta != null && !meta.has("error")) {
                                layer.geometryType = meta.optString("geometryType", "");
                            }
                        } catch (Exception e) {
                            Log.w(TAG, "geometryType fetch failed for " + layer.url, e);
                        }
                    }
                }

                List<ArcGISRestClient.DownloadedFeature> features =
                        restClient.downloadLayerAsCoT(layer.url, token, cotMapping);
                Log.d(TAG, "downloadLayer: downloaded " + features.size() + " features from " + layer.url);
                layer.lastSync = System.currentTimeMillis();
                mainHandler.post(() -> {
                    saveLayerOfSection(layer);
                    // Swap out old map items for this layer
                    MapGroup root = getMapView().getRootGroup();
                    removeLayerItems(layer);
                    List<MapItem> added = new ArrayList<>(features.size());
                    int debugLogged = 0;
                    for (ArcGISRestClient.DownloadedFeature f : features) {
                        List<MapItem> items;
                        if ("esriGeometryPolyline".equals(layer.geometryType) && !f.paths.isEmpty()) {
                            items = buildPolylineShapes(f, displayConfig);
                        } else if ("esriGeometryPolygon".equals(layer.geometryType) && !f.rings.isEmpty()) {
                            items = buildPolygonShapes(f, displayConfig);
                        } else {
                            items = Collections.singletonList(buildMarker(f, displayConfig));
                            if (debugLogged < 8 && displayConfig != null) {
                                debugLogged++;
                                String symType = displayConfig.sym != null ? displayConfig.sym.type : "null";
                                String fieldName = displayConfig.sym != null ? displayConfig.sym.fieldName : "";
                                String rawVal = fieldName.isEmpty() ? "" : f.attributes.getOrDefault(fieldName, "<missing>");
                                int advCount = (displayConfig.sym != null && displayConfig.sym.advValues != null)
                                        ? displayConfig.sym.advValues.size() : -1;
                                Log.d(TAG, "downloadLayer icon-debug: uid=" + f.uid
                                        + " symType=" + symType
                                        + " field=" + fieldName + "=" + rawVal
                                        + " advValues.size=" + advCount);
                            }
                        }
                        for (MapItem item : items) {
                            item.setVisible(layer.visible);
                            root.addItem(item);
                            added.add(item);
                        }
                    }
                    if (!added.isEmpty()) layerItems.put(layer.url, added);
                    Toast.makeText(pluginContext,
                            "Downloaded: " + layer.name + " (" + features.size() + " features)",
                            Toast.LENGTH_SHORT).show();
                });
            } catch (Exception e) {
                Log.e(TAG, "Download failed for " + layer.name, e);
                mainHandler.post(() -> Toast.makeText(pluginContext,
                        "Download failed: " + layer.name, Toast.LENGTH_SHORT).show());
            }
        });
    }

    /** Builds a point marker for one downloaded feature — the original per-feature styling
     * logic, unchanged, just extracted so downloadLayer() can dispatch by geometry type. */
    private Marker buildMarker(ArcGISRestClient.DownloadedFeature f, DisplayConfig displayConfig) {
        GeoPoint gp = Double.isNaN(f.hae)
                ? new GeoPoint(f.lat, f.lon)
                : new GeoPoint(f.lat, f.lon, f.hae);
        Marker m = new Marker(gp, f.uid);
        m.setType(f.cotType);

        if (displayConfig != null) {
            // Apply custom iconset icon (sym type "ic", or "adv" per-value icon)
            String iconsetPath = displayConfig.resolveIconsetPath(f.attributes);
            if (iconsetPath != null) {
                m.setMetaString(com.atakmap.android.icons.UserIcon.IconsetPath, iconsetPath);
            }

            // Marker color also tints custom icon bitmaps — a colored fill only makes sense for
            // shape symbology. When an icon actually applies, force white (no tint) so the icon
            // shows its own real colors.
            int color = iconsetPath != null ? Color.WHITE : displayConfig.resolveColor(f.attributes);
            m.setColor(color);

            String label = displayConfig.resolveLabel(f.attributes, f.callsign);
            m.setMetaString("callsign", label);
            m.setTitle(label);

            String popupRemarks = displayConfig.buildRemarks(f.attributes);
            String remarks = popupRemarks.isEmpty() ? f.remarks
                    : (f.remarks.isEmpty() ? popupRemarks : popupRemarks + "\n" + f.remarks);
            if (!remarks.isEmpty()) m.setMetaString("remarks", remarks);
        } else {
            m.setMetaString("callsign", f.callsign);
            m.setTitle(f.callsign);
            if (!f.remarks.isEmpty()) m.setMetaString("remarks", f.remarks);
        }

        m.setMetaBoolean("readiness", true);
        return m;
    }

    /** Maps a DisplayConfig.ShapeStyle.strokeDash string ("solid"/"dash"/"dot") to the ATAK
     * Polyline BASIC_LINE_STYLE_* constant, defaulting to solid. */
    private static int basicLineStyleFrom(String dash) {
        if ("dash".equals(dash)) return Polyline.BASIC_LINE_STYLE_DASHED;
        if ("dot".equals(dash))  return Polyline.BASIC_LINE_STYLE_DOTTED;
        return Polyline.BASIC_LINE_STYLE_SOLID;
    }

    /** Converts one [lon,lat] vertex list into a GeoPoint[] (ATAK's GeoPoint is lat,lon order). */
    private static GeoPoint[] toGeoPoints(List<double[]> vertices) {
        GeoPoint[] points = new GeoPoint[vertices.size()];
        for (int i = 0; i < vertices.size(); i++) {
            double[] v = vertices.get(i);
            points[i] = new GeoPoint(v[1], v[0]);
        }
        return points;
    }

    /** Applies the same title/callsign/remarks/readiness metadata buildMarker() applies, shared
     * by the shape builders below (stroke/fill styling is set separately by each caller, since
     * points/lines/polygons resolve style differently). */
    private void applyFeatureMeta(MapItem item, ArcGISRestClient.DownloadedFeature f,
            DisplayConfig displayConfig) {
        item.setType(f.cotType);
        String label = displayConfig != null
                ? displayConfig.resolveLabel(f.attributes, f.callsign) : f.callsign;
        item.setMetaString("callsign", label);
        item.setTitle(label);
        String remarks;
        if (displayConfig != null) {
            String popupRemarks = displayConfig.buildRemarks(f.attributes);
            remarks = popupRemarks.isEmpty() ? f.remarks
                    : (f.remarks.isEmpty() ? popupRemarks : popupRemarks + "\n" + f.remarks);
        } else {
            remarks = f.remarks;
        }
        if (!remarks.isEmpty()) item.setMetaString("remarks", remarks);
        item.setMetaBoolean("readiness", true);
    }

    /** One ATAK Polyline shape per path — a multi-part ArcGIS polyline feature becomes N sibling
     * shapes sharing a "{uid}-p{i}" sub-UID scheme, since ATAK's Polyline is single-part. Stroke
     * styling comes from the layer's DisplayConfig.resolveShapeStyle() (esriSLS), falling back to
     * ATAK's default blue/solid/2px when no style resolves (e.g. no renderer symbology found). */
    private List<MapItem> buildPolylineShapes(ArcGISRestClient.DownloadedFeature f,
            DisplayConfig displayConfig) {
        List<MapItem> shapes = new ArrayList<>();
        DisplayConfig.ShapeStyle style = displayConfig != null
                ? displayConfig.resolveShapeStyle(f.attributes) : null;
        int strokeColor = style != null ? style.strokeColor : Color.BLUE;
        float strokeWeight = style != null ? style.strokeWidthPx : 2f;
        int lineStyle = basicLineStyleFrom(style != null ? style.strokeDash : "solid");

        for (int i = 0; i < f.paths.size(); i++) {
            List<double[]> path = f.paths.get(i);
            if (path.size() < 2) continue;
            String uid = f.paths.size() > 1 ? f.uid + "-p" + i : f.uid;
            Polyline line = new Polyline(uid);
            line.setPoints(toGeoPoints(path));
            line.setStyle(Polyline.STYLE_STROKE_MASK);
            line.setStrokeColor(strokeColor);
            line.setStrokeWeight(strokeWeight);
            line.setBasicLineStyle(lineStyle);
            applyFeatureMeta(line, f, displayConfig);
            shapes.add(line);
        }
        return shapes;
    }

    /** One ATAK Polyline shape (closed + filled) for a polygon feature's outer ring. ATAK's
     * Polyline has no native multi-ring/hole support (its backing point list is a single flat
     * ring, confirmed against the ATAK 5.7 SDK), so a polygon with holes renders solid rather
     * than silently dropping the whole feature — holes beyond the first ring are simply not
     * rendered. Fill/stroke styling comes from the layer's DisplayConfig.resolveShapeStyle()
     * (esriSFS/esriSLS), falling back to ATAK's default blue/solid/2px stroke with no fill. */
    private List<MapItem> buildPolygonShapes(ArcGISRestClient.DownloadedFeature f,
            DisplayConfig displayConfig) {
        List<MapItem> shapes = new ArrayList<>();
        List<double[]> outerRing = f.rings.get(0);
        if (outerRing.size() < 3) return shapes;
        if (f.rings.size() > 1) {
            Log.d(TAG, "buildPolygonShapes: " + f.uid + " has " + (f.rings.size() - 1)
                    + " hole ring(s) — not rendered (outer ring only)");
        }

        DisplayConfig.ShapeStyle style = displayConfig != null
                ? displayConfig.resolveShapeStyle(f.attributes) : null;
        int strokeColor = style != null ? style.strokeColor : Color.BLUE;
        float strokeWeight = style != null ? style.strokeWidthPx : 2f;
        int lineStyle = basicLineStyleFrom(style != null ? style.strokeDash : "solid");
        boolean filled = style != null && !"none".equals(style.fillStyle);

        Polyline poly = new Polyline(f.uid);
        poly.setPoints(toGeoPoints(outerRing));
        int styleMask = Polyline.STYLE_CLOSED_MASK | Polyline.STYLE_STROKE_MASK
                | (filled ? Polyline.STYLE_FILLED_MASK : 0);
        poly.setStyle(styleMask);
        poly.setStrokeColor(strokeColor);
        poly.setStrokeWeight(strokeWeight);
        poly.setBasicLineStyle(lineStyle);
        if (filled) poly.setFillColor(style.fillColor);
        applyFeatureMeta(poly, f, displayConfig);
        shapes.add(poly);
        return shapes;
    }

    // -------------------------------------------------------------------------
    // PLI layer management
    // -------------------------------------------------------------------------

    private void createPliLayer() {
        String token    = authManager.getToken();
        String username = authManager.getUsername();
        String portal   = prefs.getString(PREF_PORTAL_URL, "https://www.arcgis.com");
        if (token == null || username == null) return;

        String layerName = pliLayerNameEdit.getText().toString().trim();
        pliActionBtn.setEnabled(false);
        pliStatusText.setText("Creating feature service…");

        executor.submit(() -> {
            String serviceUrl = restClient.createPliFeatureService(
                    portal, username, token, layerName.isEmpty() ? null : layerName);
            mainHandler.post(() -> {
                pliActionBtn.setEnabled(true);
                if (serviceUrl != null) {
                    setPliLayerUrl(serviceUrl);
                    pliLayerUrlEdit.setText(serviceUrl);
                    pliStatusText.setText("Created: " + serviceUrl);
                    updateQrShareButton();
                    updateSetPliEndpointBtn();
                } else {
                    pliStatusText.setText("Failed to create service.");
                }
            });
        });
    }

    private void joinPliLayer() {
        String url = pliLayerUrlEdit.getText().toString().trim();
        if (url.isEmpty()) {
            Toast.makeText(pluginContext, "Enter a Feature Layer URL", Toast.LENGTH_SHORT).show();
            return;
        }
        setPliLayerUrl(url);
        pliStatusText.setText("Joined: " + url);
        Toast.makeText(pluginContext, "PLI layer configured", Toast.LENGTH_SHORT).show();
        updateQrShareButton();
        updateSetPliEndpointBtn();
    }

    // -------------------------------------------------------------------------
    // QR code: share PLI config
    // -------------------------------------------------------------------------

    private void showQrCodeDialog() {
        if (pliLayerUrl == null || pliLayerUrl.isEmpty()) {
            Toast.makeText(pluginContext, "Configure a PLI layer first", Toast.LENGTH_SHORT).show();
            return;
        }
        String portal    = prefs.getString(PREF_PORTAL_URL, "https://www.arcgis.com");
        String layerName = pliLayerNameEdit.getText().toString().trim();
        final String url = pliLayerUrl;

        executor.submit(() -> {
            try {
                String json = QrHelper.buildPliPayload(portal, url, layerName);
                Bitmap qr   = QrHelper.generateBitmap(json, 512);
                mainHandler.post(() -> displayQrDialog(qr, json, "Scan to Connect",
                        "Scan on another device, then sign in with ArcGIS."));
            } catch (Exception e) {
                Log.e(TAG, "QR generation failed", e);
                mainHandler.post(() -> Toast.makeText(pluginContext,
                        "Failed to generate QR code", Toast.LENGTH_SHORT).show());
            }
        });
    }

    private void displayQrDialog(Bitmap qr, String rawJson, String title, String message) {
        Context activityCtx = getMapView().getContext();
        ImageView iv = new ImageView(activityCtx);
        iv.setImageBitmap(qr);
        int pad = dpToPx(12);
        iv.setPadding(pad, pad, pad, pad);

        new AlertDialog.Builder(activityCtx)
                .setTitle(title)
                .setMessage(message)
                .setView(iv)
                .setNeutralButton("Copy Text", (d, w) -> copyToClipboard(rawJson))
                .setPositiveButton("Close", null)
                .show();
    }

    private void copyToClipboard(String text) {
        ClipboardManager cm = (ClipboardManager)
                pluginContext.getSystemService(Context.CLIPBOARD_SERVICE);
        if (cm != null) {
            cm.setPrimaryClip(ClipData.newPlainText("FeatureLink Config", text));
            Toast.makeText(pluginContext, "Config copied to clipboard", Toast.LENGTH_SHORT).show();
        }
    }

    // -------------------------------------------------------------------------
    // QR code: scan
    // -------------------------------------------------------------------------

    private void startPliUrlQrScan() {
        QrScanDialog dialog = new QrScanDialog(getMapView().getContext(), payload -> {
            mainHandler.post(() -> {
                String url = null;
                JSONObject config = QrHelper.parse(payload);
                if (config != null) {
                    url = config.optString(QrHelper.URL, "");
                } else if (payload.trim().startsWith("http")) {
                    url = payload.trim();
                }
                if (url != null && !url.isEmpty()) {
                    pliLayerUrlEdit.setText(url);
                    joinLayerRb.setChecked(true);
                } else {
                    Toast.makeText(pluginContext, "No layer URL found in QR code",
                            Toast.LENGTH_SHORT).show();
                }
            });
        });
        dialog.show();
    }

    private void startQrScan() {
        QrScanDialog dialog = new QrScanDialog(getMapView().getContext(), payload -> {
            // Mode 4 — "Saved Dataset Link": a bare URL, not JSON. Fetch it and treat the
            // response body as the real (Mode 1/2/3) config payload.
            if (QrHelper.isSavedDatasetLink(payload)) {
                resolveSavedDatasetLink(payload.trim());
                return;
            }
            mainHandler.post(() -> {
                // Close the Add Layer overlay (if that's what triggered this scan) — the
                // apply* handlers below navigate to whichever page the result belongs on.
                hideOverlay();
                applyScannedPayload(payload);
            });
        });
        dialog.show();
    }

    /** Fetches a Mode 4 link's response body and applies it as a normal scanned payload. */
    private void resolveSavedDatasetLink(String url) {
        executor.submit(() -> {
            String body;
            try {
                body = QrHelper.fetchSavedDatasetLink(url);
            } catch (Exception e) {
                Log.e(TAG, "Failed to fetch saved dataset link: " + url, e);
                body = null;
            }
            final String fetched = body;
            mainHandler.post(() -> {
                hideOverlay();
                if (fetched == null || fetched.isEmpty()) {
                    Toast.makeText(pluginContext, "Could not load config from link",
                            Toast.LENGTH_SHORT).show();
                    return;
                }
                applyScannedPayload(fetched);
            });
        });
    }

    /** Parses a scanned (or fetched) payload as Mode 1/2/3 display config or an operational QR. */
    private void applyScannedPayload(String payload) {
        Log.d(TAG, "applyScannedPayload: payload=" + payload);
        // Mode 1/2/3 display config takes priority over operational (credentials/PLI/layer) QR
        DisplayConfig displayConfig = QrHelper.parseDisplayConfig(payload);
        Log.d(TAG, "applyScannedPayload: parseDisplayConfig -> " + (displayConfig == null ? "null"
                : ("url='" + displayConfig.url + "' sym=" + (displayConfig.sym != null) + " lbl=" + (displayConfig.lbl != null) + " popup=" + (displayConfig.popup != null))));
        if (displayConfig != null) {
            applyScannedDisplayConfig(displayConfig);
            return;
        }
        JSONObject config = QrHelper.parse(payload);
        if (config != null) applyScannedConfig(config);
        else Toast.makeText(pluginContext,
                "Not a valid FeatureLink QR code", Toast.LENGTH_SHORT).show();
    }

    /** Iconset UIDs this config references that this device's UserIconDatabase doesn't have —
     * checked before actually applying a display config so a missing custom iconset doesn't
     * silently fall back to default markers with no explanation. */
    private Set<String> getMissingIconsetUids(DisplayConfig config) {
        Set<String> referenced = config.referencedIconsetUids();
        if (referenced.isEmpty()) return referenced;
        Set<String> missing = new HashSet<>();
        com.atakmap.android.icons.UserIconDatabase db =
                com.atakmap.android.icons.UserIconDatabase.instance(getMapView().getContext());
        for (String uid : referenced) {
            if (db.getIconSet(uid, false, false) == null) missing.add(uid);
        }
        return missing;
    }

    private void applyScannedDisplayConfig(DisplayConfig config) {
        Set<String> missing = getMissingIconsetUids(config);
        if (missing.isEmpty()) {
            applyDisplayConfigNow(config);
            return;
        }

        // Missing custom iconset(s). If we know the source layer URL, regenerate them on-device
        // (AUTO-ICONSET-SPEC.md / Phase A) instead of prompting: the federated generator reproduces
        // the identical UID/group/filenames the config references, so the "missing" set becomes
        // present locally with no server and no dialog. The zip is written to atak/iconsets/ and
        // ATAK imports it asynchronously (ADD_ICONSET), so we proceed to apply — any briefly
        // unmatched markers self-correct once the import lands (spec §0).
        //
        // cfg.rendererOverride, when present, means TAK Portal built this config's icon symbology
        // from a Web Map's per-layer style override, not cfg.url's own default renderer (§2.3) —
        // re-deriving from the bare URL here would extract from a DIFFERENT renderer and compute
        // a non-matching uid/group, so the exact renderer/uid/group TAK Portal already computed
        // are passed through instead of letting generate() re-derive anything.
        if (config.url != null && !config.url.isEmpty()) {
            Toast.makeText(pluginContext, "Fetching icons for this layer…", Toast.LENGTH_SHORT).show();
            final DisplayConfig cfg = config;
            final String token = config.isPrivate ? authManager.getToken() : null;
            final JSONObject rendererOverride = cfg.rendererOverride != null
                    ? cfg.rendererOverride.optJSONObject("renderer") : null;
            final String uidOverride = (cfg.rendererOverride != null && cfg.rendererOverride.has("uid"))
                    ? cfg.rendererOverride.optString("uid") : null;
            final String groupOverride = (cfg.rendererOverride != null && cfg.rendererOverride.has("group"))
                    ? cfg.rendererOverride.optString("group") : null;
            executor.submit(() -> {
                try {
                    AutoIconset.generate(pluginContext, restClient, cfg.url, null, token,
                            rendererOverride, uidOverride, groupOverride);
                } catch (Exception e) {
                    Log.w(TAG, "regenerate missing iconset failed for " + cfg.url, e);
                }
                mainHandler.post(() -> applyDisplayConfigNow(cfg));
            });
            return;
        }

        // No source layer to regenerate from (e.g. a display-only v:1 config with no url) — this is
        // the documented offline limitation (spec §10). Warn and let the operator decide.
        showMissingIconsetDialog(config, missing);
    }

    /** The pre-existing "can't fix it automatically" prompt — now only the fallback when there's
     * no source layer URL to regenerate the missing iconset(s) from (spec §10). */
    private void showMissingIconsetDialog(DisplayConfig config, Set<String> missing) {
        new AlertDialog.Builder(getMapView().getContext())
                .setTitle("Missing Icon Set" + (missing.size() > 1 ? "s" : ""))
                .setMessage("This layer's styling uses " + missing.size()
                        + " custom icon set" + (missing.size() > 1 ? "s" : "")
                        + " not installed on this device:\n\n" + String.join("\n", missing)
                        + "\n\nUnmatched features will fall back to a default marker until "
                        + "those icon sets are imported (Settings > Import Content). "
                        + "Continue anyway, or stop and get the icon sets first?")
                .setPositiveButton("Continue", (d, w) -> applyDisplayConfigNow(config))
                .setNegativeButton("Stop", null)
                .show();
    }

    private void applyDisplayConfigNow(DisplayConfig config) {
        if (config.url != null && !config.url.isEmpty()) {
            Log.d(TAG, "applyScannedDisplayConfig: url branch, url=" + config.url);
            // v:2 — store the config, load the layer, and download with styling applied
            layerDisplayConfigs.put(config.url, config);
            saveDisplayConfigs();
            executor.submit(() -> {
                ArcGISLayer layer = restClient.fetchLayerInfo(config.url);
                Log.d(TAG, "applyScannedDisplayConfig: fetchLayerInfo -> " + (layer == null ? "null" : ("name=" + layer.name + " url=" + layer.url)));
                mainHandler.post(() -> {
                    if (layer == null) {
                        Toast.makeText(pluginContext, "Could not load layer from display config",
                                Toast.LENGTH_SHORT).show();
                        return;
                    }
                    if (!config.name.isEmpty()) layer.name = config.name;
                    // config.isPrivate means this came from sharing a "My ArcGIS Layers" layer —
                    // it needs the recipient's own ArcGIS access (e.g. group membership) to
                    // actually download, so it lands in "Private Layers" rather than "Public
                    // Layers". Sharing it doesn't grant that access, just the layer reference.
                    layer.type = config.isPrivate ? "private" : "public";
                    // Avoid duplicate entries by URL
                    boolean alreadyAdded = false;
                    for (ArcGISLayer l : publicLayers) {
                        if (l.url.equals(config.url)) { alreadyAdded = true; break; }
                    }
                    for (ArcGISLayer l : privateLayers) {
                        if (l.url.equals(config.url)) { alreadyAdded = true; break; }
                    }
                    for (ArcGISLayer l : sharedPrivateLayers) {
                        if (l.url.equals(config.url)) { alreadyAdded = true; break; }
                    }
                    Log.d(TAG, "applyScannedDisplayConfig: alreadyAdded=" + alreadyAdded
                            + " publicLayers.size=" + publicLayers.size() + " lookup(displayConfig present)=" + (layerDisplayConfigs.get(layer.url) != null));
                    if (!alreadyAdded) {
                        // TAK Portal's recommended refresh interval is only ever the *initial*
                        // value for a layer new to this device — once added, the interval lives
                        // in this device's own SharedPreferences (see LayerListAdapter's public
                        // interval row) and re-scanning this same config never touches it again.
                        layer.recurrenceInterval = config.freqInterval;
                        layer.recurrenceUnit     = config.freqUnit;
                        if (config.isPrivate) {
                            sharedPrivateLayers.add(layer);
                            saveSharedPrivateLayers();
                        } else {
                            publicLayers.add(layer);
                            savePublicLayers();
                        }
                    }
                    navigatePage(1);
                    refreshLayersList();
                    downloadLayer(layer);
                    Toast.makeText(pluginContext,
                            "Display config loaded: " + layer.name, Toast.LENGTH_SHORT).show();
                });
            });
        } else {
            Log.d(TAG, "applyScannedDisplayConfig: else branch (no url) — display-only, needs existing layer");
            // v:1 display-only — apply to an existing layer chosen by the user
            List<ArcGISLayer> all = new ArrayList<>();
            all.addAll(publicLayers);
            all.addAll(privateLayers);
            all.addAll(sharedPrivateLayers);
            if (all.isEmpty()) {
                Toast.makeText(pluginContext,
                        "Add a layer first, then scan the display config again",
                        Toast.LENGTH_LONG).show();
                return;
            }
            String[] names = new String[all.size()];
            for (int i = 0; i < all.size(); i++) names[i] = all.get(i).name;
            new android.app.AlertDialog.Builder(getMapView().getContext())
                    .setTitle("Apply display config to:")
                    .setItems(names, (dlg, which) -> {
                        ArcGISLayer layer = all.get(which);
                        layerDisplayConfigs.put(layer.url, config);
                        saveDisplayConfigs();
                        downloadLayer(layer);
                    })
                    .setNegativeButton("Cancel", null)
                    .show();
        }
    }

    private void applyScannedConfig(JSONObject config) {
        switch (config.optString(QrHelper.TYPE, "")) {
            case QrHelper.TYPE_CREDENTIALS:
                applyScannedCredentials(config);
                break;
            case QrHelper.TYPE_PLI_ENDPOINT:
                applyScannedPliEndpoint(config);
                break;
            case QrHelper.TYPE_LAYER_CONFIG:
                applyScannedLayerConfig(config);
                break;
            default: // TYPE_PLI_CONFIG or unrecognised with creds — treat as full bundle
                applyScannedPliConfig(config);
                break;
        }
    }

    private void applyScannedCredentials(JSONObject config) {
        navigatePage(0);
        Toast.makeText(pluginContext, "Tap 'Sign in with ArcGIS' to continue",
                Toast.LENGTH_LONG).show();
    }

    private void applyScannedPliEndpoint(JSONObject config) {
        String url = config.optString(QrHelper.URL, "");
        if (url.isEmpty()) return;

        setPliLayerUrl(url);
        updateQrShareButton();
        updateSetPliEndpointBtn();

        if (authManager.isAuthenticated()) {
            pliLayerUrlEdit.setText(url);
            pliStatusText.setText("PLI layer: " + url);
        }
        Toast.makeText(pluginContext, "PLI endpoint set", Toast.LENGTH_SHORT).show();
    }

    private void applyScannedPliConfig(JSONObject config) {
        String url = config.optString(QrHelper.URL, "");

        navigatePage(0);

        if (!url.isEmpty()) {
            setPliLayerUrl(url);
            joinLayerRb.setChecked(true);
        }

        Toast.makeText(pluginContext, "Config loaded — tap 'Sign in with ArcGIS' to continue",
                Toast.LENGTH_LONG).show();
    }

    private void applyScannedLayerConfig(JSONObject config) {
        String url       = config.optString(QrHelper.URL,  "");
        String name      = config.optString(QrHelper.NAME, url);
        boolean isPrivate = config.optBoolean(QrHelper.IS_PRIVATE, false);

        if (isPrivate && !authManager.isAuthenticated()) {
            Toast.makeText(pluginContext, "Sign in on Home tab to add private layers",
                    Toast.LENGTH_LONG).show();
            return;
        }

        addPublicLayerBtn.setEnabled(false);
        executor.submit(() -> {
            ArcGISLayer layer = restClient.fetchLayerInfo(url);
            mainHandler.post(() -> {
                addPublicLayerBtn.setEnabled(true);
                if (layer != null) {
                    if (!name.isEmpty()) layer.name = name;
                    layer.type = isPrivate ? "private" : "public";
                    if (isPrivate) {
                        privateLayers.add(layer);
                        savePrivateLayers();
                    } else {
                        publicLayers.add(layer);
                        savePublicLayers();
                    }
                    navigatePage(1);
                    refreshLayersList();
                    downloadLayer(layer);
                    Toast.makeText(pluginContext, "Layer added: " + layer.name,
                            Toast.LENGTH_SHORT).show();
                } else {
                    Toast.makeText(pluginContext, "Could not load layer from QR",
                            Toast.LENGTH_SHORT).show();
                }
            });
        });
    }

    /** Opens a system file picker for a FeatureLink config JSON — either a manually-saved
     * display config, or (see sendLayerShare()) a layer someone shared with you via ATAK
     * Mission Package, once you've accepted the incoming file transfer. */
    private void showPrefFileDialog() {
        Intent fileBrowser = new Intent(Intent.ACTION_GET_CONTENT);
        fileBrowser.setType("*/*");
        fileBrowser.addCategory(Intent.CATEGORY_OPENABLE);
        try {
            Context ctx = getMapView().getContext();
            if (ctx instanceof Activity) {
                ((Activity) ctx).startActivityForResult(
                        Intent.createChooser(fileBrowser, "Select FeatureLink config JSON"),
                        PREF_FILE_PICK_REQUEST_CODE);
            } else {
                ctx.startActivity(Intent.createChooser(fileBrowser, "Select FeatureLink config JSON"));
            }
        } catch (Exception e) {
            Log.e(TAG, "showPrefFileDialog failed to launch picker", e);
            Toast.makeText(pluginContext, "Could not open file picker", Toast.LENGTH_LONG).show();
        }
    }

    /** ATAK re-broadcasts any plugin-launched startActivityForResult() outcome via this action
     * (extras: "requestCode", "resultCode", "data") rather than a normal onActivityResult()
     * override — see the SDK's HelloWorldDropDownReceiver sample for the same pattern. */
    private void registerPrefFileResultReceiver() {
        prefFileResultReceiver = new BroadcastReceiver() {
            @Override
            public void onReceive(Context context, Intent intent) {
                if (intent.getIntExtra("requestCode", -1) != PREF_FILE_PICK_REQUEST_CODE) return;
                if (intent.getIntExtra("resultCode", Activity.RESULT_CANCELED) != Activity.RESULT_OK) return;
                Intent data = intent.getParcelableExtra("data");
                Uri uri = data != null ? data.getData() : null;
                if (uri != null) handlePrefFileSelected(uri);
            }
        };
        AtakBroadcast.getInstance().registerReceiver(prefFileResultReceiver,
                new AtakBroadcast.DocumentedIntentFilter("com.atakmap.android.ACTIVITY_FINISHED"));
    }

    private void handlePrefFileSelected(Uri uri) {
        executor.submit(() -> {
            String content;
            try {
                content = readUriAsString(uri);
            } catch (Exception e) {
                Log.e(TAG, "Failed to read selected pref file: " + uri, e);
                mainHandler.post(() -> Toast.makeText(pluginContext,
                        "Could not read file", Toast.LENGTH_SHORT).show());
                return;
            }
            mainHandler.post(() -> {
                hideOverlay();
                applyScannedPayload(content);
            });
        });
    }

    private String readUriAsString(Uri uri) throws java.io.IOException {
        try (java.io.InputStream is = pluginContext.getContentResolver().openInputStream(uri)) {
            if (is == null) throw new java.io.IOException("could not open " + uri);
            java.io.ByteArrayOutputStream buf = new java.io.ByteArrayOutputStream();
            byte[] chunk = new byte[8192];
            int n;
            while ((n = is.read(chunk)) != -1) buf.write(chunk, 0, n);
            return buf.toString("UTF-8");
        }
    }

    /** Called by FeatureLinkImporter.importData() — ATAK's import system invokes this off the
     * main thread when an accepted Mission Package's contents matched FeatureLinkMarshal (see
     * sendLayerShare()'s ImportInstructions). Reads the file synchronously (cheap, already off
     * the main thread) and hands the actual apply — network calls, UI — to the main thread,
     * same as the manual "Upload Pref File" path (handlePrefFileSelected) already does. Returns
     * whether the file was at least readable; the apply itself is fire-and-forget from here,
     * identical to how handlePrefFileSelected's caller never waits on the result either. */
    boolean importFeatureLinkShareUri(Uri uri) {
        String content;
        try {
            content = readUriAsString(uri);
        } catch (Exception e) {
            Log.e(TAG, "Failed to read incoming FeatureLink share: " + uri, e);
            return false;
        }
        if (content.trim().isEmpty()) return false;
        mainHandler.post(() -> {
            hideOverlay();
            applyScannedPayload(content);
        });
        return true;
    }

    // -------------------------------------------------------------------------
    // PLI auto-send
    // -------------------------------------------------------------------------

    /** Points PLI sends at a (possibly new/different) layer — clears the remembered objectId
     * so the first send after this goes back to add-then-remember-id instead of trying to
     * update an id that belongs to whatever layer was previously joined. */
    private void setPliLayerUrl(String url) {
        pliLayerUrl = url;
        prefs.edit().putString(PREF_PLI_LAYER_URL, url).remove(PREF_PLI_OBJECT_ID).apply();
    }

    private void startPliScheduler() {
        stopPliScheduler();
        pliScheduler = Executors.newSingleThreadScheduledExecutor();
        pliScheduler.scheduleAtFixedRate(this::sendPliUpdate, 0, 30, TimeUnit.SECONDS);
        Log.d(TAG, "PLI auto-send started");
    }

    private void stopPliScheduler() {
        if (pliScheduler != null) {
            pliScheduler.shutdownNow();
            pliScheduler = null;
        }
    }

    private void sendPliUpdate() {
        if (pliLayerUrl == null || pliLayerUrl.isEmpty()) return;
        String token = authManager.getToken();
        if (token == null) {
            Log.w(TAG, "PLI auto-send skipped: session expired, re-login required");
            return;
        }
        try {
            MapView mv = getMapView();
            if (mv == null) return;
            PointMapItem self = mv.getSelfMarker();
            if (self == null) return;

            GeoPoint pt  = self.getPoint();
            long     now = System.currentTimeMillis();
            String   icon    = self.getMetaString("icon",    "");
            String   remarks = self.getMetaString("remarks", "");
            String   how     = self.getMetaString("how",     "m-g");
            String   groupName = self.getMetaString("team",         "");
            String   groupRole = self.getMetaString("atakRoleType", "");
            String   username  = authManager.getUsername();

            // First send for this PLI layer creates the feature and remembers its objectId;
            // every send after that updates the same row in place instead of adding a new one.
            long objectId = prefs.getLong(PREF_PLI_OBJECT_ID, -1L);
            if (objectId < 0) {
                objectId = restClient.addPliFeature(
                        pliLayerUrl, token,
                        self.getUID(), self.getType(), mv.getDeviceCallsign(),
                        icon, remarks, how, username, groupName, groupRole,
                        pt.getLatitude(), pt.getLongitude(), pt.getAltitude(),
                        pt.getCE(), pt.getLE(),
                        now, now, now + 30_000L, "");
                if (objectId >= 0) {
                    prefs.edit().putLong(PREF_PLI_OBJECT_ID, objectId).apply();
                    Log.d(TAG, "PLI feature created, objectId=" + objectId);
                } else {
                    Log.w(TAG, "PLI add did not return an objectId — will retry as an add next tick");
                }
            } else {
                boolean updated = restClient.updatePliFeature(
                        pliLayerUrl, token, objectId,
                        self.getUID(), self.getType(), mv.getDeviceCallsign(),
                        icon, remarks, how, username, groupName, groupRole,
                        pt.getLatitude(), pt.getLongitude(), pt.getAltitude(),
                        pt.getCE(), pt.getLE(),
                        now, now, now + 30_000L, "");
                if (updated) {
                    Log.d(TAG, "PLI feature updated, objectId=" + objectId);
                } else {
                    // Feature's likely gone server-side — forget the id so the next tick adds
                    // a fresh one instead of updating a row that no longer exists.
                    prefs.edit().remove(PREF_PLI_OBJECT_ID).apply();
                    Log.w(TAG, "PLI update failed for objectId=" + objectId + " — will re-add next tick");
                }
            }

            if (pliHistoryOverlay != null) pliHistoryOverlay.addPoint(pt);
        } catch (Exception e) {
            Log.e(TAG, "PLI send failed", e);
        }
    }

    // -------------------------------------------------------------------------
    // Radial menu handler
    // -------------------------------------------------------------------------

    private void handleSendToLayer(String uid) {
        if (pliLayerUrl == null || pliLayerUrl.isEmpty()) {
            Toast.makeText(pluginContext,
                    "Configure a PLI Feature Layer on the Home tab first",
                    Toast.LENGTH_LONG).show();
            return;
        }
        MapItem item = getMapView().getMapItem(uid);
        if (item == null) return;

        String token = authManager.getToken();
        executor.submit(() -> {
            try {
                GeoPoint pt = null;
                if (item instanceof PointMapItem) pt = ((PointMapItem) item).getPoint();
                if (pt == null) return;

                String callsign = item.getMetaString("callsign", item.getTitle());
                long   now      = System.currentTimeMillis();

                restClient.addPliFeature(
                        pliLayerUrl, token,
                        item.getUID(),
                        item.getType(),
                        callsign,
                        item.getMetaString("icon",    ""),
                        item.getMetaString("remarks", ""),
                        item.getMetaString("how",     "h-g-i-g-o"),
                        authManager.getUsername(),
                        item.getMetaString("team",         ""),
                        item.getMetaString("atakRoleType", ""),
                        pt.getLatitude(),
                        pt.getLongitude(),
                        pt.getAltitude(),
                        pt.getCE(),
                        pt.getLE(),
                        now,
                        now,
                        now + 7 * 24 * 60 * 60 * 1000L,
                        "");
                mainHandler.post(() -> Toast.makeText(pluginContext,
                        "Sent to Feature Layer: " + callsign, Toast.LENGTH_SHORT).show());
            } catch (Exception e) {
                Log.e(TAG, "Send to layer failed for " + uid, e);
                mainHandler.post(() -> Toast.makeText(pluginContext,
                        "Failed to send to Feature Layer", Toast.LENGTH_SHORT).show());
            }
        });
    }

    // -------------------------------------------------------------------------
    // Persistence helpers
    // -------------------------------------------------------------------------

    private void loadSavedData() {
        try {
            String privateJson = prefs.getString(PREF_LAYERS_JSON, null);
            if (privateJson != null) {
                JSONArray arr = new JSONArray(privateJson);
                for (int i = 0; i < arr.length(); i++)
                    privateLayers.add(ArcGISLayer.fromJson(arr.getJSONObject(i)));
            }
            String publicJson = prefs.getString(PREF_PUBLIC_LAYERS_JSON, null);
            if (publicJson != null) {
                JSONArray arr = new JSONArray(publicJson);
                for (int i = 0; i < arr.length(); i++)
                    publicLayers.add(ArcGISLayer.fromJson(arr.getJSONObject(i)));
            }
            String sharedPrivateJson = prefs.getString(PREF_SHARED_PRIVATE_LAYERS_JSON, null);
            if (sharedPrivateJson != null) {
                JSONArray arr = new JSONArray(sharedPrivateJson);
                for (int i = 0; i < arr.length(); i++)
                    sharedPrivateLayers.add(ArcGISLayer.fromJson(arr.getJSONObject(i)));
            }
            String sharedWithMeJson = prefs.getString(PREF_SHARED_WITH_ME_LAYERS_JSON, null);
            if (sharedWithMeJson != null) {
                JSONArray arr = new JSONArray(sharedWithMeJson);
                for (int i = 0; i < arr.length(); i++)
                    sharedWithMeLayers.add(ArcGISLayer.fromJson(arr.getJSONObject(i)));
            }
            pliLayerUrl = prefs.getString(PREF_PLI_LAYER_URL, null);
            if (prefs.getBoolean(PREF_PLI_AUTO_SEND, false)) startPliScheduler();
            updateSetPliEndpointBtn();
        } catch (Exception e) {
            Log.e(TAG, "Failed to load saved layer data", e);
        }
        loadDisplayConfigs();
        privateLayersExpanded       = prefs.getBoolean(PREF_SECTION_PRIVATE_EXPANDED, false);
        sharedPrivateLayersExpanded = prefs.getBoolean(PREF_SECTION_SHARED_PRIVATE_EXPANDED, false);
        publicLayersExpanded        = prefs.getBoolean(PREF_SECTION_PUBLIC_EXPANDED, false);
        applyLayersSectionVisibility();
        checkLayerRecurrence();
    }

    /** layerDisplayConfigs was purely in-memory until this — a plugin reload (device reboot,
     * ATAK force-stop/crash, or just closing the app) silently threw away every layer's
     * styling, so re-downloading it fell back to plain markers with no explanation. Persisted
     * the same way privateLayers/publicLayers already are. */
    private void saveDisplayConfigs() {
        try {
            JSONObject all = new JSONObject();
            for (Map.Entry<String, DisplayConfig> e : layerDisplayConfigs.entrySet()) {
                JSONObject compact = e.getValue().toCompactJson();
                compact.put("v", 2);
                compact.put("url", e.getKey());
                all.put(e.getKey(), compact);
            }
            prefs.edit().putString(PREF_DISPLAY_CONFIGS_JSON, all.toString()).apply();
        } catch (Exception e) {
            Log.e(TAG, "Failed to save display configs", e);
        }
    }

    private void loadDisplayConfigs() {
        try {
            String json = prefs.getString(PREF_DISPLAY_CONFIGS_JSON, null);
            if (json == null) return;
            JSONObject all = new JSONObject(json);
            java.util.Iterator<String> keys = all.keys();
            while (keys.hasNext()) {
                String url = keys.next();
                DisplayConfig cfg = DisplayConfig.fromJson(all.getJSONObject(url));
                if (cfg != null) layerDisplayConfigs.put(url, cfg);
            }
        } catch (Exception e) {
            Log.e(TAG, "Failed to load display configs", e);
        }
    }

    private void savePrivateLayers() {
        try {
            JSONArray arr = new JSONArray();
            for (ArcGISLayer l : privateLayers) arr.put(l.toJson());
            prefs.edit().putString(PREF_LAYERS_JSON, arr.toString()).apply();
        } catch (Exception e) {
            Log.e(TAG, "Failed to save private layers", e);
        }
    }

    private void savePublicLayers() {
        try {
            JSONArray arr = new JSONArray();
            for (ArcGISLayer l : publicLayers) arr.put(l.toJson());
            prefs.edit().putString(PREF_PUBLIC_LAYERS_JSON, arr.toString()).apply();
        } catch (Exception e) {
            Log.e(TAG, "Failed to save public layers", e);
        }
    }

    private void saveSharedPrivateLayers() {
        try {
            JSONArray arr = new JSONArray();
            for (ArcGISLayer l : sharedPrivateLayers) arr.put(l.toJson());
            prefs.edit().putString(PREF_SHARED_PRIVATE_LAYERS_JSON, arr.toString()).apply();
        } catch (Exception e) {
            Log.e(TAG, "Failed to save shared-private layers", e);
        }
    }

    private void saveSharedWithMeLayers() {
        try {
            JSONArray arr = new JSONArray();
            for (ArcGISLayer l : sharedWithMeLayers) arr.put(l.toJson());
            prefs.edit().putString(PREF_SHARED_WITH_ME_LAYERS_JSON, arr.toString()).apply();
        } catch (Exception e) {
            Log.e(TAG, "Failed to save shared-with-me layers", e);
        }
    }

    private void restoreLayerPreferences(List<ArcGISLayer> layers, String prefKey) {
        try {
            String savedJson = prefs.getString(prefKey, null);
            if (savedJson == null) return;
            JSONArray arr = new JSONArray(savedJson);
            for (int i = 0; i < arr.length(); i++) {
                ArcGISLayer saved = ArcGISLayer.fromJson(arr.getJSONObject(i));
                for (ArcGISLayer fresh : layers) {
                    if (fresh.url.equals(saved.url)) {
                        fresh.downloadEnabled    = saved.downloadEnabled;
                        fresh.recurrenceInterval = saved.recurrenceInterval;
                        fresh.recurrenceUnit     = saved.recurrenceUnit;
                        fresh.lastSync           = saved.lastSync;
                        break;
                    }
                }
            }
        } catch (Exception e) {
            Log.e(TAG, "Failed to restore layer prefs", e);
        }
    }

    private void checkLayerRecurrence() {
        // Deliberately excludes privateLayers/sharedWithMeLayers — those are the "My ArcGIS
        // Layers"/"Shared with me" browse lists, never-yet-downloaded items the user hasn't
        // opted into. Every fresh ArcGISLayer defaults to a 180s recurrence with lastSync=0,
        // which reads as "always overdue" — auto-refreshing this loop against the browse lists
        // used to silently download every browsable layer in the account on every plugin load.
        // Only layers already on-device (sharedPrivateLayers/publicLayers) get auto-refreshed.
        final List<ArcGISLayer> sharedPrivateSnap = new ArrayList<>(sharedPrivateLayers);
        final List<ArcGISLayer> publicSnap  = new ArrayList<>(publicLayers);
        executor.submit(() -> {
            long now = System.currentTimeMillis();
            for (ArcGISLayer layer : sharedPrivateSnap) {
                long threshold = layer.recurrenceMillis();
                if (threshold > 0 && (now - layer.lastSync) >= threshold) downloadLayer(layer);
            }
            for (ArcGISLayer layer : publicSnap) {
                long threshold = layer.recurrenceMillis();
                if (threshold > 0 && (now - layer.lastSync) >= threshold) downloadLayer(layer);
            }
        });
    }

    // -------------------------------------------------------------------------
    // Utility
    // -------------------------------------------------------------------------

    private int dpToPx(int dp) {
        return Math.round(dp * pluginContext.getResources().getDisplayMetrics().density);
    }

    // -------------------------------------------------------------------------
    // DropDownReceiver callbacks
    // -------------------------------------------------------------------------

    @Override
    public void onReceive(Context context, Intent intent) {
        final String action = intent.getAction();
        if (action == null) return;
        if (SHOW_PLUGIN.equals(action)) {
            showDropDown(mainView, HALF_WIDTH, FULL_HEIGHT, FULL_WIDTH, HALF_HEIGHT, this);
        } else if (SEND_TO_LAYER.equals(action)) {
            String uid = intent.getStringExtra("uid");
            if (uid != null) handleSendToLayer(uid);
        } else if (IMPORT_CONFIG.equals(action)) {
            String config = intent.getStringExtra("config");
            Log.d(TAG, "IMPORT_CONFIG received, payload length=" + (config != null ? config.length() : -1));
            if (config != null) {
                hideOverlay();
                applyScannedPayload(config);
            }
        }
    }

    @Override public void onDropDownSelectionRemoved() {}
    @Override public void onDropDownClose() { hideOAuthWebView(); hideOverlay(); }
    @Override public void onDropDownSizeChanged(double width, double height) {}
    @Override public void onDropDownVisible(boolean visible) {}

    @Override
    protected boolean onBackButtonPressed() {
        if (isOverlayShowing()) {
            hideOverlay();
            return true;
        }
        return false;
    }

    @Override
    public void disposeImpl() {
        stopPliScheduler();
        executor.shutdownNow();
        if (prefFileResultReceiver != null) {
            AtakBroadcast.getInstance().unregisterReceiver(prefFileResultReceiver);
        }
        // Remove all injected map items from the ATAK map
        MapGroup root = getMapView().getRootGroup();
        for (List<MapItem> items : layerItems.values()) {
            for (MapItem item : items) root.removeItem(item);
        }
        layerItems.clear();
    }
}
