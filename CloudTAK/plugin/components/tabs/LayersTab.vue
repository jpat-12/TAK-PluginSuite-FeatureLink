<template>
    <div class='fl-tab'>
        <!-- Public layers -->
        <div class='fl-card'>
            <div class='fl-card-header' @click='publicExpanded = !publicExpanded'>
                <span class='fl-card-title'>Public Layers<span class='fl-count-badge'>{{ store.publicLayers.length }}</span></span>
                <span>{{ publicExpanded ? '▾' : '▸' }}</span>
            </div>
            <div v-if='publicExpanded'>
                <button class='fl-btn primary small' @click='$emit("openAddLayer")'>Add Layer</button>
                <div v-if='!store.publicLayers.length' class='fl-empty'>No public layers yet</div>
                <LayerRow
                    v-for='l in store.publicLayers' :key='l.url' :layer='l'
                    @toggle-visible='toggleLayerVisibility(l)'
                    @interval-change='seconds => setLayerRecurrence(l, seconds)'
                    @action='downloadLayer(l).catch(() => {})'
                    @share='shareLayer(l)'
                    @delete='removePublicLayer(l)'
                />
            </div>
        </div>

        <!-- My ArcGIS Layers — browse list, only shown while signed in -->
        <div v-if='isAuthed' class='fl-card'>
            <div class='fl-card-header' @click='browseExpanded = !browseExpanded'>
                <span>My ArcGIS Layers</span>
                <span>{{ browseExpanded ? '▾' : '▸' }}</span>
            </div>
            <div v-if='browseExpanded'>
                <button class='fl-btn small' :disabled='refreshing' @click='doRefreshPrivate'>{{ refreshing ? 'Refreshing…' : 'Refresh' }}</button>
                <div v-if='!store.browseLayers.length' class='fl-empty'>No layers found in your ArcGIS account</div>
                <LayerRow
                    v-for='l in store.browseLayers' :key='l.url' :layer='l'
                    @toggle-visible='toggleLayerVisibility(l)'
                    @interval-change='seconds => setLayerRecurrence(l, seconds)'
                    @action='downloadLayer(l).catch(() => {})'
                    @delete='removeBrowseLayer(l)'
                />
            </div>
        </div>

        <!-- Private Layers — on-device layers not shared to Everyone; hidden when empty -->
        <div v-if='store.privateLayers.length' class='fl-card'>
            <div class='fl-card-header' @click='privateExpanded = !privateExpanded'>
                <span class='fl-card-title'>Private Layers<span class='fl-count-badge'>{{ store.privateLayers.length }}</span></span>
                <span>{{ privateExpanded ? '▾' : '▸' }}</span>
            </div>
            <div v-if='privateExpanded'>
                <LayerRow
                    v-for='l in store.privateLayers' :key='l.url' :layer='l'
                    @toggle-visible='toggleLayerVisibility(l)'
                    @interval-change='seconds => setLayerRecurrence(l, seconds)'
                    @action='downloadLayer(l).catch(() => {})'
                    @delete='removePrivateLayer(l)'
                />
            </div>
        </div>

        <p v-if='shareMessage' class='fl-status ok'>{{ shareMessage }}</p>
    </div>
</template>

<script setup lang="ts">
import { ref, computed } from 'vue';
import { store } from '../../lib/store.ts';
import { authState, isAuthenticated } from '../../lib/arcgisAuth.ts';
import {
    downloadLayer, fetchUserLayers, removePublicLayer, removeBrowseLayer, removePrivateLayer,
    toggleLayerVisibility, setLayerRecurrence,
} from '../../lib/layerActions.ts';
import { buildShareConfigJson, downloadAsFile } from '../../lib/layerShare.ts';
import type { ArcGISLayer } from '../../lib/types.ts';
import LayerRow from '../LayerRow.vue';

defineEmits<{ openAddLayer: [] }>();

const publicExpanded = ref(true);
const browseExpanded = ref(true);
const privateExpanded = ref(true);
const refreshing = ref(false);
const shareMessage = ref('');

const isAuthed = computed(() => { void authState.username; return isAuthenticated(); });

async function doRefreshPrivate(): Promise<void> {
    refreshing.value = true;
    try { await fetchUserLayers(); } finally { refreshing.value = false; }
}

// Interim share: download the config as a <name>.featurelinkshare file — the same file ATAK's
// LayerShareHelper produces — so it can be handed off by any channel and imported on the other
// side (ATAK/WinTAK "Upload Pref File" or CloudTAK Add Layer → Import Config). The in-app
// send-to-contact path (contacts via /api/marti/api/contacts/all + PUT /api/marti/package with
// destinations) is the follow-up; this gives a working cross-platform hand-off in the meantime.
//
// Not ".featurelink.json" (this was plain JSON before too) — WinTAK's own Mission-Package
// auto-import chain tries a GRG (Gridded Reference Graphic) importer against any unrecognized
// ".json" attachment and throws an unhandled exception trying to MGRS-decode it instead of just
// skipping it. See importIngest.ts's ENTRY_SUFFIX for the matching receive-side constant.
function shareLayer(layer: ArcGISLayer): void {
    const json = buildShareConfigJson(layer);
    const safeName = layer.name.replace(/[^a-zA-Z0-9 _-]/g, '_');
    downloadAsFile(json, `${safeName}.featurelinkshare`);
    shareMessage.value = `Downloaded "${safeName}.featurelinkshare" — send it to any ATAK/WinTAK/CloudTAK user to import`;
    window.setTimeout(() => { shareMessage.value = ''; }, 5000);
}
</script>

<style scoped>
.fl-tab { padding: 12px; display: flex; flex-direction: column; gap: 10px; }
.fl-card { border: 1px solid #333; border-radius: 6px; padding: 10px 12px; }
.fl-card-header { display: flex; justify-content: space-between; cursor: pointer; font-weight: 600; font-size: 12px; text-transform: uppercase; letter-spacing: .03em; opacity: .85; margin-bottom: 6px; }
.fl-card-title { display: inline-flex; align-items: center; gap: 6px; }
.fl-count-badge { display: inline-block; background: #1a3a44; color: #4fc3f7; border-radius: 20px; padding: 1px 8px; font-size: 10px; font-weight: 700; text-transform: none; letter-spacing: normal; }
.fl-btn { padding: 6px 10px; border-radius: 4px; border: 1px solid #555; background: transparent; color: inherit; cursor: pointer; font-size: 12px; }
.fl-btn.primary { border-color: #4caf50; color: #4caf50; }
.fl-btn.small { font-size: 11px; padding: 4px 8px; margin-bottom: 6px; }
.fl-btn:disabled { opacity: .5; cursor: default; }
.fl-empty { font-size: 11px; opacity: .6; padding: 6px 0; }
.fl-status { font-size: 12px; margin: 0; }
.fl-status.ok { color: #4caf50; }
</style>
