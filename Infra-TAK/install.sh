#!/bin/bash
# install.sh — install/update the FeatureLink Display Configurator module into
# a running infra-TAK console.
#
# What it does:
#   1. If this checkout tracks a git remote, pulls the latest first (no-op
#      otherwise — e.g. when run from inside the TAK-PluginSuite-FeatureLink
#      monorepo checkout rather than a standalone module repo).
#   2. Copies featurelink_displayconfig.py + featurelink_displayconfig_assets/
#      (index.html + bundled iconsets) into the infra-TAK install directory.
#   3. Patches app.py (idempotent — safe to re-run) to:
#        a. register the module's routes at startup, same convention as
#           esri.py's register_routes(app, login_required, ...)
#        b. add a "FeatureLink Display Config" link to the console sidebar
#   4. Restarts the takwerx-console systemd service so the link appears
#      immediately.
#
# Usage:
#   sudo bash install.sh

set -euo pipefail

MODULE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONSOLE_SERVICE="${CONSOLE_SERVICE:-takwerx-console}"

if [ "$(id -u)" -ne 0 ]; then
    echo "ERROR: run as root (sudo bash $0 $*)" >&2
    exit 1
fi

# --- 1. Self-update, if this checkout tracks a git remote -----------------
if [ -d "$MODULE_DIR/.git" ]; then
    echo "==> Updating module checkout at $MODULE_DIR..."
    git -C "$MODULE_DIR" pull --ff-only || echo "    ⚠ git pull failed — continuing with the files on disk" >&2
fi
SRC_DIR="$MODULE_DIR"

# --- 2. Locate the infra-TAK install (same search as other modules) -------
CONSOLE_DIR=""
for d in /opt/infra-TAK /opt/infra-tak /root/infra-TAK /root/infra-tak "$HOME/infra-TAK" "$HOME/infra-tak"; do
    if [ -f "$d/app.py" ]; then
        CONSOLE_DIR="$d"; break
    fi
done
if [ -z "$CONSOLE_DIR" ]; then
    CONSOLE_DIR="$(find /root /home /opt -maxdepth 3 -name app.py -path '*infra*' 2>/dev/null | head -1 | xargs -r dirname || true)"
fi
if [ -z "$CONSOLE_DIR" ]; then
    echo "ERROR: could not find an infra-TAK install (looked for app.py under /opt, /root, \$HOME)." >&2
    exit 1
fi
echo "==> infra-TAK console: $CONSOLE_DIR"

# --- 3. Sync module files into the console ------------------------------
cp -f "$SRC_DIR/featurelink_displayconfig.py" "$CONSOLE_DIR/featurelink_displayconfig.py"
rm -rf "$CONSOLE_DIR/featurelink_displayconfig_assets"
mkdir -p "$CONSOLE_DIR/featurelink_displayconfig_assets"
cp -f "$SRC_DIR/featurelink_displayconfig_assets/index.html" "$CONSOLE_DIR/featurelink_displayconfig_assets/index.html"
cp -r "$SRC_DIR/featurelink_displayconfig_assets/icons" "$CONSOLE_DIR/featurelink_displayconfig_assets/icons"
MODULE_VERSION="$(grep -m1 '^MODULE_VERSION' "$CONSOLE_DIR/featurelink_displayconfig.py" | sed -E "s/.*= *'([^']+)'.*/\1/")"
echo "==> Synced featurelink_displayconfig.py (v${MODULE_VERSION:-unknown}) + featurelink_displayconfig_assets/"

# --- 4. Patch app.py (idempotent) ---------------------------------------
python3 - "$CONSOLE_DIR/app.py" <<'PYEOF'
import sys

path = sys.argv[1]
with open(path, 'r', encoding='utf-8') as f:
    src = f.read()

MARKER = '[featurelink_displayconfig] Failed to register'
if MARKER not in src:
    anchor = "print(f'[esri] Failed to register Esri CoT Bridge module: {_e}', flush=True)\n"
    if anchor not in src:
        print("ERROR: could not find esri registration anchor in app.py — module registration NOT applied", file=sys.stderr)
        sys.exit(1)
    block = (
        "\ntry:\n"
        "    import featurelink_displayconfig as _featurelink_displayconfig_module\n"
        "    _featurelink_displayconfig_module.register_routes(app, login_required)\n"
        "except Exception as _e:\n"
        "    print(f'[featurelink_displayconfig] Failed to register FeatureLink Display Config module: {_e}', flush=True)\n"
    )
    src = src.replace(anchor, anchor + block, 1)
    print("    + registered featurelink_displayconfig.register_routes()")
else:
    print("    = module registration already present")

NAV_LINK = '    parts.append(link(\'/featurelink-display-config\', \'<span class="nav-icon material-symbols-outlined">palette</span><span>FeatureLink Display Config</span>\'))\n'
ANCHOR_LINE = '    parts.append(link(\'/marketplace\', \'<span class="nav-icon material-symbols-outlined">shopping_cart</span>Marketplace\'))\n'
if NAV_LINK not in src:
    if ANCHOR_LINE not in src:
        print("ERROR: could not find Marketplace nav-link anchor in render_sidebar() — sidebar link NOT injected", file=sys.stderr)
        sys.exit(1)
    src = src.replace(ANCHOR_LINE, NAV_LINK + ANCHOR_LINE, 1)
    print("    + injected sidebar nav link")
else:
    print("    = sidebar nav link already present")

with open(path, 'w', encoding='utf-8') as f:
    f.write(src)
PYEOF

# --- 5. Restart the console service ---------------------------------------
# See infra-TAK modules' install scripts for why this uses systemd-run
# --no-block rather than a direct `systemctl restart`: a direct restart kills
# this whole process tree (including whatever invoked this script) before it
# can finish or report back.
if command -v systemd-run >/dev/null 2>&1; then
    systemd-run --no-block --collect --quiet -- systemctl restart "$CONSOLE_SERVICE" 2>/dev/null \
        && echo "==> Restart of $CONSOLE_SERVICE scheduled (applies within a few seconds)" \
        || echo "    ⚠ Could not schedule restart of $CONSOLE_SERVICE — restart it manually" >&2
else
    (systemctl restart "$CONSOLE_SERVICE" 2>/dev/null &)
    echo "==> Restart of $CONSOLE_SERVICE requested (systemd-run unavailable, backgrounded instead)"
fi

echo ""
echo "✓ FeatureLink Display Config module installed. Open the console and look"
echo "  for 'FeatureLink Display Config' in the sidebar."
