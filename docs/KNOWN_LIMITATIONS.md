# AI Deck — Known Limitations

Recorded deliberately, per §14 of
[Issue #1](https://github.com/satoshi-hashimoto52/AI_Deck/issues/1): a feature that was
simplified or blocked must be written down, not quietly marked complete.

Last updated: **Phase 5 Mac defect fix, 2026-09-18.**

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

### 2.9 Cue monitoring is split cue, not a separate output

AI Deck plays through one output device, so there is nowhere separate to send the cue. With a
cue engaged, the left channel carries the cue and the right carries the master.

This is the standard answer on hardware with the same constraint, and it is the only
arrangement that lets a DJ hear a track that is still faded out. But it does change what the
master sounds like while a cue is on, so the mixer labels the state `SPLIT CUE (L)`.

A true separate cue output would need a second audio device, which means external DJ hardware —
out of scope for V1 by §8.

The recording is taken before the split, so a recording made while cueing contains the master
only.

### 2.10 A recording interrupted by a crash loses up to 5 seconds

The header's size fields are refreshed every 5 seconds, so a file recovered after a crash is
valid up to the last refresh. Audio written after it is present in the file but not declared
in the header, and most players ignore it.

Refreshing more often would mean more seeks during recording; 5 seconds is the compromise.
A normal `Stop()` writes the exact sizes and loses nothing.

### 2.11 Recordings made before 2026-09-18 stay in the library until removed

Recordings now go to `~/Music/AI Deck/Recordings` and a folder scan skips that subfolder and
any file named like a recording, so they no longer appear as tracks. Naming a recording file
or the `Recordings` folder explicitly in the import field still imports it, because then it is
what was asked for.

What the fix cannot do is un-know the ones already imported. A library saved before the change
keeps its `AIDeck_20260918_085224`-style rows, and they stay until they are removed with
**REMOVE**. They are not dropped automatically: the catalogue is the user's, and silently
deleting rows from it to tidy up a past defect is worse than leaving a row they can remove in
one click. **No recording file is moved or deleted** — old recordings stay exactly where they
were written.

### 2.12 An unsigned build's data folder can move between rebuilds

`Application.persistentDataPath` has been observed at both
`~/Library/Application Support/AI Deck/AI Deck/` and
`~/Library/Application Support/com.aideck.host/` on the same Mac, changing from one build to
the next with no project setting altered — the app is unsigned, and its identity to macOS
changes each time the binary does. The symptom is a rebuilt app starting with an empty
library while the old one is still on disk, untouched.

There is no fix inside the project for which folder Unity picks. What the app does instead is
say which one it is using, on its first line of log:

```
[AI Deck] Storage: Data folder: ~/Library/Application Support/AI Deck/AI Deck
```

Signing the app would give it a stable identity. That belongs with the Phase 5 signing work.

### 2.13 The iPad build is only as current as the last export

The Xcode project is generated from the C# by Unity, and Xcode rebuilds *that* project — not
the C#. Rebuilding the Mac app after a fix and pressing ▶ in Xcode therefore installs the
previous export, with no warning anywhere. It has already happened once: an export from before
four fixes sat on the iPad, and the missing jog behaviour read as an iOS touch bug.

Every player now says which commit it was built from — the controller shows it on the connect
screen and both apps log it at startup — so the question is answerable from the device. The
export itself still has to be regenerated by hand after every change; see the top of
[`BUILD_IPAD.md`](BUILD_IPAD.md).

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
| Q1 | Minimum macOS and iPadOS versions to state in the README | **macOS 12.0 settled** — built and run on 26.3.1, `LSMinimumSystemVersion 12.0`. **iPadOS 15.0 not yet confirmed**: the app compiles against the iOS 26.5 SDK with `IPHONEOS_DEPLOYMENT_TARGET 15.0`, but has not run on a device. Confirm in Phase 5 |
| Q2 | Does the iPad need `NSLocalNetworkUsageDescription` accepted before discovery works? | **Key confirmed present** in the built `AIDeck.app`. Whether the prompt appears and what happens if it is declined is a device behaviour — Phase 5 |
| Q3 | Does macOS prompt for local network access for the host? | **Not observed** on macOS 26.3.1 when the host was launched from a terminal; discovery and connection worked immediately between two processes. A first launch from the Finder may still prompt. The key is present either way |
| Q4 | Actual end-to-end control latency on the target LAN | **Not measured on a real LAN.** Both processes ran on one Mac, so the figure would be a floor rather than an answer. Measure with the iPad in Phase 5 |
| Q5 | Whether the default 1.2 s BRAKE and 0.9 s BACKSPIN feel right | Subjective; confirm in Phase 5 |
| Q6 | Whether the linear-interpolation scratch sounds acceptable at speed | Subjective; confirm in Phase 5 |
| Q7 | Behaviour when Wi-Fi drops and returns on a real device | The reconnect path is covered by an automated test over sockets, but a radio going down is not the same as a socket closing. Confirm in Phase 5 |

## 5. Verified in Phase 4

For completeness, the things that were open and are now closed:

* The macOS build produces a native arm64 binary and runs. 
* The iOS Xcode project generates **and compiles** to an arm64 `AIDeck.app` with zero errors,
  against the iOS 26.5 SDK. Only signing remains.
* Both `Info.plist` files carry `NSLocalNetworkUsageDescription`; the iPad one is iPad-only and
  landscape-only.
* 30 minutes of continuous two-deck playback — see `docs/TEST_PLAN.md` for the result.

## 6. Local music generation (Phase 1)

These belong to the optional generator in `generator/`, not to the DJ application. The deck
runs and ships without any of it.

### 6.1 A 3.5 GB language model is downloaded and never used

`acestep-5Hz-lm-1.7B` arrives as part of the unified `ACE-Step/Ace-Step1.5` repository, which
is the repository the *generation* model also lives in, and ACE-Step snapshots it whole. It
cannot be excluded without patching the pinned upstream. On this hardware it is never loaded:
the GPU tier reports `available_lm_models == ['acestep-5Hz-lm-0.6B']`, so the 1.7B is disk
cost only. It is not deleted automatically; `GENERATOR_PHASE1.md` says how to remove it.

### 6.2 About 9.5 GB is orphaned in `.aideck-generator/models`

Phase 0 set `ACESTEP_CHECKPOINTS_DIR`, which the API server ignores, so models were fetched
into two trees. Phase 1 uses the one the server actually reads and leaves the other in place
— deleting several gigabytes of someone's download is their decision, not the installer's.

### 6.3 Generation makes a 16 GB machine swap heavily

Measured: the swap file grew from 0 to 19 GB across one 30-second generation, and had not
returned to zero after the server stopped. The generator's own resident set stayed near
1.4 GB, so this is the unified-memory working set of the models rather than a leak. Nothing
in this repository reduces it. Do not generate during a live set until the soak test in
GEN-010 says it is safe.

### 6.4 The first start is slow and looks like a hang

Loading roughly 10 GB takes minutes on an M1 before the API accepts connections at all. This
is why the scripts separate "starting" from "ready" and why `status_macos.sh` exists; it is
not fixed, only made visible.

### 6.5 Generation quality is not assessed here

Whether the vocal is intelligibly Japanese and whether the music is worth playing are
listening judgements. The scripts verify that a file is a WAV of the requested length with a
non-zero peak, which is a much weaker claim.

## 7. Generating from inside AI Deck (Phase 2)

### 7.1 Generation is refused while anything is playing

Deliberate, and the refusal is enforced rather than advised. Either deck playing or fading
out, cue monitoring, or recording all grey the GENERATE button with a reason. This is not a
finding that generating during playback breaks audio — it is that **nobody has measured it**,
and one 30-second track takes this machine's swap file from nothing to 19 GB. The measurement
is GEN-010b, in Phase 4. Until then generation is a preparation activity.

### 7.2 Cancelling stops the whole engine

ACE-Step v0.1.8 has no cancel endpoint, so cancelling stops the server process by validated
PID. The generation really does stop, but the models unload with it and the next one needs
**START AI SERVER** again. A track that finishes during the cancellation stays on disk and is
not added to the library.

### 7.3 A track that finishes while the sheet is closed still arrives

The library reflection follows the bridge's state, not the sheet's visibility, so closing the
sheet mid-generation does not lose the result. It also means a track can appear in the list
while the user is looking at the deck. It is never loaded onto a deck without being asked.

### 7.4 The generator is not inside the built application

`generator/` lives in the checkout, not in `AI Deck.app`. A copy of the application moved
somewhere else will report `not-installed` and the sheet will say so. The iPad build contains
no Python and no model weights at all — verified by searching the generated Xcode project —
though the C# bridge *class* is compiled into the shared assemblies. The controller never
instantiates it.

### 7.5 One generation at a time

A second request is refused with `busy` while one is running. On 16 GB, two would not finish
faster; they would swap against each other.

### 7.6 Superseded — the on-screen flow is now verified

The first attempt could not be click-verified because the machine's display was locked, which
starves the player's update loop. It was repeated with the display awake on 2026-09-20 and the
whole flow was driven on screen: GENERATE, START AI SERVER, `Stopped → Starting → Ready`,
a 30-second track in 82 s, the library adding exactly one row by itself, and LOAD TO A putting
it on the deck with the AI server already stopped. Quitting AI Deck left no process and freed
both ports.

Two defects were found by doing it, and both are fixed: the bridge could not start the engine
at all when its standard output had been orphaned, and the LOAD TO A/B buttons disappeared the
moment the automatic stop ran.

### 7.8 The old note, kept for the record

The machine's display was locked for the whole verification window. A locked display starves
the player's main loop — the audio and socket threads keep running, but `Update` effectively
stops, and with it the bridge poll (one poll in 49 seconds against a 3-second timer). Synthetic
clicks do not reach a locked screen either.

So these are **verified**: the bridge contract end to end against the real model (a 30-second
track in 76 s, validated at 48 kHz stereo), loopback-only binding, process ownership, stopping
with nothing left behind, and every state the panel can show — the last through PlayMode tests
against a fake engine.

These are **not verified on the real machine**: pressing GENERATE, typing into the sheet,
the automatic library reflection with the real engine, and LOAD TO A/B. They are covered by
PlayMode tests but have not been seen working on screen. Do that with the display awake.

### 7.7 Audio quality is still not assessed

Phase 2 verifies that a validated WAV of the requested length reaches a deck. Whether the
vocal is intelligibly Japanese and whether the track is worth playing remain listening
judgements, and are recorded as undecided.
