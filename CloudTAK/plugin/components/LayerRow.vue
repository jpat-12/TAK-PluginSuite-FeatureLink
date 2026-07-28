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
                <div class='fl-row-name'>{{ layer.name }}</div>
                <div class='fl-row-meta'>{{ layer.featureCount < 0 ? 'error' : `${layer.featureCount} features` }} · {{ lastSyncLabel }}</div>
            </div>
        </div>
        <div class='fl-row-controls'>
            <label class='fl-interval' title='Auto-refresh interval in seconds (0 = off)'>
                <input type='number' min='0' v-model.number='layer.recurrenceInterval' @change='onIntervalChange' />s
            </label>
            <button v-if='layer.type === "private"' class='fl-icon-btn' title='Download / refresh now' @click='$emit("action")'>⟳</button>
            <button v-if='layer.type === "public"' class='fl-icon-btn' title='Share config' @click='$emit("share")'>📤</button>
            <button class='fl-icon-btn danger' title='Remove' @click='$emit("delete")'>🗑</button>
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import type { ArcGISLayer } from '../lib/types.ts';

const props = defineProps<{ layer: ArcGISLayer }>();
defineEmits<{ toggleVisible: []; intervalChange: []; action: []; share: []; delete: [] }>();

const lastSyncLabel = computed(() => (props.layer.lastSync ? new Date(props.layer.lastSync).toLocaleTimeString() : 'never synced'));

function onIntervalChange(): void {
    props.layer.recurrenceUnit = 's';
}
</script>

<style scoped>
.fl-row { display: flex; align-items: center; justify-content: space-between; gap: 8px; padding: 8px 0; border-top: 1px solid #242424; }
.fl-row-main { display: flex; align-items: center; gap: 8px; min-width: 0; }
.fl-eye { background: none; border: none; cursor: pointer; font-size: 14px; }
.fl-row-info { min-width: 0; }
.fl-row-name { font-size: 12px; font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.fl-row-meta { font-size: 10px; opacity: .65; }
.fl-row-controls { display: flex; align-items: center; gap: 6px; flex-shrink: 0; }
.fl-interval { display: flex; align-items: center; gap: 2px; font-size: 10px; opacity: .8; }
.fl-interval input { width: 44px; padding: 2px 4px; border-radius: 3px; border: 1px solid #444; background: transparent; color: inherit; }
.fl-icon-btn { background: none; border: 1px solid #444; border-radius: 4px; cursor: pointer; font-size: 12px; padding: 3px 6px; color: inherit; }
.fl-icon-btn.danger { border-color: #ff5722; }
</style>
