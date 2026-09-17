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
`TempoControl.EffectiveRate`: it produces a rate, and `DeckVoice` applies it to its read
pointer. A time-stretch implementation would replace the read pointer without changing
anything above it. Nothing in the deck, mixer or protocol layers assumes rate and musical
pitch are linked.

### 1.2 Scratch fidelity is not professional

`DeckVoice` applies the signed rate per sample and interpolates linearly between source
frames, so reverse and rapid direction changes are exact in timing. What is simplified is the
interpolation itself:

* linear interpolation adds audible high-frequency artefacts at rates well above 1x, where a
  windowed-sinc resampler would not;
* the gesture is sampled at the UI frame rate and smoothed, so a very fast flick is smoother
  than a real platter would be;
* there is no needle-drop or vinyl-emulation model.

§8 places "professional-grade scratch accuracy" out of scope for V1.

### 1.3 Simple BPM analysis

`BpmAnalyzer` uses a high-passed energy-onset envelope and normalised autocorrelation — no
FFT, no beat-grid tracking, no third-party library (which §14 forbids where the licence is
unclear).

Consequences:

* strongly percussive, steady-tempo material is detected reliably: on the project's test
  material it reports within 0.4 BPM of the truth at confidence 0.96–1.00;
* ambient, rubato, live or heavily swung material often produces **no** tempo, which is
  reported as "unknown" rather than guessed;
* a detected tempo is folded into 70–190 BPM, so a genuine 65 BPM track reports 130;
* the tempo is a single average for the track. There is no beat grid and no tracking of a
  tempo that changes part way through;
* SYNC refuses to engage when either tempo is unknown, so a failed analysis degrades to
  manual beat-matching rather than to a wrong tempo.

The confidence floor is 0.3 (`BpmAnalyzer.MinConfidence`). A wrong BPM is worse than an
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

### 2.4 The macOS build uses the Mono scripting backend

IL2CPP would be the better fit for a real-time audio path, but the macOS IL2CPP module is a
separate Unity Hub download and is not installed on this machine. The build pipeline prefers
IL2CPP when it is present and falls back to Mono otherwise, logging which it chose. Mono is
fully supported for macOS and produces a native arm64 binary; the DSP load here — two decks
of filtering, echo and a limiter at 48 kHz stereo — is well within what it handles.

To switch, install **macOS Build Support (IL2CPP)** in Unity Hub and rebuild; no code change
is needed.

### 2.5 No native file picker on the Mac

A standalone Unity player has no file dialog, and adding one would mean a plug-in of unclear
provenance (§14). The Mac window takes a typed or pasted path instead, pre-filled with
`~/Music/AI Deck`, which is created on first run. Dragging files onto the window is not
supported.

### 2.6 Recording format is fixed

16-bit PCM WAV at the device's sample rate and channel count. No 24-bit, no float, no
compressed formats. 16-bit WAV is universally readable and keeps the writer simple enough to
be provably correct under interruption.

### 2.7 Automatic connection only when the choice is unambiguous

The controller connects to a discovered host by itself only when exactly one is being heard
and the user has not chosen one this session. With two Macs on the network it lists them and
waits, because guessing which one the DJ meant would be worse than asking.

A stored address is tried first and given three seconds to answer before a discovered host is
tried instead.

### 2.8 Discovery depends on UDP broadcast

The beacon is a UDP broadcast to each interface's directed broadcast address. It needs no
service registration and no extra entitlement, and on a home or studio network it reaches
every device — but a network with client isolation (guest Wi-Fi, some mesh systems) blocks it
outright. Nothing else would work there either, which is why manual address entry sits beside
discovery rather than behind it.

Both platforms also refuse LAN access until the local network permission is granted, and they
do so *silently* — discovery simply finds nothing. The build adds
`NSLocalNetworkUsageDescription` so the prompt appears; if it was declined, re-enable it in
system settings.

### 2.9 A recording interrupted by a crash loses up to 5 seconds

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
