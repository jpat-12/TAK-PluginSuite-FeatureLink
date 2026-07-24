#!/bin/bash
# install.sh — install/update the FeatureLink Configs module into a running
# TAK Portal deployment (https://github.com/AdventureSeeker423/TAK-Portal),
# as deployed by infra-TAK (default install dir: ~/TAK-Portal, Docker Compose).
#
# What it does:
#   1. If this checkout tracks a git remote, pulls the latest first (no-op
#      when run from inside the TAK-PluginSuite-FeatureLink monorepo).
#   2. Copies routes/*.js and services/*.js into TAK Portal's routes/ and
#      services/, and the two new views/*.ejs into TAK Portal's views/.
#   3. Patches (idempotent — safe to re-run):
#      - services/permissions.registry.js: adds the page.featurelink_configs
#        permission (admin CRUD) + carve-outs for the field-user browse/
#        download API and page (open to any logged-in user)
#      - services/portalAuth.middleware.js: adds /featurelink and its API to
#        isAllowedNonAdminPath, so any logged-in user can reach it even if
#        they're not in an admin/agency-admin/bridge-member group
#      - server.js: mounts the two new route files + the two page routes
#      - views/partials/sidebar.ejs: adds "FeatureLink" under Onboarding and
#        "FeatureLink Configs" under Administration
#   4. Runs `docker compose up -d --build` in the TAK Portal directory so the
#      patched files are baked into a rebuilt image (matches what `./takportal
#      update` does).
#
# Everyone who logs into TAK Portal can download configs from /featurelink —
# that page is downloading a file through the user's own already-Authentik-
# gated browser session, same as clicking any other download link in the
# portal. The FeatureLink ATAK/WinTAK plugin itself is not a network client
# here: the user downloads the file, then imports it into the plugin
# manually (Import Config), same as any other exported FeatureLink display
# config. No separate token or API auth for the plugin to worry about.
#
# Usage:
#   bash install.sh [/path/to/TAK-Portal]

set -euo pipefail

MODULE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# --- 1. Self-update, if this checkout tracks a git remote -----------------
if git -C "$MODULE_DIR" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    echo "==> Updating module checkout at $MODULE_DIR..."
    git -C "$MODULE_DIR" pull --ff-only || echo "    ⚠ git pull failed — continuing with the files on disk" >&2
fi
SRC_DIR="$MODULE_DIR"

# --- 2. Locate the TAK Portal install -------------------------------------
PORTAL_DIR="${1:-}"
if [ -z "$PORTAL_DIR" ]; then
    for d in "$HOME/TAK-Portal" /opt/TAK-Portal /root/TAK-Portal; do
        if [ -f "$d/server.js" ]; then
            PORTAL_DIR="$d"; break
        fi
    done
fi
if [ -z "$PORTAL_DIR" ] || [ ! -f "$PORTAL_DIR/server.js" ]; then
    echo "ERROR: could not find a TAK Portal install (looked for server.js under ~/TAK-Portal, /opt/TAK-Portal, /root/TAK-Portal)." >&2
    echo "       Pass the path explicitly: bash install.sh /path/to/TAK-Portal" >&2
    exit 1
fi
echo "==> TAK Portal: $PORTAL_DIR"

# --- 3. Sync module files --------------------------------------------------
mkdir -p "$PORTAL_DIR/routes" "$PORTAL_DIR/services" "$PORTAL_DIR/views"
cp -f "$SRC_DIR/routes/featurelinkConfigsAdmin.routes.js" "$PORTAL_DIR/routes/"
cp -f "$SRC_DIR/routes/featurelinkBrowse.routes.js" "$PORTAL_DIR/routes/"
cp -f "$SRC_DIR/services/featurelinkConfigs.service.js" "$PORTAL_DIR/services/"
cp -f "$SRC_DIR/views/featurelink-configs.ejs" "$PORTAL_DIR/views/"
cp -f "$SRC_DIR/views/featurelink.ejs" "$PORTAL_DIR/views/"
echo "==> Synced routes/services/views"

# --- 4. Patch permissions.registry.js (idempotent) -------------------------
node - "$PORTAL_DIR/services/permissions.registry.js" <<'NODEEOF'
const fs = require('fs');
const path = process.argv[2];
let src = fs.readFileSync(path, 'utf-8');
let changed = false;

