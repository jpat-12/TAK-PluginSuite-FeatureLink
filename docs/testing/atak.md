# FeatureLink ATAK 5.6 / 5.7 — hands-on test instructions (WP1 remediation)

**Branch:** `audit-remediation`
**Applies to:** `ATAK5.6/` and `ATAK5.7/` (the two trees are byte-identical except for one
`ImportResolver` import in `FeatureLinkMapComponent.java` — test whichever matches your device's
ATAK, but the behaviour under test is the same in both).

> ## Read this first
>
> **Nothing in this document has been compiled or executed.** This machine has `javac` 17 but no
> `gradle` and no ATAK SDK, so the plugin could not be built, let alone run. Correctness was
> established by reading, by symbol-level cross-checking, and by running `javac` in parse-and-
> typecheck mode (which confirmed zero syntax errors and zero non-symbol semantic errors — every
> remaining diagnostic was `cannot find symbol` against the absent SDK).
>
> **Assume the first build will fail on something and budget time for it.** Section 0 covers the
> most likely causes. Section 9 lists, honestly, the changes I could not verify by reading and
> which therefore deserve the hardest testing.

---

## 0. Build and sideload

### 0.1 What you need that this machine did not have

| Requirement | Why | Notes |
|---|---|---|
| ATAK SDK for your target (5.6.0 or 5.7.0) | `app/build.gradle:279-281` puts `${atakSdkPath}/main.jar` on the compile classpath. Without it every `com.atakmap.*` / `gov.tak.*` reference fails. | You already have this locally — it is the `atak.sdk.path` in your `local.properties`. |
| Gradle (via `gradlew`) + JDK 17 | The build itself. | `compileOptions` pins source/target 17. |
| Android SDK, `compileSdk 36`, build-tools | Standard. | |
| A device or emulator with ATAK 5.6 (or 5.7) civ installed | The plugin is loaded by ATAK; it has no launcher activity of its own. | |
| An ArcGIS Online (or Enterprise) account | Sign-in, private layers, PLI publish. | |

### 0.2 Confirm the SDK path property

`app/build.gradle:44` reads **`atak.sdk.path`** (not `sdk.path`). Check `ATAK5.6/local.properties`
contains:

```properties
atak.sdk.path=<absolute path to your ATAK 5.6 SDK directory containing main.jar>
```

Sanity check before building:

```bash
cd "ATAK5.6"
grep atak.sdk.path local.properties
ls "$(grep '^atak.sdk.path' local.properties | cut -d= -f2-)/main.jar"
```

If that `ls` fails, the build will silently skip the `compileOnly` line and then produce hundreds
of "cannot find symbol com.atakmap..." errors. That is the first thing to check on a failed build.

### 0.3 Build

```bash
cd "ATAK5.6"          # or ATAK5.7
./gradlew clean assembleCivDebug
```

Output APK:

```
ATAK5.6/app/build/outputs/apk/civ/debug/ATAK-Plugin-FeatureLink-2.7.24-ATAK-5.6-debug.apk
```

### 0.4 Install

```bash
adb install -r "app/build/outputs/apk/civ/debug/ATAK-Plugin-FeatureLink-2.7.24-ATAK-5.6-debug.apk"
```

If `adb install -r` fails with `INSTALL_FAILED_UPDATE_INCOMPATIBLE` or
`INSTALL_FAILED_VERSION_DOWNGRADE`, uninstall first:

```bash
adb uninstall com.atakmap.android.featurelink.plugin
adb install "app/build/outputs/apk/.../...-debug.apk"
```

> **Expect to have to uninstall this time.** `versionCode` is still `5` (C-15 was explicitly
> deferred out of scope — see the remediation report), so Android will refuse a same-version
> upgrade. This is a known, deliberate gap, not a regression.

Then in ATAK: **Settings → Tool Preferences → Plugins**, confirm FeatureLink is enabled, and open
it from the toolbar.

### 0.5 Watch the log while testing

Every test below is easier with logcat running:

```bash
adb logcat -c
adb logcat | grep -E "FeatureLink|ArcGIS|AutoIconset|AutoSymbology|DisplayConfig"
```

### 0.6 IMPORTANT — one-time re-sign-in is expected

C-20 moved the ArcGIS refresh token from plaintext `SharedPreferences` into an
Android-Keystore-encrypted value. `SecureTokenStore.get()` **deliberately discards** any
plaintext value written by an older build rather than reading it. So on first launch after this
update you will be signed out once and must sign in again. That is correct behaviour, not a bug.
It should happen exactly once.

