# Changelog

All notable changes to AI Deck are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

Requirement IDs refer to
[Issue #1](https://github.com/satoshi-hashimoto52/AI_Deck/issues/1).

## [Unreleased]

### Added — Phase 1: Mac-only DJ (2026-09-18)

* **Own playback engine.** `DeckVoice` resamples from decoded PCM at a signed, per-sample
  rate instead of driving `AudioSource.pitch`. Scratching and BACKSPIN need reverse and
  continuous rate changes, which Unity's pitch control does not do reliably, and owning the
  read pointer also gives exact loop wrapping with no device seek. Because it lives in
  `AIDeck.Core`, playback is now testable with no audio device.
* `DeckChannel` (filter, echo, gain ramp, metering) and `MasterBus` (master gain, soft
  limiter, metering, recorder tap), both in Core and both driven block by block in tests.
* `AudioOutput`: a single `OnAudioFilterRead` on the `AudioListener` produces the whole mix,
  with a silent child source keeping Unity's DSP graph alive so fades and echo tails do not
  freeze when nothing is playing.
* `AudioEngine`: reconciles the deck models with the audio each frame, owns loading, ejecting,
  recording and the disconnect policy, and builds the state snapshot both the Mac UI and (from
  Phase 3) the controller render.
* `TrackLoader` decodes MP3, WAV and AIFF through the system decoders; `TrackImporter` adds a
  folder, analysing waveform and tempo on a worker thread and reporting every skipped file.
* `AIDeck.Platform`: application paths, atomic write-then-rename file saving, settings and
  library stores, waveform cache, LAN address lookup, and a depth-limited music folder scanner.
* `AIDeck.UI`: theme taken from the mock-up, a multi-touch router that reads `Input.touches`
  directly so cancelled pointers release their control, procedurally generated circle and
  rounded-rectangle sprites (no binary assets), and the shared widgets — button, fader, knob,
  jog wheel, waveform, meter, track list — plus the deck, mixer and browser panels the Mac and
  iPad both use.
* `AIDeck.Host`: the Mac window of §5.6 — library import and removal, search, both decks,
  mixer, effects, recording, the Mac's own LAN address, and a one-line log summary.
* Editor build pipeline for macOS and iOS, scene generation, and an `Info.plist`
  post-processor that adds `NSLocalNetworkUsageDescription` — without it both platforms refuse
  LAN access silently and discovery would simply never find anything.
* Startup options `-aideck-import`, `-aideck-autoload`, `-aideck-autoplay` so a build can be
  brought up in a known state for inspection (see `docs/TEST_PLAN.md`).
* Tests: EditMode 326 passing (was 283), PlayMode 13 passing.

### Fixed during Phase 1

* **BPM analysis was wrong on real material.** Three separate faults, each found by testing
  against generated tracks with a known tempo:
  * the energy envelope was measured over a window as short as the hop, so it tracked the
    *waveform* of the bass rather than the loudness of the mix — a 128 BPM track read 117.6;
  * with a strong bass line present it read 146 instead of 128, because sustained low
    frequencies carry energy but no timing. The envelope is now high-passed at 200 Hz;
  * the winning lag was rounded to an integer, and 128 BPM is a lag of 93.75 envelope samples.
    The peak is now interpolated.
  The autocorrelation is also normalised by both windows' energy, and confidence is now peak
  prominence — 0.96–1.00 for a clear beat against below 0.05 for white noise, where the old
  measure gave 0.6–0.7 for both.
* **Pausing never faded out.** The "stopping" flag was being read as "still playing" when the
  channel gain target was computed, so the gain was held up and the fade never completed.
  Found by a PlayMode test asserting the voice stops only *after* the ramp.
* **The playhead could end a block outside the track.** Reverse playback past the start left a
  slightly negative position. The read index was guarded per frame but the final increment was
  not.
* **The library list rendered empty.** Rows were positioned before their anchors were set, and
  the viewport used a `Mask` driven by a near-transparent graphic. Now anchored first and
  clipped with `RectMask2D`.
* Jog wheels and knobs drew as squares: uGUI `Image` needs a sprite to be anything else. Circle
  and rounded-rectangle sprites are now generated at startup rather than committed as PNGs.

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