const PERM_MARKER = 'featurelink_configs:';
if (!src.includes(PERM_MARKER)) {
  const anchor = `    plugin_manager: {
      id: "page.plugin_manager",
      label: "Plugin Manager",
      description: "Upload and manage server-side plugin packages.",
      section: "configuration",
    },`;
  if (!src.includes(anchor)) {
    console.error('ERROR: could not find plugin_manager permission entry anchor — permission NOT added');
    process.exit(1);
  }
  const block = `
    featurelink_configs: {
      id: "page.featurelink_configs",
      label: "FeatureLink Configs",
      description: "Prep FeatureLink display configs for field devices to download.",
      section: "administration",
    },`;
  src = src.replace(anchor, anchor + block);
  changed = true;
  console.log('    + added page.featurelink_configs permission');
} else {
  console.log('    = permission entry already present');
}

const CARVEOUT_MARKER = "p.startsWith(\"/api/featurelink/configs\")";
if (!src.includes(CARVEOUT_MARKER)) {
  const anchor = `  if (p.startsWith("/api/plugins/") && p.endsWith("/download") && m === "GET") return [];`;
  if (!src.includes(anchor)) {
    console.error('ERROR: could not find plugin download carve-out anchor — public API carve-outs NOT added');
    process.exit(1);
  }
  const block = `
  // FeatureLink Configs module: /featurelink (browse/download) is open to any
  // logged-in user, not just admins — see portalAuth.middleware.js's
  // isAllowedNonAdminPath for the group-membership bypass this pairs with.
  if (p.startsWith("/api/featurelink/configs")) return [];
  if (p === "/featurelink" || p.startsWith("/featurelink/")) return [];`;
  src = src.replace(anchor, anchor + block);
  changed = true;
  console.log('    + added public API carve-outs');
} else {
  console.log('    = public API carve-outs already present');
}

const PAGE_MARKER = 'p === "/featurelink-configs"';
if (!src.includes(PAGE_MARKER)) {
  const anchor = `  if (p === "/plugin-manager" || p.startsWith("/plugin-manager/")) return ["page.plugin_manager"];`;
  if (!src.includes(anchor)) {
    console.error('ERROR: could not find plugin-manager page-route anchor — admin page route NOT added');
    process.exit(1);
  }
  const block = `
  if (p === "/featurelink-configs" || p.startsWith("/featurelink-configs/")) return ["page.featurelink_configs"];
  if (p.startsWith("/api/featurelink/admin")) return ["page.featurelink_configs"];`;
  src = src.replace(anchor, anchor + block);
  changed = true;
  console.log('    + added admin page/API route mapping');
} else {
  console.log('    = admin page/API route mapping already present');
}

if (changed) fs.writeFileSync(path, src, 'utf-8');
NODEEOF

# --- 5. Patch portalAuth.middleware.js (idempotent) -------------------------
# /featurelink must be reachable by every logged-in user, not just admins —
# same tier as /setup-my-device and /plugins.
node - "$PORTAL_DIR/services/portalAuth.middleware.js" <<'NODEEOF'
const fs = require('fs');
const path = process.argv[2];
let src = fs.readFileSync(path, 'utf-8');
let changed = false;

const MARKER = 'normalizedPath === "/featurelink"';
if (!src.includes(MARKER)) {
  const anchor = `      const isAllowedNonAdminPath =
        normalizedPath === "/setup-my-device" ||
        normalizedPath.startsWith("/api/setup-my-device") ||
        normalizedPath === "/api/mou/user-agreement/accept" ||
        normalizedPath === "/api/mou/user-agreement/decline" ||
        normalizedPath === "/plugins" ||
        isPluginDownloadApi;`;
  if (!src.includes(anchor)) {
    console.error('ERROR: could not find isAllowedNonAdminPath anchor in portalAuth.middleware.js — non-admin access NOT added');
    process.exit(1);
  }
  const replacement = `      const isAllowedNonAdminPath =
        normalizedPath === "/setup-my-device" ||
        normalizedPath.startsWith("/api/setup-my-device") ||
        normalizedPath === "/api/mou/user-agreement/accept" ||
        normalizedPath === "/api/mou/user-agreement/decline" ||
        normalizedPath === "/plugins" ||
        normalizedPath === "/featurelink" ||
        normalizedPath.startsWith("/api/featurelink/configs") ||
        isPluginDownloadApi;`;
  src = src.replace(anchor, replacement);
  changed = true;
  console.log('    + added /featurelink to isAllowedNonAdminPath');
} else {
  console.log('    = /featurelink already in isAllowedNonAdminPath');
}

if (changed) fs.writeFileSync(path, src, 'utf-8');
NODEEOF

# --- 6. Patch server.js (idempotent) ---------------------------------------
node - "$PORTAL_DIR/server.js" <<'NODEEOF'
const fs = require('fs');
const path = process.argv[2];
let src = fs.readFileSync(path, 'utf-8');
let changed = false;

