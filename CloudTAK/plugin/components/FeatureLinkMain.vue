<template>
    <div class='fl-root'>
        <!-- Header: title + account button, visible on every tab -->
        <div class='fl-header'>
            <PluginIcon :size='16' />
            <span class='fl-title'>FeatureLink</span>
            <button
                class='fl-account-btn'
                :class='{ authed: isAuthed }'
                :title='isAuthed ? `Signed in as ${username}` : "Sign in to ArcGIS"'
                @click='showAccount = true'
            >{{ isAuthed ? username : 'Sign In' }}</button>

            <div class='fl-gear-wrap'>
                <button class='fl-gear-btn' title='Settings' @click='showSettingsMenu = !showSettingsMenu'>⚙</button>
                <div v-if='showSettingsMenu' class='fl-settings-backdrop' @click='showSettingsMenu = false'></div>
                <div v-if='showSettingsMenu' class='fl-settings-menu'>
                    <button class='fl-menu-item' :disabled='checkingNow' @click='onCheckForSharedConfigs'>
                        {{ checkingNow ? 'Checking…' : 'Check for Shared Configs' }}
                    </button>
                    <button class='fl-menu-item danger' @click='onClearAllLayers'>Clear All Layers</button>
                </div>
            </div>
        </div>

        <!-- Tab bar -->
        <div class='fl-tabs'>
            <button :class='{ active: tab === "home" }' @click='tab = "home"'>Home</button>
            <button :class='{ active: tab === "layers" }' @click='tab = "layers"'>Layers</button>
            <button :class='{ active: tab === "pli" }' @click='tab = "pli"'>PLI</button>
        </div>

        <div class='fl-body'>
            <HomeTab v-if='tab === "home"' @open-account='showAccount = true' @go-pli='tab = "pli"' @open-send='showSend = true' />
            <LayersTab v-else-if='tab === "layers"' @open-add-layer='showAddLayer = true' />
            <PliTab v-else />
        </div>

        <!-- Full-screen pushed overlays -->
        <AccountView v-if='showAccount' @close='showAccount = false' />
        <AddLayerView v-if='showAddLayer' @close='showAddLayer = false' />
        <SendToLayerPicker v-if='showSend' @close='showSend = false' />
    </div>
</template>

<script setup lang="ts">
import { ref, computed } from 'vue';
import PluginIcon from './PluginIcon.vue';
import HomeTab from './tabs/HomeTab.vue';
import LayersTab from './tabs/LayersTab.vue';
import PliTab from './tabs/PliTab.vue';
import AccountView from './AccountView.vue';
import AddLayerView from './AddLayerView.vue';
import SendToLayerPicker from './SendToLayerPicker.vue';
import { authState, isAuthenticated, getUsername } from '../lib/arcgisAuth.ts';
import { clearAllLayers } from '../lib/layerActions.ts';
import { checkForSharedConfigsNow } from '../lib/importIngest.ts';

const tab = ref<'home' | 'layers' | 'pli'>('home');
const showAccount = ref(false);
const showAddLayer = ref(false);
const showSend = ref(false);
const showSettingsMenu = ref(false);
const checkingNow = ref(false);

const isAuthed = computed(() => { void authState.username; return isAuthenticated(); });
const username = computed(() => getUsername());

async function onClearAllLayers(): Promise<void> {
    showSettingsMenu.value = false;
    const ok = window.confirm(
        'Remove all layers from this device? Layers from "My ArcGIS Layers" will move back '
        + 'there; any others will be removed entirely. Map markers will be cleared.',
    );
    if (!ok) return;
    await clearAllLayers();
}

// Lets the user force an immediate check instead of waiting on the 60s background poll (see
// importIngest.ts) — useful right after sharing from ATAK rather than sitting around, or if a
// tab that's been open a long time has had its timer throttled by the browser.
async function onCheckForSharedConfigs(): Promise<void> {
    checkingNow.value = true;
    try { await checkForSharedConfigsNow(); }
    finally { checkingNow.value = false; showSettingsMenu.value = false; }
}
</script>

<style scoped>
.fl-root { display: flex; flex-direction: column; height: 100%; font-family: sans-serif; font-size: 13px; }
.fl-header {
    display: flex; align-items: center; gap: 8px; padding: 8px 12px;
    background: #1a1a1a; color: #fff; flex-shrink: 0;
}
.fl-title { font-weight: 600; }
.fl-account-btn {
    margin-left: auto; font-size: 11px; padding: 4px 10px; border-radius: 4px;
    border: 1px solid #ff5722; background: transparent; color: #ff5722; cursor: pointer;
}
.fl-account-btn.authed { border-color: #4caf50; color: #4caf50; }
.fl-gear-wrap { position: relative; }
.fl-gear-btn {
    font-size: 14px; padding: 4px 8px; border-radius: 4px; border: 1px solid #555;
    background: transparent; color: inherit; cursor: pointer; line-height: 1;
}
.fl-settings-backdrop { position: fixed; inset: 0; z-index: 20; }
.fl-settings-menu {
    position: absolute; top: calc(100% + 4px); right: 0; z-index: 21; min-width: 160px;
    background: #1f1f1f; border: 1px solid #444; border-radius: 6px; padding: 4px;
    display: flex; flex-direction: column; box-shadow: 0 4px 12px rgba(0, 0, 0, .4);
}
.fl-menu-item {
    text-align: left; padding: 8px 10px; border-radius: 4px; border: none;
    background: transparent; color: inherit; cursor: pointer; font-size: 12px;
}
.fl-menu-item:hover { background: #2a2a2a; }
.fl-menu-item:disabled { opacity: .5; cursor: default; }
.fl-menu-item:disabled:hover { background: transparent; }
.fl-menu-item.danger { color: #ff5722; }
.fl-tabs { display: flex; border-bottom: 1px solid #333; flex-shrink: 0; }
.fl-tabs button {
    flex: 1; padding: 8px; background: none; border: none; border-bottom: 2px solid transparent;
    cursor: pointer; font-size: 12px;
}
.fl-tabs button.active { border-bottom-color: #4caf50; font-weight: 600; }
.fl-body { flex: 1 1 0; overflow-y: auto; min-height: 0; }
</style>
