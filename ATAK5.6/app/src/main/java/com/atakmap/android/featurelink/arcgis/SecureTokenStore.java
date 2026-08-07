package com.atakmap.android.featurelink.arcgis;

import android.content.SharedPreferences;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;
import android.util.Log;

import java.security.KeyStore;

import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

/**
 * C-20 — at-rest protection for the ArcGIS refresh token.
 *
 * <p>The refresh token was written to {@code SharedPreferences} in plaintext. An ArcGIS refresh
 * token is long-lived and grants full portal content access, so on a rooted device, a device with
 * an unlocked bootloader, or via any file-level extraction path it is a persistent compromise of
 * the operator's ArcGIS account from a lost or captured device.
 *
 * <p>This wraps every value in AES-256/GCM under a key that lives in the <b>Android Keystore</b>
 * and is never exported — the ciphertext in SharedPreferences is useless without the device's
 * hardware-backed (where available) key material.
 *
 * <p><b>Why not {@code EncryptedSharedPreferences}?</b> {@code androidx.security:security-crypto}
 * transitively depends on {@code androidx.core}, which this plugin's build explicitly excludes
 * (see {@code app/build.gradle}'s {@code configurations.implementation} block, present because
 * duplicated AndroidX classes break plugin class loading inside ATAK). Talking to the Keystore
 * directly achieves the same protection with zero new dependencies. Recorded as an owner
 * decision in {@code QUESTIONS-FOR-OWNER.md}.
 *
 * <p>Fail-soft by design: if the Keystore is unavailable (or the key was invalidated by a lock
 * screen change) the stored value is <b>discarded</b> rather than falling back to plaintext. The
 * operator is asked to sign in again — a re-auth prompt is always preferable to silently
 * downgrading to the defect this class exists to fix.
 */
final class SecureTokenStore {

    private static final String TAG = "FeatureLink.SecureTokenStore";
    private static final String KEY_ALIAS = "featurelink_token_key";
    private static final String TRANSFORM = "AES/GCM/NoPadding";
    private static final int GCM_TAG_BITS = 128;
    private static final int IV_BYTES = 12;
    /** Marks a value written by this class, so a legacy plaintext value is recognisable. */
    private static final String PREFIX = "fl1:";

    private final SharedPreferences prefs;

    SecureTokenStore(SharedPreferences prefs) {
        this.prefs = prefs;
    }

    /** Stores {@code value} encrypted. A null/empty value removes the entry. */
    void put(String key, String value) {
        if (value == null || value.isEmpty()) {
            prefs.edit().remove(key).apply();
            return;
        }
        String encoded = encrypt(value);
        if (encoded == null) {
            // Never silently downgrade to plaintext — that is precisely the defect being fixed.
            Log.e(TAG, "encryption unavailable — refusing to persist '" + key + "'");
            prefs.edit().remove(key).apply();
            return;
        }
        prefs.edit().putString(key, encoded).apply();
    }

    /**
     * Reads and decrypts. A value written by an earlier plaintext build is <b>not</b> returned:
     * it is deleted and null is returned, so the operator re-authenticates once and the plaintext
     * secret stops existing on the device.
     */
    String get(String key) {
        String raw = prefs.getString(key, null);
        if (raw == null || raw.isEmpty()) return null;
        if (!raw.startsWith(PREFIX)) {
            Log.w(TAG, "discarding legacy plaintext value for '" + key
                    + "' — sign-in will be required once");
            prefs.edit().remove(key).apply();
            return null;
        }
        String plain = decrypt(raw.substring(PREFIX.length()));
        if (plain == null) {
            prefs.edit().remove(key).apply();
            return null;
        }
        return plain;
    }

    void remove(String key) {
        prefs.edit().remove(key).apply();
    }

    // -------------------------------------------------------------------------

    private String encrypt(String plain) {
        try {
            Cipher cipher = Cipher.getInstance(TRANSFORM);
            cipher.init(Cipher.ENCRYPT_MODE, getOrCreateKey());
            byte[] iv = cipher.getIV();
            byte[] ct = cipher.doFinal(plain.getBytes(java.nio.charset.StandardCharsets.UTF_8));
            byte[] out = new byte[iv.length + ct.length];
            System.arraycopy(iv, 0, out, 0, iv.length);
            System.arraycopy(ct, 0, out, iv.length, ct.length);
            return PREFIX + Base64.encodeToString(out, Base64.NO_WRAP);
        } catch (Exception e) {
            Log.e(TAG, "encrypt failed", e);
            return null;
        }
    }

    private String decrypt(String encoded) {
        try {
            byte[] blob = Base64.decode(encoded, Base64.NO_WRAP);
            if (blob.length <= IV_BYTES) return null;
            byte[] iv = new byte[IV_BYTES];
            System.arraycopy(blob, 0, iv, 0, IV_BYTES);
            Cipher cipher = Cipher.getInstance(TRANSFORM);
            cipher.init(Cipher.DECRYPT_MODE, getOrCreateKey(),
                    new GCMParameterSpec(GCM_TAG_BITS, iv));
            byte[] plain = cipher.doFinal(blob, IV_BYTES, blob.length - IV_BYTES);
            return new String(plain, java.nio.charset.StandardCharsets.UTF_8);
        } catch (Exception e) {
            // Includes the "key permanently invalidated" case after a lock-screen change.
            Log.w(TAG, "decrypt failed — stored token will be discarded", e);
            return null;
        }
    }

    private static synchronized SecretKey getOrCreateKey() throws Exception {
        KeyStore ks = KeyStore.getInstance("AndroidKeyStore");
        ks.load(null);
        KeyStore.Entry entry = ks.getEntry(KEY_ALIAS, null);
        if (entry instanceof KeyStore.SecretKeyEntry) {
            return ((KeyStore.SecretKeyEntry) entry).getSecretKey();
        }
        KeyGenerator kg = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES,
                "AndroidKeyStore");
        kg.init(new KeyGenParameterSpec.Builder(KEY_ALIAS,
                KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                // Deliberately NOT setUserAuthenticationRequired(true): a PLI auto-send must keep
                // working with the screen locked, which is the normal field posture.
                .build());
        return kg.generateKey();
    }
}
