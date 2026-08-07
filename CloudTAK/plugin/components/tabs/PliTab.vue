<template>
    <div class='fl-tab'>
        <div v-if='!isAuthed' class='fl-empty'>Sign in to ArcGIS to configure a PLI layer</div>

        <template v-else>
            <div class='fl-card'>
                <div class='fl-card-header' :class='pliConnected ? "ok" : "err"' @click='layerSectionExpanded = !layerSectionExpanded'>
                    <span>PLI Feature Layer</span>
                    <span>{{ layerSectionExpanded ? '▾' : '▸' }}</span>
                </div>
                <template v-if='layerSectionExpanded'>
                    <div class='fl-radio-row'>
                        <label><input type='radio' value='create' v-model='mode' /> Create Layer</label>
                        <label><input type='radio' value='join' v-model='mode' /> Join Existing Layer</label>
                    </div>

                    <label v-if='mode === "create"' class='fl-field'>
                        Layer name (optional)
                        <input v-model='layerName' placeholder='FeatureLink PLI' />
                    </label>
                    <label v-else class='fl-field'>
                        Feature Layer URL
                        <input v-model='joinUrl' placeholder='https://services.arcgis.com/.../FeatureServer/0' />
                    </label>

                    <button class='fl-btn primary' :disabled='busy' @click='doAction'>
                        {{ busy ? 'Working…' : (mode === 'create' ? 'Create Layer' : 'Join Layer') }}
                    </button>

                    <p v-if='store.pliLayerUrl' class='fl-status ok'>PLI layer: {{ store.pliLayerUrl }}</p>
                    <p v-if='statusMsg' :class='["fl-status", statusOk ? "ok" : "err"]'>{{ statusMsg }}</p>
                </template>
            </div>

            <div class='fl-card'>
                <div class='fl-card-header'>Auto-Send</div>
                <label class='fl-checkbox-row'>
                    <input type='checkbox' v-model='store.pliAutoSend' :disabled='!store.pliLayerUrl' />
                    Auto-send my position every 30s
                </label>
                <p class='fl-hint'>
                    Uses CloudTAK's own self-marker position. If that isn't available yet on this
                    CloudTAK build, auto-send will silently no-op (see the browser console).
                </p>
                <label class='fl-field'>
                    Breadcrumb color
                    <input type='color' v-model='store.pliTeamColor' />
                </label>
            </div>

            <button class='fl-btn' :disabled='!store.pliLayerUrl' @click='shareEndpoint'>Share PLI Endpoint</button>
            <p v-if='shareMsg' class='fl-status ok'>{{ shareMsg }}</p>
        </template>
    </div>
</template>

<script setup lang="ts">
import { ref, computed, watch } from 'vue';
import { store, setPliLayerUrl, isPliConnected } from '../../lib/store.ts';
import { authState, isAuthenticated, getToken, getUsername, getPortalUrl } from '../../lib/arcgisAuth.ts';
import * as rest from '../../lib/arcgisRest.ts';
import { copyToClipboard } from '../../lib/layerShare.ts';

const isAuthed = computed(() => { void authState.username; return isAuthenticated(); });
const pliConnected = computed(() => { void authState.username; return isPliConnected(); });

const mode = ref<'create' | 'join'>('create');
const layerName = ref('');
const joinUrl = ref('');
const busy = ref(false);
const statusMsg = ref('');
const statusOk = ref(false);
const shareMsg = ref('');
const layerSectionExpanded = ref(true);

// Collapses the "PLI Feature Layer" section the first time it's found connected — once
// it's set up, there's nothing left to look at there, so this saves a scroll/tap on every
// later visit. Only fires once; a user who manually re-expands it isn't fought afterward
// (ported from FeatureLinkDropDownReceiver.maybeAutoCollapsePliLayerSection).
let autoCollapseDone = false;
watch(pliConnected, (connected) => {
    if (connected && !autoCollapseDone) { autoCollapseDone = true; layerSectionExpanded.value = false; }
}, { immediate: true });

async function doAction(): Promise<void> {
    statusMsg.value = '';
    if (mode.value === 'join') {
        const url = joinUrl.value.trim();
        if (!url) { statusMsg.value = 'Enter a Feature Layer URL'; statusOk.value = false; return; }
        setPliLayerUrl(url);
        statusMsg.value = `Joined: ${url}`;
        statusOk.value = true;
        return;
    }

    // busy is set at function ENTRY, before any await. It used to be set only inside the create
    // branch after two awaits, so a rapid double-click issued two createPliFeatureService calls and
    // created two hosted Feature Services in the user's ArcGIS org — one orphaned and billable
    // (§4.2).
    busy.value = true;
    try {
        const token = await getToken();
        const username = getUsername();
        if (!token || !username) { statusMsg.value = 'Session expired — sign in again'; statusOk.value = false; return; }

        const result = await rest.createPliFeatureService(getPortalUrl(), username, token, layerName.value.trim() || null);
        // Every distinct failure now carries the ArcGIS message instead of collapsing to the
        // causeless literal 'Failed to create service' (§4.2).
        statusOk.value = result.ok;
        statusMsg.value = result.message;
        if (result.ok && result.url) setPliLayerUrl(result.url);
    } finally {
        busy.value = false;
    }
}

async function shareEndpoint(): Promise<void> {
    const json = JSON.stringify({ v: 1, type: 'pli_endpoint', url: store.pliLayerUrl }, null, 2);
    const copied = await copyToClipboard(json);
    shareMsg.value = copied ? 'PLI endpoint config copied to clipboard' : 'Could not access clipboard';
    window.setTimeout(() => { shareMsg.value = ''; }, 4000);
}
</script>

<style scoped>
.fl-tab { padding: 12px; display: flex; flex-direction: column; gap: 10px; }
.fl-card { border: 1px solid #333; border-radius: 6px; padding: 10px 12px; display: flex; flex-direction: column; gap: 8px; }
.fl-card-header { display: flex; justify-content: space-between; cursor: pointer; font-weight: 600; font-size: 12px; text-transform: uppercase; letter-spacing: .03em; opacity: .85; }
.fl-card-header.ok { color: #4caf50; opacity: 1; }
.fl-card-header.err { color: #ff5722; opacity: 1; }
.fl-radio-row { display: flex; gap: 14px; font-size: 12px; }
.fl-checkbox-row { display: flex; align-items: center; gap: 8px; font-size: 12px; }
.fl-field { display: flex; flex-direction: column; gap: 4px; font-size: 12px; }
.fl-field input { padding: 6px 8px; border-radius: 4px; border: 1px solid #444; background: transparent; color: inherit; }
.fl-hint { font-size: 11px; opacity: .65; margin: 0; }
.fl-btn { padding: 8px 12px; border-radius: 4px; border: 1px solid #555; background: transparent; color: inherit; cursor: pointer; font-size: 12px; }
.fl-btn.primary { border-color: #4caf50; color: #4caf50; }
.fl-btn:disabled { opacity: .5; cursor: default; }
.fl-status { font-size: 12px; margin: 0; }
.fl-status.ok { color: #4caf50; }
.fl-status.err { color: #ff5722; }
.fl-empty { font-size: 12px; opacity: .7; padding: 12px 0; }
</style>
