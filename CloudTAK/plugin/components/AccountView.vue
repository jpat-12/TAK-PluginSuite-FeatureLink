<template>
    <div class='fl-overlay'>
        <div class='fl-overlay-header'>
            <button class='fl-back' @click='$emit("close")'>&larr;</button>
            <span>Account</span>
        </div>
        <div class='fl-overlay-body'>
            <p v-if='isAuthed' class='fl-status ok'>Signed in as: {{ displayUsername }}</p>
            <p v-else class='fl-status err'>Not signed in</p>

            <template v-if='!isAuthed'>
                <label class='fl-field'>
                    Portal URL
                    <input v-model='portalUrl' placeholder='https://www.arcgis.com' />
                </label>
                <label class='fl-field'>
                    Username
                    <input v-model='usernameInput' autocomplete='username' />
                </label>
                <label class='fl-field'>
                    Password
                    <input v-model='password' type='password' autocomplete='current-password' @keyup.enter='doSignIn' />
                </label>
                <p class='fl-hint'>
                    Your password is sent directly to the ArcGIS portal above to obtain a
                    sign-in token — it is never stored by this plugin. This only works for
                    ArcGIS "built-in" accounts, not SSO/enterprise/social logins.
                </p>
                <button class='fl-btn primary' :disabled='signingIn' @click='doSignIn'>
                    {{ signingIn ? 'Signing in…' : 'Sign In' }}
                </button>
            </template>
            <button v-else class='fl-btn' @click='doSignOut'>Sign Out</button>

            <p v-if='errorMsg' class='fl-status err'>{{ errorMsg }}</p>
        </div>
    </div>
</template>

<script setup lang="ts">
import { ref, computed } from 'vue';
import { authState, isAuthenticated, getUsername, signIn, signOut, getPortalUrl } from '../lib/arcgisAuth.ts';
import { fetchUserLayers } from '../lib/layerActions.ts';

defineEmits<{ close: [] }>();

const isAuthed = computed(() => { void authState.username; return isAuthenticated(); });
const displayUsername = computed(() => getUsername());
const portalUrl = ref(getPortalUrl());
const usernameInput = ref('');
const password = ref('');
const signingIn = ref(false);
const errorMsg = ref('');

async function doSignIn(): Promise<void> {
    errorMsg.value = '';
    if (!usernameInput.value.trim() || !password.value) {
        errorMsg.value = 'Enter both username and password';
        return;
    }
    signingIn.value = true;
    try {
        await signIn(portalUrl.value || 'https://www.arcgis.com', usernameInput.value.trim(), password.value);
        password.value = '';
        await fetchUserLayers();
    } catch (e) {
        errorMsg.value = e instanceof Error ? e.message : 'Sign-in failed';
    } finally {
        signingIn.value = false;
    }
}

function doSignOut(): void {
    signOut();
}
</script>

<style scoped>
.fl-overlay { position: absolute; inset: 0; background: var(--fl-bg, #111); color: inherit; display: flex; flex-direction: column; z-index: 10; }
.fl-overlay-header { display: flex; align-items: center; gap: 10px; padding: 10px 12px; border-bottom: 1px solid #333; font-weight: 600; }
.fl-back { background: none; border: none; font-size: 18px; cursor: pointer; color: inherit; }
.fl-overlay-body { padding: 14px; display: flex; flex-direction: column; gap: 12px; overflow-y: auto; }
.fl-field { display: flex; flex-direction: column; gap: 4px; font-size: 12px; }
.fl-field input, .fl-field textarea { padding: 6px 8px; border-radius: 4px; border: 1px solid #444; background: transparent; color: inherit; }
.fl-hint { font-size: 11px; opacity: .65; margin: 0; }
.fl-btn { padding: 8px 12px; border-radius: 4px; border: 1px solid #555; background: transparent; color: inherit; cursor: pointer; }
.fl-btn.primary { border-color: #4caf50; color: #4caf50; }
.fl-btn:disabled { opacity: .5; cursor: default; }
.fl-status { font-size: 12px; margin: 0; }
.fl-status.ok { color: #4caf50; }
.fl-status.err { color: #ff5722; }
</style>
