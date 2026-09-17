# Building AI Deck for macOS

> **Verified on 2026-09-18** on macOS 26.3.1 / Apple M1 with Unity 6000.3.23f1. The build
> succeeds, produces a native arm64 binary and runs.

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

Output: `AIDeckUnity/build/mac/AI Deck.app`.

Every command on this page is meant to be run from the **repository root**. The build script
asks Unity for `build/mac`, and Unity resolves a relative build path against the *Unity project*
folder rather than the working directory — so the app lands inside `AIDeckUnity/`, one level
below where the command was run.

## From the editor

**AI Deck → Build → macOS (Apple Silicon)**.

## What the build script sets

* Target: `StandaloneOSX`, architecture **Apple Silicon** (`ARM64`)
* Scripting backend: **IL2CPP when that module is installed, otherwise Mono**. The script
  probes the editor's playback engine folder and logs which it chose, so a build is never
  silently different from what was expected. IL2CPP is the better fit for a real-time audio
  path, but the macOS IL2CPP module is a separate Unity Hub download; Mono is fully supported
  and still produces a native arm64 binary. To switch, install **macOS Build Support (IL2CPP)**
  in Unity Hub — no code change is needed.
* Bundle identifier: `com.aideck.host`
* Minimum macOS version: 12.0
* `NSLocalNetworkUsageDescription` added to `Info.plist` — macOS prompts before an app may
  reach other devices on the LAN, and without the key the prompt never appears and discovery
  silently fails

Verified in the produced `AI Deck.app`:

| | |
| --- | --- |
| Executable | `Mach-O 64-bit executable arm64` |
| `CFBundleIdentifier` | `com.aideck.host` |
| `LSMinimumSystemVersion` | `12.0` |
| `NSLocalNetworkUsageDescription` | present |
| Build output | 65 MB, 0 errors |

## Where the app keeps its data

Unity keys `persistentDataPath` on the **bundle identifier**, so the Mac host's settings,
library and waveform cache live in:

```
~/Library/Application Support/com.aideck.host/
```

Deleting that folder resets the library and settings; it never touches your music.

## Running it without clicking

The host accepts startup options for bringing it up in a known state — useful for scripting
and for the checks in [`TEST_PLAN.md`](TEST_PLAN.md):

```bash
"AIDeckUnity/build/mac/AI Deck.app/Contents/MacOS/AI Deck" \
    -aideck-import "~/Music/AI Deck" -aideck-autoplay
```

## First launch

macOS asks for permission to find devices on the local network. AI Deck cannot talk to the
iPad without it. If it was declined, re-enable it in
**System Settings → Privacy & Security → Local Network**.

An unsigned build also triggers Gatekeeper. Either sign it with your own Developer ID, or
open it once from the Finder context menu with **Open**.

## Clean build

```bash
rm -rf AIDeckUnity/Library AIDeckUnity/Temp AIDeckUnity/build/mac
```

Deleting `Library/` forces a full reimport, which takes several minutes.
