# AI Deck — Known Limitations

Recorded deliberately, per §14 of
[Issue #1](https://github.com/satoshi-hashimoto52/AI_Deck/issues/1): a feature that was
simplified or blocked must be written down, not quietly marked complete.

Last updated: **Phase 0 complete, 2026-09-18.**

## 1. Accepted for V1 by the specification

These are limitations the master issue explicitly allows (§8). They are not defects.

### 1.1 Tempo changes pitch

Changing tempo changes the playback rate, so pitch moves with it. A track pulled to −6 %
sounds 6 % lower.

§8 permits this for V1 and defers key-lock to V2. The seam is
`TempoControl.EffectiveRate`: it produces a rate, and `DeckAudioSource` applies it to
`AudioSource.pitch`. A time-stretch implementation would replace the application point
without changing anything above it. Nothing in the deck, mixer or protocol layers assumes
rate and musical pitch are linked.

### 1.2 Scratch fidelity is not professional

Scratching drives `AudioSource.pitch`, including negative values for reverse. This gives a
recognisable, playable scratch, but it is not a sample-accurate scratch DSP:

* rate changes take effect at buffer boundaries, so very fast gestures quantise to roughly
  the DSP buffer length;
* Unity's resampler is not designed for rapid rate reversal, and hard direction changes can
  be slightly gritty;
* there is no separate needle-drop or vinyl-emulation model.

§8 places "professional-grade scratch accuracy" out of scope for V1.

### 1.3 Simple BPM analysis

`BpmAnalyzer` uses an energy-onset envelope and autocorrelation — no FFT, no beat-grid
tracking, no third-party library (which §14 forbids where the licence is unclear).

Consequences:

* strongly percussive, steady-tempo material is detected reliably;
* ambient, rubato, live or heavily swung material often produces **no** tempo, which is
  reported as "unknown" rather than guessed;
* a detected tempo is folded into 70–190 BPM, so a genuine 65 BPM track reports 130;
* SYNC refuses to engage when either tempo is unknown, so a failed analysis degrades to
  manual beat-matching rather than to a wrong tempo.

The confidence floor is 0.15 (`BpmAnalyzer.MinConfidence`). A wrong BPM is worse than an
absent one because SYNC would act on it.

### 1.4 One cue point per deck

The mock-up has a single CUE button and V1 implements exactly that. Hot cues are a V2 item.

### 1.5 One controller at a time

The host refuses a second controller while one is connected, with an explanatory error. Two
controllers driving one deck would need an arbitration rule that V1 has no reason to define.

### 1.6 LAN only, no authentication

The protocol has no authentication or encryption. Anyone who can reach the host's ports on
the LAN can drive it. This is acceptable for the stated deployment — a home or studio network
— and internet operation is out of scope (§8). **Do not expose ports 47810–47812 beyond a
trusted network.**

## 2. Consequences of implementation choices

### 2.1 Clips are fully decompressed in memory

Tracks load with `DecompressOnLoad`, costing roughly 10 MB per stereo minute at 48 kHz — so
about 50 MB for a five-minute track, 100 MB with both decks loaded.

The alternative, streaming, would rule out `GetData` for waveform and BPM analysis and makes
negative-pitch reverse playback unreliable. On the Apple Silicon Macs this targets the memory
is not a constraint. A library of any size is unaffected: only the two loaded tracks are
resident.

### 2.2 Waveform resolution is fixed

86 buckets per second, which is about one bucket per screen pixel on the iPad mini at the
mock-up's waveform width. Zooming further in would show the envelope stretched rather than
finer detail.

### 2.3 UI is built in code, not in scenes

Every screen is constructed from C# at runtime; the scene contains one bootstrap object.
This makes the UI reviewable in a diff and mergeable, at the cost of not being editable by
dragging in the Unity editor. See [`ARCHITECTURE.md`](ARCHITECTURE.md) §3.

### 2.4 Recording format is fixed

16-bit PCM WAV at the device's sample rate and channel count. No 24-bit, no float, no
compressed formats. 16-bit WAV is universally readable and keeps the writer simple enough to
be provably correct under interruption.

### 2.5 A recording interrupted by a crash loses up to 5 seconds

The header's size fields are refreshed every 5 seconds, so a file recovered after a crash is
valid up to the last refresh. Audio written after it is present in the file but not declared
in the header, and most players ignore it.

Refreshing more often would mean more seeks during recording; 5 seconds is the compromise.
A normal `Stop()` writes the exact sizes and loses nothing.

## 3. Requires hardware or a person

These cannot be closed by automated tests. They are listed in §10.3 of the issue and are
confirmed in Phase 5.

* Xcode signing, Team selection, provisioning profiles.
* Enabling developer mode on the iPad and trusting the developer certificate.
* Installing on the device.
* iPad mini landscape layout, legibility, and safe-area behaviour on real hardware.
* Two-finger and multi-finger simultaneous operation on a real touchscreen.
* Whether jog tracking *feels* responsive.
* Whether the audio sounds right — levels, filter sweeps, echo, scratch character.
* 30-minute continuous playback on the real pair of devices.
* Wi-Fi off/on recovery, and backgrounding and returning.

## 4. Open questions

Carried forward and resolved as the phases proceed.

| # | Question | Status |
| --- | --- | --- |
| Q1 | Minimum macOS and iPadOS versions to state in the README | Project is configured for macOS 12.0 and iOS 15.0; to be confirmed against the actual iPad mini in Phase 5 |
| Q2 | Does the iPad need `NSLocalNetworkUsageDescription` accepted before discovery works? | Expected yes on iOS 14+; the build post-processor adds it. Confirm on device in Phase 5 |
| Q3 | Does macOS 26 prompt for local network access for the host? | Expected yes; confirm in Phase 4/5 |
| Q4 | Actual end-to-end control latency on the target LAN | Measure in Phase 4 |
| Q5 | Whether the default 1.2 s BRAKE and 0.9 s BACKSPIN feel right | Subjective; confirm in Phase 5 |
