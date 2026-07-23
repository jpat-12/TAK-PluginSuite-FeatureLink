#!/bin/bash
# uninstall.sh — remove the FeatureLink Display Configurator module from an
# infra-TAK console.
#
# Reverses exactly what install.sh applied:
#   1. Removes the featurelink_displayconfig.register_routes() block from app.py
#   2. Removes the "FeatureLink Display Config" sidebar link
#   3. Deletes featurelink_displayconfig.py and featurelink_displayconfig_assets/
#      from the console install
#   4. Restarts takwerx-console so the removal takes effect immediately
#
# Safe to run even if the module was never installed (no-ops cleanly).
#
# Usage:
#   sudo bash uninstall.sh

set -euo pipefail

CONSOLE_SERVICE="${CONSOLE_SERVICE:-takwerx-console}"

if [ "$(id -u)" -ne 0 ]; then
    echo "ERROR: run as root (sudo bash $0)" >&2
    exit 1
fi

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

python3 - "$CONSOLE_DIR/app.py" <<'PYEOF'
import sys

path = sys.argv[1]
with open(path, 'r', encoding='utf-8') as f:
    src = f.read()

REG_BLOCK = (
    "\ntry:\n"
    "    import featurelink_displayconfig as _featurelink_displayconfig_module\n"
    "    _featurelink_displayconfig_module.register_routes(app, login_required)\n"
    "except Exception as _e:\n"
    "    print(f'[featurelink_displayconfig] Failed to register FeatureLink Display Config module: {_e}', flush=True)\n"
)
if REG_BLOCK in src:
    src = src.replace(REG_BLOCK, '', 1)
    print("    - removed module registration")
else:
    print("    = module registration not present, nothing to remove")

NAV_LINK = '    parts.append(link(\'/featurelink-display-config\', \'<span class="nav-icon material-symbols-outlined">palette</span><span>FeatureLink Display Config</span>\'))\n'
if NAV_LINK in src:
    src = src.replace(NAV_LINK, '')
    print("    - removed sidebar nav link")
else:
    print("    = sidebar nav link not present, nothing to remove")

with open(path, 'w', encoding='utf-8') as f:
    f.write(src)
PYEOF

if [ -f "$CONSOLE_DIR/featurelink_displayconfig.py" ]; then
    rm -f "$CONSOLE_DIR/featurelink_displayconfig.py"
    echo "    - deleted featurelink_displayconfig.py"
else
    echo "    = featurelink_displayconfig.py not present, nothing to delete"
fi

if [ -d "$CONSOLE_DIR/featurelink_displayconfig_assets" ]; then
    rm -rf "$CONSOLE_DIR/featurelink_displayconfig_assets"
    echo "    - deleted featurelink_displayconfig_assets/"
else
    echo "    = featurelink_displayconfig_assets/ not present, nothing to delete"
fi

# See install.sh for why this uses systemd-run --no-block rather than a
# direct `systemctl restart`.
if command -v systemd-run >/dev/null 2>&1; then
    systemd-run --no-block --collect --quiet -- systemctl restart "$CONSOLE_SERVICE" 2>/dev/null \
        && echo "==> Restart of $CONSOLE_SERVICE scheduled (applies within a few seconds)" \
        || echo "    ⚠ Could not schedule restart of $CONSOLE_SERVICE — restart it manually" >&2
else
    (systemctl restart "$CONSOLE_SERVICE" 2>/dev/null &)
    echo "==> Restart of $CONSOLE_SERVICE requested (systemd-run unavailable, backgrounded instead)"
fi

echo ""
echo "✓ FeatureLink Display Config module uninstalled."
