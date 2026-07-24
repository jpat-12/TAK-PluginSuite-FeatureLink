"""
featurelink_displayconfig.py — FeatureLink Display Configurator module for infra-TAK

Registers /featurelink-display-config (the configurator), /featurelink-display-config/admin
(the saved-datasets list), and their supporting APIs directly on the Flask app.
Call register_routes(app, login_required) from app.py.

The configurator itself (featurelink_displayconfig_assets/index.html) is a
self-contained client-side tool: upload FeatureLayer data (CSV/TSV/JSON/
GeoJSON/Excel or a live FeatureLayer URL), configure symbology, labels,
popups, and layer properties, then export a display config JSON (with QR
code) for the FeatureLink ATAK plugin.

This module adds persistence on top of that: "Save" on the configurator
POSTs the dataset (a FeatureLayer URL, or the raw uploaded file) + the
display config to this module, which stores it under CONFIG_DIR and lists
it on the admin page. Opening a saved entry reloads the dataset and
re-applies the saved config exactly (no lossy round-trip through the Esri
export format — the internal config object is stored as-is).
"""

import base64
import datetime
import json
import os
import re
import shutil
import uuid

from flask import (
    abort, jsonify, make_response, render_template_string, request,
    send_file, send_from_directory,
)

MODULE_VERSION = '1.1.1'

ASSETS_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'featurelink_displayconfig_assets')

CONFIG_DIR = os.environ.get('CONFIG_DIR') or os.path.join(
    os.path.dirname(os.path.abspath(__file__)), '.config'
)
DATASETS_DIR = os.path.join(CONFIG_DIR, 'featurelink_displayconfig', 'datasets')

# Same brand asset infra-TAK uses for the TAK Server card/nav-link — stand-in
# icon for this module until it has its own.
ICON_URL = 'https://tak.gov/assets/logos/brand-06b80939.svg'

_ID_RE = re.compile(r'[^a-fA-F0-9]')


def _clean_id(dataset_id):
    return _ID_RE.sub('', dataset_id or '')


def _dataset_dir(dataset_id):
    return os.path.join(DATASETS_DIR, dataset_id)


def _record_path(dataset_id):
    return os.path.join(_dataset_dir(dataset_id), 'record.json')


def _data_path(dataset_id):
    return os.path.join(_dataset_dir(dataset_id), 'data.bin')


def list_datasets():
    if not os.path.isdir(DATASETS_DIR):
        return []
    out = []
    for entry in os.listdir(DATASETS_DIR):
        record = load_dataset(entry)
        if record is None:
            continue
        out.append({
            'id': record['id'],
            'name': record.get('name', 'Untitled'),
            'source_type': record.get('source_type', 'file'),
            'source_url': record.get('source_url', ''),
            'file_name': record.get('file_name', ''),
            'field_count': len(record.get('state', {}).get('fields', []) or []),
            'created_at': record.get('created_at', ''),
            'updated_at': record.get('updated_at', ''),
        })
    out.sort(key=lambda d: d.get('updated_at', ''), reverse=True)
    return out


def load_dataset(dataset_id):
    dataset_id = _clean_id(dataset_id)
    if not dataset_id:
        return None
    rp = _record_path(dataset_id)
    if not os.path.isfile(rp):
        return None
    try:
        with open(rp, 'r', encoding='utf-8') as f:
            record = json.load(f)
    except Exception:
        return None
    record['id'] = dataset_id
    return record


def save_dataset(payload):
    dataset_id = _clean_id(payload.get('id')) or uuid.uuid4().hex
    d = _dataset_dir(dataset_id)
    os.makedirs(d, exist_ok=True)

    existing = load_dataset(dataset_id) or {}
    now = datetime.datetime.utcnow().isoformat() + 'Z'
    source_type = 'url' if payload.get('source_type') == 'url' else 'file'

    record = {
        'name': (payload.get('name') or 'Untitled').strip()[:200],
        'source_type': source_type,
        'source_url': (payload.get('source_url') or '').strip(),
        'file_name': (payload.get('file_name') or '').strip(),
        'state': payload.get('state') or {},
        'created_at': existing.get('created_at') or now,
        'updated_at': now,
    }

    file_b64 = payload.get('file_content_b64')
    if source_type == 'file' and file_b64:
        try:
            raw = base64.b64decode(file_b64)
        except Exception:
            raise ValueError('invalid file_content_b64')
        with open(_data_path(dataset_id), 'wb') as f:
            f.write(raw)

    with open(_record_path(dataset_id), 'w', encoding='utf-8') as f:
        json.dump(record, f)

    record['id'] = dataset_id
    return record


