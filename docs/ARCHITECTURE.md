# AI Deck — Architecture

This document describes how AI Deck V1 is put together and, where a choice was not
obvious, why it went the way it did. It is the companion to
[`REQUIREMENTS.md`](REQUIREMENTS.md); the authoritative requirement list is
[GitHub Issue #1](https://github.com/satoshi-hashimoto52/AI_Deck/issues/1).

## 1. Shape of the system

AI Deck is two applications built from **one Unity project**:

| Role | Platform | Responsibility |
| --- | --- | --- |
| **Host** | macOS (Apple Silicon) | Library, audio, mixing, effects, recording. The authority for all state. |
| **Controller** | iPadOS (iPad mini, landscape) | Touch surface. Sends intents, renders the state the host reports. |

The controller never plays track audio and never computes transport state. It sends an
intent ("play deck A", "crossfader is now at 0.31") and draws whatever the host's next
state snapshot says. This is the single most load-bearing decision in the project: it
makes a dropped packet a cosmetic problem rather than a divergence, and it means the
question "what is actually playing?" always has exactly one answer.

```
          iPad mini (Controller)                        Mac (Host)
   ┌───────────────────────────────┐          ┌──────────────────────────────┐
   │ Touch input → intents         │  TCP     │ Command router               │
   │                               │ ───────► │  → DeckModel A / B           │
   │ ControllerScreen (uGUI)       │  UDP     │  → MixerState                │
   │                               │ ───────► │  → WavRecorder               │
   │                               │          │            │                 │
   │ Renders StateSnapshot         │  UDP     │            ▼                 │
   │                               │ ◄─────── │ AudioEngine (Unity audio)    │
   │                               │  TCP     │  decks → filters → master    │
   └───────────────────────────────┘ ◄─────── └──────────────────────────────┘
                                      library / waveform          │
                                                                  ▼
                                                            Mac audio out
```

## 2. Assemblies

Assembly definitions enforce the layering. A reference that does not appear here is a
reference that does not compile.

| Assembly | Path | References | Notes |
| --- | --- | --- | --- |
| `AIDeck.Core` | `Assets/AIDeck/Core` | *(none)* | `noEngineReferences: true` — pure .NET. |
| `AIDeck.Audio` | `Assets/AIDeck/Audio` | Core | Unity audio graph, decoding, recording pump. |
| `AIDeck.Net` | `Assets/AIDeck/Net` | Core | Sockets, discovery, session lifecycle. |
| `AIDeck.UI` | `Assets/AIDeck/UI` | Core | Shared widgets, theme, touch routing. |
| `AIDeck.Host` | `Assets/AIDeck/Host` | Core, Audio, Net, UI | Mac application. |
| `AIDeck.Controller` | `Assets/AIDeck/Controller` | Core, Net, UI | iPad application. |
| `AIDeck.App` | `Assets/AIDeck/App` | all of the above | Bootstrap and role selection. |
| `AIDeck.Editor` | `Assets/AIDeck/Editor` | Core, App | Build pipeline, editor tooling. |
| `AIDeck.Tests.EditMode` | `Assets/Tests/EditMode` | Core | Pure logic, no engine needed. |
| `AIDeck.Tests.PlayMode` | `Assets/Tests/PlayMode` | Core, Audio, Net, UI, Host, Controller | Runtime behaviour. |

### Why `AIDeck.Core` has no engine reference

`noEngineReferences: true` is not decoration. It means the deck state machine, the loop
maths, the crossfader curves, the wire codec, the WAV writer and the settings repair path
**cannot** accidentally reach for `UnityEngine.Time`, `Debug.Log` or a `MonoBehaviour`.
The consequences are concrete:

* every rule in §6 and §9 of the issue is testable in EditMode, with no audio device, no
  scene and no frame loop — the 283 EditMode tests run in seconds;
* the audio thread can call into Core without touching engine state, which is what makes
  `OnAudioFilterRead` safe to write;
* the same code runs identically on the host and the controller.

## 3. Runtime composition

One scene, `Assets/Scenes/AIDeck.unity`, containing a single bootstrap object. Everything
else — canvases, widgets, audio sources, sockets — is built in code at runtime.

This is deliberate. A Unity scene or prefab is a YAML document with binary-ish GUID
references; a UI authored that way cannot be reviewed in a diff, and a merge conflict in
one is effectively unrecoverable. Building the UI from C# means the entire interface is
ordinary reviewable source, and the scene file stays a handful of lines that nobody has to
merge. The cost is that layout is expressed as code rather than dragged in the editor,
which for a fixed landscape control surface is a fair trade.

`AppBootstrap` picks the role:

1. `-aideck-role host|controller` on the command line (used by the automated tests and by
   running both ends on one Mac);
2. otherwise, the platform: iOS → controller, macOS and the editor → host.

## 4. The layers

### 4.1 `AIDeck.Core`

| Area | Types | Covers |
| --- | --- | --- |
| Model | `TrackInfo`, `DeckId`, `AudioSafety` | FR-007, FR-008, §9 |
| Library | `TrackLibrary`, `AddReport` | FR-001…FR-011 |
| Deck | `DeckModel`, `DeckStateMachine`, `CuePoint`, `LoopRegion`, `TempoControl`, `PlatterMotion` | FR-020…FR-034 |
| Mixer | `MixerState`, `CrossfaderCurve` | FR-040…FR-046 |
| Fx | `StateVariableFilter`, `EchoProcessor`, `LevelMeter`, `FilterParams` | FR-043…FR-045 |
| Analysis | `WaveformBuilder`, `BpmAnalyzer` | FR-027, FR-029 |
| Audio | `WavHeader`, `WavRecorder`, `AudioRingBuffer` | FR-050…FR-055 |
| Net | `MessageCodec`, `SequenceGate`, `OutboundQueue`, `StateSnapshot` | FR-060…FR-068 |
| Settings | `AppSettings` | FR-080…FR-084 |
| Json | `JsonValue`, `JsonParser`, `JsonWriter` | persistence for the above |
| Diagnostics | `DiagnosticLog` | NFR-006, NFR-007 |

`AudioSafety` is the choke point for §9. Every value that can reach an audio parameter
passes through `Sanitize`, `Sanitize01`, `SanitizeBipolar` or `SoftLimit`, and each of
those replaces NaN and Infinity with a documented fallback before clamping. A corrupt
network packet, a hand-edited settings file and an arithmetic accident all converge on the
same defended path.

### 4.2 `AIDeck.Audio`

Wraps `AIDeck.Core` in Unity's audio graph. See [`AUDIO_ENGINE.md`](AUDIO_ENGINE.md).

### 4.3 `AIDeck.Net`

Sockets and session lifecycle over the codec in Core. See
[`NETWORK_PROTOCOL.md`](NETWORK_PROTOCOL.md).

### 4.4 `AIDeck.UI`

Code-built uGUI: a theme, a set of `MaskableGraphic` subclasses for the jog wheel, faders
and waveform, and a touch router that reads `Input.touches` directly rather than going
through `EventSystem`.

The custom router exists because the controller has to get multi-touch exactly right
(FR-071) and has to react to `TouchPhase.Canceled` (FR-073). Routing touches by hand means
a pointer that is cancelled by a system gesture, a phone call or the app being backgrounded
releases whatever it was holding, instead of leaving a fader stuck.

### 4.5 `AIDeck.Host` / `AIDeck.Controller`

The two application shells: screen construction, wiring, and the command router that turns
a decoded message into a call on `DeckModel` or `MixerState`.

## 5. Threading

| Thread | Work | Rule |
| --- | --- | --- |
| Unity main | UI, input, per-frame model ticks | never blocks on I/O |
| Unity audio (`OnAudioFilterRead`) | mixing, filter, echo, limiter, metering, recorder submit | no allocation, no locks, no file I/O |
| Recorder writer | drains the ring buffer to disk | owns the file handle |
| Network receive | one per socket; decodes and enqueues | never touches audio or UI state |
| Analysis workers | waveform and BPM for a newly added track | results marshalled to the main thread |

The audio thread and the recorder writer meet only at `AudioRingBuffer`, a single-producer
single-consumer lock-free ring. When the writer falls behind, the producer drops the excess
and counts it in `OverflowCount` rather than blocking — a disk hiccup costs a gap in the
recording, never a gap in the audio.

Network receive threads hand decoded messages to a queue that the main thread drains once
per frame. Audio parameters are therefore only ever written from the main thread and only
ever read from the audio thread, with `volatile`/interlocked access on the few fields that
cross.

## 6. State flow

1. A touch on the iPad produces an intent.
2. `ControllerSession` stamps it with a per-(type, deck) sequence number and enqueues it on
   `OutboundQueue`. Continuous controls coalesce: only the newest crossfader value survives
   (NFR-004).
3. The message goes out over TCP (reliable) or UDP (fast) according to
   `MessageType.Channel()`.
4. `HostSession` decodes, passes it through `SequenceGate` (FR-067) and routes it.
5. The host mutates `DeckModel` / `MixerState`. The audio engine picks the change up on its
   next buffer.
6. Every 50 ms the host broadcasts a full `StateSnapshot` over UDP.
7. The controller renders the snapshot. It does not render its own optimistic prediction,
   except for the in-flight position of a control the user is physically touching.

That last exception is the one place the two ends can briefly disagree, and it is bounded:
the moment the finger lifts, the next snapshot wins.

## 7. Safety and failure

| Situation | Behaviour | Requirement |
| --- | --- | --- |
| Controller disconnects | Release all continuous controls; then stop or continue per `AppSettings.OnDisconnect`, default **stop** | FR-066, §9 |
| App backgrounded | Controller releases every held control and marks itself disconnected | FR-074 |
| Touch cancelled | The affected widget releases; the platter returns to free running | FR-073 |
| Protocol version mismatch | Session refused with an explicit message, never a partial connection | FR-068 |
| Corrupt packet | Counted and dropped; the stream resynchronises on the magic bytes | NFR-007 |
| Settings file corrupt | Defaults restored, user told what happened | FR-084 |
| Library row corrupt | That row skipped, the rest loads, the count is reported | FR-005 |
| Recording fails | Recorder reports and stops; playback is untouched | FR-055 |
| Decode fails | Deck enters `Error` with a short reason; the other deck keeps playing | FR-005 |
| Host quits | Decks fade out over ~12 ms before the device is released | NFR-010 |

## 8. What is deliberately not here

Per §8 of the master issue: no DRM streaming services, no stem separation, no external
controller or MIDI support, no cloud sync or accounts, no remote operation, no App Store
submission, no purchases, no plugin hosting, no video.

V1 changes tempo by changing playback rate, so pitch moves with tempo. This is explicitly
allowed by §8. The seam for a future key-lock implementation is `TempoControl.EffectiveRate`
and the rate application in `DeckAudioSource`: a time-stretch implementation would replace
the latter without touching the former. See
[`KNOWN_LIMITATIONS.md`](KNOWN_LIMITATIONS.md).