---

## 1. C-07 — symbology on the download path *(the owner's reported defect)*

**Your report:** *"still not downloading individual layers with their symbology."*

**What was actually wrong.** The renderer fetch, `AutoIconset.generate`, `AutoSymbology.extract`
and `DisplayConfig.forAutoIcons` all lived inside `addPublicLayer()`. `downloadLayer()` contained
none of them — it only read `layerDisplayConfigs.get(layer.url)`. So **exactly one of six routes**
onto the map produced symbology: pasting a URL into "Add Layer". Every other route rendered ATAK
default blue with no icon.

**What "correct" looks like vs the fallback.** The fallback is ATAK's default feature blue,
`#3388ff` — a plain blue marker with no custom icon, or a plain blue 2px solid line with no fill.
If you see that on a layer whose ArcGIS renderer defines icons or colours, symbology did not
resolve.

### Preconditions
- Signed into ArcGIS in the plugin.
- **A point layer** whose ArcGIS renderer uses `esriPMS` picture-marker symbols (custom PNG
  icons), ideally a `uniqueValue` renderer keyed on some attribute — so different attribute
  values get visibly different icons.
- **A polyline or polygon layer** whose renderer is `uniqueValue` over `esriSLS`/`esriSFS`
  **and also declares a `defaultSymbol`** (this combination is what C-24 was about).

### Test 1.1 — the previously-working route (regression check)
1. Layers tab → **Add Layer** → paste the point layer's FeatureServer layer URL → **Add**.
2. **Expect:** toast `Icons ready: <Group> (<n>)`, then `Downloaded: <name> (<n> features)`, and
   markers on the map wearing the layer's **custom icons**, differing by attribute value.
3. **Before the fix:** this route already worked. If it is now broken, that is a regression I
   introduced — report it.

### Test 1.2 — browse-list download *(this is the one that was broken)*
1. Layers tab → **My ArcGIS Layers** → **Refresh**.
2. Find the same point layer in the browse list. Tap its **download** action button.
3. **Expect:** `Icons ready: …` toast, then features on the map **with their custom icons**.
4. **Before the fix:** the layer downloaded and markers appeared, but every one was a plain
   `#3388ff` blue marker with no icon, and no `Icons ready` toast ever appeared.

### Test 1.3 — re-download / refresh
1. On any already-downloaded layer, tap the **↻ refresh** action.
2. **Expect:** icons and shape styling still correct after the refresh.
3. **Before the fix:** the ↻ path stripped styling — markers reverted to blue.

### Test 1.4 — auto-refresh tick
1. On a downloaded layer set **Refresh every** to `60` seconds. Leave the plugin open.
2. Wait ~2 minutes.
3. **Expect:** the layer re-downloads and **keeps** its symbology.
4. **Before the fix:** styling was lost on the automatic refresh.
   *(Note: whether the tick fires at all is C-25, which is deferred — see §10. If nothing
   re-downloads, that is the deferred item, not this one.)*

### Test 1.5 — restart persistence
1. Fully close ATAK (force-stop). Reopen. Open FeatureLink. Re-download the layer.
2. **Expect:** symbology resolves again — either from the persisted config or by re-resolving the
   renderer.
3. **Before the fix:** guaranteed plain blue after a restart.

### Test 1.6 — private layer symbology
1. Add a **private** (not shared-to-everyone) layer by URL while signed in.
2. **Expect:** it downloads with symbology.
3. **Before the fix:** the add path hardcoded a `null` token for the renderer fetch, so a private
   layer's renderer request returned an ArcGIS auth error, nothing checked it, and the layer
   rendered unstyled. It also always set `type = "public"`, so the feature query itself ran
   unauthenticated and returned `0 features` **while reporting success**.

### Test 1.7 — polyline/polygon per-value styling (C-24)
1. Add the polygon/polyline layer with the `uniqueValue` + `defaultSymbol` renderer.
2. **Expect:** features are styled **per attribute value** — different values, different
   stroke/fill colours.
3. **Before the fix:** every feature rendered in the `defaultSymbol` style. `resolveShapeStyle()`
   checked `singleShapeStyle` (populated from `defaultSymbol`) *before* `shapeStyleByValue`, so
   per-value styling was dead code for exactly the renderers that had it.

