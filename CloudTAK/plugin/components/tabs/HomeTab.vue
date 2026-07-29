<template>
    <div class='fl-tab'>
        <!-- Quick-glance status card -->
        <div class='fl-card'>
            <div class='fl-status-row'>
                <span>Account</span>
                <b :class='isAuthed ? "ok" : "err"'>{{ isAuthed ? 'Signed In' : 'Not Signed In' }}</b>
            </div>
            <div class='fl-status-row'>
                <span>PLI</span>
                <b :class='pliConnected ? "ok" : "err"'>{{ pliConnected ? 'Connected' : 'Not Configured' }}</b>
            </div>
            <div class='fl-status-row'>
                <span>Auto-Send</span>
                <b :class='store.pliAutoSend ? "ok" : "muted"'>{{ store.pliAutoSend ? 'On' : 'Off' }}</b>
            </div>
            <div class='fl-status-row'>
                <span>Layers</span>
                <b>{{ store.browseLayers.length + store.privateLayers.length }} private, {{ store.publicLayers.length }} public</b>
            </div>
            <div v-if='ingestState.status !== "idle"' class='fl-status-row'>
                <span>Auto-Import</span>
                <b :class='ingestState.status === "ok" ? "ok" : "err"' :title='ingestState.message'>
                    {{ ingestState.status === 'ok' ? 'OK' : 'Error' }}
                </b>
            </div>
        </div>
        <p v-if='ingestState.status !== "idle"' class='fl-ingest-msg'>{{ ingestState.message }}</p>

        <button v-if='!store.pliLayerUrl' class='fl-btn primary' @click='$emit("goPli")'>Set PLI Endpoint</button>
        <button class='fl-btn' @click='$emit("openSend")'>Send Item to Feature Layer</button>
        <button v-if='!isAuthed' class='fl-btn' @click='$emit("openAccount")'>Sign In</button>

        <!-- Feature Statistics -->
        <div class='fl-card'>
            <div class='fl-card-header' @click='expanded = !expanded'>
                <span>Feature Statistics</span>
                <span>{{ expanded ? '▾' : '▸' }}</span>
            </div>
            <div v-if='expanded'>
                <p class='fl-total'>Total Features: {{ totalCount }}</p>
                <button class='fl-btn small' :disabled='loading' @click='refresh'>{{ loading ? 'Refreshing…' : 'Refresh' }}</button>
                <ul class='fl-stat-list'>
                    <li v-for='l in allLayers' :key='l.url'>
                        {{ l.name }} <span class='fl-kind'>[{{ l.type }}]</span> — {{ l.featureCount < 0 ? 'error' : `${l.featureCount} features` }}
                    </li>
                </ul>
            </div>
        </div>
    </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue';
import { store, isPliConnected } from '../../lib/store.ts';
import { authState, isAuthenticated } from '../../lib/arcgisAuth.ts';
import { refreshFeatureCounts } from '../../lib/layerActions.ts';
import { ingestState } from '../../lib/importIngest.ts';

defineEmits<{ openAccount: []; goPli: []; openSend: [] }>();

const expanded = ref(true);
const loading = ref(false);

const isAuthed = computed(() => { void authState.username; return isAuthenticated(); });
const pliConnected = computed(() => { void authState.username; return isPliConnected(isAuthenticated()); });
const allLayers = computed(() => [...store.browseLayers, ...store.privateLayers, ...store.publicLayers]);
const totalCount = computed(() => allLayers.value.reduce((sum, l) => sum + Math.max(0, l.featureCount), 0));

async function refresh(): Promise<void> {
    loading.value = true;
    try { await refreshFeatureCounts(); } finally { loading.value = false; }
}

onMounted(refresh);
</script>

<style scoped>
.fl-tab { padding: 12px; display: flex; flex-direction: column; gap: 10px; }
.fl-card { border: 1px solid #333; border-radius: 6px; padding: 10px 12px; }
.fl-card-header { display: flex; justify-content: space-between; cursor: pointer; font-weight: 600; font-size: 12px; text-transform: uppercase; letter-spacing: .03em; opacity: .85; }
.fl-status-row { display: flex; justify-content: space-between; font-size: 12px; padding: 3px 0; }
.fl-status-row .ok { color: #4caf50; }
.fl-status-row .err { color: #ff5722; }
.fl-status-row .muted { color: #7a7a7a; }
.fl-ingest-msg { font-size: 11px; opacity: .7; margin: -4px 0 0; }
.fl-btn { padding: 8px 12px; border-radius: 4px; border: 1px solid #555; background: transparent; color: inherit; cursor: pointer; font-size: 12px; }
.fl-btn.primary { border-color: #4caf50; color: #4caf50; }
.fl-btn.small { font-size: 11px; padding: 4px 8px; margin: 6px 0; }
.fl-btn:disabled { opacity: .5; cursor: default; }
.fl-total { font-size: 13px; font-weight: 600; margin: 6px 0 0; }
.fl-stat-list { list-style: none; margin: 6px 0 0; padding: 0; font-size: 11px; display: flex; flex-direction: column; gap: 4px; }
.fl-kind { opacity: .6; }
</style>
