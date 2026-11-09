#!/bin/bash
# uninstall.sh — remove the FeatureLink Configs module from a TAK Portal
# install. Reverses install.sh's patches to server.js, permissions.registry.js,
# portalAuth.middleware.js, and views/partials/sidebar.ejs, deletes the copied
# route/service/view files, and rebuilds. Does NOT delete
# data/featurelink-configs/ — remove that by hand if you also want the saved
# configs gone.
#
# Usage:
#   bash uninstall.sh [/path/to/TAK-Portal]

set -euo pipefail

PORTAL_DIR="${1:-}"
if [ -z "$PORTAL_DIR" ]; then
    for d in "$HOME/TAK-Portal" /opt/TAK-Portal /root/TAK-Portal; do
        if [ -f "$d/server.js" ]; then
            PORTAL_DIR="$d"; break
        fi
    done
fi
if [ -z "$PORTAL_DIR" ] || [ ! -f "$PORTAL_DIR/server.js" ]; then
    echo "ERROR: could not find a TAK Portal install. Pass the path explicitly: bash uninstall.sh /path/to/TAK-Portal" >&2
    exit 1
fi
echo "==> TAK Portal: $PORTAL_DIR"

node - "$PORTAL_DIR/server.js" <<'NODEEOF'
const fs = require('fs');
const path = process.argv[2];
let src = fs.readFileSync(path, 'utf-8');

// Old (pre-configurator-port) mount, in case uninstall runs against an older install.
src = src.replace(`
app.use("/api/featurelink/admin", requirePermission("page.featurelink_configs"), require("./routes/featurelinkConfigsAdmin.routes"));
app.use("/api/featurelink", require("./routes/featurelinkBrowse.routes"));`, '');

src = src.replace(`
app.use("/api/featurelink/admin/datasets", requirePermission("page.featurelink_configs"), require("./routes/featurelinkDatasetsAdmin.routes"));
app.use("/featurelink-configs/configurator", requirePermission("page.featurelink_configs"), require("./routes/featurelinkConfigurator.routes"));
app.use("/api/featurelink", require("./routes/featurelinkBrowse.routes"));`, '');

// Open-access variant (permission wrapper removed by a later install.sh run)
src = src.replace(`
app.use("/api/featurelink/admin/datasets", require("./routes/featurelinkDatasetsAdmin.routes"));
app.use("/featurelink-configs/configurator", require("./routes/featurelinkConfigurator.routes"));
app.use("/api/featurelink", require("./routes/featurelinkBrowse.routes"));`, '');

// Open-access variant + custom icon set routes (current state)
src = src.replace(`
app.use("/api/featurelink/admin/datasets", require("./routes/featurelinkDatasetsAdmin.routes"));
app.use("/api/featurelink/admin/custom-icons", require("./routes/featurelinkCustomIcons.routes"));
app.use("/featurelink-configs/configurator", require("./routes/featurelinkConfigurator.routes"));
app.use("/api/featurelink", require("./routes/featurelinkBrowse.routes"));`, '');

src = src.replace(`app.get("/featurelink-configs", requirePermission("page.featurelink_configs"), (req, res) =>
  res.render("featurelink-configs")
);
app.get("/featurelink", (req, res) => res.render("featurelink"));

`, '');

fs.writeFileSync(path, src, 'utf-8');
console.log('    - removed FeatureLink Configs routes/pages from server.js');
NODEEOF

node - "$PORTAL_DIR/services/permissions.registry.js" <<'NODEEOF'
const fs = require('fs');
const path = process.argv[2];
let src = fs.readFileSync(path, 'utf-8');

// The featurelink_configs entry, whatever section it was filed under ("administration" on
// older installs, "operations" since the 2.0.32-compatible installer).
{
  const at = src.indexOf('\n    featurelink_configs: {');
  const end = at < 0 ? -1 : src.indexOf('\n    },', at + 1);
  if (at >= 0 && end >= 0) src = src.slice(0, at) + src.slice(end + '\n    },'.length);
}

src = src.replace(`
  // FeatureLink Configs module: /featurelink (browse/download) is open to any
  // logged-in user, not just admins — see portalAuth.middleware.js's
  // isAllowedNonAdminPath for the group-membership bypass this pairs with.
  if (p.startsWith("/api/featurelink/configs")) return [];
  if (p === "/featurelink" || p.startsWith("/featurelink/")) return [];`, '');