---

## 2. C-08 — multi-sublayer FeatureServers

**What was wrong.** `ensureLayerIndex()` hard-appended `/0`, so a FeatureServer exposing layers
0, 1, 2 appeared as **one row permanently pinned to layer 0**. Layers 1 and 2 were unreachable.
This is the most literal reading of "individual layers".

### Preconditions
A FeatureServer **service root** URL (ending `/FeatureServer`, with no trailing `/0`) that
exposes **more than one** sublayer.

### Test 2.1 — the picker
1. Add Layer → paste the **service root** URL (no `/0`) → **Add**.
2. **Expect:** a dialog *"This service has N layers"* with a checkbox per sublayer, each labelled
   `<name>  [<id>]`, all pre-checked.
3. Uncheck one, tap **Add selected**.
4. **Expect:** one row per selected sublayer in Public Layers, each downloading its **own**
   features, each with its own symbology.
5. **Before the fix:** one row, always layer 0, and no way to reach the others.

### Test 2.2 — single-sublayer service (no regression)
1. Paste a service root that has only one sublayer.
2. **Expect:** no picker; it just adds, exactly as before.

### Test 2.3 — explicit layer URL (no regression)
1. Paste a URL that already ends `/FeatureServer/2`.
2. **Expect:** no picker; that specific sublayer is added.

### Test 2.4 — duplicate rejection
1. Add a layer, then try to add the **same** URL again (also try it with a trailing `/` and with
   the hostname in a different case).
2. **Expect:** toast *"That layer is already added"*.
3. **Before the fix:** it added a second row keyed to the same URL, so removing one orphaned the
   other's markers on the map permanently.

---

## 3. C-06 — pagination and truncation

**What was wrong.** `downloadLayerAsCoT` issued a single unpaginated query. ArcGIS Online caps a
response at the service's `maxRecordCount` (commonly 1,000–2,000). Any larger layer was **silently
truncated** and the operator was told `Downloaded: X (2000 features)` as if complete.

### Preconditions
A layer with **more features than its `maxRecordCount`** — ideally 3,000+. Check the real count in
a browser: `<layerUrl>/query?where=1=1&returnCountOnly=true&f=json`, and the page size at
`<layerUrl>?f=json` (look for `maxRecordCount`).

### Test 3.1 — full download
1. Download the large layer.
2. **Expect:** the completion toast reports a count **matching the real total**, not the page size.
3. Watch logcat for `downloadLayerFeatures: <n> features` — it should exceed `maxRecordCount`.
4. **Before the fix:** the count stopped exactly at `maxRecordCount` (e.g. exactly 2000) and
   claimed success.

> **Tell-tale:** a downloaded count that is *exactly* 1000 or *exactly* 2000 is the classic
> signature of the old bug.

### Test 3.2 — the truncation warning
1. Find or construct a layer with **more than 50,000** features (the hard ceiling).
2. **Expect:** a modal dialog *"Layer download incomplete"* naming the layer, the number loaded,
   and the service page size. Not a toast — a dialog you must dismiss.
3. **Before the fix:** no warning of any kind existed.

### Test 3.3 — portal search pagination
1. Sign in with an account owning **more than 100** Feature Services.
2. Layers → My ArcGIS Layers → Refresh.
3. **Expect:** more than 100 rows.
4. **Before the fix:** capped at exactly 100, silently.

---

## 4. C-22 — ArcGIS error bodies (HTTP 200 + `{"error":…}`)

**What was wrong.** ArcGIS returns application errors with **HTTP 200** and an `error` object in
the body. That was checked at only 4 of ~14 parse sites, so an expired session
(`{"error":{"code":498,"message":"Invalid token"}}`) parsed as a *successful empty result*. The
operator saw **"Downloaded: MyLayer (0 features)"** and an empty map, with no hint the session had
died. This was the single most operationally dangerous defect in the codebase — an
authoritative-looking empty map instead of an error.

### Test 4.1 — expired / invalid session
1. Sign in. Download a private layer successfully.
2. Invalidate the session — easiest reliable method: on ArcGIS Online, **Profile → Settings →
   Security → sign out of all devices**, or revoke the app's authorization. (Simply waiting for
   expiry also works but is slow, and the silent refresh may paper over it.)
3. Back in the plugin, tap **↻** on that private layer.
4. **Expect:** a **"Download failed"** dialog reading *"Your ArcGIS session is no longer valid
   (error 498). Open the Account page and sign in again."*
