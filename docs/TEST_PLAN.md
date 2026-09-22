# AI Deck — Test Plan

Covers §10 of [Issue #1](https://github.com/satoshi-hashimoto52/AI_Deck/issues/1).

A rule from §14 that this plan exists to serve: **an unrun test is never reported as a
passing test.** Everything below states how it is run and where its result comes from.

## 1. Levels

| Level | Where | Runs | Needs |
| --- | --- | --- | --- |
| EditMode | `Assets/Tests/EditMode` | Unity batch mode | Nothing but the editor |
| PlayMode | `Assets/Tests/PlayMode` | Unity batch mode | An audio device and a loopback socket |
| Integration | PlayMode, host + simulated controller in one process | Unity batch mode | As above |
| Manual | Real Mac + real iPad mini | A person | Hardware, signing, ears |

## 2. Running them

```bash
UNITY=/Applications/Unity/Hub/Editor/6000.3.23f1/Unity.app/Contents/MacOS/Unity

# EditMode
"$UNITY" -batchmode -nographics -projectPath AIDeckUnity \
         -runTests -testPlatform EditMode \
         -testResults /tmp/editmode-results.xml -logFile /tmp/editmode.log

# PlayMode
"$UNITY" -batchmode -nographics -projectPath AIDeckUnity \
         -runTests -testPlatform PlayMode \
         -testResults /tmp/playmode-results.xml -logFile /tmp/playmode.log

# The 30-minute soak (NFR-005). Opt-in: without the variable it reports ignored, not passed.
AIDECK_SOAK_MINUTES=30 "$UNITY" -batchmode -nographics -projectPath AIDeckUnity \
         -runTests -testPlatform PlayMode -testFilter AIDeck.Tests.PlayMode.SoakTests \
         -testResults /tmp/soak.xml -logFile /tmp/soak.log
```

Exit code 0 means every test passed; 2 means at least one failed. The XML holds the
per-test detail.

Unity must not be open in the editor while a batch run is in progress — it holds a lock on
the project. The three runs above cannot overlap for the same reason.

## 3. EditMode coverage (§10.1)

| §10.1 item | Fixture |
| --- | --- |
| Track metadata model | `TrackInfoTests` |
| Deck state transitions | `DeckStateMachineTests`, `DeckModelTests` |
| CUE position | `CuePointTests` |
| LOOP range | `LoopRegionTests` |
| Crossfader curve | `MixerTests` |
| Tempo boundaries | `TempoControlTests` |
| Effect boundaries | `EffectTests` |
| Message serialise / deserialise | `MessageCodecTests` |
| Stale-sequence rejection | `SequenceGateTests` |
| State snapshot | `StateSnapshotTests` |
| All-stop on disconnect | `DeckModelTests`, `PlatterMotionTests` (logic); PlayMode (wiring) |
| Settings save / restore | `AppSettingsTests` |
| WAV header and file creation | `RecordingTests` |
| Safe behaviour on invalid input | Throughout — every fixture has NaN / out-of-range / corrupt-input cases |

Supporting fixtures: `AudioSafetyTests`, `JsonTests`, `TrackLibraryTests`,
`PlatterMotionTests`, `AnalysisTests`, `OutboundQueueTests`, `DeckVoiceTests`,
`AudioChainTests`.

`DeckVoiceTests` and `AudioChainTests` are worth calling out: because the whole signal path
lives in `AIDeck.Core`, playback, resampling, reverse, loop wrapping, the gain fades and the
master limiter are all driven block by block here with **no audio device**. That is what makes
the §9 fade rules and the NFR-009 limiting checkable rather than merely asserted.

### Principles

* Every numeric input is tested with NaN, ±Infinity and out-of-range values, because §9
  requires those never to reach an audio parameter.
* Every persisted document is tested with a truncated, malformed and wrong-typed version,
  because FR-084 requires repair rather than failure.
* Every protocol frame is tested truncated, with bad magic, with a bad version and with
  reserved bits set.
* Tests assert behaviour, not implementation. Where a test encodes a judgement call — the
  cue-point fallback, the post-brake rate — the reasoning is in a comment so a future change
  is a deliberate decision rather than an accident.

## 4. PlayMode coverage (§10.2)

| §10.2 item | Fixture | Status |
| --- | --- | --- |
| A controller connects to the host in-process | `NetworkIntegrationTests.AControllerConnectsToTheHost` | **done** |
| The controller drives a deck | `NetworkIntegrationTests.TheControllerDrivesADeck` | **done** |
| Host state reaches the controller | `NetworkIntegrationTests.HostStateReachesTheController` | **done** |
| Disconnect and reconnect | `NetworkIntegrationTests.DisconnectingAndReconnectingSucceeds` | **done** |
| High-rate fader input does not grow the queue | `NetworkIntegrationTests.HighRateFaderInputDoesNotGrowTheQueue` | **done** |
| Two decks playing while recording | `AudioEnginePlayModeTests.TwoDecksPlayTogetherAndRecordToAWavFile` | **done** |

`NetworkIntegrationTests` runs both ends in one process over **real sockets on loopback**, not
over a mocked transport. The things most likely to be wrong here — framing across reads, the
handshake, the heartbeat, who is allowed to send on the fast channel — are exactly the things
a mock would define away. It also covers a command reaching the audio engine, a second
controller being refused with an explanation, connecting to nothing failing with a reason
rather than hanging, and the disconnect policy releasing the platter and stopping playback.

`AudioEnginePlayModeTests` also covers loading a real file, a failing load leaving the other
deck alone, ejecting, the pause fade, `AllStop`, the default disconnect policy, a recording
failure not disturbing playback, and shutdown leaving nothing playing.

`HostPlayModeTests` drives the **Mac window** the way a person does: it builds the real
`HostApp`, finds the real library buttons and injects real pointer events. It exists because of
a defect that every other test missed — see §4.1 below — and nothing in it calls a command
method directly.

`TouchRouterTests` and `ControllerPlayModeTests` cover the control surface. The router is fed
pointers directly rather than through `Input`, which is the only way to test simultaneous
multi-touch (FR-071) and cancellation (FR-073) without a touchscreen:

| Behaviour | Test |
| --- | --- |
| Two controls driven at once, independently | `TwoWidgetsAreOperatedSimultaneously` |
| A grabbed control keeps its finger when it leaves the rectangle | `AWidgetKeepsItsFinger…` |
| A second finger on one control is ignored | `ASecondFingerOnTheSameWidget…` |
| A cancelled pointer releases without firing the command | `ACancelledPointerReleases…` |
| Backgrounding releases every held control | `BackgroundingReleasesHeldControls` |
| Overlapping rectangles resolve by priority | `HigherPriorityWinsWhenRectanglesOverlap` |
| Every primary deck control is at least 44 pt | `EveryTouchTargetMeetsTheMinimumSize` |
| The connect sheet appears whenever there is no link | `TheConnectSheetIsHiddenOnlyWhileConnected` |
| A typed address, with or without a port, is used and remembered | `ConnectingFromTheSheet…`, `AnAddressWithAPortIsParsed` |

The fixture generates its own WAV with the shipping `WavRecorder` rather than committing a
binary test asset, so every load test is also an end-to-end check that what AI Deck writes,
AI Deck can read.

`QualityPlayModeTests` covers the non-functional requirements that can only be shown by
running the thing:

| Requirement | Test | How it is shown |
| --- | --- | --- |
| NFR-001 audio independent of UI load | `AudioKeepsRunningWhileTheMainThreadStalls` | The main thread busy-waits for 300 ms; the audio thread must keep producing blocks |
| NFR-002 main thread not blocked | `LoadingDoesNotBlockTheMainThread` | Frames keep advancing during a decode, and no frame exceeds 2 s |
| NFR-005 shape of the long run | `ShortSoakKeepsPlayingWithoutDrift` | Five seconds of looping two-deck playback with no drift out of the loop |
| NFR-006 no personal data in logs | `DiagnosticLogNeverCarriesAFullPath`, `TheSnapshotCarriesAFileNameNotAPath` | A recording failure — the most likely place for a path to leak — is checked, as is the broadcast snapshot |

`SoakTests` is the full NFR-005 run. It is **opt-in**: without `AIDECK_SOAK_MINUTES` it
reports *ignored*, never passed. §14 draws a hard line between a test that was not run and one
that succeeded, and a half-hour test silently counted as passing would be exactly that mistake.

### 4.1 Why the host fixture exists

Two defects reached a real build because every test drove a layer *below* the one that was
broken:

* **The library's A and B buttons did nothing.** `BrowserView.LoadRequested` was raised and
  subscribed by nobody on the host. The controller path *was* wired and was tested, and the
  host was only ever exercised by calling `AudioEngine.LoadTrack` directly — as did the manual
  check, which used the `-aideck-autoload` startup option and therefore also bypassed the
  buttons.
* **Every text field was inert.** There was no `EventSystem` in the app at all, and uGUI takes
  focus through it. The existing test set `InputField.text` in code, which bypasses focus.

The lesson those two share is that a test must enter through the same door as the user. The
host fixture presses buttons; the search test types into the field's value and asserts the list
actually filtered; and a focus test asserts the preconditions a click needs. Removing the fix
makes 8 of the 10 load tests fail, which is what makes them a guard rather than decoration.

### When there is no audio device

Batch mode can run without one, in which case Unity never calls `OnAudioFilterRead` and no
samples can be produced. Tests that depend on it report **inconclusive**, never passed —
§14 draws a hard line between a test that was not run and one that succeeded. On the
development Mac the audio device *is* available in batch mode, and the recording test
verifies the file actually contains audio.

## 5. Driving the built Mac app without clicking

The Mac host accepts three startup options so a build can be brought up in a known state for
inspection. They are part of the product, documented here because this is where they are used.

```bash
"AIDeckUnity/build/mac/AI Deck.app/Contents/MacOS/AI Deck" \
    -aideck-import "~/Music/AI Deck" \
    -aideck-autoplay
```

| Option | Effect |
| --- | --- |
| `-aideck-import <path>` | Import a file or folder at launch |
| `-aideck-autoload` | Load the first two library tracks onto decks A and B |
| `-aideck-autoplay` | As above, then start both decks |

`-aideck-role` overrides the platform default:

| Value | Effect |
| --- | --- |
| `host` | The Mac window |
| `controller` | The iPad control surface, which will connect over the network |
| `controller-local` | The control surface bound straight to a host in the same process |

`controller-local` is how the iPad layout is verified without an iPad — it drives the real
screen, the real command router and the real audio engine, with the network replaced by a
method call. Run it at the device's point size to see what the iPad will show:

```bash
"AIDeckUnity/build/mac/AI Deck.app/Contents/MacOS/AI Deck" \
    -aideck-role controller-local \
    -aideck-import "~/Music/AI Deck" -aideck-autoplay \
    -screen-width 1133 -screen-height 744 -screen-fullscreen 0
```

### Phase 1 verification performed this way

Run on the built app with three generated files, one per supported container:

| Check | Result |
| --- | --- |
| MP3, WAV and AIFF all import | 3 tracks, formats shown correctly |
| Tempo analysis | 123.7 / 127.7 / 139.6 BPM against true 124 / 128 / 140 |
| Waveforms render with transients | yes, both decks |
| Two decks play together | yes |
| Duplicate detection on re-import | "Added 0 tracks, skipped 3. This file is already in the library." |
| End of track stops the deck | yes, with a notice |
| No exceptions in the player log | none |

### Phase 2 verification performed this way

| Check | Result |
| --- | --- |
| Controller layout at iPad mini size (1133×744) | 40 / 60 split holds; deck A, mixer, deck B all legible |
| Deck B mirrored | yes — badge, state and tempo reading reflected |
| Library, waveforms and meters render from host state | yes |
| Connect sheet when not connected | shown, sized to its contents, manual entry always available |
| No exceptions in the player log | none |

The safe-area inset cannot be exercised on a Mac, where the safe area is the whole window.
That one is confirmed on the device in Phase 5.

### Phase 3 verification performed this way

Two copies of the built app, on one Mac, over the real network interface — not loopback:

```bash
"AIDeckUnity/build/mac/AI Deck.app/Contents/MacOS/AI Deck" -aideck-role host \
    -aideck-import "~/Music/AI Deck" -aideck-autoplay &
"AIDeckUnity/build/mac/AI Deck.app/Contents/MacOS/AI Deck" -aideck-role controller \
    -screen-width 1133 -screen-height 744 -screen-fullscreen 0 &
```

| Check | Result |
| --- | --- |
| The controller finds the Mac by broadcast (FR-060) | Host listed by name and address |
| It connects without being told an address | Auto-connected to the single discovered host |
| Both ends agree on the connection state (FR-062) | "Connected to …" on the controller, "… connected." on the Mac |
| The library streams across | 3 tracks with correct BPM, duration and format |
| The state snapshot renders (FR-064) | Deck titles, positions, transport state, tempo, mixer |
| Waveforms stream across and render | Both decks |
| No exceptions in either player log | none |

A stale stored address is worth noting: the first run tried a leftover address, reported "The
Mac did not answer", and then switched to the discovered host on its own. That path is the
reason the auto-connect rule exists.

### Phase 4 verification performed this way

Suite totals (updated after the Phase 5 Mac defect fix):

| Suite | Tests | Passed | Failed |
| --- | --- | --- | --- |
| EditMode | 386 | 386 | 0 |
| PlayMode | 134 | 134 | 0 |
| 30-minute soak (opt-in) | 1 | 1 | 0 |

Compiler warnings: 0. Not run: the on-device tests M1–M8 below, and the real-LAN latency
figure for NFR-003.

| Check | Result |
| --- | --- |
| Clean Mac build from a deleted `Library/` | Succeeded, 28 s, arm64, 0 errors |
| iOS Xcode project generated | Succeeded, 692 MB project |
| iOS project compiles (`xcodebuild`, no signing) | `** BUILD SUCCEEDED **`, arm64 `AIDeck.app`, 0 errors |
| 30-minute soak (NFR-005) | Passed: heap 19 MB → 19 MB, peak 21 MB, longest silence 0.00 s |
| Control latency, in-process floor (NFR-003) | 0.1 ms average, 0.2 ms worst over 30 samples |
| Audio through a 300 ms main-thread stall (NFR-001) | Audio thread kept producing |

## 6. Manual tests (§10.3)

Run in Phase 5 on real hardware, with the result recorded as an Issue comment. None of
these may be marked complete from a simulator or from reasoning.

| # | Test | Pass condition |
| --- | --- | --- |
| M1 | iPad mini landscape | Every label and control visible and legible; no clipping |
| M2 | Safe area | Nothing important under the corners, notch or home indicator |
| M3 | Multi-touch | Two or more controls operate simultaneously and independently |
| M4 | Jog tracking | Following a finger feels usable, not laggy |
| M5 | 10-minute session | No stuck notes, no runaway audio, no wedged control |
| M6 | Wi-Fi off then on | Controller shows reconnecting, then recovers with correct state |
| M7 | Background and return | No held control survives; state is correct on return |
| M8 | 30-minute playback | No crash, no dropout, no unbounded memory growth |

## 7. Definition of Done mapping (§13)

| DoD item | Evidence |
| --- | --- |
| No unresolved compile errors | Unity batch run exits 0 with no `error CS` in the log |
| All automated tests pass | EditMode and PlayMode result XML, quoted in the Issue comment |
| Mac clean build | `docs/BUILD_MAC.md` procedure run from a clean `Library/` |
| Mac-only 2-deck DJ operation and recording | PlayMode `AudioEnginePlayModeTests` + manual check |
| iPad Xcode project generated | `docs/BUILD_IPAD.md` procedure |
| Installs on the iPad mini | Manual, Phase 5 |
| iPad drives the Mac | Manual, Phase 5 |
| Library, waveform, decks, mixer stay in sync | PlayMode + manual |
| No audio after disconnect, backgrounding or an exception | PlayMode + M5, M6, M7 |
| 30-minute run completed | M8 |
| Zero known High-severity defects | `KNOWN_LIMITATIONS.md` plus the Issue's defect list |
| Docs match reality | Reviewed at the end of each phase |
| No unrun test reported as passing | This plan; every claim cites its run |
| Device results recorded in the Issue | Phase 5 comment |
| User confirms V1 | The user |
