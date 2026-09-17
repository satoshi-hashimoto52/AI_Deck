# Building AI Deck for iPad mini

> **Verification status: not yet verified.** The Unity side is executed in Phase 4. The
> signing and installation steps require an Apple ID, a Team selection and a physical device,
> so they are completed by the user in Phase 5.

## Prerequisites

* Unity **6000.3.23f1** with **iOS Build Support**
* Xcode **26.6** or later
* An Apple ID added to Xcode (a free account is enough for development installs)
* iPad mini running iPadOS 15.0 or later
* A USB cable, or the iPad paired for wireless development

## Step 1 — generate the Xcode project (automated)

```bash
UNITY=/Applications/Unity/Hub/Editor/6000.3.23f1/Unity.app/Contents/MacOS/Unity

"$UNITY" -batchmode -nographics -quit \
         -projectPath AIDeckUnity \
         -executeMethod AIDeck.Editor.BuildPipeline.BuildIos \
         -logFile /tmp/aideck-build-ios.log
```

Output: `build/ios/Unity-iPhone.xcodeproj`.

Or from the editor: **AI Deck → Build → iOS (Xcode project)**.

### What the build script sets

* Target: `iOS`, device SDK, IL2CPP, ARM64
* Bundle identifier: `com.aideck.controller`
* Minimum iOS version: 15.0
* Target device family: iPad
* Orientation: landscape left and right only, auto-rotation between them (FR-070)
* `NSLocalNetworkUsageDescription` added to `Info.plist` — iOS 14 and later require it
  before an app may reach other devices on the LAN, and without it discovery silently fails
  (FR-060)
* `UIRequiresPersistentWiFi` set, so the Wi-Fi radio is not powered down mid-set

## Step 2 — signing (requires you)

These steps cannot be automated because they involve your Apple ID.

1. Open `build/ios/Unity-iPhone.xcodeproj` in Xcode.
2. Select the **Unity-iPhone** target → **Signing & Capabilities**.
3. Tick **Automatically manage signing**.
4. Choose your **Team**. If none is listed, add your Apple ID in
   **Xcode → Settings → Accounts**.
5. If Xcode reports that the bundle identifier is unavailable, change it to something unique
   such as `com.<yourname>.aideck.controller`. This only affects your own build.

## Step 3 — prepare the iPad (requires you)

1. Connect the iPad and unlock it.
2. Tap **Trust** on the "Trust This Computer?" prompt.
3. On the iPad, enable **Settings → Privacy & Security → Developer Mode**, then restart when
   prompted.
4. After a first install with a free Apple ID, approve the certificate at
   **Settings → General → VPN & Device Management**.

## Step 4 — install and run

1. Select your iPad as the run destination in Xcode.
2. **Product → Run** (⌘R).
3. On first launch the iPad asks for permission to find devices on the local network. Allow
   it — AI Deck cannot reach the Mac otherwise.
4. Hold the iPad in landscape. The app does not rotate to portrait by design.

## Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| The iPad never finds the Mac | Local network permission declined, or the network blocks broadcast | Re-allow in **Settings → Privacy & Security → Local Network**; otherwise type the Mac's IP address, which the Mac shows in its status bar |
| "Untrusted Developer" on launch | Free-account certificate not yet approved | **Settings → General → VPN & Device Management** → trust it |
| The app quits after seven days | Free Apple accounts issue seven-day provisioning profiles | Rebuild and reinstall, or use a paid account |
| Build fails with a signing error | No Team selected | Step 2 |

## Rebuilding

Regenerating the Xcode project overwrites `build/ios/`, including the Team selection. Unity
can append to an existing project instead, but the reliable path for a clean result is to
regenerate and re-select the Team, which takes a few seconds.