5. **Before the fix:** `Downloaded: MyLayer (0 features)` and every marker vanished from the map,
   with no error anywhere.

### Test 4.2 — no access to a shared private layer
1. Have someone share a private layer config to you that your account cannot actually read.
2. Download it.
3. **Expect:** an explicit authorisation error naming the ArcGIS message.
4. **Before the fix:** "0 features", reported as success.

### Test 4.3 — bad URL / no network
1. Add Layer → paste a valid-looking but nonexistent host → Add.
2. Turn off wifi and mobile data, then retry a download.
3. **Expect:** distinct messages — *"The server could not be reached…"* vs *"The server did not
   respond in time…"* vs an ArcGIS-reported message.
4. **Before the fix:** all five distinct causes collapsed into one *"Could not load layer from
   URL"*.

### Test 4.4 — Home tab totals
1. With one layer deliberately broken (bad URL) and others fine, open the Home tab.
2. **Expect:** `Total Features: <n>  (1 unavailable)`, and that layer's row reads `unavailable`.
3. **Before the fix:** a failed count returned `-1` and was **added into the running total**, so
   the headline figure was silently reduced by 1 per failed layer and could go negative.

---

## 5. C-21 — token out of URLs and out of logs

**What was wrong.** The OAuth access token was appended as `&token=<token>` to **every** request
URL, and those URLs were written to logcat on any failure. A live ArcGIS access token therefore
landed in the device log, readable by any app holding `READ_LOGS`, by any bugreport, and by ATAK's
own crash bundles. Query-string tokens are additionally logged by every proxy in the path.

### Test 5.1 — no token in logs
1. `adb logcat -c`, then start logcat capturing to a file.
2. Sign in, download a private layer, let a PLI send happen, then cause a failure (turn off the
   network mid-download).
3. Stop the capture and search it:

```bash
adb logcat -d > /tmp/fl.log
grep -n "token=" /tmp/fl.log
```

4. **Expect:** either no hits at all, or only `token=<redacted>`. **Never** a real token value.
5. **Before the fix:** `GET failed: https://…&token=AAPK…` with the live token in plain text.

### Test 5.2 — everything still authenticates
Because the token moved from the query string to an `Authorization: Bearer` header, **every**
authenticated call is a potential regression. Exercise all of them:
- Sign in
- My ArcGIS Layers → Refresh (portal search)
- Shared with me list populates
- Download a private layer
- Home tab feature counts for private layers
- Create a PLI feature service
- PLI auto-send (add, then update)
- Share a layer

**If any one of these returns 401/403 or an "Invalid token" error, the header migration is the
first suspect.** See §9 — this is high-risk.

---

## 6. C-09 — OAuth `state` (login CSRF / session fixation)

**What was wrong.** No `state` nonce was generated, sent, or verified anywhere.
`OAuthCallbackActivity` performed *no validation at all* and then fired an **implicit,
unprotected, system-wide broadcast** carrying the authorization code. Any installed app could
invoke `featurelink://auth?code=ATTACKER_CODE` and force the plugin to exchange an
attacker-controlled code — binding the operator's session to the **attacker's** ArcGIS account, so
all subsequently uploaded PLI and layer data went to the attacker's portal. Separately, the PKCE
verifier was persisted to plaintext prefs and was *not* cleared on cancel.

### Test 6.1 — normal sign-in still works
1. Account → **Sign in with ArcGIS** → complete in the WebView.
2. **Expect:** `Signed in as: <username>`, layers load.

### Test 6.2 — the state nonce is actually sent
1. With logcat running, start a sign-in.
2. Inspect the authorize URL the WebView loads (it is visible in the WebView, or add a breakpoint).
3. **Expect:** the URL contains `&state=<43-ish char random string>`.
4. **Before the fix:** no `state` parameter at all.

### Test 6.3 — forged callback is rejected *(the security test)*
With **no sign-in in progress**, from a shell:

```bash
adb shell am start -a android.intent.action.VIEW \
  -d "featurelink://auth?code=FORGED_ATTACKER_CODE&state=wrong"
```

4. **Expect:** nothing happens to your session. Logcat shows either
   `OAuth callback rejected: state mismatch (possible login-CSRF attempt)` or
   *"No sign-in is in progress"*. You stay signed in as yourself.
