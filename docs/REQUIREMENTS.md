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

Status as of **Phase 0 complete** (2026-09-18). Paths are relative to
`AIDeckUnity/Assets/`.

## 6.1 Track library

| ID | Requirement | Implementation | Test | Status |
| --- | --- | --- | --- | --- |
| FR-001 | Load MP3 | `AIDeck/Core/Model/TrackInfo.cs` (format detection) | `TrackInfoTests`, `TrackLibraryTests` | partial — decode path lands in Phase 1 |
| FR-002 | Load WAV | same | same | partial |
| FR-003 | Load AIFF | same | same | partial |
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
| FR-020 | Load into deck A | `DeckModel.CompleteLoad` | `DeckModelTests` | done (logic) |
| FR-021 | Load into deck B | same | same | done (logic) |
| FR-022 | Play both decks at once | `AIDeck/Audio` | PlayMode | todo |
| FR-023 | Play / pause / return to start | `DeckStateMachine`, `DeckModel` | `DeckStateMachineTests`, `DeckModelTests` | done |
| FR-024 | Set and return to CUE | `CuePoint`, `DeckModel.CueReturn` | `CuePointTests`, `DeckModelTests` | done |
| FR-025 | Seek | `DeckModel.Seek` | `DeckModelTests.Seek…` | done |
| FR-026 | Elapsed and remaining time | `DeckSnapshot.RemainingSeconds`, `TrackInfo.FormatDuration` | `DeckModelTests`, `TrackInfoTests` | done |
| FR-027 | Build, cache and show the waveform | `WaveformBuilder`, `WaveformData` | `AnalysisTests` | partial — display in Phase 1/2 |
| FR-028 | Change tempo | `TempoControl` | `TempoControlTests` | done |
| FR-029 | Simple BPM analysis | `BpmAnalyzer` | `AnalysisTests` | done |
| FR-030 | SYNC | `TempoControl.EnableSync` | `TempoControlTests.EnableSync…` | done |
| FR-031 | Loop a region | `LoopRegion`, `DeckModel` | `LoopRegionTests`, `DeckModelTests` | done |
| FR-032 | Simple scratch | `PlatterMotion` scratch | `PlatterMotionTests.Scratch…` | done (logic) |
| FR-033 | BRAKE | `PlatterMotion.StartBrake` | `PlatterMotionTests.Brake…` | done (logic) |
| FR-034 | BACKSPIN | `PlatterMotion.StartBackspin` | `PlatterMotionTests.Backspin…` | done (logic) |

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
| FR-050 | Record the master output | `WavRecorder` | `RecordingTests` | done (writer) |
| FR-051 | Show recording state on both ends | `StateSnapshot.IsRecording` | `StateSnapshotTests` | partial — UI in Phase 1/2 |
| FR-052 | Show elapsed recording time | `WavRecorder.ElapsedSeconds` | `RecordingTests.ElapsedTime…` | done |
| FR-053 | Save as WAV | `WavHeader`, `WavRecorder` | `RecordingTests` | done |
| FR-054 | Show destination and file name | `WavRecorder.OutputPath` | `RecordingTests` | partial — UI in Phase 1 |
| FR-055 | A recording failure must not disturb playback | `WavRecorder` never throws | `RecordingTests.AnUnwritableTarget…` | done |

## 6.5 Networking

| ID | Requirement | Implementation | Test | Status |
| --- | --- | --- | --- | --- |
| FR-060 | Discover the host on the LAN | `DiscoveryPayload`, `AIDeck/Net` | `StateSnapshotTests.DiscoveryPayload…` | partial — sockets in Phase 3 |
| FR-061 | Connect by IP address | `AppSettings.LastHostAddress` | `AppSettingsTests` | partial |
| FR-062 | Show connection state | `AIDeck/Net` session | PlayMode | todo |
| FR-063 | Controller actions reach the host | command router | PlayMode | todo |
| FR-064 | Host state reaches the controller | `StateSnapshot` | `StateSnapshotTests` | done (payload) |
| FR-065 | Reconnect after a brief outage | heartbeat | PlayMode | todo |
| FR-066 | Release continuous controls on disconnect | `DeckModel.ReleaseContinuousControls` | `DeckModelTests`, `PlatterMotionTests.Cancel…` | done (logic) |
| FR-067 | Ignore stale sequence numbers | `SequenceGate` | `SequenceGateTests` | done |
| FR-068 | Reject an incompatible protocol version | `MessageCodec`, `ProtocolInfo` | `MessageCodecTests.IncompatibleVersion…` | done |

## 6.6 Controller interaction

| ID | Requirement | Implementation | Test | Status |
| --- | --- | --- | --- | --- |
| FR-070 | Landscape layout holds | `AIDeck/Controller` | manual | todo |
| FR-071 | Multi-touch | `AIDeck/UI` touch router | PlayMode + manual | todo |
| FR-072 | Jog wheel | `PlatterMotion.Nudge` | `PlatterMotionTests.Nudge…` | done (logic) |
| FR-073 | Touch cancel releases the control | `PlatterMotion.Cancel` | `PlatterMotionTests.Cancel…` | done (logic) |
| FR-074 | Backgrounding releases held controls | `AIDeck/Controller` lifecycle | PlayMode | todo |
| FR-075 | Important controls inside the safe area | `docs/mockups/`, `AIDeck/Controller` | manual | todo |
| FR-076 | Suppress auto-sleep while playing | `AppSettings.PreventSleepWhilePlaying` | `AppSettingsTests` | partial |

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
| NFR-001 | Audio independent of UI load | `OnAudioFilterRead` allocates nothing and takes no lock; `AudioRingBuffer` is lock-free | partial — measured in Phase 4 |
| NFR-002 | Never block the main thread | decode on coroutines, analysis on workers | partial |
| NFR-003 | No perceptible control lag on a normal LAN | UDP fast channel, coalescing queue | manual |
| NFR-004 | Bounded send queue | `OutboundQueue`, cap 512, fast messages coalesce | done |
| NFR-005 | 30 minutes without a crash, leak or dropout | no per-frame allocation on the audio path | manual — Phase 4 |
| NFR-006 | No personal data in logs | `DiagnosticLog` truncation; file names only on the wire | done |
| NFR-007 | Exceptions surfaced, not swallowed | `DiagnosticLog.Exception` separates user notice from diagnostics | done |
| NFR-008 | Non-destructive handling of media | no write path to the source file | done |
| NFR-009 | Gain limiting before output | `AudioSafety.SoftLimit` on the master bus | done |
| NFR-010 | No audio after a disconnect or quit | fade-out then stop on both paths | partial — Phase 3 |

## 9 Safety

| Rule | How it is met | Status |
| --- | --- | --- |
| Short fade on play, stop and load | ~12 ms gain ramp in `DeckDsp` | partial — Phase 1 |
| No NaN / Infinity / out-of-range to audio | `AudioSafety`, sanitising setters everywhere | done |
| Release continuous state on disconnect | `DeckModel.ReleaseContinuousControls` | done (logic) |
| Configurable disconnect policy, safe default | `AppSettings.OnDisconnect` = `StopPlayback` | done |
| Never modify the source file on error | no write path | done |
| Recoverable partial recording | header sizes refreshed every 5 s | done |
