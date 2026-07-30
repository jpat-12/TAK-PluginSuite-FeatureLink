<!-- Port of the ATAK plugin's page_account.xml: header + status line + Sign in/Sign out
     buttons. No portal URL field, matching ATAK's account screen — OAuth sign-in there is
     always against https://www.arcgis.com too (see arcgisAuth.ts). -->
<template>
    <div class='fl-overlay'>
        <div class='fl-overlay-header'>
            <button class='fl-back' @click='$emit("close")' aria-label='Back'>&larr;</button>
            <span class='fl-title'>ArcGIS Account</span>
        </div>
        <div class='fl-overlay-body'>
            <div class='fl-card'>
                <p class='fl-status' :class='isAuthed ? "ok" : "err"'>
                    {{ isAuthed ? `Signed in as: ${displayUsername}` : statusText }}
                </p>

                <button v-if='!isAuthed' class='fl-btn primary' :disabled='signingIn' @click='doSignIn'>
                    {{ signingIn ? 'Signing in…' : 'Sign in with ArcGIS' }}
                </button>
                <button v-else class='fl-btn secondary' @click='doSignOut'>Sign Out</button>
            </div>
        </div>
    </div>
</template>

<script setup lang="ts">
import { ref, computed } from 'vue';
import { authState, isAuthenticated, getUsername, beginSignIn, signOut } from '../lib/arcgisAuth.ts';
import { fetchUserLayers } from '../lib/layerActions.ts';

defineEmits<{ close: [] }>();

const isAuthed = computed(() => { void authState.username; return isAuthenticated(); });
const displayUsername = computed(() => getUsername());
const signingIn = ref(false);
const errorMsg = ref('');

const statusText = computed(() => errorMsg.value || 'Not signed in');

async function doSignIn(): Promise<void> {
    errorMsg.value = '';
    signingIn.value = true;
    try {
        await beginSignIn(); // opens ArcGIS's hosted login page in a popup and awaits the result
        await fetchUserLayers();
    } catch (e) {
        errorMsg.value = e instanceof Error ? e.message : 'Sign-in failed';
    } finally {
        signingIn.value = false;
    }
}

function doSignOut(): void {
    signOut();
    errorMsg.value = '';
}
</script>

<style scoped>
.fl-overlay { position: absolute; inset: 0; background: #0A0A0A; color: #FFFFFF; display: flex; flex-direction: column; z-index: 10; }
.fl-overlay-header { display: flex; align-items: center; gap: 10px; height: 52px; padding: 0 12px 0 4px; }
.fl-back { width: 40px; height: 40px; background: none; border: none; font-size: 18px; cursor: pointer; color: #FFFFFF; }
.fl-title { font-size: 18px; font-weight: 700; color: #FFFFFF; }
.fl-overlay-body { padding: 12px; }
.fl-card { background: #161616; border-radius: 10px; padding: 12px; display: flex; flex-direction: column; gap: 12px; }
.fl-status { font-size: 11px; margin: 0; }
.fl-status.ok { color: #4CAF50; }
.fl-status.err { color: #FF5722; }
.fl-btn { min-height: 40px; border-radius: 8px; font-size: 13px; font-weight: 600; cursor: pointer; }
.fl-btn.primary { background: #0099CC; border: none; color: #FFFFFF; }
.fl-btn.primary:disabled { background: #1A3A44; cursor: default; }
.fl-btn.secondary { background: transparent; border: 1px solid #2A2A2A; color: #AEB4B8; }
</style>
