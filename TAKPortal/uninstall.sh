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

src = src.replace(`
    featurelink_configs: {
      id: "page.featurelink_configs",
      label: "FeatureLink Configs",
      description: "Prep FeatureLink display configs for field devices to download.",
      section: "administration",
    },`, '');

src = src.replace(`
  // FeatureLink Configs module: /featurelink (browse/download) is open to any
  // logged-in user, not just admins — see portalAuth.middleware.js's
  // isAllowedNonAdminPath for the group-membership bypass this pairs with.
  if (p.startsWith("/api/featurelink/configs")) return [];
  if (p === "/featurelink" || p.startsWith("/featurelink/")) return [];`, '');

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

const patched = `      const isAllowedNonAdminPath =
        normalizedPath === "/setup-my-device" ||
        normalizedPath.startsWith("/api/setup-my-device") ||
        normalizedPath === "/api/mou/user-agreement/accept" ||
        normalizedPath === "/api/mou/user-agreement/decline" ||
        normalizedPath === "/plugins" ||
        normalizedPath === "/featurelink" ||
        normalizedPath.startsWith("/api/featurelink/configs") ||
        isPluginDownloadApi;`;

const original = `      const isAllowedNonAdminPath =
        normalizedPath === "/setup-my-device" ||
        normalizedPath.startsWith("/api/setup-my-device") ||
        normalizedPath === "/api/mou/user-agreement/accept" ||
        normalizedPath === "/api/mou/user-agreement/decline" ||
        normalizedPath === "/plugins" ||
        isPluginDownloadApi;`;

src = src.replace(patched, original);
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

src = src.replace(`    _perm("page.featurelink_configs") ||\n    canSeeMouNav;`, `    canSeeMouNav;`);

src = src.replace(`
            <a href="/featurelink" class="<%= isActive('/featurelink') ? 'active' : '' %>">FeatureLink</a>`, '');

fs.writeFileSync(path, src, 'utf-8');
console.log('    - removed FeatureLink nav links from sidebar.ejs');
NODEEOF

rm -f "$PORTAL_DIR/routes/featurelinkConfigsAdmin.routes.js"
rm -f "$PORTAL_DIR/routes/featurelinkDatasetsAdmin.routes.js"
rm -f "$PORTAL_DIR/routes/featurelinkConfigurator.routes.js"
rm -f "$PORTAL_DIR/routes/featurelinkBrowse.routes.js"
rm -f "$PORTAL_DIR/services/featurelinkConfigs.service.js"
rm -f "$PORTAL_DIR/services/featurelinkDatasets.service.js"
rm -f "$PORTAL_DIR/views/featurelink-configs.ejs"
rm -f "$PORTAL_DIR/views/featurelink.ejs"
rm -rf "$PORTAL_DIR/assets/featurelink-configurator"
echo "==> Removed module files"

echo "==> Rebuilding TAK Portal (docker compose up -d --build)..."
( cd "$PORTAL_DIR" && docker compose up -d --build )
echo "==> Done. Saved configs (if any) remain under $PORTAL_DIR/data/featurelink-configs/ — remove by hand if you want them gone too."
