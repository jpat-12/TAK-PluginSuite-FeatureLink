#!/bin/bash
# install.sh — install/update the FeatureLink Configs module into a running
# TAK Portal deployment (https://github.com/AdventureSeeker423/TAK-Portal),
# as deployed by infra-TAK (default install dir: ~/TAK-Portal, Docker Compose).
#
# What it does:
#   1. If this checkout tracks a git remote, pulls the latest first (no-op
#      when run from inside the TAK-PluginSuite-FeatureLink monorepo).
#   2. Copies routes/*.js and services/*.js into TAK Portal's routes/ and
#      services/, the two views/*.ejs into TAK Portal's views/, and the ported
#      Display Configurator (index.html + iconsets) into TAK Portal's assets/.
#   3. Patches (idempotent — safe to re-run):
#      - services/permissions.registry.js: adds the page.featurelink_configs
#        permission (admin) + carve-outs for the field-user browse/download
#        API and page (open to any logged-in user)
#      - services/portalAuth.middleware.js: adds /featurelink and its API to
#        isAllowedNonAdminPath, so any logged-in user can reach it even if
#        they're not in an admin/agency-admin/bridge-member group
#      - server.js: mounts the route files (admin dataset CRUD, the
#        configurator's static assets, the field-user browse API) + the two
#        page routes
#      - views/partials/sidebar.ejs: adds "FeatureLink" under Onboarding and
#        "FeatureLink Configs" under Administration
#   4. Runs `docker compose up -d --build` in the TAK Portal directory so the
#      patched files are baked into a rebuilt image (matches what `./takportal
#      update` does).
#
# Administration → FeatureLink Configs is the full Display Configurator
# (upload/URL data, symbology, labels, popups, QR sharing) — the same tool
# infra-TAK runs, ported in as its own copy rather than a hand-built form.
# Field users browse the saved configs at Onboarding → FeatureLink and
# download or Open in ATAK through their own already-Authentik-gated browser
# session — the FeatureLink ATAK/WinTAK plugin itself never makes its own
# network call here, so there's no separate token/API auth for it to need.
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

# Migrate off the old hand-built catalog (superseded by the ported configurator below)
rm -f "$PORTAL_DIR/routes/featurelinkConfigsAdmin.routes.js"
rm -f "$PORTAL_DIR/services/featurelinkConfigs.service.js"

cp -f "$SRC_DIR/routes/featurelinkDatasetsAdmin.routes.js" "$PORTAL_DIR/routes/"
cp -f "$SRC_DIR/routes/featurelinkConfigurator.routes.js" "$PORTAL_DIR/routes/"
cp -f "$SRC_DIR/routes/featurelinkCustomIcons.routes.js" "$PORTAL_DIR/routes/"
cp -f "$SRC_DIR/routes/featurelinkBrowse.routes.js" "$PORTAL_DIR/routes/"
cp -f "$SRC_DIR/services/featurelinkDatasets.service.js" "$PORTAL_DIR/services/"
cp -f "$SRC_DIR/services/featurelinkCustomIcons.service.js" "$PORTAL_DIR/services/"
cp -f "$SRC_DIR/services/featurelinkArcgisIconset.service.js" "$PORTAL_DIR/services/"
cp -f "$SRC_DIR/views/featurelink-configs.ejs" "$PORTAL_DIR/views/"
cp -f "$SRC_DIR/views/featurelink.ejs" "$PORTAL_DIR/views/"

# The ported Display Configurator (index.html + bundled iconsets) — same asset
# infra-TAK's Infra-TAK/featurelink_displayconfig_assets/ serves, copied in as-is
# except for its three hardcoded URL references (see index.html's edit comments).
mkdir -p "$PORTAL_DIR/assets"
rm -rf "$PORTAL_DIR/assets/featurelink-configurator"
cp -r "$SRC_DIR/assets/featurelink-configurator" "$PORTAL_DIR/assets/featurelink-configurator"

