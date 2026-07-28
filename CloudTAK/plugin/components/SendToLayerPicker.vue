<!--
    Replaces the ATAK plugin's radial-menu "Send to Feature Layer" action (no context-menu
    injection point exists in CloudTAK): pick an on-screen map item, pick a target layer, Send.
    One-shot addPliFeature call, matching FeatureLinkDropDownReceiver.handleSendToLayer's
    7-day stale time and "h-g-i-g-o" how default (no objectId tracking — always adds a row).
-->
<template>
    <div class='fl-overlay'>
        <div class='fl-overlay-header'>
            <button class='fl-back' @click='$emit("close")'>&larr;</button>
            <span>Send to Feature Layer</span>
        </div>
        <div class='fl-overlay-body'>
            <label class='fl-field'>
                Map item
                <select v-model='selectedUid'>
                    <option value=''>— pick an on-screen item —</option>
                    <option v-for='m in markers' :key='m.uid' :value='m.uid'>{{ m.callsign }}</option>
                </select>
            </label>
            <p class='fl-hint'>Off-screen item? Paste its UID instead:</p>
            <input v-model='manualUid' class='fl-manual' placeholder='CoT UID' />

            <label class='fl-field'>
                Target layer
                <select v-model='targetUrl'>
                    <option value=''>— pick a layer —</option>
                    <optgroup label='Private'>
                        <option v-for='l in store.privateLayers' :key='l.url' :value='l.url'>{{ l.name }}</option>
                    </optgroup>
                    <optgroup label='Public'>
                        <option v-for='l in store.publicLayers' :key='l.url' :value='l.url'>{{ l.name }}</option>
                    </optgroup>
                </select>
            </label>

            <button class='fl-btn primary' :disabled='busy || !targetUrl || !(selectedUid || manualUid)' @click='doSend'>
                {{ busy ? 'Sending…' : 'Send' }}
            </button>

            <p v-if='message' :class='["fl-status", ok ? "ok" : "err"]'>{{ message }}</p>
        </div>
    </div>
</template>

<script setup lang="ts">
import { ref, computed } from 'vue';
import { store, findLayer } from '../lib/store.ts';
import { listCotMarkers, findCotMarker } from '../lib/cot.ts';
import * as auth from '../lib/arcgisAuth.ts';
import * as rest from '../lib/arcgisRest.ts';
import type { PliFeatureInput } from '../lib/types.ts';

defineEmits<{ close: [] }>();

const markers = computed(() => listCotMarkers());
const selectedUid = ref('');
const manualUid = ref('');
const targetUrl = ref('');
const busy = ref(false);
const message = ref('');
const ok = ref(false);

async function doSend(): Promise<void> {
    const uid = selectedUid.value || manualUid.value.trim();
    if (!uid || !targetUrl.value) return;

    const marker = selectedUid.value ? findCotMarker(uid) : findCotMarker(uid);
    if (!marker) {
        ok.value = false;
        message.value = 'Could not find that item on the map (it may be off-screen or its UID is wrong)';
        return;
    }

    const layer = findLayer(targetUrl.value);
    if (!layer) return;

    busy.value = true;
    try {
        const token = layer.type === 'private' ? await auth.getToken() : null;
        const now = Date.now();
        const input: PliFeatureInput = {
            uid: marker.uid, cotType: marker.cotType, callsign: marker.callsign,
            iconPath: '', remarks: '', how: 'h-g-i-g-o',
            sentByUser: auth.getUsername() ?? '', groupName: '', groupRole: '',
            lat: marker.lat, lon: marker.lon, hae: NaN, ce: NaN, le: NaN,
            timeMs: now, startMs: now, staleMs: now + 7 * 24 * 3600 * 1000,
            rawCotXml: '',
        };
        const objectId = await rest.addPliFeature(layer.url, token, input);
        ok.value = objectId >= 0;
        message.value = ok.value ? `Sent "${marker.callsign}" to ${layer.name}` : 'Send failed';
    } catch (e) {
        ok.value = false;
        message.value = e instanceof Error ? e.message : 'Send failed';
    } finally {
        busy.value = false;
    }
}
</script>

<style scoped>
.fl-overlay { position: absolute; inset: 0; background: var(--fl-bg, #111); color: inherit; display: flex; flex-direction: column; z-index: 10; }
.fl-overlay-header { display: flex; align-items: center; gap: 10px; padding: 10px 12px; border-bottom: 1px solid #333; font-weight: 600; }
.fl-back { background: none; border: none; font-size: 18px; cursor: pointer; color: inherit; }
.fl-overlay-body { padding: 14px; display: flex; flex-direction: column; gap: 10px; overflow-y: auto; }
.fl-field { display: flex; flex-direction: column; gap: 4px; font-size: 12px; }
.fl-field select, .fl-manual { padding: 6px 8px; border-radius: 4px; border: 1px solid #444; background: transparent; color: inherit; }
.fl-hint { font-size: 11px; opacity: .7; margin: 0; }
.fl-btn { padding: 8px 12px; border-radius: 4px; border: 1px solid #555; background: transparent; color: inherit; cursor: pointer; font-size: 12px; }
.fl-btn.primary { border-color: #4caf50; color: #4caf50; }
.fl-btn:disabled { opacity: .5; cursor: default; }
.fl-status { font-size: 12px; margin: 0; }
.fl-status.ok { color: #4caf50; }
.fl-status.err { color: #ff5722; }
</style>
