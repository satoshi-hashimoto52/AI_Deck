# Building AI Deck for macOS

> **Verification status: not yet verified.** This procedure is written from the project
> configuration and is executed end to end in Phase 4. Until this notice is removed, treat it
> as the intended procedure rather than a confirmed one.

## Prerequisites

* Apple Silicon Mac, macOS 12.0 or later
* Unity **6000.3.23f1** with **macOS Build Support**
* Xcode command line tools (for code signing and notarisation only; not needed to build)

## Command line

```bash
UNITY=/Applications/Unity/Hub/Editor/6000.3.23f1/Unity.app/Contents/MacOS/Unity

"$UNITY" -batchmode -nographics -quit \
         -projectPath AIDeckUnity \
         -executeMethod AIDeck.Editor.BuildPipeline.BuildMac \
         -logFile /tmp/aideck-build-mac.log
```

Output: `build/mac/AI Deck.app`.

## From the editor

**AI Deck → Build → macOS (Apple Silicon)**.

## What the build script sets

* Target: `StandaloneOSX`, architecture **Apple Silicon** (`ARM64`)
* Scripting backend: IL2CPP
* Bundle identifier: `com.aideck.host`
* Minimum macOS version: 12.0
* `NSLocalNetworkUsageDescription` added to `Info.plist` — macOS prompts before an app may
  reach other devices on the LAN, and without the key the prompt never appears and discovery
  silently fails

## First launch

macOS asks for permission to find devices on the local network. AI Deck cannot talk to the
iPad without it. If it was declined, re-enable it in
**System Settings → Privacy & Security → Local Network**.

An unsigned build also triggers Gatekeeper. Either sign it with your own Developer ID, or
open it once from the Finder context menu with **Open**.

## Clean build

```bash
rm -rf AIDeckUnity/Library AIDeckUnity/Temp build/mac
```

Deleting `Library/` forces a full reimport, which takes several minutes.