5. **Before the fix:** the plugin accepted it and attempted the code exchange.

Repeat **during** a live sign-in (start the sign-in, then fire the command before completing it) —
it must still be rejected, because the forged `state` will not match the pending nonce.

### Test 6.4 — cancel clears the flow
1. Start a sign-in, then tap **Cancel** in the WebView header.
2. **Expect:** `Sign-in cancelled`. Then fire the Test 6.3 command — it must be rejected.
3. **Before the fix:** cancelling left a usable PKCE verifier on disk indefinitely.

### Test 6.5 — back button mid-sign-in
1. Start a sign-in, then press the device **Back** button while the WebView is showing.
2. **Expect:** the WebView closes, the flow is cancelled, and the drop-down **stays open**.
3. **Before the fix:** Back closed the whole drop-down mid-sign-in, leaving the WebView state and
   a layout listener attached to ATAK's own decor view for the rest of the ATAK session.

---

## 7. C-02 — third-party map/config injection

**What was wrong.** The `IMPORT_CONFIG` receiver was registered `RECEIVER_EXPORTED` with **no
permission**, so *any app on the device* could broadcast
`com.atakmap.android.featurelink.IMPORT_CONFIG` with an arbitrary `config` extra. That landed in
`applyScannedPayload()` and added arbitrary feature layers to the operator's map, fetched
attacker-chosen URLs, wrote attacker-named iconset zips to external storage, and rendered
attacker-controlled markers — **with no confirmation dialog anywhere**. A remote map-injection /
disinformation primitive reachable from any unprivileged app.

### Test 7.1 — hostile broadcast is now blocked
```bash
adb shell am broadcast \
  -a com.atakmap.android.featurelink.IMPORT_CONFIG \
  --es config '{"v":2,"url":"https://evil.example.com/arcgis/rest/services/X/FeatureServer/0"}'
```
1. **Expect:** nothing reaches the plugin. `adb shell am broadcast` runs as the shell UID, which
   does not hold the signature-level permission, so the receiver never fires. No dialog, no layer.
2. **Before the fix:** the layer was added and downloaded silently.