// Current (open-access + custom icon sets) state
src = src.replace(`
  if (p === "/featurelink-configs/configurator" || p.startsWith("/featurelink-configs/configurator/")) return [];
  if (p === "/featurelink-configs" || p.startsWith("/featurelink-configs/")) return ["page.featurelink_configs"];
  if (p.startsWith("/api/featurelink/admin/datasets") || p.startsWith("/api/featurelink/admin/custom-icons")) return [];`, '');

// Open-access state, pre-custom-icon-sets
src = src.replace(`
  if (p === "/featurelink-configs/configurator" || p.startsWith("/featurelink-configs/configurator/")) return [];
  if (p === "/featurelink-configs" || p.startsWith("/featurelink-configs/")) return ["page.featurelink_configs"];
  if (p.startsWith("/api/featurelink/admin/datasets")) return [];`, '');

// Older (fully permission-gated, pre-open-access) state, in case uninstall runs against that
src = src.replace(`
  if (p === "/featurelink-configs" || p.startsWith("/featurelink-configs/")) return ["page.featurelink_configs"];
  if (p.startsWith("/api/featurelink/admin")) return ["page.featurelink_configs"];`, '');

fs.writeFileSync(path, src, 'utf-8');
console.log('    - removed FeatureLink Configs permission + route mappings');
NODEEOF

node - "$PORTAL_DIR/services/portalAuth.middleware.js" <<'NODEEOF'
const fs = require('fs');
const path = process.argv[2];
let src = fs.readFileSync(path, 'utf-8');

// Remove exactly the lines install.sh inserts into isAllowedNonAdminPath. Line-based, so it
// works on every upstream shape (2.0.32 added /map and isAtakApkDownloadApi to the expression,
// which the old whole-expression replacements never matched) without touching upstream's lines.
const FL_LINES = new Set([
  'normalizedPath === "/featurelink" ||',
  'normalizedPath.startsWith("/api/featurelink/configs") ||',
  'normalizedPath.startsWith("/featurelink-configs/configurator") ||',
  'normalizedPath.startsWith("/api/featurelink/admin/datasets") ||',
  'normalizedPath.startsWith("/api/featurelink/admin/custom-icons") ||',
]);
src = src.split('\n').filter((l) => !FL_LINES.has(l.trim())).join('\n');

fs.writeFileSync(path, src, 'utf-8');
console.log('    - removed /featurelink from isAllowedNonAdminPath');
NODEEOF

node - "$PORTAL_DIR/views/partials/sidebar.ejs" <<'NODEEOF'
const fs = require('fs');
const path = process.argv[2];
let src = fs.readFileSync(path, 'utf-8');

src = src.replace(`
            <% if (_perm("page.featurelink_configs")) { %>
              <a href="/featurelink-configs" class="<%= isActive('/featurelink-configs') ? 'active' : '' %>">FeatureLink Configs</a>
            <% } %>`, '');

// Every inserted Onboarding link (admins' section AND the non-admin else-branch), and the
// section-visibility clause (canSeeAdminSection on older portals, canSeeOperationsSection now).
const SIDEBAR_LINES = new Set([
  `<a href="/featurelink" class="<%= isActive('/featurelink') ? 'active' : '' %>">FeatureLink</a>`,
  '_perm("page.featurelink_configs") ||',
]);
src = src.split('\n').filter((l) => !SIDEBAR_LINES.has(l.trim())).join('\n');

fs.writeFileSync(path, src, 'utf-8');
console.log('    - removed FeatureLink nav links from sidebar.ejs');
NODEEOF

# Every featurelink*-prefixed route/service/view install.sh copied (by pattern, matching
# install.sh), plus the legacy featurelinkConfigs* files an older install may have left.
rm -f "$PORTAL_DIR"/routes/featurelink*.js "$PORTAL_DIR"/services/featurelink*.js "$PORTAL_DIR"/views/featurelink*.ejs
rm -rf "$PORTAL_DIR/assets/featurelink-configurator"
echo "==> Removed module files"

echo "==> Rebuilding TAK Portal (docker compose up -d --build)..."
( cd "$PORTAL_DIR" && docker compose up -d --build )
echo "==> Done. Saved configs (if any) remain under $PORTAL_DIR/data/featurelink-configs/ — remove by hand if you want them gone too."
