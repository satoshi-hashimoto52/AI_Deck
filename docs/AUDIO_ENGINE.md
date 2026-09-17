# AI Deck — Audio Engine

Covers FR-020 to FR-055, NFR-001, NFR-009, NFR-010 and §9 of
[Issue #1](https://github.com/satoshi-hashimoto52/AI_Deck/issues/1).

All audio runs on the Mac host. The iPad never decodes or plays track audio.

## 1. Signal path

```
  AudioClip A ─► AudioSource A ─► DeckDsp A ──┐
                 (rate, seek)     filter      │
                                  echo        │
                                  gain ramp   │
                                              ▼
                                        MasterDsp ─► AudioListener ─► output
                                        master gain
                                        soft limiter          │
                                        peak / RMS / clip     └─► WavRecorder
  AudioClip B ─► AudioSource B ─► DeckDsp B ──┘
                                              (ring buffer → writer thread → .wav)
```

`DeckDsp` and `MasterDsp` are `MonoBehaviour`s implementing `OnAudioFilterRead`. Unity calls
that on the audio thread with the interleaved buffer of the object it sits on: on an
`AudioSource`'s object it is that source's output, on the `AudioListener`'s object it is the
final mix. That gives a per-deck insert point and a master insert point without an
`AudioMixer` asset, which keeps the whole graph in reviewable source (see
[`ARCHITECTURE.md`](ARCHITECTURE.md) §3).

## 2. Audio thread discipline (NFR-001)

Inside `OnAudioFilterRead`:

* **no allocation** — every buffer is allocated once at construction and reused;
* **no locks** — parameters cross from the main thread through plain fields that are
  written once per frame and read once per buffer; the worst case is a parameter landing one
  buffer late, which is inaudible;
* **no file I/O** — the recorder's `Submit` only writes to a lock-free ring buffer;
* **no Unity API calls** — `Time`, `Debug` and the object model are all off limits there.

Everything the audio thread calls into lives in `AIDeck.Core`, which cannot reference
UnityEngine at all. The assembly definition enforces the rule rather than trusting it.

## 3. Loading (FR-001…FR-003, FR-020, FR-021)

MP3, WAV and AIFF are decoded through `UnityWebRequestMultimedia.GetAudioClip` against a
`file://` URL, with `AudioType` chosen from the extension. macOS decodes all three natively,
so no third-party decoder is needed — which also keeps the project clear of any dependency
with an unclear licence.

Clips load with `DecompressOnLoad`. That costs memory — roughly 10 MB per stereo minute at
48 kHz — and buys two things V1 needs: `GetData` for waveform and BPM analysis, and reliable
behaviour under a negative `pitch` for reverse playback. A five-minute track is about
50 MB, which is unremarkable on the Apple Silicon Macs this targets.

Load sequence:

1. `DeckModel.BeginLoad()` — the deck refuses transport commands from here on.
2. Decode on a coroutine; the main thread keeps running (NFR-002).
3. On success: `CompleteLoad(id, length, bpm)`. Cue, loop, sync and platter state all reset,
   because carrying a loop over from the previous track would be silently wrong.
4. On failure: `FailLoad(reason)`. The deck shows the reason, the **other deck keeps
   playing**, and the source file is untouched (FR-005, NFR-008).
5. Waveform and BPM analysis run on a worker; results are applied on the main thread and
   pushed to the controller when ready.

The source file is opened read-only and never written, moved or deleted. There is no code
path in `TrackLibrary` or the loader that can modify it (FR-010, NFR-008), and a test asserts
that the library exposes no such method.

## 4. Transport and rate

### Position authority

`AudioSource.timeSamples` is the physical truth; `DeckModel.PositionSeconds` is the logical
truth. Once per frame the host reads the device position and calls
`DeckModel.ReportPosition`, which returns either `null` ("carry on") or a corrected position
when a loop wrapped or the track ended. Only then does the host seek the source.

Asking the model rather than the device means loop wrapping, the end-of-track stop and the
reverse-past-zero rule are all pure logic with EditMode tests, and the audio device is told
what to do rather than interrogated about what it did.

### Rate (FR-028)

`DeckModel.EffectiveRate = TempoControl.EffectiveRate × PlatterMotion.Rate`, applied to
`AudioSource.pitch`.

Unity's `pitch` resamples, so **pitch moves with tempo**. §8 of the master issue explicitly
allows this for V1. `TempoControl.EffectiveRate` is the seam a key-lock implementation would
plug into; nothing above it assumes the rate and the musical pitch are linked. See
[`KNOWN_LIMITATIONS.md`](KNOWN_LIMITATIONS.md).

The final rate is clamped to ±6× by `AudioSafety.Sanitize` before it reaches the source, and
NaN maps to 1.0. A corrupt rate is the one value that can make the audio device misbehave
rather than merely sound wrong, so it is defended twice: once in `TempoControl`, once at the
point of application.

### SYNC (FR-030)

`TempoControl.EnableSync(ownBpm, targetBpm)` returns `false` and leaves SYNC off when either
tempo is unknown or the match would need a rate outside the safe window. Refusing is the
right answer: a guessed ratio produces an audible, unrecoverable tempo jump mid-mix, and the
usual cause is a half- or double-time BPM analysis error rather than a genuine 4× tempo
difference.

The tempo fader keeps its position; SYNC contributes only the remainder. Releasing SYNC
therefore restores exactly the fader's own rate with no drift.

## 5. Platter effects

`PlatterMotion` is pure and frame-rate independent — see `Core/Deck/PlatterMotion.cs`.

| Gesture | Behaviour |
| --- | --- |
| **Jog nudge** (FR-072) | Adds a rate offset that decays exponentially (τ = 0.25 s) back to 1.0. A flick pushes the track without changing tempo. Capped at ±0.6. |
| **Scratch** (FR-032) | Finger down pins the rate to 0; motion drives it directly, including negative. Release glides back to 1.0 over 120 ms. Capped at ±6×. |
| **Brake** (FR-033) | Ease-out ramp to zero over a configurable time (default 1.2 s), then the transport pauses. Most of the slow-down happens early, like real platter friction. |
| **Backspin** (FR-034) | Jumps to −4× and decays exponentially over ~0.9 s, then hands control back. |

`Tick` clamps its delta to 250 ms, so a stalled frame cannot teleport a brake to completion.
`Cancel()` collapses any gesture to free running and is called on touch cancel (FR-073), app
backgrounding (FR-074) and disconnect (FR-066).

Reverse playback relies on a negative `AudioSource.pitch`, which Unity supports for
`DecompressOnLoad` clips. Fidelity is limited — see `KNOWN_LIMITATIONS.md`.

## 6. Mixer and effects

Per deck, in `DeckDsp.OnAudioFilterRead`:

1. **Filter** — `MultiChannelFilter`, one zero-delay-feedback state-variable filter per
   channel. Chosen over a biquad because it stays stable while the cutoff is swept fast,
   which is what the FILTER knob does, and because its two integrator states are trivial to
   clear on a discontinuity. A non-finite sample clears the state and returns silence rather
   than lodging in the integrators forever.
2. **Echo** — `EchoProcessor`, a feedback delay whose wet level ramps over 50 ms so toggling
   ECHO does not click. Feedback is hard-capped at 0.85, which is what guarantees the tail
   decays instead of building (FR-047).
3. **Gain** — channel fader × crossfader curve × mute, smoothed per sample toward its
   target. No gain change is ever applied as a step.

The bipolar FILTER knob maps to a low-pass sweeping down from 20 kHz on the left and a
high-pass sweeping up from 20 Hz on the right, geometrically so the knob feels even across
the band. A ±0.02 dead zone at the centre guarantees a genuine bypass.

In `MasterDsp`:

4. **Master gain**, smoothed the same way.
5. **Soft limiter** — `AudioSafety.SoftLimit`. Below 0.98 the signal passes untouched; above
   it the excess is compressed with `tanh`, so an overloaded mix saturates instead of
   producing the hard-clip crackle FR-047 forbids. Output magnitude never exceeds 1.0.
6. **Metering** — `LevelMeter` reports peak and RMS and latches every clipped sample, so a
   transient between UI frames is still counted (FR-045).
7. **Recorder tap** — the post-limiter buffer is copied into the recorder's ring buffer.

The recording is taken **after** the limiter, so the file matches what was heard.

## 7. Fades (§9)

Every discontinuity gets a ~12 ms ramp: play, pause, cue jump, load, eject, disconnect stop,
application quit. Implemented as a per-sample gain ramp in `DeckDsp` toward a target the main
thread sets.

12 ms is below the threshold at which a DJ perceives the start as soft, and far above the
~0 ms that produces a click. Cutting a playing buffer to zero in one sample is a step edge —
broadband, at full output level, through whatever the Mac is plugged into.

On quit, `OnApplicationQuit` ramps both decks out and waits for the ramp before releasing the
device (NFR-010).

## 8. Recording (FR-050…FR-055)

`WavRecorder` writes 16-bit PCM WAV at the device sample rate and channel count.

* The audio thread calls `Submit`, which only writes to `AudioRingBuffer` (4 s deep).
* A writer thread calls `Drain`, which converts to PCM16 and writes to the file.
* Both size fields in the header are rewritten every 5 seconds. **A recording interrupted by
  a crash or a power loss is therefore still a valid, playable WAV** up to the last refresh
  — which is what §9 asks for, without a separate recovery format.
* Any I/O failure moves the recorder to `Failed` with a short reason and stops it consuming
  audio. It never throws into the audio path and never touches playback (FR-055).
* Samples dropped because the disk could not keep up are counted in `DroppedSamples` and
  surfaced, not hidden.

File name: `AIDeck_YYYYMMDD_HHMMSS.wav` — sortable, and derived from nothing but the clock
(NFR-006).

## 9. Analysis

**Waveform** (FR-027): `WaveformBuilder` reduces PCM to a peak/RMS envelope at 86 buckets
per second, stored as two byte arrays. A three-minute track is about 31 kB — small enough to
cache and to stream to the iPad in a handful of frames.

**BPM** (FR-029): `BpmAnalyzer` computes a short-time energy envelope, half-wave rectifies
its first difference into an onset function, removes the running mean, and autocorrelates
over lags corresponding to 70–190 BPM. No FFT and no dependency.

It reports a confidence and returns *no tempo* below 0.15 rather than guessing. A wrong BPM
is worse than an absent one, because SYNC would act on it. Accuracy on strongly percussive
material is good; on ambient or rubato material it correctly declines to answer.

## 10. Device and format

The host adopts Unity's output sample rate and channel count as reported by
`AudioSettings.outputSampleRate` and the current `AudioConfiguration`, and configures the
DSP buffer for low latency. All Core DSP objects take the sample rate at construction and are
rebuilt if the device configuration changes under them (`AudioSettings.OnAudioConfigurationChanged`).

Mixing a clip whose sample rate differs from the device is handled by Unity's own
resampling in `AudioSource`.
