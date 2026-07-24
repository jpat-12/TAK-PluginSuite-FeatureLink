#!/bin/bash
# uninstall.sh — remove the FeatureLink Display Configurator module from an
# infra-TAK console.
#
# Reverses exactly what install.sh applied:
#   1. Removes the featurelink_displayconfig.register_routes() block from app.py
#   2. Removes the "FeatureLink Display Config" sidebar link and its icon constant
#   3. Removes its home page module card entry (detect_modules()) and its
#      entry in the module-card name-label whitelist
#   4. Deletes featurelink_displayconfig.py and featurelink_displayconfig_assets/
#      from the console install
#   5. Restarts takwerx-console so the removal takes effect immediately
#
# Saved dataset configs under CONFIG_DIR/featurelink_displayconfig/datasets/
# are left in place — they're user data, not part of the console integration.
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

NAV_LINK = (
    "    parts.append(link('/featurelink-display-config', "
    "f'<img src=\"{html.escape(FEATURELINK_DISPLAYCONFIG_ICON_URL)}\" alt=\"FeatureLink Display Config\" "
    "class=\"nav-icon\" style=\"height:24px;width:auto;max-width:48px;object-fit:contain;display:block\">"
    "<span>FeatureLink Display Config</span>', 'FeatureLink Display Config'))\n"
)
if NAV_LINK in src:
    src = src.replace(NAV_LINK, '')
    print("    - removed sidebar nav link")
else:
    print("    = sidebar nav link not present, nothing to remove")

ICON_CONST = "FEATURELINK_DISPLAYCONFIG_ICON_URL = 'https://tak.gov/assets/logos/brand-06b80939.svg'\n"
if ICON_CONST in src:
    src = src.replace(ICON_CONST, '')
    print("    - removed icon constant")
else:
    print("    = icon constant not present, nothing to remove")

MODULE_ENTRY = (
    "    # FeatureLink Display Config — static configurator page; installed once\n"
    "    # this module's routes are registered (no separate deploy/running state)\n"
    "    modules['featurelink_displayconfig'] = {\n"
    "        'name': 'FeatureLink Display Config',\n"
    "        'installed': True,\n"
    "        'running': True,\n"
    "        'description': 'Build & save FeatureLink display configs — symbology, labels, popups, QR export',\n"
    "        'icon': '\\U0001f3a8',\n"
    "        'icon_url': FEATURELINK_DISPLAYCONFIG_ICON_URL,\n"
    "        'route': '/featurelink-display-config',\n"
    "        'priority': 2,\n"
    "    }\n"
)
if MODULE_ENTRY in src:
    src = src.replace(MODULE_ENTRY, '')
    print("    - removed home page module card entry")
else:
    print("    = home page module card entry not present, nothing to remove")

CARD_WHITELIST_OLD = "'takportal', 'fedhub', 'emailrelay', 'fail2ban', 'webodm', 'tak_video_restreamer', 'netbird'"
CARD_WHITELIST_WITH_US = CARD_WHITELIST_OLD + ", 'featurelink_displayconfig'"
n = src.count(CARD_WHITELIST_WITH_US)
if n:
    src = src.replace(CARD_WHITELIST_WITH_US, CARD_WHITELIST_OLD)
    print(f"    - removed from module-card name-label whitelist ({n} occurrence(s))")
else:
    print("    = module-card name-label whitelist unchanged, nothing to remove")

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
