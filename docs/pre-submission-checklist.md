# BINA Platform Connector — Pre-Submission Checklist

Walk through this list end-to-end **before** uploading
`BinaConnector.bundle.zip` to the Autodesk App Store at
<https://aps.autodesk.com/app-store/publisher-center/revit>.

The build script (`build-bundle.ps1`) will warn about any unfilled
`[PLACEHOLDER]` strings it finds — but a clean build is not a green light to
submit. Every box below has to be ticked.

---

## A. Replace placeholders

These are intentionally left as visible strings so they cannot be missed.

- [x] `BinaApiConfig.cs:DEFAULT_BASE_URL` → `https://api.bina.cloud`.
- [x] `BinaApiConfig.cs:DEFAULT_WEB_APP_URL` → `https://app.bina.cloud`.
- [x] `bundle-templates/PackageContents.xml` — `https://bina.cloud` /
      `info@bina.cloud`.
- [x] `bundle-templates/2024.addin`, `2025.addin`, `2026.addin` —
      `https://bina.cloud` in `<VendorDescription>`.
- [x] `bundle-templates/help/index.html` — `https://bina.cloud` /
      `info@bina.cloud`.
- [x] `bundle-templates/EULA.html` — `https://bina.cloud` / `info@bina.cloud`.
- [x] `EulaService.cs:EulaText` — same values as bundled EULA. Keep in sync.

## B. Confirm pinned addin GUIDs

Three GUIDs are baked into the per-version `.addin` templates. Once the addin
ships, **never regenerate these**, or every customer's install will look like a
brand-new addin to Revit (causing duplicate ribbon entries).

- [ ] `bundle-templates/2024.addin` AddInId = `d1145ebb-15ba-4bd2-ba44-661bfe42b779`
- [ ] `bundle-templates/2025.addin` AddInId = `960afc57-a477-4c3b-86b4-2ee0f75277bf`
- [ ] `bundle-templates/2026.addin` AddInId = `971f9c67-7aba-4682-b3f4-7bc56be35bd4`

If you need to change them for any reason (e.g. you forked from an internal
build that already used these GUIDs in the wild), regenerate ALL THREE in one
go and document the rotation in the App Store submission notes.

## C. Replace placeholder artwork

- [ ] Replace `bundle-templates/icons/upload_16.png` and `upload_32.png` with
      branded versions. Same for `settings_16/32.png` and `account_16/32.png`.
- [ ] Source size should be 16×16 and 32×32, not larger downscaled.
      Transparent backgrounds. No anti-aliased text in 16px icons (it blurs).
      Follow the Autodesk Revit Icon Guidelines from the Revit SDK.
- [ ] Mirror the same files into `Resources/` (the compiled DLL embeds them as
      `BinaConnector.Resources.upload_16.png` etc. — names must match what
      `App.cs` references).

## D. Legal review of EULA

- [ ] Have legal counsel review `bundle-templates/EULA.html` (and the
      identical text in `EulaService.cs:EulaText`). The current text is a
      generic free-software EULA template under Malaysian jurisdiction —
      placeholder language, not legal advice.
- [ ] If the EULA changes, bump `EulaService.CurrentVersion` so existing users
      are re-prompted to accept.

## E. Build verification (no Revit required)

Run on a Windows dev machine:

```powershell
pwsh ./build-bundle.ps1
```

- [ ] Both build steps complete with **zero warnings**. Treat warnings as
      errors here — the App Store reviewer's build server is strict.
- [ ] `BinaConnector.bundle.zip` is created at the repo root.
- [ ] Unzipping it produces:
      ```
      PackageContents.xml
      Contents/2024/BinaConnector.{addin,dll}
      Contents/2025/BinaConnector.{addin,dll}
      Contents/2026/BinaConnector.{addin,dll}
      Contents/Resources/EULA.html
      Contents/Resources/help/index.html
      Contents/Resources/icons/{upload,settings,account}_{16,32}.png
      ```
- [ ] Each `Contents/{year}/` folder contains **only** `BinaConnector.dll` +
      `BinaConnector.addin`. No `Newtonsoft.Json.dll`, no
      `Microsoft.CodeAnalysis.*.dll`, no `System.*.dll` runtime files.
