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

const tab = ref<'home' | 'layers' | 'pli'>('home');
const showAccount = ref(false);
const showAddLayer = ref(false);
const showSend = ref(false);

const isAuthed = computed(() => { void authState.username; return isAuthenticated(); });
const username = computed(() => getUsername());
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
.fl-tabs { display: flex; border-bottom: 1px solid #333; flex-shrink: 0; }
.fl-tabs button {
    flex: 1; padding: 8px; background: none; border: none; border-bottom: 2px solid transparent;
    cursor: pointer; font-size: 12px;
}
.fl-tabs button.active { border-bottom-color: #4caf50; font-weight: 600; }
.fl-body { flex: 1 1 0; overflow-y: auto; min-height: 0; }
</style>