### Test 7.2 — the legitimate deep link now asks first
1. On the device, open a `featurelink://import?config=<url-encoded JSON>` link (the "Open in ATAK"
   button on TAK Portal's FeatureLink page).
2. **Expect:** an **"Apply external configuration?"** dialog showing:
   - `Source: featurelink://import deep link`
   - the target service URL(s) it points at
   - a warning that applying will add layers and download features
   - **Apply** / **Reject** buttons, not dismissible by tapping outside
3. Tap **Reject** → nothing is added. Repeat and tap **Apply** → the layer is added as before.
4. **Before the fix:** it applied immediately with no prompt.

### Test 7.3 — PLI endpoint warning
1. Deep-link a payload of type `pli_endpoint` (or one containing `pli_url`).
2. **Expect:** the same dialog, plus an explicit **WARNING** that the payload changes where your
   own position reports are sent.
3. **Before the fix:** silently applied, silently repointing your PLI.

### Test 7.4 — no double-apply
1. Apply a deep link once.
2. **Expect:** exactly **one** copy of the layer.
3. **Before the fix:** a same-process broadcast was delivered twice (once via ATAK's
   `DocumentedIntentFilter`, once via the `Context` registration), so the config applied twice and
   produced duplicate layers and duplicate markers.

### Test 7.5 — QR path unaffected
Scan a config QR as before. It should behave exactly as it always has (the QR path already
involves a deliberate camera action, so it is not gated by the new dialog).

---

## 8. C-20 / C-39 and the smaller items

### Test 8.1 — C-20, encrypted token storage
1. Sign in.
2. On a rooted device or emulator:
```bash
adb shell run-as com.atakmap.app.civ cat shared_prefs/featurelink_prefs.xml
```
3. **Expect:** the `oauth_refresh_token` value starts with `fl1:` followed by base64 ciphertext.
   **No readable token.**
4. **Before the fix:** the ArcGIS refresh token — long-lived, granting full portal content access
   — sat there in plain text.
5. Confirm the session survives an ATAK restart (the key is in the Android Keystore, so decryption
   must still work).
6. **Known acceptable behaviour:** if you change the device lock screen, Android may invalidate
   the key. The plugin then discards the token and asks you to sign in again — it must never fall
   back to storing plaintext.

### Test 8.2 — C-39, locale-independent iconset UID
This is the cross-platform federation guarantee — the same layer must produce a byte-identical
iconset UID on every device and every platform.
1. Add a layer with custom icons on a device set to **English**. Note the generated group from the
   `Icons ready: <Group>` toast, and grab the UID from logcat
   (`installed iconset uid=<hex> group='…'`).
2. Change the device language to **Türkçe (Turkish)**. Force-stop ATAK, reopen, delete and re-add
   the same layer.
3. **Expect:** the **exact same UID hex string**.
4. **Before the fix:** bare `toLowerCase()` used the default locale, so a hostname containing `I`
   folded to the dotless `ı` in Turkish, producing a different canonical URL, a different SHA-256,
   and therefore a different UID from every other device — silently breaking icon matching for
   shared CoT, with a regeneration path that never converged.
5. Also worth trying: Arabic (`ar-EG`) or Hindi, which exercise the non-Latin-digit hex-formatting
   half of the same defect.

### Test 8.3 — cleartext is refused
1. Add Layer → paste an `http://` (not `https://`) URL → Add.
2. **Expect:** toast *"Only https:// service URLs are accepted"*. Nothing is fetched.
3. **Before the fix:** it was fetched in cleartext, letting an on-path attacker control the
   display config, layer URLs and iconset payloads.

### Test 8.4 — coordinate range validation
1. Find (or publish) a layer stored in Web Mercator whose service ignores `outSR`.
2. Download it.
3. **Expect:** out-of-range features are skipped, the completion toast reports
   `…, N skipped`, and logcat shows `Skipping feature with out-of-range WGS84 coordinate`.
4. **Before the fix:** markers were placed at nonsense coordinates (longitude ≈ -13,000,000).

### Test 8.5 — `lastSync` only on success
1. Download a layer successfully, note the synced indicator.
2. Break it (turn off the network) and hit ↻.
3. **Expect:** the failure dialog, and the layer is **not** marked as freshly synced.
4. **Before the fix:** `lastSync` was stamped on the executor thread right after the network call,
   before and regardless of whether the map apply succeeded — so a layer that never rendered still
   showed as synced and the recurrence check thought it was current.

### Test 8.6 — teardown / no leaks
1. Open the plugin, sign in, start a download, then **disable the plugin** from ATAK's plugin
   manager mid-download.
2. **Expect:** no crash, no toast firing after teardown, and no FeatureLink markers left on the
   map.
3. **Before the fix:** posted UI lambdas ran against a disposed drop-down, and an
   `IllegalArgumentException` from unregistering a never-registered receiver aborted the rest of
   teardown — **stranding every marker the plugin had added on the map permanently**.
4. Then re-enable the plugin in the same ATAK session and confirm it loads cleanly.

### Test 8.7 — PLI scheduler is not reset by tab switching
1. PLI tab → enable **Auto-send PLI every 30 seconds**.
2. Now switch tabs repeatedly (Home → Layers → PLI → Home …) for two minutes.
3. **Expect:** PLI updates continue to land in the feature service roughly every 30 s.
4. **Before the fix:** `syncPliPageAuthState()` called `setChecked()` on every PLI-tab navigation,
   which fired the listener, which tore down and rebuilt the scheduler and reset its 30 s phase —
   so a tab-switching operator could go indefinitely without ever sending a position report.

---

## 9. High risk — test these hardest

**Nothing here was compiled or run.** These are the changes where reading alone was least
sufficient, ranked by how bad it is if I got them wrong.

| # | Change | Why it is risky | What failure looks like |
|---|---|---|---|
| 1 | **`Authorization: Bearer` instead of `?token=`** (C-21) | Every single authenticated ArcGIS call changed shape at once. I could not confirm against a live portal that Esri accepts the header on *all* of these endpoints — in particular `applyEdits`, `addItem` (multipart), `publish`, and `updateDefinition`. ArcGIS Enterprise in particular may behave differently from ArcGIS Online. | 401/403, or an ArcGIS `error` 498/499 on a call that used to work. **Run all of Test 5.2.** If only *some* calls fail, tell me which — the fix is per-endpoint. |
| 2 | **Pagination loop termination** (C-06) | The loop trusts `exceededTransferLimit` (top level *or* under `properties`) and falls back to "a full page means keep going". A service that ignores `resultOffset` would re-serve page 1 forever; I added a 200-page / 50,000-feature cap as the backstop, but that path is unexercised. | A download that never finishes, or a feature count that is a suspiciously round multiple of the page size, or duplicate markers. |
| 3 | **`SecureTokenStore` / Android Keystore** (C-20) | AES-GCM against the AndroidKeyStore, written blind. Key generation, the IV-prefix framing, and the "key invalidated" recovery path have never executed. I deliberately used the Keystore directly rather than `EncryptedSharedPreferences` because `androidx.security-crypto` pulls `androidx.core`, which this build explicitly excludes. | Sign-in appears to work but does not survive an app restart; or a crash on first token write; or a silent inability to persist the token (logcat: `encryption unavailable — refusing to persist`). |
| 4 | **Signature-permission broadcast registration** (C-02) | `registerReceiver(receiver, filter, permission, handler, flags)` is called from code running inside **ATAK's** process while the permission is declared by the **plugin's** APK. I reasoned this works because the check is on the *sender*, and the sender (the plugin's own activities) is signed with the key that defines the permission — but this is exactly the kind of cross-app permission subtlety that reads correctly and behaves otherwise. | The legitimate deep link stops working entirely (Test 7.2 shows no dialog and nothing happens). If so, logcat will show a `SecurityException` naming the permission. |
| 5 | **Sublayer picker** (C-08) | New UI, new dialog, and it changes what a "layer URL" means for rows created through it. Interaction with existing persisted layers is untested. | Picker does not appear when it should, appears when it should not, or added sublayers collide with each other in `layerItems` / `layerDisplayConfigs`. |
| 6 | **Thread-pool split + `disposed` flag** (C-28) | I found and fixed a self-deadlock in my *own* first attempt at this (a task blocking on `invokeAll` against its own pool). That is evidence this area is easy to get wrong. The `statsExecutor` → `bgExecutor` fan-out is the current shape. | The Home tab hangs on "Total Features" forever, or the whole plugin's networking wedges after a few actions. **Tap Home repeatedly with many layers.** |
| 7 | **`ImportResolver` on 5.7** | Pre-existing divergence, not mine, and still unverified: `ImportInPlaceResolver` explicitly does not copy the file; plain `ImportResolver` may. | On ATAK 5.7 only, accepting a `.featurelinkshare` leaves an orphan file in `atak/tools/datapackage/`. Check that directory after an accepted share. |
| 8 | **`fetchJson` now throws instead of returning an error object** | `AutoIconset.generate` and the geometry-type backfill both called it and checked `meta.has("error")`. I changed the contract; I believe I updated every caller, but a missed one would now propagate an exception where it previously degraded quietly. | An unexpected "Download failed" dialog on a layer that used to work, with an ArcGIS message in it. |

---

## 10. Explicitly NOT fixed — do not test for these

These were descoped and remain exactly as the audit found them. If you hit them, they are known.

| C-ID | Status |
|---|---|
| **C-15** | `versionCode` is still `5`, and both trees still ship identical `applicationId` + `versionCode` + `versionName 2.7.24`. **You will need `adb uninstall` before installing.** The 5.6 and 5.7 APKs still mutually overwrite. |
| **C-25** | Auto-refresh scheduling: a `ScheduledExecutorService` was added in the receiver, but this item was descoped before end-to-end verification. Treat the auto-refresh interval as unproven. |
| **C-37** | `minSdkVersion` is still `21`. The API-24/26 calls (`Map.getOrDefault`, `String.join`) were **replaced with plain-Java equivalents in the code I touched**, so the specific crash paths the audit named are gone — but the build still declares `minSdk 21` with no desugaring, so other API-24+ calls could exist elsewhere. |
| **C-40** | The keystore and its plaintext passwords remain in `build.gradle` and in git. Unchanged. |
| **C-23** | The `shp` block *was* added to `toCompactJson`/`fromJson`, but no round-trip test exists. Shape styling persistence across a plugin reload is worth spot-checking during Test 1.7. |
| **C-14** | No test source set. There are still zero automated tests. |

---

## 11. Reporting back

For each failure please capture:
1. Which numbered test.
2. The exact toast/dialog text you saw.
3. `adb logcat -d > fail.log` filtered on `FeatureLink|ArcGIS|AutoIconset|AutoSymbology|DisplayConfig`.
4. The layer URL (redact the token if one somehow appears — and if one does, that is itself a
   C-21 finding worth reporting loudly).
