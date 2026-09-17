# AI Deck — Audio Engine

Covers FR-020 to FR-055, NFR-001, NFR-009, NFR-010 and §9 of
[Issue #1](https://github.com/satoshi-hashimoto52/AI_Deck/issues/1).

All audio runs on the Mac host. The iPad never decodes or plays track audio.

## 1. Signal path

```
  DeckVoice A --> DeckChannel A --+        AudioOutput (OnAudioFilterRead
  (own resampler,   filter        |         on the AudioListener object)
   signed rate)     echo          |
                    gain ramp     v
                              MasterBus --> Unity output device
  DeckVoice B --> DeckChannel B --+   master gain
  (own resampler,   filter            soft limiter          |
   signed rate)     echo              peak / RMS / clip     +--> WavRecorder
                    gain ramp                                    (ring buffer ->
                                                                  writer thread -> .wav)
```

**AI Deck renders its own audio.** It does not play tracks through `AudioSource`. One
`OnAudioFilterRead` on the `AudioListener` object produces the whole mix: each `DeckVoice`
resamples from the decoded PCM at a signed, per-sample rate, `DeckChannel` applies that
deck's filter, echo and gain, and `MasterBus` applies master gain, the limiter, metering and
the recorder tap.

The reason is scratching and BACKSPIN (FR-032, FR-034). Those need a rate that is signed,
continuously variable and applied per sample. `AudioSource.pitch` reverses unreliably and
quantises rate changes to DSP block boundaries, and the position it reports is where it has
already got to rather than where the deck logic wants it. Owning the read pointer gives exact
reverse, exact loop wrapping with no device seek, and a playhead the deck model controls.

The second benefit is structural: `DeckVoice`, `DeckChannel` and `MasterBus` all live in
`AIDeck.Core`, which cannot reference UnityEngine. The whole signal path is therefore driven
block by block in EditMode tests with no audio device - which is how the fade behaviour of
section 9 and the limiting of NFR-009 are actually verified rather than merely asserted.

### Keeping the graph alive

Unity only calls `OnAudioFilterRead` while the audio graph is running. A silent looping
`AudioSource` on a **child** object keeps it running when nothing is playing, so gain ramps
and echo tails keep advancing instead of freezing part way. It has to be a child: a source on
the listener's own object would take over that object's filter chain, and `AudioOutput` would
receive the source's output rather than the final mix.

## 2. Audio thread discipline (NFR-001)

Inside `OnAudioFilterRead`:

* **no allocation** - every buffer is allocated once in `DeckChannel.Prepare` and reused;
* **no locks** - parameters cross from the main thread through plain and `volatile` fields;
  the worst case is a parameter landing one buffer late, which is inaudible;
* **no file I/O** - the recorder's `Submit` only writes to a lock-free ring buffer;
* **no Unity API calls** - `Time`, `Debug` and the object model are all off limits.

Everything the audio thread calls into lives in `AIDeck.Core`, whose assembly definition sets
`noEngineReferences: true`. The rule is enforced by the compiler rather than by discipline.

The playhead crosses threads through an explicit atomic: `DeckVoice` publishes it as the bits
of a `double` via `Interlocked.Exchange`, and seeks arrive through a pending-seek slot the
audio thread consumes. A plain `double` field could be read in a torn state, and a torn
playhead is a jump to a random position.

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

`DeckVoice` owns the playhead and advances it per sample; `DeckModel` owns the rules. Once per
frame `AudioEngine` reads the voice's published position and calls `DeckModel.ReportPosition`,
which returns either `null` ("carry on") or a corrected position when a loop wrapped or the
track ended. Only then does the engine seek the voice.

The voice also wraps an active loop itself, sample-accurately, so a loop point never costs a
buffer of silence while the main thread catches up. The model's wrap is the same arithmetic
and therefore agrees; it exists so the rule is testable without an audio device.

### Rate (FR-028)

`DeckModel.EffectiveRate = TempoControl.EffectiveRate x PlatterMotion.Rate`, assigned to
`DeckVoice.Rate` and applied per sample with linear interpolation between source frames.

Resampling moves pitch with tempo, so **pitch moves with tempo**. Section 8 of the master
issue explicitly allows this for V1. `TempoControl.EffectiveRate` is the seam a key-lock
implementation would plug into; nothing above it assumes rate and musical pitch are linked.

The rate is clamped twice - once in `TempoControl`, once on assignment to the voice - and NaN
maps to 1.0. A corrupt rate is the one value that can make playback run away rather than
merely sound wrong.

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

Reverse playback is a negative `DeckVoice.Rate`: the read pointer steps backwards through the
decoded samples. Fidelity is limited by the linear interpolation between frames rather than by
anything in the engine — see `KNOWN_LIMITATIONS.md`.

## 6. Mixer and effects

Per deck, in `DeckChannel.RenderInto`:

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

In `MasterBus.Process`:

4. **Master gain**, smoothed the same way.
5. **Soft limiter** — `AudioSafety.SoftLimit`. Below 0.98 the signal passes untouched; above
   it the excess is compressed with `tanh`, so an overloaded mix saturates instead of
   producing the hard-clip crackle FR-047 forbids. Output magnitude never exceeds 1.0.
6. **Metering** — `LevelMeter` reports peak and RMS and latches every clipped sample, so a
   transient between UI frames is still counted (FR-045).
7. **Recorder tap** — the post-limiter buffer is copied into the recorder's ring buffer.

The recording is taken **after** the limiter, so the file matches what was heard.

## 7. Fades (§9)

Every discontinuity gets a ramp: play, pause, cue jump, load, eject, disconnect stop and
application quit. It is a per-sample gain ramp in `DeckChannel` toward a target the main
thread sets, and the transport waits for it: `AudioEngine` only stops a voice once
`DeckChannel.IsSilent` reports the ramp has run out (with a 0.5 s timeout so a stop can never
hang if the device has stopped producing callbacks).

**12 ms** for a transport change. That is below the threshold at which a DJ perceives the
start as soft, and far above the ~0 ms that produces a click. Cutting a playing buffer to zero
in one sample is a step edge: broadband, at full output level, through whatever the Mac is
plugged into.

**3 ms** after a loop wrap. A full 12 ms fade at a loop point would be an audible dip on a
tight loop, while 3 ms is long enough to remove the edge and short enough to stay inaudible.

On quit, `AudioEngine.Shutdown` clears both channels and detaches the output before the
device is released (NFR-010).

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

**BPM** (FR-029): `BpmAnalyzer` high-passes a mono mixdown at 200 Hz, measures short-time
energy over an overlapping 40 ms window, half-wave rectifies the first difference into an
onset function, and autocorrelates over lags corresponding to 70-190 BPM. No FFT and no
dependency.

Three details are load-bearing, and each was added after the naive version got a real track
wrong:

* **The high-pass.** A sustained bass note carries a great deal of energy and no timing.
  Without removing it, a click track over a strong bass line read 146 BPM instead of 128.
* **The overlapping energy window.** Measuring RMS over a window as short as the hop makes the
  envelope track the *waveform* of the bass rather than the loudness of the mix - a 55 Hz note
  has an 18 ms period, far longer than a 5 ms hop. This alone moved a 128 BPM track to 117.6.
* **Sub-sample peak interpolation.** At a 200 Hz envelope rate, 128 BPM is a lag of 93.75
  samples. Rounding to an integer lag is a 0.3 % error - several beats of drift over a
  four-minute track.

The autocorrelation is normalised by the energy of both overlapping windows, so no lag is
favoured by arithmetic alone, and confidence is the winner's *prominence* above the mean of
the search range. On the project's test material that reads 0.96-1.00 for a clear beat and
below 0.05 for white noise; the acceptance floor is 0.3. A wrong BPM is worse than an absent
one, because SYNC would act on it. On ambient or rubato material the analyser correctly
declines to answer.

## 10. Device and format

The host adopts Unity's output sample rate and channel count as reported by
`AudioSettings.outputSampleRate` and the current `AudioConfiguration`. Every Core DSP object
takes the sample rate at construction, so a device change rebuilds them
(`AudioSettings.OnAudioConfigurationChanged`); a recording in progress is stopped and the user
is told, because the file's header already declares the old format.

A file whose sample rate differs from the device is resampled by `DeckVoice` as part of the
same read-pointer arithmetic that applies the tempo rate: the step per output frame is
`rate x (fileRate / deviceRate)`.

A block whose channel count or sample rate does not match what `DeckChannel.Prepare` was given
is skipped rather than processed, because rebuilding the DSP objects there would allocate on
the audio thread. The engine rebuilds them on the next frame.

## 11. Loading files without a file picker

A standalone Unity player has no native file dialog, and adding one would mean a plug-in of
unclear provenance, which section 14 rules out. The Mac window therefore takes a typed or
pasted path - a file or a folder - and `MusicFolderScanner` walks it, depth- and count-limited
so that pointing it at a home directory returns a useful set quickly instead of stat-ing the
whole disk. `~/Music/AI Deck` is created on first run and pre-filled in the field, so there is
an obvious place to drop music.
