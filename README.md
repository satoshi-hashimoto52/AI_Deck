# AI Deck

A two-deck DJ system for music you already have on your Mac, played from an iPad mini.

The **Mac** is the DJ deck: it holds the library, decodes and plays both tracks, mixes,
filters, applies effects and records the result. The **iPad mini**, held in landscape, is the
control surface: jog wheels, faders, CUE, SYNC, LOOP, FILTER, ECHO, BRAKE and BACKSPIN.

Both applications are built from one Unity project. The requirement specification and the
running progress log live in
[Issue #1](https://github.com/satoshi-hashimoto52/AI_Deck/issues/1).

> **Status: in development.** See [Progress](#progress) for what actually works today. The
> project does not claim a feature until a test that covers it has been run.

## Why it is split this way

The Mac owns every piece of audio state and the iPad owns none of it. The iPad sends an
intent — "play deck A", "the crossfader is at 0.31" — and then draws whatever state the Mac
reports back, twenty times a second.

That makes a dropped Wi-Fi packet a cosmetic problem instead of a divergence, and it means
"what is actually playing right now?" always has exactly one answer. The cost is that the
iPad cannot do anything while disconnected, which for a controller is the correct trade.

## Requirements

| | |
| --- | --- |
| Mac | Apple Silicon, macOS 12.0 or later |
| iPad | iPad mini, iPadOS 15.0 or later, landscape |
| Unity | 6000.3.23f1 with macOS and iOS build support |
| Xcode | 26.6 or later, for the iPad build |
| Network | Both devices on the same LAN |
| Audio | Mac built-in speakers, headphones, or any attached audio device |

The macOS and iPadOS minimums are what the project is configured for. They are confirmed
against the actual iPad mini in Phase 5 and updated here if the device requires otherwise —
see Q1 in [`docs/KNOWN_LIMITATIONS.md`](docs/KNOWN_LIMITATIONS.md).

Supported audio formats: **MP3, WAV, AIFF**. Your files are opened read-only and are never
modified, moved or deleted.

## Getting started

```bash
git clone https://github.com/satoshi-hashimoto52/AI_Deck.git
cd AI_Deck
```

Open `AIDeckUnity/` in Unity 6000.3.23f1.

* Build the Mac app: [`docs/BUILD_MAC.md`](docs/BUILD_MAC.md)
* Build the iPad app: [`docs/BUILD_IPAD.md`](docs/BUILD_IPAD.md)

### Running it

1. Start AI Deck on the Mac.
2. Add music: put files in `~/Music/AI Deck` (created on first launch) and press **ADD
   FILES**, or type any other file or folder path into the field first. Unity players have no
   native file dialog and AI Deck uses no third-party plug-ins, so a typed path is how files
   get in.
3. Start AI Deck on the iPad, in landscape.
4. The iPad finds the Mac automatically. If your network blocks broadcast, type the Mac's
   IP address — the Mac shows it in its top bar.
5. Tap **A** or **B** next to a track to load it onto that deck.
6. Play both, and move the crossfader between them.
7. **● REC** records the master output to a WAV file. The Mac shows where it was saved.

The first launch on each device asks for permission to find devices on the local network.
AI Deck cannot reach the other device without it.

## Documentation

| Document | What it covers |
| --- | --- |
| [`docs/REQUIREMENTS.md`](docs/REQUIREMENTS.md) | Every requirement, where it is implemented, which test holds it |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | How the project is put together and why |
| [`docs/NETWORK_PROTOCOL.md`](docs/NETWORK_PROTOCOL.md) | Wire format, message catalogue, session lifecycle |
| [`docs/AUDIO_ENGINE.md`](docs/AUDIO_ENGINE.md) | Signal path, threading rules, recording |
| [`docs/BUILD_MAC.md`](docs/BUILD_MAC.md) | Building the Mac application |
| [`docs/BUILD_IPAD.md`](docs/BUILD_IPAD.md) | Generating the Xcode project and installing on the iPad |
| [`docs/TEST_PLAN.md`](docs/TEST_PLAN.md) | What is tested, how to run it, what is manual |
| [`docs/KNOWN_LIMITATIONS.md`](docs/KNOWN_LIMITATIONS.md) | What V1 deliberately does not do, and why |
| [`docs/mockups/`](docs/mockups/) | The controller design reference |
| [`CLAUDE.md`](CLAUDE.md) | Conventions for anyone — human or agent — working in this repository |

## Repository layout

```
AI_Deck/
├── AIDeckUnity/                   Unity project
│   └── Assets/
│       ├── AIDeck/
│       │   ├── Core/              Pure .NET: decks, mixer, DSP, protocol, persistence
│       │   ├── Audio/             Unity audio graph (host)
│       │   ├── Net/               Sockets, discovery, sessions
│       │   ├── UI/                Shared widgets, theme, touch routing
│       │   ├── Host/              Mac application
│       │   ├── Controller/        iPad application
│       │   ├── App/               Bootstrap and role selection
│       │   └── Editor/            Build pipeline
│       ├── Scenes/
│       └── Tests/                 EditMode and PlayMode suites
├── docs/
└── README.md
```

`AIDeck.Core` declares `noEngineReferences: true` — it cannot reference UnityEngine at all.
That is what lets the deck state machine, the loop maths, the crossfader curves, the wire
codec and the WAV writer be tested in seconds with no audio device and no scene.

## Running the tests

```bash
UNITY=/Applications/Unity/Hub/Editor/6000.3.23f1/Unity.app/Contents/MacOS/Unity

"$UNITY" -batchmode -nographics -projectPath AIDeckUnity \
         -runTests -testPlatform EditMode \
         -testResults /tmp/editmode-results.xml -logFile /tmp/editmode.log
```

Exit code 0 means everything passed. See [`docs/TEST_PLAN.md`](docs/TEST_PLAN.md).

## Progress

| Phase | Scope | State |
| --- | --- | --- |
| 0 | Project, assemblies, core domain, test harness, design docs | **complete** |
| 1 | Mac-only DJ: library, two decks, waveform, mixer, effects, recording, Mac UI | **complete** — 326 EditMode + 13 PlayMode tests passing, Mac app builds and runs |
| 2 | iPad controller UI | not started |
| 3 | Networking | not started |
| 4 | Builds and quality | not started |
| 5 | Device verification | not started |

## Licence and content

The code in this repository is the project's own. No paid assets, no paid APIs and no
third-party code of unclear provenance are used — the JSON parser, wire codec, DSP, WAV
writer and BPM analyser are all written here for that reason.

AI Deck plays audio files you already have. It does not download, stream or unlock content,
and it makes no judgement about whether you have the right to use a given track.