const ROUTES_MARKER = 'featurelinkConfigsAdmin.routes';
if (!src.includes(ROUTES_MARKER)) {
  const anchor = 'app.use("/api/plugins", requirePermission("page.plugin_manager"), require("./routes/plugins.routes"));';
  if (!src.includes(anchor)) {
    console.error('ERROR: could not find plugins.routes mount anchor in server.js — routes NOT mounted');
    process.exit(1);
  }
  const block = `
app.use("/api/featurelink/admin", requirePermission("page.featurelink_configs"), require("./routes/featurelinkConfigsAdmin.routes"));
app.use("/api/featurelink", require("./routes/featurelinkBrowse.routes"));`;
  src = src.replace(anchor, anchor + block);
  changed = true;
  console.log('    + mounted FeatureLink Configs routes');
} else {
  console.log('    = routes already mounted');
}

const PAGE_MARKER = 'app.get("/featurelink-configs"';
if (!src.includes(PAGE_MARKER)) {
  const anchor = `app.get("/plugin-manager", requirePermission("page.plugin_manager"), async (req, res) => {`;
  if (!src.includes(anchor)) {
    console.error('ERROR: could not find plugin-manager page-route anchor in server.js — page routes NOT added');
    process.exit(1);
  }
  const block = `app.get("/featurelink-configs", requirePermission("page.featurelink_configs"), (req, res) =>
  res.render("featurelink-configs")
);
app.get("/featurelink", (req, res) => res.render("featurelink"));

`;
  src = src.replace(anchor, block + anchor);
  changed = true;
  console.log('    + added page routes');
} else {
  console.log('    = page routes already present');
}

if (changed) fs.writeFileSync(path, src, 'utf-8');
NODEEOF

# --- 7. Patch views/partials/sidebar.ejs (idempotent) -----------------------
node - "$PORTAL_DIR/views/partials/sidebar.ejs" <<'NODEEOF'
const fs = require('fs');
const path = process.argv[2];
let src = fs.readFileSync(path, 'utf-8');
let changed = false;

const ADMIN_MARKER = '/featurelink-configs';
if (!src.includes(ADMIN_MARKER)) {
  const anchor = `            <% if (canSeeMouNav) { %>
              <a href="/mou" class="<%= isActive('/mou') || isActive('/admin/mou') ? 'active' : '' %>">MOU Documents</a>
            <% } %>`;
  if (!src.includes(anchor)) {
    console.error('ERROR: could not find Administration section anchor in sidebar.ejs — admin link NOT added');
    process.exit(1);
  }
  const block = `
            <% if (_perm("page.featurelink_configs")) { %>
              <a href="/featurelink-configs" class="<%= isActive('/featurelink-configs') ? 'active' : '' %>">FeatureLink Configs</a>
            <% } %>`;
  src = src.replace(anchor, anchor + block);
  changed = true;
  console.log('    + added Administration nav link');
} else {
  console.log('    = Administration nav link already present');
}

// Also make the Administration section actually show up if FeatureLink Configs is the only granted permission.
const CANSEE_MARKER = '    _perm("page.featurelink_configs") ||\n    canSeeMouNav;';
if (!src.includes(CANSEE_MARKER)) {
  const anchor = `    canSeeMouNav;`;
  if (src.includes(anchor)) {
    src = src.replace(anchor, `    _perm("page.featurelink_configs") ||\n    canSeeMouNav;`);
    changed = true;
    console.log('    + included page.featurelink_configs in canSeeAdminSection');
  } else {
    console.log('    ⚠ could not find canSeeAdminSection anchor — Administration section may stay hidden for users with only page.featurelink_configs');
  }
} else {
  console.log('    = canSeeAdminSection already includes page.featurelink_configs');
}

const ONBOARD_MARKER = '/featurelink"';
if (!src.includes(ONBOARD_MARKER)) {
  const anchor = `            <a href="/plugins" class="<%= isActive('/plugins') ? 'active' : '' %>">ATAK Plugins</a>`;
  if (!src.includes(anchor)) {
    console.error('ERROR: could not find Onboarding section anchor in sidebar.ejs — onboarding link NOT added');
    process.exit(1);
  }
  const block = `
            <a href="/featurelink" class="<%= isActive('/featurelink') ? 'active' : '' %>">FeatureLink</a>`;
  src = src.replace(anchor, anchor + block);
  changed = true;
  console.log('    + added Onboarding nav link');
} else {
  console.log('    = Onboarding nav link already present');
}

if (changed) fs.writeFileSync(path, src, 'utf-8');
NODEEOF

# --- 8. Rebuild + restart the stack ----------------------------------------
echo "==> Rebuilding TAK Portal (docker compose up -d --build)..."
( cd "$PORTAL_DIR" && docker compose up -d --build )
echo "==> Done. FeatureLink Configs is live: Onboarding → FeatureLink (everyone), Administration → FeatureLink Configs (admins)."