echo "==> Synced routes/services/views/assets"

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
  if (p === "/featurelink-configs/configurator" || p.startsWith("/featurelink-configs/configurator/")) return [];
  if (p === "/featurelink-configs" || p.startsWith("/featurelink-configs/")) return ["page.featurelink_configs"];
  if (p.startsWith("/api/featurelink/admin/datasets") || p.startsWith("/api/featurelink/admin/custom-icons")) return [];`;
  src = src.replace(anchor, anchor + block);
  changed = true;
  console.log('    + added admin page/API route mapping');
} else {
  console.log('    = admin page/API route mapping already present');
}

// Open access migration: anyone can create/edit their own configs — the configurator
// and its dataset API no longer require page.featurelink_configs (ownership is now
// enforced per-record inside featurelinkDatasetsAdmin.routes.js instead). The
// Administration hub page (/featurelink-configs exactly) stays admin-only above.
const OPEN_MARKER = 'featurelink-configs/configurator" || p.startsWith("/featurelink-configs/configurator/")) return [];';
if (!src.includes(OPEN_MARKER)) {
  const oldAdminLine = '  if (p.startsWith("/api/featurelink/admin")) return ["page.featurelink_configs"];';
  if (src.includes(oldAdminLine)) {
    src = src.replace(oldAdminLine, '  if (p.startsWith("/api/featurelink/admin/datasets") || p.startsWith("/api/featurelink/admin/custom-icons")) return [];');
  }
  const featurelinkConfigsRule = '  if (p === "/featurelink-configs" || p.startsWith("/featurelink-configs/")) return ["page.featurelink_configs"];';
  if (src.includes(featurelinkConfigsRule)) {
    const configuratorCarveout = '  if (p === "/featurelink-configs/configurator" || p.startsWith("/featurelink-configs/configurator/")) return [];\n';
    src = src.replace(featurelinkConfigsRule, configuratorCarveout + featurelinkConfigsRule);
  }
  changed = true;
  console.log('    + opened /api/featurelink/admin/datasets and /featurelink-configs/configurator to any logged-in user');
} else {
  console.log('    = open-access carve-outs already present');
}

// Independent of the two migrations above: add the custom-icons carve-out even if this
// file was already fully migrated to the open-access state by an earlier install.sh run
// (before custom icon sets existed) — those two blocks' own guard markers would already
// be satisfied and skip entirely otherwise.
const CUSTOM_ICONS_CARVEOUT_MARKER = '/api/featurelink/admin/custom-icons';
if (!src.includes(CUSTOM_ICONS_CARVEOUT_MARKER)) {
  const datasetsLine = '  if (p.startsWith("/api/featurelink/admin/datasets")) return [];';
  if (src.includes(datasetsLine)) {
    src = src.replace(datasetsLine, '  if (p.startsWith("/api/featurelink/admin/datasets") || p.startsWith("/api/featurelink/admin/custom-icons")) return [];');
    changed = true;
    console.log('    + added custom icon set carve-out');
  }
} else {
  console.log('    = custom icon set carve-out already present');
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
        normalizedPath.startsWith("/featurelink-configs/configurator") ||
        normalizedPath.startsWith("/api/featurelink/admin/datasets") ||
        normalizedPath.startsWith("/api/featurelink/admin/custom-icons") ||
        isPluginDownloadApi;`;
  src = src.replace(anchor, replacement);
  changed = true;
  console.log('    + added /featurelink, the configurator, and its dataset API to isAllowedNonAdminPath');
} else {
  console.log('    = /featurelink already in isAllowedNonAdminPath');
}

// Migration: an earlier version of this patch only added /featurelink + /api/featurelink/configs —
// add the configurator + dataset-API paths too if they're missing from an already-migrated file.
const CONFIGURATOR_MARKER = 'featurelink-configs/configurator")';
if (src.includes(MARKER) && !src.includes(CONFIGURATOR_MARKER)) {
  const oldTail = `        normalizedPath === "/featurelink" ||
        normalizedPath.startsWith("/api/featurelink/configs") ||
        isPluginDownloadApi;`;
  if (src.includes(oldTail)) {
    src = src.replace(oldTail, `        normalizedPath === "/featurelink" ||
        normalizedPath.startsWith("/api/featurelink/configs") ||
        normalizedPath.startsWith("/featurelink-configs/configurator") ||
        normalizedPath.startsWith("/api/featurelink/admin/datasets") ||
        normalizedPath.startsWith("/api/featurelink/admin/custom-icons") ||
        isPluginDownloadApi;`);
    changed = true;
    console.log('    + added the configurator + dataset-API paths to an already-migrated isAllowedNonAdminPath');
  }
} else if (src.includes(CONFIGURATOR_MARKER)) {
  console.log('    = configurator + dataset-API paths already in isAllowedNonAdminPath');
}

// Independent of the above: add custom-icons even if this file already has the
// configurator + dataset-API paths from an earlier install.sh run (before custom icon
// sets existed) — both blocks above would already consider themselves done otherwise.
const CUSTOM_ICONS_MARKER = 'admin/custom-icons")';
if (!src.includes(CUSTOM_ICONS_MARKER)) {
  const datasetsLine = '        normalizedPath.startsWith("/api/featurelink/admin/datasets") ||';
  if (src.includes(datasetsLine)) {
    src = src.replace(datasetsLine, datasetsLine + '\n        normalizedPath.startsWith("/api/featurelink/admin/custom-icons") ||');
    changed = true;
    console.log('    + added custom icon set path to isAllowedNonAdminPath');
  }
} else {
  console.log('    = custom icon set path already in isAllowedNonAdminPath');
}

