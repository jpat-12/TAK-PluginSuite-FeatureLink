#!/usr/bin/env bash
#
# install.sh — deploy this CloudTAK plugin into a CloudTAK checkout.
#
# This is a web-only plugin (no server routes): the web half lands in
#   plugin/  → <CloudTAK>/api/web/plugins/featurelink/   (entry = featurelink/index.ts)
# and the API image is rebuilt so it is baked in.
#
# CloudTAK's native WEB_PLUGINS env var canNOT install this cleanly: it clones the whole
# repo (nesting the web entry one level too deep for Vite's glob). So we copy the web half
# into place ourselves and rebuild.
#
# Usage:
#   Install:  ./install.sh [/path/to/CloudTAK]
#   Update:   ./install.sh --pull [/path/to/CloudTAK]   (git pull this repo, then reinstall + rebuild)
#   Remove:   ./install.sh --remove [/path/to/CloudTAK]
#
# Options:
#   /path/to/CloudTAK   Your CloudTAK checkout (the dir containing docker-compose.yml). Default: ~/CloudTAK
#   --pull              git pull this plugin repo first (latest version).
#   --no-build          Copy/remove files only; skip the docker rebuild + restart.
#   --remove            Uninstall: delete the copied files, then rebuild.
#
# Requires: bash; git (only for --pull); and (unless --no-build) docker + docker compose.

set -euo pipefail

# Web-plugin dir name under api/web/plugins/. Must match the route paths/keys in plugin/index.ts.
INSTALL_DIR_NAME="featurelink"

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

usage() { sed -n '/^# Usage:/,/^# Requires:/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }

# --- parse args --------------------------------------------------------------------
ORIGINAL_ARGS=("$@")
CT_DIR=""; DO_BUILD=1; DO_PULL=0; ACTION="install"
while [ $# -gt 0 ]; do
    case "$1" in
        --pull)       DO_PULL=1 ;;
        --no-build)   DO_BUILD=0 ;;
        --remove)     ACTION="remove" ;;
        -h|--help)    usage; exit 0 ;;
        -*)           echo "Unknown option: $1" >&2; echo >&2; usage >&2; exit 2 ;;
        *)            CT_DIR="$1" ;;
    esac
    shift
done
CT_DIR="${CT_DIR:-$HOME/CloudTAK}"

# --- validate the CloudTAK checkout ------------------------------------------------
[ -d "$CT_DIR" ]     || { echo "ERROR: CloudTAK dir not found: $CT_DIR" >&2; exit 1; }
[ -d "$CT_DIR/api" ] || { echo "ERROR: $CT_DIR is not a CloudTAK checkout (no api/ dir)." >&2; exit 1; }
if [ "$DO_BUILD" -eq 1 ] && [ ! -f "$CT_DIR/docker-compose.yml" ]; then
    echo "ERROR: no docker-compose.yml in $CT_DIR — re-run with --no-build to copy only." >&2; exit 1
fi

WEB_DEST="$CT_DIR/api/web/plugins/$INSTALL_DIR_NAME"

echo "CloudTAK:  $CT_DIR"
echo "Plugin:    $REPO_DIR"
echo "Action:    $ACTION"
echo

# --- optional self-update ----------------------------------------------------------
if [ "$DO_PULL" -eq 1 ]; then
    # Resolve the repository ROOT rather than assuming this script sits at it. This plugin used
    # to be its own repo; inside the suite it lives at <root>/CloudTAK/, so the old
    # `[ -d "$REPO_DIR/.git" ]` test always failed and --pull could not be used at all.
    GIT_ROOT="$(git -C "$REPO_DIR" rev-parse --show-toplevel 2>/dev/null || true)"
    [ -n "$GIT_ROOT" ] || { echo "ERROR: --pull given but $REPO_DIR is not inside a git checkout." >&2; exit 1; }
    echo "Pulling latest plugin source into $GIT_ROOT..."; git -C "$GIT_ROOT" pull; echo
    _no_pull=()
    for _a in "${ORIGINAL_ARGS[@]}"; do [ "$_a" != "--pull" ] && _no_pull+=("$_a"); done
    exec bash "$BASH_SOURCE" "${_no_pull[@]}"
fi

if [ "$ACTION" = "remove" ]; then
    # --- uninstall -----------------------------------------------------------------
    [ -d "$WEB_DEST" ] && { rm -rf "$WEB_DEST"; echo "Removed web plugin: api/web/plugins/$INSTALL_DIR_NAME"; }
else
    # --- install / update ----------------------------------------------------------
    [ -d "$REPO_DIR/plugin" ] || { echo "ERROR: $REPO_DIR/plugin not found — run from the plugin repo." >&2; exit 1; }
    mkdir -p "$CT_DIR/api/web/plugins"

    # Replace the dir wholesale so removed files don't linger, and so the entry lands
    # at the correct depth: api/web/plugins/<NAME>/index.ts
    rm -rf "$WEB_DEST"; cp -R "$REPO_DIR/plugin" "$WEB_DEST"

    # Strip dev-only files from the DEPLOYED copy. CloudTAK runs `npm run lint` and
    # `npm run check` (vue-tsc) across ./plugins/ as part of the api image build, so anything
    # left here is compiled by CloudTAK — and these files import vitest, @vue/test-utils and
    # @vitejs/plugin-vue, none of which exist inside that image. They are for developing this
    # plugin in isolation and have no business in the bundle. node_modules is stripped for the
    # same reason plus size: `cp -R` would otherwise copy a local `npm install` into CloudTAK.
    rm -rf "$WEB_DEST/node_modules" "$WEB_DEST/test"
    rm -f  "$WEB_DEST/vitest.config.ts" "$WEB_DEST/eslint.config.js" \
           "$WEB_DEST/tsconfig.json" "$WEB_DEST/package.json" "$WEB_DEST/package-lock.json"

    echo "Installed web plugin: api/web/plugins/$INSTALL_DIR_NAME"
fi
echo

# --- rebuild -----------------------------------------------------------------------
if [ "$DO_BUILD" -eq 1 ]; then
    echo "Rebuilding CloudTAK API image — this takes 5-15 minutes..."
    ( cd "$CT_DIR" && docker compose build --no-cache api )
    echo "Restarting CloudTAK API container..."
    ( cd "$CT_DIR" && docker compose up -d --force-recreate api )
else
    echo "Skipped CloudTAK rebuild (--no-build). To apply, run in $CT_DIR:"
    echo "    docker compose build --no-cache api && docker compose up -d --force-recreate api"
fi

echo
if [ "$ACTION" = "remove" ]; then
    echo "✓ Plugin removed."
else
    echo "✓ Plugin installed."
    echo "  → In CloudTAK: Settings → Refresh App to activate the new service worker."
    echo "    (A hard refresh does NOT work — the service worker intercepts requests.)"
    echo "    The plugin appears in the right-side menu as FeatureLink."
    echo "  → Account sign-in uses your ArcGIS username/password directly — no app"
    echo "    registration needed. See README.md for details/trade-offs."
fi
