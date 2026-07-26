package com.atakmap.android.featurelink;

import android.app.AlertDialog;
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
import com.atakmap.android.dropdown.DropDown;
import com.atakmap.android.dropdown.DropDownReceiver;
import com.atakmap.android.featurelink.arcgis.ArcGISAuthManager;
import com.atakmap.android.featurelink.arcgis.ArcGISLayer;
import com.atakmap.android.featurelink.arcgis.ArcGISRestClient;
import com.atakmap.android.featurelink.plugin.R;
import com.atakmap.android.maps.MapGroup;
import com.atakmap.android.maps.MapItem;
import com.atakmap.android.maps.MapView;
import com.atakmap.android.maps.Marker;
import com.atakmap.android.maps.PointMapItem;
import android.util.Log;
import com.atakmap.coremap.maps.coords.GeoPoint;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
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
    private static final String PREF_PLI_AUTO_SEND      = "pli_auto_send";
    private static final String PREF_PORTAL_URL         = "portal_url";
    private static final String PREF_LAYERS_JSON        = "layers_json";
    private static final String PREF_PUBLIC_LAYERS_JSON = "public_layers_json";

    private final Context pluginContext;
    private final ArcGISAuthManager authManager;
    private final ArcGISRestClient restClient;
    private final SharedPreferences prefs;
    private final ExecutorService executor = Executors.newFixedThreadPool(2);
    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private ScheduledExecutorService pliScheduler;

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
    private EditText publicLayerUrlEdit;

    // Layers page — private layers section
    private LinearLayout privateLayersList;
    private TextView layersSignInHint;

    // Layers page — collapsible section state
    private LinearLayout privateLayersContent, publicLayersContent;
    private android.widget.ImageButton collapsePrivateBtn, collapsePublicBtn;
    private boolean privateLayersExpanded = true;
    private boolean publicLayersExpanded  = true;

    // Data
    private final List<ArcGISLayer> privateLayers = new ArrayList<>();
    private final List<ArcGISLayer> publicLayers  = new ArrayList<>();
    private String pliLayerUrl = null;

    /** Tracks ATAK Markers added per layer URL so they can be refreshed or removed. */
    private final Map<String, List<Marker>> layerMarkers = new HashMap<>();

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

        tabHome.setOnClickListener(v   -> navigatePage(0));
        tabLayers.setOnClickListener(v -> navigatePage(1));
        tabPli.setOnClickListener(v    -> navigatePage(2));
        accountBtn.setOnClickListener(v -> showAccountPage());

        setupSwipeGesture(pageContainer);
        wireHomePageViews();
        wireLayersPageViews();
        wirePliPageViews();
        wireAccountPageView();
        wireAddLayerPageView();

        loadSavedData();
        navigatePage(0);
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

        statusLayersText.setText(privateLayers.size() + " private, " + publicLayers.size() + " public");
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

    /** Colors the "PLI Feature Layer" section header red/green based on connection status. */
    private void updatePliConnectionIndicator() {
        if (pliFeatureLayerTitle == null) return;
        pliFeatureLayerTitle.setTextColor(isPliConnected() ? 0xFF4CAF50 : 0xFFFF5722);
    }

    // -------------------------------------------------------------------------
    // Home page — feature statistics
    // -------------------------------------------------------------------------

    private void refreshHomeStats() {
        final List<ArcGISLayer> privateSnap = new ArrayList<>(privateLayers);
        final List<ArcGISLayer> publicSnap  = new ArrayList<>(publicLayers);
        executor.submit(() -> {
            final List<String[]> stats = new ArrayList<>();
            int total = 0;
            String token = authManager.getToken();

            for (ArcGISLayer layer : privateSnap) {
                if (!layer.downloadEnabled) continue;
                long count = cachedCount(layer.url, token);
                layer.featureCount = count;
                total += count;
                stats.add(new String[]{layer.name, String.valueOf(count), "private"});
            }
            for (ArcGISLayer layer : publicSnap) {
                long count = cachedCount(layer.url, null);
                layer.featureCount = count;
                total += count;
                stats.add(new String[]{layer.name, String.valueOf(count), "public"});
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
            List<ArcGISLayer> layers = restClient.searchUserLayers(portal, token, username);
            mainHandler.post(() -> {
                privateLayers.clear();
                for (ArcGISLayer l : layers) {
                    l.type = "private";
                    privateLayers.add(l);
                }
                restoreLayerPreferences(privateLayers);
                savePrivateLayers();
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
        publicLayersList    = layersPageView.findViewById(R.id.public_layers_list);
        publicLayersContent = layersPageView.findViewById(R.id.public_layers_content);
        collapsePublicBtn   = layersPageView.findViewById(R.id.collapse_public_btn);
        Button openAddLayerBtn = layersPageView.findViewById(R.id.open_add_layer_btn);
        openAddLayerBtn.setOnClickListener(v -> showAddLayerPage());
        collapsePublicBtn.setOnClickListener(v -> togglePublicLayersSection());

        // Private layers section
        privateLayersList    = layersPageView.findViewById(R.id.private_layers_list);
        privateLayersContent = layersPageView.findViewById(R.id.private_layers_content);
        collapsePrivateBtn   = layersPageView.findViewById(R.id.collapse_private_btn);
        layersSignInHint     = layersPageView.findViewById(R.id.layers_sign_in_hint);
        Button refreshPrivateBtn = layersPageView.findViewById(R.id.refresh_private_layers_btn);
        refreshPrivateBtn.setOnClickListener(v -> {
            if (authManager.isAuthenticated()) fetchUserLayers();
            else refreshPrivateLayers();
        });
        collapsePrivateBtn.setOnClickListener(v -> togglePrivateLayersSection());
    }

    private void togglePrivateLayersSection() {
        privateLayersExpanded = !privateLayersExpanded;
        privateLayersContent.setVisibility(privateLayersExpanded ? View.VISIBLE : View.GONE);
        collapsePrivateBtn.setImageResource(privateLayersExpanded
                ? R.drawable.ic_chevron_up : R.drawable.ic_chevron_down);
    }

    private void togglePublicLayersSection() {
        publicLayersExpanded = !publicLayersExpanded;
        publicLayersContent.setVisibility(publicLayersExpanded ? View.VISIBLE : View.GONE);
        collapsePublicBtn.setImageResource(publicLayersExpanded
                ? R.drawable.ic_chevron_up : R.drawable.ic_chevron_down);
    }

    private void refreshLayersList() {
        refreshPublicLayers();
        refreshPrivateLayers();
        refreshHomeStatusCard();
    }

    private void refreshPublicLayers() {
        populateLayerList(publicLayersList, publicLayers);
    }

    private void refreshPrivateLayers() {
        boolean authed = authManager.isAuthenticated();
        layersSignInHint.setVisibility(authed ? View.GONE    : View.VISIBLE);
        privateLayersList.setVisibility(authed ? View.VISIBLE : View.GONE);
        populateLayerList(privateLayersList, privateLayers);
    }

    /**
     * Populates a plain vertical container (rather than a ListView) so each list can grow to
     * its natural height and the whole Layers page scrolls as one unit.
     */
    private void populateLayerList(LinearLayout container, List<ArcGISLayer> layers) {
        container.removeAllViews();
        LayerListAdapter adapter = new LayerListAdapter(
                pluginContext, layers, this::onLayerAction, this::toggleLayerVisibility);
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

    private void toggleLayerVisibility(ArcGISLayer layer) {
        layer.visible = !layer.visible;
        List<Marker> markers = layerMarkers.get(layer.url);
        if (markers != null) {
            for (Marker m : markers) m.setVisible(layer.visible);
        }
        if ("private".equals(layer.type)) {
            savePrivateLayers();
            refreshPrivateLayers();
        } else {
            savePublicLayers();
            refreshPublicLayers();
        }
    }

    private void onLayerAction(ArcGISLayer layer) {
        if ("public".equals(layer.type)) {
            // Action button on a public layer = remove it — confirm first, it also removes
            // any markers already placed on the map
            if (publicLayers.contains(layer)) {
                confirmRemovePublicLayer(layer);
            }
        } else {
            // Action button or interval change on a private layer — save and sync
            savePrivateLayers();
            downloadLayer(layer);
        }
    }

    private void confirmRemovePublicLayer(ArcGISLayer layer) {
        new AlertDialog.Builder(getMapView().getContext())
                .setTitle("Remove Layer?")
                .setMessage("Remove \"" + layer.name + "\" from your layer list? "
                        + "Any markers it added to the map will also be removed.")
                .setPositiveButton("Remove", (d, w) -> {
                    publicLayers.remove(layer);
                    savePublicLayers();
                    removeLayerMarkers(layer);
                    layerDisplayConfigs.remove(layer.url);
                    refreshPublicLayers();
                })
                .setNegativeButton("Cancel", null)
                .show();
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
            mainHandler.post(() -> {
                addPublicLayerBtn.setEnabled(true);
                if (layer != null) {
                    layer.type = "public";
                    publicLayers.add(layer);
                    savePublicLayers();
                    publicLayerUrlEdit.setText("");
                    hideOverlay();
                    navigatePage(1);
                    downloadLayer(layer);
                } else {
                    Toast.makeText(pluginContext, "Could not load layer from URL",
                            Toast.LENGTH_SHORT).show();
                }
            });
        });
    }

    /** Removes any ATAK markers previously placed on the map for this layer. */
    private void removeLayerMarkers(ArcGISLayer layer) {
        List<Marker> old = layerMarkers.remove(layer.url);
        if (old != null) {
            MapGroup root = getMapView().getRootGroup();
            for (Marker m : old) root.removeItem(m);
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
                List<ArcGISRestClient.DownloadedFeature> features =
                        restClient.downloadLayerAsCoT(layer.url, token, cotMapping);
                Log.d(TAG, "downloadLayer: downloaded " + features.size() + " features from " + layer.url);
                layer.lastSync = System.currentTimeMillis();
                savePrivateLayers();
                mainHandler.post(() -> {
                    // Swap out old markers for this layer
                    MapGroup root = getMapView().getRootGroup();
                    removeLayerMarkers(layer);
                    List<Marker> added = new ArrayList<>(features.size());
                    int debugLogged = 0;
                    for (ArcGISRestClient.DownloadedFeature f : features) {
                        GeoPoint gp = Double.isNaN(f.hae)
                                ? new GeoPoint(f.lat, f.lon)
                                : new GeoPoint(f.lat, f.lon, f.hae);
                        Marker m = new Marker(gp, f.uid);
                        m.setType(f.cotType);

                        if (displayConfig != null) {
                            // Apply custom iconset icon (sym type "ic", or "adv" per-value icon)
                            String iconsetPath = displayConfig.resolveIconsetPath(f.attributes);
                            if (iconsetPath != null) {
                                m.setMetaString(com.atakmap.android.icons.UserIcon.IconsetPath,
                                        iconsetPath);
                            }
                            if (debugLogged < 8) {
                                debugLogged++;
                                String symType = displayConfig.sym != null ? displayConfig.sym.type : "null";
                                String fieldName = displayConfig.sym != null ? displayConfig.sym.fieldName : "";
                                String rawVal = fieldName.isEmpty() ? "" : f.attributes.getOrDefault(fieldName, "<missing>");
                                int advCount = (displayConfig.sym != null && displayConfig.sym.advValues != null)
                                        ? displayConfig.sym.advValues.size() : -1;
                                Log.d(TAG, "downloadLayer icon-debug: uid=" + f.uid
                                        + " symType=" + symType
                                        + " field=" + fieldName + "=" + rawVal
                                        + " advValues.size=" + advCount
                                        + " resolvedIconsetPath=" + iconsetPath);
                            }

                            // Marker color also tints custom icon bitmaps — a colored fill only
                            // makes sense for shape symbology. When an icon actually applies,
                            // force white (no tint) so the icon shows its own real colors.
                            int color = iconsetPath != null ? Color.WHITE : displayConfig.resolveColor(f.attributes);
                            m.setColor(color);

                            // Apply label from lbl.field; fall back to callsign
                            String label = displayConfig.resolveLabel(f.attributes, f.callsign);
                            m.setMetaString("callsign", label);
                            m.setTitle(label);

                            // Build remarks from popup fields; append any existing remarks
                            String popupRemarks = displayConfig.buildRemarks(f.attributes);
                            String remarks = popupRemarks.isEmpty() ? f.remarks
                                    : (f.remarks.isEmpty() ? popupRemarks
                                            : popupRemarks + "\n" + f.remarks);
                            if (!remarks.isEmpty()) m.setMetaString("remarks", remarks);
                        } else {
                            m.setMetaString("callsign", f.callsign);
                            m.setTitle(f.callsign);
                            if (!f.remarks.isEmpty()) m.setMetaString("remarks", f.remarks);
                        }

                        m.setMetaBoolean("readiness", true);
                        m.setVisible(layer.visible);
                        root.addItem(m);
                        added.add(m);
                    }
                    if (!added.isEmpty()) layerMarkers.put(layer.url, added);
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
                    pliLayerUrl = serviceUrl;
                    pliLayerUrlEdit.setText(serviceUrl);
                    prefs.edit().putString(PREF_PLI_LAYER_URL, serviceUrl).apply();
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
        pliLayerUrl = url;
        prefs.edit().putString(PREF_PLI_LAYER_URL, url).apply();
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

    private void applyScannedDisplayConfig(DisplayConfig config) {
        if (config.url != null && !config.url.isEmpty()) {
            Log.d(TAG, "applyScannedDisplayConfig: url branch, url=" + config.url);
            // v:2 — store the config, load the layer, and download with styling applied
            layerDisplayConfigs.put(config.url, config);
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
                    layer.type = "public";
                    // Avoid duplicate entries by URL
                    boolean alreadyAdded = false;
                    for (ArcGISLayer l : publicLayers) {
                        if (l.url.equals(config.url)) { alreadyAdded = true; break; }
                    }
                    for (ArcGISLayer l : privateLayers) {
                        if (l.url.equals(config.url)) { alreadyAdded = true; break; }
                    }
                    Log.d(TAG, "applyScannedDisplayConfig: alreadyAdded=" + alreadyAdded
                            + " publicLayers.size=" + publicLayers.size() + " lookup(displayConfig present)=" + (layerDisplayConfigs.get(layer.url) != null));
                    if (!alreadyAdded) {
                        publicLayers.add(layer);
                        savePublicLayers();
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

        pliLayerUrl = url;
        prefs.edit().putString(PREF_PLI_LAYER_URL, url).apply();
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
            pliLayerUrl = url;
            prefs.edit().putString(PREF_PLI_LAYER_URL, url).apply();
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

    private void showPrefFileDialog() {
        Intent fileBrowser = new Intent(Intent.ACTION_GET_CONTENT);
        fileBrowser.setType("application/json");
        fileBrowser.addCategory(Intent.CATEGORY_OPENABLE);
        try {
            getMapView().getContext().startActivity(
                    Intent.createChooser(fileBrowser, "Select display preferences JSON"));
        } catch (Exception e) {
            Toast.makeText(pluginContext,
                    "Place your display_prefs.json in /sdcard/atak/featurelink/ and tap Load",
                    Toast.LENGTH_LONG).show();
        }
    }

    // -------------------------------------------------------------------------
    // PLI auto-send
    // -------------------------------------------------------------------------

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

            restClient.addPliFeature(
                    pliLayerUrl, token,
                    self.getUID(),
                    self.getType(),
                    mv.getDeviceCallsign(),
                    self.getMetaString("icon",    ""),
                    self.getMetaString("remarks", ""),
                    self.getMetaString("how",     "m-g"),
                    authManager.getUsername(),
                    pt.getLatitude(),
                    pt.getLongitude(),
                    pt.getAltitude(),
                    pt.getCE(),
                    pt.getLE(),
                    now,
                    now,
                    now + 30_000L,
                    "");
            Log.d(TAG, "PLI sent to feature layer");

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
            pliLayerUrl = prefs.getString(PREF_PLI_LAYER_URL, null);
            if (prefs.getBoolean(PREF_PLI_AUTO_SEND, false)) startPliScheduler();
            updateSetPliEndpointBtn();
        } catch (Exception e) {
            Log.e(TAG, "Failed to load saved layer data", e);
        }
        checkLayerRecurrence();
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

    private void restoreLayerPreferences(List<ArcGISLayer> layers) {
        try {
            String savedJson = prefs.getString(PREF_LAYERS_JSON, null);
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
        final List<ArcGISLayer> privateSnap = new ArrayList<>(privateLayers);
        final List<ArcGISLayer> publicSnap  = new ArrayList<>(publicLayers);
        executor.submit(() -> {
            long now = System.currentTimeMillis();
            for (ArcGISLayer layer : privateSnap) {
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
        // Remove all injected markers from the ATAK map
        MapGroup root = getMapView().getRootGroup();
        for (List<Marker> markers : layerMarkers.values()) {
            for (Marker m : markers) root.removeItem(m);
        }
        layerMarkers.clear();
    }
}
