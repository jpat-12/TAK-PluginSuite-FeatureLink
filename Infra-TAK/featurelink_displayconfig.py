"""
featurelink_displayconfig.py — FeatureLink module for infra-TAK

Registers the /featurelink hub (saved-datasets list + entry point), the
/featurelink/featurelink-display-config configurator, and their supporting
APIs directly on the Flask app. Call register_routes(app, login_required)
from app.py.

The configurator itself (featurelink_displayconfig_assets/index.html) is a
self-contained client-side tool: upload FeatureLayer data (CSV/TSV/JSON/
GeoJSON/Excel or a live FeatureLayer URL), configure symbology, labels,
popups, and layer properties, then export a display config JSON (with QR
code) for the FeatureLink ATAK plugin.

This module adds persistence on top of that: "Save" on the configurator
POSTs the dataset (a FeatureLayer URL, or the raw uploaded file) + the
display config to this module, which stores it under CONFIG_DIR and lists
it on the /featurelink hub. Opening a saved entry reloads the dataset and
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
    abort, jsonify, make_response, redirect, render_template_string, request,
    send_file, send_from_directory,
)

MODULE_VERSION = '1.4.0'

ASSETS_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'featurelink_displayconfig_assets')

CONFIG_DIR = os.environ.get('CONFIG_DIR') or os.path.join(
    os.path.dirname(os.path.abspath(__file__)), '.config'
)
DATASETS_DIR = os.path.join(CONFIG_DIR, 'featurelink_displayconfig', 'datasets')

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


# Same design system as infra-TAK's other module pages (esri.py's
# ESRI_TEMPLATE, etc.) — shared CSS variables/classes so /featurelink reads
# as part of the console rather than a bolted-on standalone page.
# {{ sidebar_html }} is auto-injected by app.py's context_processor for any
# non-/api route, same as every other module page — nothing to pass here.
HUB_TEMPLATE = '''<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8"><meta name="viewport" content="width=device-width,initial-scale=1.0">
<title>FeatureLink — infra-TAK</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link href="https://fonts.googleapis.com/css2?family=DM+Sans:wght@400;500;600;700&family=JetBrains+Mono:wght@400;500;600&display=swap" rel="stylesheet">
<link href="https://fonts.googleapis.com/css2?family=Material+Symbols+Outlined:opsz,wght,FILL,GRAD@24,400,0,0" rel="stylesheet">
<style>
:root{--bg-deep:#080b14;--bg-surface:#0f1219;--bg-card:#161b26;--border:#1e2736;--text-primary:#f1f5f9;--text-secondary:#cbd5e1;--text-dim:#94a3b8;--accent:#3b82f6;--cyan:#06b6d4;--green:#10b981;--red:#ef4444;--yellow:#eab308}
*{box-sizing:border-box;margin:0;padding:0}
body{background:var(--bg-deep);color:var(--text-primary);font-family:'DM Sans',sans-serif;min-height:100vh;display:flex;flex-direction:row}
.sidebar{width:220px;min-width:220px;background:var(--bg-surface);border-right:1px solid var(--border);padding:24px 0;flex-shrink:0}
.material-symbols-outlined{font-family:'Material Symbols Outlined';font-weight:400;font-style:normal;font-size:20px;line-height:1;letter-spacing:normal;white-space:nowrap;direction:ltr;-webkit-font-smoothing:antialiased}
.nav-icon.material-symbols-outlined{font-size:22px;width:22px;text-align:center}
.sidebar-logo{padding:0 20px 24px;border-bottom:1px solid var(--border);margin-bottom:16px}
.sidebar-logo span{font-size:15px;font-weight:700}.sidebar-logo small{display:block;font-size:10px;color:var(--text-dim);font-family:'JetBrains Mono',monospace;margin-top:2px}
.nav-item{display:flex;align-items:center;gap:10px;padding:9px 20px;color:var(--text-secondary);text-decoration:none;font-size:13px;font-weight:500;transition:all .15s;border-left:2px solid transparent}
.nav-item:hover{color:var(--text-primary);background:rgba(255,255,255,.03)}.nav-item.active{color:var(--cyan);background:rgba(6,182,212,.06);border-left-color:var(--cyan)}
.nav-icon{font-size:15px;width:18px;text-align:center}
.main{flex:1;min-width:0;overflow-y:auto;padding:32px}
.page-header{margin-bottom:28px;display:flex;align-items:center;justify-content:space-between;gap:16px;flex-wrap:wrap}
.page-header h1{font-size:22px;font-weight:700;display:flex;align-items:center;gap:10px}
.page-header p{color:var(--text-secondary);font-size:13px;margin-top:4px}
.card{background:var(--bg-card);border:1px solid var(--border);border-radius:12px;padding:24px;margin-bottom:20px}
.card-title{font-size:13px;font-weight:600;color:var(--text-dim);text-transform:uppercase;letter-spacing:.08em;margin-bottom:16px}
.btn{display:inline-flex;align-items:center;gap:8px;padding:10px 20px;border-radius:8px;font-size:13px;font-weight:600;cursor:pointer;border:none;transition:opacity .15s;text-decoration:none}
.btn:hover{opacity:.85}
.btn-primary{background:var(--accent);color:#fff}
.btn-ghost{background:rgba(255,255,255,.05);color:var(--text-secondary);border:1px solid var(--border)}
.btn-sm{padding:7px 14px;font-size:12px}
.btn-danger-ghost{background:rgba(239,68,68,.08);color:var(--red);border:1px solid rgba(239,68,68,.2)}
table{width:100%;border-collapse:collapse;font-size:13px}
th{text-align:left;padding:8px 12px;color:var(--text-dim);font-size:11px;text-transform:uppercase;letter-spacing:.06em;border-bottom:1px solid var(--border)}
td{padding:10px 12px;border-bottom:1px solid rgba(30,39,54,.6);vertical-align:middle}
tr:last-child td{border-bottom:none}
.src-cell{font-family:'JetBrains Mono',monospace;font-size:12px;color:var(--text-dim);max-width:320px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;display:block}
.badge{display:inline-flex;align-items:center;gap:4px;padding:3px 8px;border-radius:20px;font-size:11px;font-weight:600}
.badge-cyan{background:rgba(6,182,212,.15);color:var(--cyan)}
.badge-green{background:rgba(16,185,129,.15);color:var(--green)}
.row-actions{display:flex;gap:8px;justify-content:flex-end}
.empty-state{text-align:center;padding:48px;color:var(--text-secondary)}
.empty-state a{color:var(--cyan)}
.toast{position:fixed;bottom:24px;right:24px;padding:12px 20px;border-radius:10px;font-size:13px;font-weight:600;z-index:9999;opacity:0;transition:opacity .3s;pointer-events:none}
.toast.show{opacity:1}
.toast.success{background:var(--green);color:#fff}
.toast.error{background:var(--red);color:#fff}
.modal-overlay{display:none;position:fixed;inset:0;background:rgba(0,0,0,.6);z-index:1000;align-items:center;justify-content:center}
.modal-overlay.open{display:flex}
.modal{background:var(--bg-card);border:1px solid var(--border);border-radius:14px;padding:28px;width:320px;max-width:90vw;text-align:center}
.modal-title{font-size:14px;font-weight:700;margin-bottom:16px;word-break:break-word}
.qr-canvas-wrap{display:flex;justify-content:center;margin-bottom:16px;min-height:220px;align-items:center}
.qr-canvas-wrap canvas{border-radius:8px}
.qr-link-text{font-family:'JetBrains Mono',monospace;font-size:11px;color:var(--text-dim);word-break:break-all;background:rgba(255,255,255,.03);border:1px solid var(--border);border-radius:8px;padding:8px 10px;margin-bottom:16px}
.modal-actions{display:flex;gap:8px;justify-content:center}
</style>
</head>
<body>
{{ sidebar_html }}
<div class="main">
  <div class="page-header">
    <div>
      <h1>&#127912; FeatureLink</h1>
      <p>Saved display configs for FeatureLayer exports — symbology, labels, popups, and QR sharing for the FeatureLink ATAK plugin.</p>
    </div>
    <a class="btn btn-primary" href="/featurelink/featurelink-display-config">+ New Dataset Config</a>
  </div>

  <div class="card">
    <div class="card-title">Saved Dataset Configs</div>
    {% if not datasets %}
    <div class="empty-state">
      No saved dataset configs yet.<br><br>
      <a href="/featurelink/featurelink-display-config">Open the Display Configurator</a> to upload a
      dataset, set up its display config, and click <strong>Save</strong> — it'll show up here.
    </div>
    {% else %}
    <table>
      <thead>
        <tr><th>Name</th><th>Source</th><th>Fields</th><th>Updated</th><th></th></tr>
      </thead>
      <tbody id="dataset-list">
        {% for d in datasets %}
        <tr data-id="{{ d.id }}">
          <td>{{ d.name }}</td>
          <td>
            {% if d.source_type == 'url' %}
            <span class="badge badge-cyan">URL</span> <span class="src-cell" title="{{ d.source_url }}">{{ d.source_url }}</span>
            {% else %}
            <span class="badge badge-green">File</span> <span class="src-cell" title="{{ d.file_name }}">{{ d.file_name }}</span>
            {% endif %}
          </td>
          <td>{{ d.field_count }}</td>
          <td>{{ d.updated_at.split('T')[0] if d.updated_at else '' }}</td>
          <td>
            <div class="row-actions">
              <a class="btn btn-primary btn-sm" href="/featurelink/featurelink-display-config?load={{ d.id }}">Open</a>
              <button class="btn btn-ghost btn-sm" onclick="showQR('{{ d.id }}', {{ d.name|tojson }})">QR</button>
              <button class="btn btn-ghost btn-sm" onclick="copyLink('{{ d.id }}')">Copy link</button>
              <button class="btn btn-danger-ghost btn-sm" onclick="deleteDataset('{{ d.id }}')">Delete</button>
            </div>
          </td>
        </tr>
        {% endfor %}
      </tbody>
    </table>
    {% endif %}
  </div>
</div>
<div class="toast" id="toast"></div>
<div class="modal-overlay" id="qr-modal-overlay" onclick="if(event.target===this) closeQRModal()">
  <div class="modal">
    <div class="modal-title" id="qr-modal-name"></div>
    <div class="qr-canvas-wrap" id="qr-canvas-wrap"></div>
    <div class="qr-link-text" id="qr-modal-link"></div>
    <div class="modal-actions">
      <button class="btn btn-ghost btn-sm" onclick="copyLinkFromModal()">Copy link</button>
      <button class="btn btn-primary btn-sm" onclick="closeQRModal()">Close</button>
    </div>
  </div>
</div>
<!-- QR code generation. Pinned to 1.4.4 — see featurelink-display-config's
     index.html for why (1.5.x's build/ isn't a standalone browser bundle). -->
<script src="https://cdn.jsdelivr.net/npm/qrcode@1.4.4/build/qrcode.min.js"></script>
<script>
let qrModalId = null;
function showQR(id, name){
  qrModalId = id;
  const url = window.location.origin + '/featurelink/featurelink-display-config?load=' + encodeURIComponent(id);
  document.getElementById('qr-modal-name').textContent = name || 'Saved Dataset Config';
  document.getElementById('qr-modal-link').textContent = url;
  const wrap = document.getElementById('qr-canvas-wrap');
  wrap.innerHTML = '';
  const canvas = document.createElement('canvas');
  wrap.appendChild(canvas);
  QRCode.toCanvas(canvas, url, {
    width: 220, margin: 1, errorCorrectionLevel: 'M',
    color: { dark: '#000000', light: '#ffffff' },
  }, function(err){
    if (err) wrap.innerHTML = '<div style="color:var(--red);font-size:12px">QR generation failed: ' + err.message + '</div>';
  });
  document.getElementById('qr-modal-overlay').classList.add('open');
}
function closeQRModal(){
  document.getElementById('qr-modal-overlay').classList.remove('open');
  qrModalId = null;
}
function copyLinkFromModal(){
  if (qrModalId) copyLink(qrModalId);
}
let toastTimer;
function showToast(msg, kind){
  const el = document.getElementById('toast');
  el.textContent = msg;
  el.className = 'toast show' + (kind ? ' ' + kind : '');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { el.classList.remove('show'); }, 2600);
}
function copyLink(id){
  const url = window.location.origin + '/featurelink/featurelink-display-config?load=' + encodeURIComponent(id);
  navigator.clipboard.writeText(url).then(
    () => showToast('Link copied — scan or share it to open this config directly', 'success'),
    () => showToast(url)
  );
}
async function deleteDataset(id){
  if (!confirm('Delete this saved dataset config? This cannot be undone.')) return;
  try {
    const r = await fetch('/api/featurelink/datasets/' + encodeURIComponent(id), { method: 'DELETE' });
    if (!r.ok) throw new Error('delete failed');
    const row = document.querySelector('tr[data-id="' + id + '"]');
    if (row) row.remove();
    const list = document.getElementById('dataset-list');
    if (list && !list.children.length) location.reload();
    showToast('Deleted', 'success');
  } catch (e) {
    showToast('Could not delete — try again', 'error');
  }
}
</script>
</body>
</html>
'''


def register_routes(app, login_required):
    @app.route('/featurelink')
    @app.route('/featurelink/')
    @login_required
    def featurelink_hub_page():
        resp = make_response(render_template_string(
            HUB_TEMPLATE, datasets=list_datasets(),
        ))
        resp.headers['Cache-Control'] = 'no-store, no-cache, must-revalidate'
        return resp

    @app.route('/featurelink/featurelink-display-config')
    @app.route('/featurelink/featurelink-display-config/')
    @login_required
    def featurelink_displayconfig_page():
        resp = send_from_directory(ASSETS_DIR, 'index.html')
        resp.headers['Cache-Control'] = 'no-store, no-cache, must-revalidate'
        return resp

    @app.route('/featurelink/featurelink-display-config/icons/<path:filename>')
    @login_required
    def featurelink_displayconfig_icon(filename):
        return send_from_directory(os.path.join(ASSETS_DIR, 'icons'), filename)

    # Legacy redirects — the module used to live directly at
    # /featurelink-display-config (and /featurelink-display-config/admin)
    # before the /featurelink hub was added. Preserve any already-shared
    # links/QR codes/bookmarks.
    @app.route('/featurelink-display-config')
    @app.route('/featurelink-display-config/')
    @login_required
    def featurelink_displayconfig_legacy_redirect():
        qs = request.query_string.decode()
        target = '/featurelink/featurelink-display-config' + (f'?{qs}' if qs else '')
        return redirect(target, code=301)

    @app.route('/featurelink-display-config/admin')
    @login_required
    def featurelink_displayconfig_admin_legacy_redirect():
        return redirect('/featurelink', code=301)

    @app.route('/api/featurelink/datasets', methods=['GET', 'POST'])
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

    @app.route('/api/featurelink/datasets/<dataset_id>', methods=['GET', 'DELETE'])
    @login_required
    def featurelink_displayconfig_dataset_api(dataset_id):
        if request.method == 'DELETE':
            delete_dataset(dataset_id)
            return jsonify({'ok': True})
        record = load_dataset(dataset_id)
        if record is None:
            return jsonify({'ok': False, 'error': 'not found'}), 404
        return jsonify(record)

    @app.route('/api/featurelink/datasets/<dataset_id>/file')
    @login_required
    def featurelink_displayconfig_dataset_file(dataset_id):
        path = _data_path(_clean_id(dataset_id))
        if not os.path.isfile(path):
            abort(404)
        record = load_dataset(dataset_id) or {}
        return send_file(path, as_attachment=False, download_name=record.get('file_name') or 'dataset')
