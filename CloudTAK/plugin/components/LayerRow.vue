<!-- Port of LayerListAdapter.java's per-row rendering: visibility toggle, interval editor
     (always committed in whole seconds, per the ATAK version's later UI simplification —
     recurrenceMillis() still tolerates legacy min/hr values loaded from older saved layers),
     and a type-specific primary action (download/refresh for private, delete for public). -->
<template>
    <div class='fl-row'>
        <div class='fl-row-main'>
            <button class='fl-eye' :title='layer.visible ? "Hide markers" : "Show markers"' @click='$emit("toggleVisible")'>
                {{ layer.visible ? '👁' : '🚫' }}
            </button>
            <div class='fl-row-info'>
                <div class='fl-row-name'>{{ layer.name }}<span class='fl-config-badge' :class='{ on: hasConfig }'>{{ hasConfig ? 'Config' : 'No Config' }}</span></div>
                <div class='fl-row-meta'>{{ layer.featureCount < 0 ? 'error' : `${layer.featureCount} features` }} · {{ lastSyncLabel }}</div>
            </div>
        </div>
        <div class='fl-row-controls'>
            <label class='fl-interval' title='Auto-refresh interval in seconds (0 = off)'>
                <input type='number' min='0' :value='layer.recurrenceInterval' @change='onIntervalChange' />s
            </label>
            <button v-if='layer.type === "private"' class='fl-icon-btn' :title='layer.lastSync ? "Refresh now" : "Download now"' @click='$emit("action")'>{{ layer.lastSync ? '↻' : '⬇' }}</button>
            <button v-if='layer.type === "public"' class='fl-icon-btn' title='Share config' @click='$emit("share")'>📤</button>
            <button class='fl-icon-btn danger' title='Remove' @click='onDeleteClick'>🗑</button>
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import { store } from '../lib/store.ts';
import type { ArcGISLayer } from '../lib/types.ts';

const props = defineProps<{ layer: ArcGISLayer }>();
const emit = defineEmits<{ toggleVisible: []; intervalChange: [seconds: number]; action: []; share: []; delete: [] }>();

const lastSyncLabel = computed(() => (props.layer.lastSync ? new Date(props.layer.lastSync).toLocaleTimeString() : 'never synced'));
// Always-visible bubble: whether this layer has a display config (icons/colors/labels/popups)
// attached — see store.displayConfigs. Ports item_layer.xml's layer_config_badge.
const hasConfig = computed(() => Boolean(store.displayConfigs[props.layer.url]));

function onIntervalChange(e: Event): void {
    emit('intervalChange', Number((e.target as HTMLInputElement).value));
}

// Port of confirmRemovePublicLayer()/onLayerDelete()'s AlertDialog confirmation — the CloudTAK
// port originally deleted straight away with no confirmation at all, unlike ATAK.
function onDeleteClick(): void {
    const message = props.layer.type === 'private'
        ? `Remove "${props.layer.name}" from your layer list here? It stays in your ArcGIS account — this only hides it on this device. Any markers it added to the map will also be removed.`
        : `Remove "${props.layer.name}" from your layer list? Any markers it added to the map will also be removed.`;
    if (window.confirm(message)) emit('delete');
}
</script>

<style scoped>
.fl-row { display: flex; align-items: center; justify-content: space-between; gap: 8px; padding: 8px 0; border-top: 1px solid #242424; }
.fl-row-main { display: flex; align-items: center; gap: 8px; min-width: 0; }
.fl-eye { background: none; border: none; cursor: pointer; font-size: 14px; }
.fl-row-info { min-width: 0; }
.fl-row-name { font-size: 12px; font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; display: flex; align-items: center; gap: 6px; }
.fl-config-badge { flex-shrink: 0; font-size: 9px; font-weight: 700; border-radius: 20px; padding: 1px 7px; background: #3a1b1b; color: #ff5252; }
.fl-config-badge.on { background: #1b3a1e; color: #4caf50; }
.fl-row-meta { font-size: 10px; opacity: .65; }
.fl-row-controls { display: flex; align-items: center; gap: 6px; flex-shrink: 0; }
.fl-interval { display: flex; align-items: center; gap: 2px; font-size: 10px; opacity: .8; }
.fl-interval input { width: 44px; padding: 2px 4px; border-radius: 3px; border: 1px solid #444; background: transparent; color: inherit; }
.fl-icon-btn { background: none; border: 1px solid #444; border-radius: 4px; cursor: pointer; font-size: 12px; padding: 3px 6px; color: inherit; }
.fl-icon-btn.danger { border-color: #ff5722; }
</style>