if (changed) fs.writeFileSync(path, src, 'utf-8');
NODEEOF

# --- 6. Patch server.js (idempotent) ---------------------------------------
node - "$PORTAL_DIR/server.js" <<'NODEEOF'
const fs = require('fs');
const path = process.argv[2];
let src = fs.readFileSync(path, 'utf-8');
let changed = false;

// Migrate off the old hand-built-catalog mount (superseded by the ported configurator).
// Removed entirely (not left in place) — the fresh-install block below re-adds
// featurelinkBrowse.routes alongside the two new mounts, so this must not leave
// a lone copy behind or that route would end up mounted twice.
const OLD_MOUNT = `
app.use("/api/featurelink/admin", requirePermission("page.featurelink_configs"), require("./routes/featurelinkConfigsAdmin.routes"));
app.use("/api/featurelink", require("./routes/featurelinkBrowse.routes"));`;
if (src.includes(OLD_MOUNT)) {
  src = src.replace(OLD_MOUNT, '');
  changed = true;
  console.log('    - migrated off the old featurelinkConfigsAdmin.routes mount');
}

const ROUTES_MARKER = 'featurelinkDatasetsAdmin.routes';
if (!src.includes(ROUTES_MARKER)) {
  const anchor = 'app.use("/api/plugins", requirePermission("page.plugin_manager"), require("./routes/plugins.routes"));';
  if (!src.includes(anchor)) {
    console.error('ERROR: could not find plugins.routes mount anchor in server.js — routes NOT mounted');
    process.exit(1);
  }
  const block = `
app.use("/api/featurelink/admin/datasets", require("./routes/featurelinkDatasetsAdmin.routes"));
app.use("/api/featurelink/admin/custom-icons", require("./routes/featurelinkCustomIcons.routes"));
app.use("/featurelink-configs/configurator", require("./routes/featurelinkConfigurator.routes"));
app.use("/api/featurelink", require("./routes/featurelinkBrowse.routes"));`;
  src = src.replace(anchor, anchor + block);
  changed = true;
  console.log('    + mounted FeatureLink Configs routes');
} else {
  console.log('    = routes already mounted');
}

// Open access migration: an earlier version of this patch gated both mounts behind
// requirePermission("page.featurelink_configs") — any logged-in user can now reach them
// (ownership of individual configs is enforced inside featurelinkDatasetsAdmin.routes.js).
// Runs BEFORE the custom-icons mount check below so that check's anchor search always
// sees the current (unwrapped) mount form within a single pass, even on a first run
// against an old, permission-gated install.
const OLD_DATASETS_MOUNT = 'app.use("/api/featurelink/admin/datasets", requirePermission("page.featurelink_configs"), require("./routes/featurelinkDatasetsAdmin.routes"));';
const OLD_CONFIGURATOR_MOUNT = 'app.use("/featurelink-configs/configurator", requirePermission("page.featurelink_configs"), require("./routes/featurelinkConfigurator.routes"));';
if (src.includes(OLD_DATASETS_MOUNT) || src.includes(OLD_CONFIGURATOR_MOUNT)) {
  src = src.replace(OLD_DATASETS_MOUNT, 'app.use("/api/featurelink/admin/datasets", require("./routes/featurelinkDatasetsAdmin.routes"));');
  src = src.replace(OLD_CONFIGURATOR_MOUNT, 'app.use("/featurelink-configs/configurator", require("./routes/featurelinkConfigurator.routes"));');
  changed = true;
  console.log('    + opened the dataset API + configurator mounts to any logged-in user');
} else {
  console.log('    = dataset API + configurator mounts already open');
}

const CUSTOM_ICONS_MOUNT_MARKER = 'featurelinkCustomIcons.routes';
if (!src.includes(CUSTOM_ICONS_MOUNT_MARKER)) {
  const anchor = 'app.use("/api/featurelink/admin/datasets", require("./routes/featurelinkDatasetsAdmin.routes"));';
  if (src.includes(anchor)) {
    src = src.replace(anchor, anchor + '\napp.use("/api/featurelink/admin/custom-icons", require("./routes/featurelinkCustomIcons.routes"));');
    changed = true;
    console.log('    + mounted custom icon set routes');
  }
} else {
  console.log('    = custom icon set routes already mounted');
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