def delete_dataset(dataset_id):
    dataset_id = _clean_id(dataset_id)
    if not dataset_id:
        return
    d = _dataset_dir(dataset_id)
    if os.path.isdir(d):
        shutil.rmtree(d)


ADMIN_TEMPLATE = '''<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8"><meta name="viewport" content="width=device-width,initial-scale=1.0">
<title>FeatureLink Display Configs — infra-TAK</title>
<style>
:root{--bg:#1a1d23;--surface:#22262f;--surface2:#2b3040;--border:#3a3f50;--accent:#0078d4;--accent2:#005a9e;
--accent-green:#107c10;--text:#e8eaf0;--text-muted:#8b92a8;--danger:#d13438;--radius:6px}
*{box-sizing:border-box;margin:0;padding:0}
body{font-family:'Segoe UI',system-ui,sans-serif;background:var(--bg);color:var(--text);min-height:100vh}
header{background:var(--surface);border-bottom:1px solid var(--border);padding:0 20px;height:52px;
display:flex;align-items:center;gap:14px;position:sticky;top:0;z-index:10}
header img.logo{height:24px;width:auto}
header h1{font-size:16px;font-weight:600}
header .spacer{flex:1}
header a.back-link{color:var(--text-muted);text-decoration:none;font-size:13px}
header a.back-link:hover{color:var(--text)}
.new-btn{background:var(--accent-green);border:none;color:#fff;padding:8px 18px;border-radius:var(--radius);
cursor:pointer;font-size:13px;font-weight:600;text-decoration:none;display:inline-flex;align-items:center;gap:6px}
.new-btn:hover{opacity:.85}
main{max-width:1000px;margin:0 auto;padding:28px 20px}
.empty{background:var(--surface);border:1px solid var(--border);border-radius:10px;padding:48px;text-align:center;color:var(--text-muted)}
.empty a{color:var(--accent)}
.list{display:flex;flex-direction:column;gap:10px}
.row{background:var(--surface);border:1px solid var(--border);border-radius:10px;padding:14px 18px;
display:flex;align-items:center;gap:16px}
.row-main{flex:1;min-width:0}
.row-name{font-size:14px;font-weight:600;margin-bottom:3px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.row-meta{font-size:12px;color:var(--text-muted);display:flex;gap:10px;flex-wrap:wrap}
.row-meta .src{font-family:monospace;overflow:hidden;text-overflow:ellipsis;max-width:360px;white-space:nowrap}
.row-actions{display:flex;gap:8px;flex-shrink:0}
.row-actions button, .row-actions a{background:var(--surface2);border:1px solid var(--border);color:var(--text);
padding:7px 12px;border-radius:var(--radius);cursor:pointer;font-size:12px;text-decoration:none;white-space:nowrap}
.row-actions a.open-btn{background:var(--accent);border-color:var(--accent)}
.row-actions button.del-btn:hover{background:var(--danger);border-color:var(--danger)}
.row-actions a:hover, .row-actions button:hover{opacity:.85}
.toast{position:fixed;bottom:24px;right:24px;padding:12px 20px;border-radius:10px;font-size:13px;font-weight:600;
z-index:9999;opacity:0;transition:opacity .3s;pointer-events:none;background:var(--accent);color:#fff}
.toast.show{opacity:1}
</style>
</head>
<body>
<header>
<img class="logo" src="{{ icon_url }}" alt="">
<h1>FeatureLink Display Configs</h1>
<span class="spacer"></span>
<a class="back-link" href="/console">&larr; Console</a>
<a class="new-btn" href="/featurelink-display-config">+ New Dataset Config</a>
</header>
<main>
{% if not datasets %}
<div class="empty">
  No saved dataset configs yet.<br><br>
  <a href="/featurelink-display-config">Open the configurator</a> to upload a dataset, set up its
  display config, and click <strong>Save</strong> — it'll show up here.
</div>
{% else %}
<div class="list" id="dataset-list">
{% for d in datasets %}
<div class="row" data-id="{{ d.id }}">
  <div class="row-main">
    <div class="row-name">{{ d.name }}</div>
    <div class="row-meta">
      <span>{{ d.field_count }} field{{ 's' if d.field_count != 1 else '' }}</span>
      <span class="src">{% if d.source_type == 'url' %}&#128279; {{ d.source_url }}{% else %}&#128196; {{ d.file_name }}{% endif %}</span>
      <span>updated {{ d.updated_at.split('T')[0] if d.updated_at else '' }}</span>
    </div>
  </div>
  <div class="row-actions">
    <a class="open-btn" href="/featurelink-display-config?load={{ d.id }}">Open</a>
    <button onclick="copyLink('{{ d.id }}')">Copy link</button>
    <button class="del-btn" onclick="deleteDataset('{{ d.id }}')">Delete</button>
  </div>
</div>
{% endfor %}
</div>
{% endif %}
</main>
<div class="toast" id="toast"></div>
<script>
let toastTimer;
function showToast(msg){
  const el = document.getElementById('toast');
  el.textContent = msg;
  el.classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.classList.remove('show'), 2400);
}
function copyLink(id){
  const url = window.location.origin + '/featurelink-display-config?load=' + encodeURIComponent(id);
  navigator.clipboard.writeText(url).then(
    () => showToast('Link copied — scan or share it to open this config directly'),
    () => showToast(url)
  );
}
async function deleteDataset(id){
  if (!confirm('Delete this saved dataset config? This cannot be undone.')) return;
  try {
    const r = await fetch('/api/featurelink-display-config/datasets/' + encodeURIComponent(id), { method: 'DELETE' });
    if (!r.ok) throw new Error('delete failed');
    const row = document.querySelector('.row[data-id="' + id + '"]');
    if (row) row.remove();
    const list = document.getElementById('dataset-list');
    if (list && !list.children.length) location.reload();
    showToast('Deleted');
  } catch (e) {
    showToast('Could not delete — try again');
  }
}
</script>
</body>
</html>
'''


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

    @app.route('/featurelink-display-config/admin')
    @login_required
    def featurelink_displayconfig_admin():
        resp = make_response(render_template_string(
            ADMIN_TEMPLATE, datasets=list_datasets(), icon_url=ICON_URL,
        ))
        resp.headers['Cache-Control'] = 'no-store, no-cache, must-revalidate'
        return resp

    @app.route('/api/featurelink-display-config/datasets', methods=['GET', 'POST'])
    @login_required
    def featurelink_displayconfig_datasets_api():
        if request.method == 'GET':
            return jsonify({'ok': True, 'datasets': list_datasets()})
        data = request.get_json(silent=True) or {}
        if not (data.get('name') or '').strip():
            return jsonify({'ok': False, 'error': 'name is required'}), 400
        try:
            record = save_dataset(data)
        except ValueError as e:
            return jsonify({'ok': False, 'error': str(e)}), 400
        return jsonify(record)

    @app.route('/api/featurelink-display-config/datasets/<dataset_id>', methods=['GET', 'DELETE'])
    @login_required
    def featurelink_displayconfig_dataset_api(dataset_id):
        if request.method == 'DELETE':
            delete_dataset(dataset_id)
            return jsonify({'ok': True})
        record = load_dataset(dataset_id)
        if record is None:
            return jsonify({'ok': False, 'error': 'not found'}), 404
        return jsonify(record)

    @app.route('/api/featurelink-display-config/datasets/<dataset_id>/file')
    @login_required
    def featurelink_displayconfig_dataset_file(dataset_id):
        path = _data_path(_clean_id(dataset_id))
        if not os.path.isfile(path):
            abort(404)
        record = load_dataset(dataset_id) or {}
        return send_file(path, as_attachment=False, download_name=record.get('file_name') or 'dataset')
