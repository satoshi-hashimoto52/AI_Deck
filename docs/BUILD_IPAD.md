# Building AI Deck for iPad mini

> **Steps 1 and the compile are verified** (2026-09-18, Xcode 26.6, iOS 26.5 SDK): Unity
> generates the Xcode project and it builds to an arm64 `AIDeck.app` with zero errors.
> **Signing and installation are not** — they need an Apple ID, a Team selection and a
> physical iPad, so they are yours to do.

## Prerequisites

* Unity **6000.3.23f1** with **iOS Build Support**
* Xcode **26.6** or later
* An Apple ID added to Xcode (a free account is enough for development installs)
* iPad mini running iPadOS 15.0 or later
* A USB cable, or the iPad paired for wireless development

## Before anything: regenerate after every code change

The Xcode project is **generated**, and a generated project is not rebuilt because the source
changed. Both of these are easy to do and neither reports anything wrong:

* rebuilding only the Mac app after a fix and installing yesterday's iPad build;
* pressing ▶ in Xcode, which rebuilds the *Xcode* project from the C++ Unity exported last
  time — not from the current C# .

That happened. An export made before four fixes stayed on the iPad, and the missing jog
behaviour read as a touch-input bug on iOS. **Run Step 1 again after every code change**, then
build in Xcode.

To check what is actually on the device, look at the bottom of the iPad's connect screen, or
the first lines of the log:

```
[AI Deck] Build: v1.0 · commit dd55743 · built 2026-09-18 11:40 UTC · protocol v1
```

`unknown` means the player was run from the editor. A commit ending in `-dirty` means it was
built from a tree with uncommitted changes, so it is not exactly the commit it names.

## Step 1 — generate the Xcode project (automated)

```bash
UNITY=/Applications/Unity/Hub/Editor/6000.3.23f1/Unity.app/Contents/MacOS/Unity

"$UNITY" -batchmode -nographics -quit \
         -projectPath AIDeckUnity \
         -executeMethod AIDeck.Editor.AIDeckBuildPipeline.BuildIos \
         -logFile /tmp/aideck-build-ios.log
```

Output: `AIDeckUnity/build/ios/Unity-iPhone.xcodeproj`.

Every command on this page is meant to be run from the **repository root**. The build script
asks Unity for `build/ios`, and Unity resolves a relative build path against the *Unity project*
folder rather than the working directory — so the project lands inside `AIDeckUnity/`, one level
below where the command was run.

Or from the editor: **AI Deck → Build → iOS (Xcode project)**.

### Checking it compiles before you touch signing

Worth doing once, because it separates "the project is wrong" from "my Team is wrong":

```bash
cd AIDeckUnity/build/ios
xcodebuild -project Unity-iPhone.xcodeproj -target Unity-iPhone \
           -configuration Release -sdk iphoneos \
           CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO build
```

This takes several minutes — it compiles the IL2CPP C++ output — and ends in
`** BUILD SUCCEEDED **` with `build/Release-iphoneos/AIDeck.app` inside. That app cannot be
installed, because it is unsigned; it only proves the generated project is sound.

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

Verified in the generated project and the compiled app:

| | |
| --- | --- |
| `IPHONEOS_DEPLOYMENT_TARGET` | `15.0` |
| `PRODUCT_BUNDLE_IDENTIFIER` | `com.aideck.controller` |
| `TARGETED_DEVICE_FAMILY` / `UIDeviceFamily` | `2` (iPad only) |
| `ARCHS` | `arm64` |
| `UISupportedInterfaceOrientations~ipad` | landscape left and right only |
| `NSLocalNetworkUsageDescription` | present |
| `UIRequiresPersistentWiFi` | `true` |
| Executable | `Mach-O 64-bit executable arm64` |

## Step 2 — signing (requires you)

These steps cannot be automated because they involve your Apple ID.

1. Open `AIDeckUnity/build/ios/Unity-iPhone.xcodeproj` in Xcode.
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

Regenerating the Xcode project overwrites `AIDeckUnity/build/ios/`, including the Team selection. Unity
can append to an existing project instead, but the reliable path for a clean result is to
regenerate and re-select the Team, which takes a few seconds.
