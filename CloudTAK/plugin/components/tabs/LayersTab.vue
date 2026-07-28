<template>
    <div class='fl-tab'>
        <!-- Public layers -->
        <div class='fl-card'>
            <div class='fl-card-header' @click='publicExpanded = !publicExpanded'>
                <span>Public Layers</span>
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

        <!-- Private (signed-in) layers -->
        <div class='fl-card'>
            <div class='fl-card-header' @click='privateExpanded = !privateExpanded'>
                <span>My ArcGIS Layers</span>
                <span>{{ privateExpanded ? '▾' : '▸' }}</span>
            </div>
            <div v-if='privateExpanded'>
                <div v-if='!isAuthed' class='fl-empty'>Sign in to see your ArcGIS layers</div>
                <template v-else>
                    <button class='fl-btn small' :disabled='refreshing' @click='doRefreshPrivate'>{{ refreshing ? 'Refreshing…' : 'Refresh' }}</button>
                    <div v-if='!store.privateLayers.length' class='fl-empty'>No layers found in your ArcGIS account</div>
                    <LayerRow
                        v-for='l in store.privateLayers' :key='l.url' :layer='l'
                        @toggle-visible='toggleLayerVisibility(l)'
                        @interval-change='seconds => setLayerRecurrence(l, seconds)'
                        @action='downloadLayer(l).catch(() => {})'
                        @delete='removePrivateLayer(l)'
                    />
                </template>
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
    downloadLayer, fetchUserLayers, removePublicLayer, removePrivateLayer, toggleLayerVisibility, setLayerRecurrence,
} from '../../lib/layerActions.ts';
import { buildShareConfigJson, copyToClipboard } from '../../lib/layerShare.ts';
import type { ArcGISLayer } from '../../lib/types.ts';
import LayerRow from '../LayerRow.vue';

defineEmits<{ openAddLayer: [] }>();

const publicExpanded = ref(true);
const privateExpanded = ref(true);
const refreshing = ref(false);
const shareMessage = ref('');

const isAuthed = computed(() => { void authState.username; return isAuthenticated(); });

async function doRefreshPrivate(): Promise<void> {
    refreshing.value = true;
    try { await fetchUserLayers(); } finally { refreshing.value = false; }
}

async function shareLayer(layer: ArcGISLayer): Promise<void> {
    const json = buildShareConfigJson(layer);
    const copied = await copyToClipboard(json);
    shareMessage.value = copied
        ? `Copied "${layer.name}" config to clipboard`
        : 'Could not access clipboard — copy manually from the browser console';
    if (!copied) console.info('[featurelink] share config:', json);
    window.setTimeout(() => { shareMessage.value = ''; }, 4000);
}
</script>

<style scoped>
.fl-tab { padding: 12px; display: flex; flex-direction: column; gap: 10px; }
.fl-card { border: 1px solid #333; border-radius: 6px; padding: 10px 12px; }
.fl-card-header { display: flex; justify-content: space-between; cursor: pointer; font-weight: 600; font-size: 12px; text-transform: uppercase; letter-spacing: .03em; opacity: .85; margin-bottom: 6px; }
.fl-btn { padding: 6px 10px; border-radius: 4px; border: 1px solid #555; background: transparent; color: inherit; cursor: pointer; font-size: 12px; }
.fl-btn.primary { border-color: #4caf50; color: #4caf50; }
.fl-btn.small { font-size: 11px; padding: 4px 8px; margin-bottom: 6px; }
.fl-btn:disabled { opacity: .5; cursor: default; }
.fl-empty { font-size: 11px; opacity: .6; padding: 6px 0; }
.fl-status { font-size: 12px; margin: 0; }
.fl-status.ok { color: #4caf50; }
</style>
