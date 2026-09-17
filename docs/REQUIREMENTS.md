# AI Deck — Requirements Traceability

The authoritative requirement list is
[Issue #1](https://github.com/satoshi-hashimoto52/AI_Deck/issues/1). This file maps each
requirement to the code that implements it and the test that holds it in place, so a claim
of "done" can be checked rather than taken on trust.

**Status values**

| Value | Meaning |
| --- | --- |
| `done` | Implemented, and covered by a test that has been run and passed. |
| `partial` | Implemented, with a limitation recorded in [`KNOWN_LIMITATIONS.md`](KNOWN_LIMITATIONS.md). |
| `todo` | Not implemented yet. |
| `manual` | Correct behaviour can only be confirmed on hardware or by ear. |

Status as of **Phase 2 complete** (2026-09-18). Paths are relative to
`AIDeckUnity/Assets/`.

## 6.1 Track library

| ID | Requirement | Implementation | Test | Status |
| --- | --- | --- | --- | --- |
| FR-001 | Load MP3 | `TrackLoader`, `TrackImporter` | `AudioEnginePlayModeTests`; verified on a real 192 kbps MP3 | done |
| FR-002 | Load WAV | `TrackLoader`, `TrackImporter` | `AudioEnginePlayModeTests` loads a generated WAV | done |
| FR-003 | Load AIFF | `TrackLoader`, `TrackImporter` | verified on a real AIFF in the built Mac app | done |
| FR-004 | Bulk add | `TrackLibrary.AddRange` | `TrackLibraryTests.BulkAdd…` | done |
| FR-005 | Skip broken files with a reason | `TrackLibrary.AddReport` | `TrackLibraryTests.UnsupportedFiles…` | done |
| FR-006 | Duplicate detection | `TrackLibrary.FindContentDuplicate` | `TrackLibraryTests.TheSameFile…` | done |
| FR-007 | Keep title, file name, length, format, added time | `TrackInfo` | `TrackInfoTests.JsonRoundTrip…` | done |
| FR-008 | Keep BPM when analysable | `TrackInfo.Bpm`, `BpmAnalyzer` | `AnalysisTests`, `TrackInfoTests.NormalizeBpm…` | done |
| FR-009 | Persist and restore the library | `TrackLibrary.ToJson` / `LoadJson` | `TrackLibraryTests.JsonRoundTrip…` | done |
| FR-010 | Never modify or delete the source file | no write path exists | `TrackLibraryTests.TheLibraryNeverExposes…` | done |
| FR-011 | Search | `TrackLibrary.Search`, `TrackInfo.Matches` | `TrackLibraryTests.SearchFilters…` | done |

## 6.2 Decks

| ID | Requirement | Implementation | Test | Status |
| --- | --- | --- | --- | --- |
| FR-020 | Load into deck A | `AudioEngine.LoadTrack`, `DeckModel.CompleteLoad` | `DeckModelTests`, `AudioEnginePlayModeTests` | done |
| FR-021 | Load into deck B | same | same | done |
| FR-022 | Play both decks at once | `DeckChannel`, `MasterBus` | `AudioChainTests`, `AudioEnginePlayModeTests.TwoDecksPlayTogether…` | done |
| FR-023 | Play / pause / return to start | `DeckStateMachine`, `DeckModel` | `DeckStateMachineTests`, `DeckModelTests` | done |
| FR-024 | Set and return to CUE | `CuePoint`, `DeckModel.CueReturn` | `CuePointTests`, `DeckModelTests` | done |
| FR-025 | Seek | `DeckModel.Seek` | `DeckModelTests.Seek…` | done |
| FR-026 | Elapsed and remaining time | `DeckSnapshot.RemainingSeconds`, `TrackInfo.FormatDuration` | `DeckModelTests`, `TrackInfoTests` | done |
| FR-027 | Build, cache and show the waveform | `WaveformBuilder`, `WaveformCache`, `WaveformView` | `AnalysisTests`; rendered in the Mac app | done |
| FR-028 | Change tempo | `TempoControl` | `TempoControlTests` | done |
| FR-029 | Simple BPM analysis | `BpmAnalyzer` | `AnalysisTests` | done |
| FR-030 | SYNC | `TempoControl.EnableSync` | `TempoControlTests.EnableSync…` | done |
| FR-031 | Loop a region | `LoopRegion`, `DeckModel` | `LoopRegionTests`, `DeckModelTests` | done |
| FR-032 | Simple scratch | `PlatterMotion`, `DeckVoice` signed rate | `PlatterMotionTests`, `DeckVoiceTests` | done (host); controller gesture in Phase 2 |
| FR-033 | BRAKE | `PlatterMotion.StartBrake` | `PlatterMotionTests`, `DeckModelTests` | done (host) |
| FR-034 | BACKSPIN | `PlatterMotion.StartBackspin`, `DeckVoice` reverse | `PlatterMotionTests`, `DeckVoiceTests` | done (host) |

## 6.3 Mixer and effects

| ID | Requirement | Implementation | Test | Status |
| --- | --- | --- | --- | --- |
| FR-040 | Per-deck volume | `MixerState.ChannelGain` | `MixerTests` | done |
| FR-041 | Crossfader | `CrossfaderCurve` | `MixerTests` | done |
| FR-042 | Per-deck MUTE | `MixerState.SetMute` | `MixerTests.Mute…` | done |
| FR-043 | Per-deck FILTER | `FilterParams`, `StateVariableFilter` | `EffectTests` | done |
| FR-044 | Per-deck ECHO | `EchoProcessor` | `EffectTests` | done |
| FR-045 | Clip detection | `LevelMeter` | `EffectTests.LevelMeter…` | done |
| FR-046 | Master volume | `MixerState.MasterGain` | `MixerTests` | done |
| FR-047 | No sudden loud noise under load | `AudioSafety.SoftLimit`, echo cap, gain ramps | `AudioSafetyTests`, `EffectTests` | done |

## 6.4 Recording

| ID | Requirement | Implementation | Test | Status |
| --- | --- | --- | --- | --- |
| FR-050 | Record the master output | `MasterBus` tap, `WavRecorder`, `RecorderPump` | `RecordingTests`, `AudioEnginePlayModeTests` | done |
| FR-051 | Show recording state on both ends | `MixerPanelView`, `ControllerScreen` status strip | `StateSnapshotTests`, `ControllerPlayModeTests` | done |
| FR-052 | Show elapsed recording time | `WavRecorder.ElapsedSeconds` | `RecordingTests.ElapsedTime…` | done |
| FR-053 | Save as WAV | `WavHeader`, `WavRecorder` | `RecordingTests` | done |
| FR-054 | Show destination and file name | `WavRecorder.OutputPath`, Mac status line | `RecordingTests` | done |
| FR-055 | A recording failure must not disturb playback | `WavRecorder` never throws | `RecordingTests.AnUnwritableTarget…` | done |

## 6.5 Networking

| ID | Requirement | Implementation | Test | Status |
| --- | --- | --- | --- | --- |
| FR-060 | Discover the host on the LAN | `DiscoveryPayload`, `ConnectPanel` host list | `StateSnapshotTests` | partial — sockets in Phase 3 |
| FR-061 | Connect by IP address | `ConnectPanel`, `AppSettings.LastHostAddress` | `ControllerPlayModeTests.ConnectingFromTheSheet…` | done (UI); sockets in Phase 3 |
| FR-062 | Show connection state | `ConnectionState`, `ControllerScreen.SetConnectionState` | `ControllerPlayModeTests.TheConnectSheetIsHidden…` | done |
| FR-063 | Controller actions reach the host | command router | PlayMode | todo |
| FR-064 | Host state reaches the controller | `StateSnapshot` | `StateSnapshotTests` | done (payload) |
| FR-065 | Reconnect after a brief outage | heartbeat | PlayMode | todo |
| FR-066 | Release continuous controls on disconnect | `DeckModel.ReleaseContinuousControls`, `AudioEngine.AllStop` | `DeckModelTests`, `AudioEnginePlayModeTests.AllStop…` | done (host); wiring in Phase 3 |
| FR-067 | Ignore stale sequence numbers | `SequenceGate` | `SequenceGateTests` | done |
| FR-068 | Reject an incompatible protocol version | `MessageCodec`, `ProtocolInfo` | `MessageCodecTests.IncompatibleVersion…` | done |

## 6.6 Controller interaction

| ID | Requirement | Implementation | Test | Status |
| --- | --- | --- | --- | --- |
| FR-070 | Landscape layout holds | `ControllerScreen`, landscape-only player settings | verified at 1133×744; device check in Phase 5 | done (simulated) |
| FR-071 | Multi-touch | `TouchRouter`, `TouchWidget` capture rule | `TouchRouterTests.TwoWidgetsAreOperatedSimultaneously` | done |
| FR-072 | Jog wheel | `PlatterMotion.Nudge` | `PlatterMotionTests.Nudge…` | done (logic) |
| FR-073 | Touch cancel releases the control | `TouchRouter`, `TouchWidget.OnCancelled` | `TouchRouterTests.ACancelledPointerReleases…` | done |
| FR-074 | Backgrounding releases held controls | `ControllerApp.OnApplicationPause`, `TouchRouter.CancelAll` | `ControllerPlayModeTests.BackgroundingReleases…` | done |
| FR-075 | Important controls inside the safe area | `UiFactory.ApplySafeArea` on the content root, re-applied on rotation | manual on device, Phase 5 | done (code); device check pending |
| FR-076 | Suppress auto-sleep while playing | `ControllerApp.ApplySleepPolicy`, tied to `PreventSleepWhilePlaying` | `AppSettingsTests` | done |

## 6.7 Settings

| ID | Requirement | Implementation | Test | Status |
| --- | --- | --- | --- | --- |
| FR-080 | Remember the last host | `AppSettings.LastHostAddress` | `AppSettingsTests` | done |
| FR-081 | Remember the recording folder | `AppSettings.RecordingFolder` | `AppSettingsTests` | done |
| FR-082 | Remember the master volume | `AppSettings.MasterVolume` | `AppSettingsTests` | done |
| FR-083 | Remember UI settings | `AppSettings` | `AppSettingsTests` | done |
| FR-084 | Repair a corrupt settings file | `AppSettings.Deserialize` | `AppSettingsTests.AnUnreadableFile…` | done |

## 7 Non-functional

| ID | Requirement | How it is met | Status |
| --- | --- | --- | --- |
| NFR-001 | Audio independent of UI load | one `OnAudioFilterRead`, no allocation or locks after `Prepare`; `AudioRingBuffer` is lock-free | partial — measured in Phase 4 |
| NFR-002 | Never block the main thread | decode on coroutines, analysis on `ThreadPool`, disk on the recorder pump | done for Phase 1 paths |
| NFR-003 | No perceptible control lag on a normal LAN | UDP fast channel, coalescing queue | manual |
| NFR-004 | Bounded send queue | `OutboundQueue`, cap 512, fast messages coalesce | done |
| NFR-005 | 30 minutes without a crash, leak or dropout | no per-frame allocation on the audio path | manual — Phase 4 |
| NFR-006 | No personal data in logs | `DiagnosticLog` truncation; file names only on the wire | done |
| NFR-007 | Exceptions surfaced, not swallowed | `DiagnosticLog.Exception` separates user notice from diagnostics | done |
| NFR-008 | Non-destructive handling of media | no write path to the source file | done |
| NFR-009 | Gain limiting before output | `AudioSafety.SoftLimit` on the master bus | done |
| NFR-010 | No audio after a disconnect or quit | `AudioEngine.Shutdown` clears both channels and detaches the output | `AudioEnginePlayModeTests.ShutdownLeavesNothingPlaying`; disconnect path in Phase 3 |

## 9 Safety

| Rule | How it is met | Status |
| --- | --- | --- |
| Short fade on play, stop and load | 12 ms ramp in `DeckChannel`, 3 ms after a loop wrap; the transport waits for it (covered by `AudioChainTests` and `AudioEnginePlayModeTests`) | done |
| No NaN / Infinity / out-of-range to audio | `AudioSafety`, sanitising setters everywhere | done |
| Release continuous state on disconnect | `DeckModel.ReleaseContinuousControls`, `AudioEngine.AllStop` | done (host); wiring in Phase 3 |
| Configurable disconnect policy, safe default | `AppSettings.OnDisconnect` = `StopPlayback` | done |
| Never modify the source file on error | no write path | done |
| Recoverable partial recording | header sizes refreshed every 5 s | done |
