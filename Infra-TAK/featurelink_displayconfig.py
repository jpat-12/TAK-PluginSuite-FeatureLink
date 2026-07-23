"""
featurelink_displayconfig.py — FeatureLink Display Configurator module for infra-TAK

Registers /featurelink-display-config (and its bundled icon assets) directly
on the Flask app. Call register_routes(app, login_required) from app.py.

Self-contained client-side tool: upload FeatureLayer data (CSV/TSV/JSON/
GeoJSON/Excel), configure symbology, labels, popups, and layer properties,
then export a display config JSON (with QR code) for the FeatureLink ATAK
plugin. Everything runs in the browser — this module just serves the static
page and its bundled iconsets from the console, gated behind the console
login like every other module page. No server-side processing happens here.
"""

import os
from flask import send_from_directory

MODULE_VERSION = '1.0.0'

ASSETS_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'featurelink_displayconfig_assets')


def register_routes(app, login_required):
    @app.route('/featurelink-display-config')
    @app.route('/featurelink-display-config/')
    @login_required
    def featurelink_displayconfig_page():
        resp = send_from_directory(ASSETS_DIR, 'index.html')
        resp.headers['Cache-Control'] = 'no-store, no-cache, must-revalidate'
        return resp

    @app.route('/featurelink-display-config/icons/<path:filename>')
    @login_required
    def featurelink_displayconfig_icon(filename):
        return send_from_directory(os.path.join(ASSETS_DIR, 'icons'), filename)
