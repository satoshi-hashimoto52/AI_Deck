# Changelog

All notable changes to AI Deck are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

Requirement IDs refer to
[Issue #1](https://github.com/satoshi-hashimoto52/AI_Deck/issues/1).

## [Unreleased]

### Added — Phase 0: foundation (2026-09-18)

* Unity 6000.3.23f1 project at `AIDeckUnity/`, configured for macOS and iPadOS with
  landscape-only orientation and the legacy input backend (needed for direct multi-touch
  handling, FR-071 and FR-073).
* Assembly layout with `AIDeck.Core` declared `noEngineReferences: true`, so the domain
  logic cannot reach UnityEngine and is testable without a scene or an audio device.
* `AIDeck.Core`:
  * `AudioSafety` — the single choke point for §9: NaN, Infinity and out-of-range handling,
    soft limiting, dB conversion, gain smoothing.
  * `TrackInfo`, `TrackLibrary` — library model with bulk add, per-file skip reasons,
    duplicate detection, search and JSON persistence (FR-001…FR-011).
  * `DeckModel`, `DeckStateMachine`, `CuePoint`, `LoopRegion`, `TempoControl`,
    `PlatterMotion` — deck logic including cue, loop wrapping, tempo, SYNC, jog, scratch,
    BRAKE and BACKSPIN (FR-020…FR-034).
  * `MixerState`, `CrossfaderCurve` — three crossfader curves, per-deck gain, mute, filter
    and echo routing (FR-040…FR-046).
  * `StateVariableFilter`, `EchoProcessor`, `LevelMeter`, `FilterParams` — filter and echo
    DSP with bounded feedback and clip detection (FR-043…FR-047).
  * `WaveformBuilder`, `BpmAnalyzer` — waveform envelope and confidence-reporting tempo
    analysis with no external dependency (FR-027, FR-029).
  * `WavHeader`, `WavRecorder`, `AudioRingBuffer` — lock-free capture and a WAV writer whose
    header is refreshed periodically so an interrupted recording is still playable
    (FR-050…FR-055).
  * `MessageCodec`, `SequenceGate`, `SequenceSource`, `OutboundQueue`, `StateSnapshot` —
    wire protocol v1, stale-message rejection and a bounded, coalescing send queue
    (FR-060…FR-068, NFR-004).
  * `AppSettings` — persisted settings that repair themselves rather than fail
    (FR-080…FR-084).
  * `JsonValue` / `JsonParser` / `JsonWriter` — a small dependency-free JSON layer whose
    accessors return fallbacks instead of throwing.
  * `DiagnosticLog` — bounded, truncating log that separates the user notice from the
    diagnostic detail (NFR-006, NFR-007).
* EditMode test suite — **283 tests, all passing**, covering every item in §10.1.
* Design documents: `REQUIREMENTS.md`, `ARCHITECTURE.md`, `NETWORK_PROTOCOL.md`,
  `AUDIO_ENGINE.md`, `TEST_PLAN.md`, `KNOWN_LIMITATIONS.md`.
* Controller design reference at `docs/mockups/ai-dj-controller-mockup.html`.
* `README.md`, `CLAUDE.md`, Unity `.gitignore`.

### Fixed during Phase 0

* Library and settings timestamps were written as .NET tick counts through a JSON number.
  A modern date needs more than the 53 bits a JSON number carries exactly, so every
  save/load round trip shifted the stored time. Timestamps are now ISO-8601 text.
* `AppSettings` validated its byte-backed enums with `Enum.IsDefined` against a boxed `int`,
  which throws rather than returning false. Replaced with an explicit range check, so a
  corrupt `onDisconnect` value falls back to the safe `StopPlayback` as intended.