- [ ] `PackageContents.xml` is well-formed XML (the build script asserts this).

## F. Smoke tests in each Revit version

Repeat for **2024**, **2025**, and **2026**. (You can install the bundle by
unzipping it into `%PROGRAMDATA%\Autodesk\ApplicationPlugins\BinaConnector.bundle\`
or by running the App Store-built installer when you have one.)

For each Revit version:

- [ ] Launch Revit. The **BINA** tab appears with a `Cloud Sync` panel
      containing three buttons: Upload to BINA (large), Project Settings (small),
      Sign In / Account (small).
- [ ] No error dialogs appear at Revit startup.
- [ ] No entries appear under **Add-Ins → External Tools** for BinaConnector
      (the bundle uses a single `<AddIn Type="Application">` only — no
      `Type="Command"` entries).
- [ ] Hover each ribbon button — tooltip is correct and contextual help (F1)
      opens `Resources/help/index.html` in the default browser.
- [ ] Click **Upload to BINA** while signed out → friendly "Please sign in"
      dialog. No stack trace.
- [ ] Click **Sign In / Account** with no internet connection → friendly
      "Could not reach BINA Cloud" message (not "Login failed. Please check
      your email and password.").
- [ ] Sign in with a valid account → project picker appears → choose a project.
- [ ] Click **Upload to BINA** for the first time → EULA dialog appears, scroll
      to bottom enables the "I agree" checkbox, click Accept → upload proceeds.
- [ ] Subsequent uploads do **not** show the EULA again.
- [ ] Upload a small saved Revit document → results window shows BINA storage
      success, Autodesk viewer status, and registration success.
- [ ] Open **Project Settings** → switch project, change default discipline,
      toggle "confirm before uploading" → Save → settings persist after closing
      and reopening Revit.
- [ ] Click **Sign In / Account** while signed in → user info window shows
      correct username and project; "Sign out" clears the session.

## G. Security verification

- [ ] After signing in, inspect `%APPDATA%\BINA\BinaConnector\config.json` —
      it must NOT contain a `password`, `Email`, `accessToken`, or
      `RefreshToken` field. Only `userId`, `userName`, `projectId`,
      `projectName`, and `encryptedRefreshToken` (base64).
- [ ] Confirm the file cannot be read by a different Windows user account
      (DPAPI CurrentUser scope makes this automatic, but verify by switching
      users and checking that `EncryptedRefreshToken` decryption fails).
- [ ] Logs at `%APPDATA%\BINA\BinaConnector\logs\` do not contain any
      passwords or full access tokens. Tokens may appear in truncated form for
      debugging; full plaintext tokens are a leak.

## H. Network-down test

- [ ] Disable Wi-Fi / disconnect Ethernet.
- [ ] Launch Revit. BINA tab still appears (ribbon initialization makes no
      network calls).
- [ ] Click **Upload to BINA** → friendly "Could not reach BINA Cloud" error
      from the sign-in path. No stack trace.
- [ ] Re-enable network → next upload succeeds.

## I. Known limitation to document

- [ ] **Token refresh is not yet implemented end-to-end.** The connector
      stores an encrypted refresh token but does not silently re-authenticate
      at startup or on AccessToken expiry. Users will be re-prompted for
      password when a Revit session restarts. Decide whether to:
      1. Document this in the App Store description ("Sign in once per Revit
         session"), or
      2. Implement silent re-auth using the stored refresh token before v1.0
         submission. This requires the BINA backend to expose a refresh
         endpoint.

## J. Submit

- [ ] Go to <https://aps.autodesk.com/app-store/publisher-center/revit>.
- [ ] Sign in with the BINA CLOUDTECH SDN BHD publisher account.
- [ ] Create a new app submission. Title: **BINA Platform Connector**.
      Category: **Cloud / Collaboration**. Pricing: **Free**.
- [ ] Upload `BinaConnector.bundle.zip`.
- [ ] Provide marketing description, screenshots, and the publisher icon
      (separate from the in-ribbon icons).
- [ ] Submit. Autodesk's ADN team will package the bundle into the final MSI
      installer — do **not** upload an MSI yourself.

---

**Last updated:** 2026-04-27. Bump this date whenever the checklist changes
materially.
