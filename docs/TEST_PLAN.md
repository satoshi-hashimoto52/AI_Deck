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
```

Exit code 0 means every test passed; 2 means at least one failed. The XML holds the
per-test detail.

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
`PlatterMotionTests`, `AnalysisTests`, `OutboundQueueTests`.

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

| §10.2 item | Planned fixture | Status |
| --- | --- | --- |
| Simulated controller connects to the host in-process | `HostSessionPlayModeTests` | Phase 3 |
| Simulated controller drives a deck | `HostSessionPlayModeTests` | Phase 3 |
| Host state reaches the simulated controller | `HostSessionPlayModeTests` | Phase 3 |
| Disconnect and reconnect | `HostSessionPlayModeTests` | Phase 3 |
| High-rate fader input does not grow the queue | `OutboundQueue` (EditMode) + `HostSessionPlayModeTests` | Phase 3 |
| Two decks playing while recording | `AudioEnginePlayModeTests` | Phase 1 |

## 5. Manual tests (§10.3)

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

## 6. Definition of Done mapping (§13)

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
