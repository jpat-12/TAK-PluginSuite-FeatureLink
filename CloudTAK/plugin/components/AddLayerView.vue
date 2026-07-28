<template>
    <div class='fl-overlay'>
        <div class='fl-overlay-header'>
            <button class='fl-back' @click='$emit("close")'>&larr;</button>
            <span>Add Layer</span>
        </div>
        <div class='fl-overlay-body'>
            <section>
                <h4>Public Layer URL</h4>
                <label class='fl-field'>
                    Feature Service URL
                    <input v-model='url' placeholder='https://services.arcgis.com/.../FeatureServer/0' />
                </label>
                <button class='fl-btn primary' :disabled='busy' @click='doAddPublic'>Add Layer</button>
            </section>

            <section>
                <h4>Import Config</h4>
                <p class='fl-hint'>Paste a FeatureLink config JSON (from another user's Share button, or TAK Portal's configurator), or upload a .json file.</p>
                <label class='fl-field'>
                    Config JSON
                    <textarea v-model='configText' rows='6' placeholder='{ "v": 2, "url": "...", ... }' />
                </label>
                <div class='fl-row'>
                    <button class='fl-btn primary' :disabled='busy' @click='doApplyText'>Apply</button>
                    <label class='fl-btn file'>
                        Upload .json
                        <input type='file' accept='.json,application/json' style='display:none' @change='onFile' />
                    </label>
                </div>
            </section>

            <p v-if='message' :class='["fl-status", ok ? "ok" : "err"]'>{{ message }}</p>
        </div>
    </div>
</template>

<script setup lang="ts">
import { ref } from 'vue';
import { addPublicLayer } from '../lib/layerActions.ts';
import { applyConfigText, applyConfigFile } from '../lib/importConfig.ts';

const emit = defineEmits<{ close: [] }>();

const url = ref('');
const configText = ref('');
const busy = ref(false);
const message = ref('');
const ok = ref(false);

async function doAddPublic(): Promise<void> {
    busy.value = true;
    try {
        const result = await addPublicLayer(url.value);
        ok.value = result.ok;
        message.value = result.message;
        if (result.ok) { url.value = ''; emit('close'); }
    } finally {
        busy.value = false;
    }
}

async function doApplyText(): Promise<void> {
    busy.value = true;
    try {
        const result = await applyConfigText(configText.value);
        ok.value = result.ok;
        message.value = result.message;
        if (result.ok) { configText.value = ''; emit('close'); }
    } finally {
        busy.value = false;
    }
}

async function onFile(ev: Event): Promise<void> {
    const file = (ev.target as HTMLInputElement).files?.[0];
    if (!file) return;
    busy.value = true;
    try {
        const result = await applyConfigFile(file);
        ok.value = result.ok;
        message.value = result.message;
        if (result.ok) emit('close');
    } finally {
        busy.value = false;
        (ev.target as HTMLInputElement).value = '';
    }
}
</script>

<style scoped>
.fl-overlay { position: absolute; inset: 0; background: var(--fl-bg, #111); color: inherit; display: flex; flex-direction: column; z-index: 10; }
.fl-overlay-header { display: flex; align-items: center; gap: 10px; padding: 10px 12px; border-bottom: 1px solid #333; font-weight: 600; }
.fl-back { background: none; border: none; font-size: 18px; cursor: pointer; color: inherit; }
.fl-overlay-body { padding: 14px; display: flex; flex-direction: column; gap: 18px; overflow-y: auto; }
section h4 { margin: 0 0 8px; font-size: 12px; text-transform: uppercase; letter-spacing: .04em; opacity: .8; }
.fl-hint { font-size: 11px; opacity: .7; margin: 0 0 8px; }
.fl-field { display: flex; flex-direction: column; gap: 4px; font-size: 12px; margin-bottom: 8px; }
.fl-field input, .fl-field textarea { padding: 6px 8px; border-radius: 4px; border: 1px solid #444; background: transparent; color: inherit; font-family: inherit; }
.fl-row { display: flex; gap: 8px; }
.fl-btn { padding: 8px 12px; border-radius: 4px; border: 1px solid #555; background: transparent; color: inherit; cursor: pointer; font-size: 12px; }
.fl-btn.primary { border-color: #4caf50; color: #4caf50; }
.fl-btn.file { display: inline-flex; align-items: center; }
.fl-status { font-size: 12px; margin: 0; }
.fl-status.ok { color: #4caf50; }
.fl-status.err { color: #ff5722; }
</style>
