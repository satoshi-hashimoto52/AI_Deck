# Changelog

All notable changes to AI Deck are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

Requirement IDs refer to
[Issue #1](https://github.com/satoshi-hashimoto52/AI_Deck/issues/1).

## [Unreleased]

### Added — local music generation, Phase 2 (2026-09-20)

Generating from inside AI Deck. The DJ application behaves identically when the sheet is
closed, and the iPad gained nothing.

* **A GENERATE button and a modal sheet**: title, style, lyrics, length, BPM, key, vocal
  language and an optional seed; buttons to start the engine, generate, cancel, stop the
  engine and close; the state and elapsed seconds; and, before anything is pressed, what the
  first start costs and what it does to swap on 16 GB. "Stop the AI server after generating"
  is on by default.
* **A safety gate.** Generation is refused — with the reason on screen — while either deck is
  playing, while one is still fading out, while a deck is cued into the headphones, and while
  the master output is being recorded. A deck mid fade-out counts as playing; "not playing"
  and "silent" are twelve milliseconds apart. This is not a finding that generating during
  playback is safe or unsafe: it has not been measured, and that measurement is Phase 4.
* **A local bridge** (`generator/aideck_generator/bridge.py`) that is the only thing Unity
  talks to. Ten states, six operations, loopback-only, one generation at a time, and prompts
  and lyrics reduced to lengths before anything is logged.
* **Explicit process ownership**: AI Deck owns the bridge it started, the bridge owns the
  engine it started, and neither stops one it merely found. Stopping goes through the
  PID-validating script; there is no `pkill` anywhere in the chain.
* **Cancel that actually interrupts.** ACE-Step v0.1.8 has no cancellation endpoint — checked
  route by route — so cancelling stops the engine by validated PID. The models unload with it.
  Marking the UI cancelled while the work continued was rejected: a 16 GB machine would grind
  on for nothing and a track would appear minutes after the user thought they had stopped it.
* **One finished track reaches the library**, through the same importer ADD FILES uses, so
  duplicate detection, BPM analysis and the waveform cache are the tested ones. The `.json`
  sidecar is never offered. Failed, cancelled and invalid results add nothing.
* Tests: EditMode 386, PlayMode 113 (+1 opt-in soak), Python 66. No test loads a model.

### Fixed — local music generation, Phase 2 (2026-09-20)

* **The bridge answered HTTP/1.0 to Unity's HTTP/1.1 request.** `http.server` does that by
  default. `curl` copes by treating the connection as closing; `UnityWebRequest` sends
  keep-alive and waits for a response that never arrives, so the deck's very first poll hung
  and a finished track was never noticed. The handler now speaks HTTP/1.1, which the accurate
  `Content-Length` on every response already supported.
* **The built app could not find the repository.** The walk up from `Application.dataPath`
  stopped six directories short — one too few for `…/build/mac/AI Deck.app/Contents/Resources/Data`
  — so a built player reported the generator as not installed.
* **The poll was a long-lived coroutine and stopped after one pass**, with no exception and no
  timeout. It is now a timer in `Update` with one short-lived request per tick, and it says in
  the log when it starts watching and if it ever stops, because the failure it replaced was
  completely silent.

### Added — local music generation, Phase 1 (2026-09-20)

Operational work only: the deck is untouched, and there is still no generation UI.

* **The M1 Safe profile**, defined once in `generator/scripts/common.sh`: `acestep-v15-turbo`
  for generation, `acestep-5Hz-lm-0.6B` on the MLX backend, eager model loading, and
  deliberately *no* `ACESTEP_CHECKPOINTS_DIR`. Measured on this machine, the GPU tier offers
  only the 0.6B, so that model is what loads; `/health` and `status_macos.sh` show which one
  it actually is.
* **`stop_macos.sh` and `status_macos.sh`.** Stopping validates the PID against the process's
  own command line before signalling anything, walks the `uv run` → Python tree, and signals
  the process group. There is no `pkill python` in it: an unrelated `uvicorn` belonging to
  another project was running throughout the verification and was untouched. Status exits 0
  ready, 3 not running, 4 starting, 5 HTTP error.
* **`memory_report.sh`**, taking a labelled RSS and `vm.swapusage` snapshot into
  `.aideck-generator/logs/memory.log`, so the effect of a run is measured rather than asserted.
* **`start_macos.sh`** now refuses a second copy, names whoever already holds port 8001,
  prints the models, PID, log and checkpoint paths, distinguishes "the process is up" from
  "the API is ready", and stops the server cleanly on Ctrl+C.
* **`setup_macos.sh`** is re-runnable: it reuses the clone and the virtual environment,
  re-downloads nothing, lists what is already on disk, and reports a version mismatch instead
  of replacing anything.

### Fixed — local music generation, Phase 1 (2026-09-20)

* **Every failure was "Local generator is not reachable".** That sentence was right for one
  case out of seven and sent the user to restart a server that was up and loading. Connection
  refused, still-initialising, HTTP status, task failure, response timeout, the server process
  having exited, and audio that is not audio are now separate types with their own exit codes.
* **One 30-second timeout covered everything**, so a normal generation could be failed on the
  clock — the measured Phase 0 run was 86.7 s. Control requests keep 30 s, downloads get
  600 s, and the generation gets its own budget (default 1800 s) with the limit named in the
  message.
* **Success was declared on twelve bytes.** A file was accepted if it began `RIFF…WAVE`, which
  a header-only file also does. The WAV is now parsed for frame count, length and peak
  amplitude, and a silent or truncated result is deleted and reported as a failure.
* **The output name was not path-safe.** A title containing separators or leading dots could
  write outside the output folder; it is stripped now, and a name that reduces to nothing
  falls back rather than producing a dotfile.
* **Traced why a 3.5 GB 1.7B language model was downloaded** when the 0.6B was asked for: the
  generation model shares the unified `ACE-Step/Ace-Step1.5` repository, which ACE-Step
  snapshots whole. Phase 0 also set `ACESTEP_CHECKPOINTS_DIR`, which
  `acestep/api/startup_model_init.py` ignores, so the download happened into two trees. The
  1.7B is never loaded on this hardware. Nothing was deleted; see `KNOWN_LIMITATIONS.md` §6.
* Tests: 33 Python unit tests, all passing, none loading a model.

### Fixed — Phase 5, found on the Mac (2026-09-18)

* **The library's A and B buttons did nothing.** Clicking one showed its pressed state and
  then nothing happened: no track on the deck, no title, no BPM, no waveform, no time, still
  `EMPTY`. `BrowserView.LoadRequested` was raised correctly all the way up from the row, and on
  the host **nobody was subscribed to it** — an event with no subscriber is a silent no-op.
  `ControllerApp` had wired the same event in Phase 2; `HostApp` never did.

  It survived every check because each one entered below the break: the PlayMode tests called
  `AudioEngine.LoadTrack` directly or drove the controller, and the manual verification used
  the `-aideck-autoload` startup option, which also bypasses the buttons.

* **Every text field in the app was inert.** There was no `EventSystem` anywhere, and uGUI's
  `InputField` takes focus through it, so the library search (FR-011), the Mac's import path
  and the controller's manual address entry (FR-061) could be clicked and typed at with no
  effect. FR-061 is the documented fallback for networks where discovery fails, so this
  mattered. The existing test set `InputField.text` in code, which bypasses focus entirely.

  The canvas now creates an `EventSystem`, a `StandaloneInputModule` and a `GraphicRaycaster`.
  It does not disturb the touch router, which reads input directly and never consults the event
  system, and every graphic except the fields' own backgrounds has ray casting off.

* **The window acted on clicks aimed at other applications.** The host runs with
  `runInBackground` on, because a DJ set must not stop when the window loses focus. Unity then
  keeps calling `Update` and keeps reporting the operating system's mouse state, and the touch
  router acted on it — so a click meant for another app could move a control. Observed on the
  Mac build as controls changing on their own. The router now ignores the mouse path unless the
  application has focus; pointers injected directly (tests, and the network layer) are
  unaffected, and losing focus still cancels whatever was held.

* **The jog wheel barely worked.** Turning the platter moved the track erratically or not at
  all. Four separate causes, each enough on its own:

  * A **stopped** `DeckVoice` never advances, so `AudioEngine.ReportPosition` reconciling the
    model from it threw away every seek made between audio callbacks — which is every seek a
    paused scratch makes. The voice now leads only while it is actually rendering.
  * Only the **inner 62 %** of the disc started a scratch; the visible outer ring nudged
    instead. A hand lands on the outer ring, which is where the grip is.
  * The angle was measured from the rect's **pivot**, which the layout puts at the top-left
    corner, so a steady drag produced deltas of the wrong size and sometimes the wrong sign.
    It is now measured about the centre of the disc.
  * The gesture only reported a **rate**, which the safety clamp truncates. A drag now also
    reports the displacement it made, so what the hand did survives the clamp.

  The whole visible disc now starts a drag, clockwise goes forward and anticlockwise back
  across the 0°/359° boundary, the drag continues outside the disc — and outside the window —
  until the pointer lifts, a playing deck follows it and stays playing, a paused deck moves
  and stays paused, and the two decks are independent. `ScratchMove` carries the displacement
  to the host from the iPad as well.

* **Recordings appeared in the library as tracks.** `~/Music/AI Deck` was both the folder the
  user is told to put music in and the folder recordings were written to, so the next **ADD
  FILES** imported them as tracks called `AIDeck_20260918_085224`. Recordings now go to
  `~/Music/AI Deck/Recordings`, a folder scan skips that subfolder and any file named like a
  recording, and naming either explicitly still imports it. A file that decodes to no audio at
  all is refused with "The file contains no audio." **No existing recording is moved or
  deleted**, and rows already in a saved library stay until they are removed by hand — see
  §2.11 of `KNOWN_LIMITATIONS.md`.

* **The click that raises the window operated the control under it.** macOS brings a
  background window forward with the same click that lands on a control, and the operating
  system reports the button going down again when focus returns mid-drag. Both started
  gestures nobody asked for; the second one was caught on the Mac build loading deck B during
  a jog drag on deck A. A mouse button that was already held when focus arrived is now ignored
  until it is released.

* **The iPad's jog wheels did nothing.** Every other control worked: discovery, connection,
  the library, loading a deck, the waveform, buttons and faders. The build on the iPad was an
  Xcode export generated at 04:50, before Phase 4 and before all three Phase 5 fixes — it
  contains `JogNudge` but no `ScratchMove`, no `ScrubBy`, no `EventSystem` and no
  `PointerSource`. It was the Phase 3 jog, which only responded to the inner 62 % of the disc
  and measured its angle about the corner of the wheel: exactly the Mac symptom, fixed in
  `dd55743` and never rebuilt for iOS. **Root cause: a stale export, not iOS input.**

  So that this cannot happen silently again, every player now carries the commit it was built
  from. `AIDeckBuildPipeline` writes the short git hash — with `-dirty` when the tree had
  uncommitted changes — and the build time into a resource, and `BuildStamp` reads it back.
  The host and the controller log it as their first line, and the controller's connect screen
  shows it under the address field, because the iPad is the device you cannot check from here.

### Added — Phase 5 diagnostics

* `HostPlayModeTests`: presses the real buttons in the real `HostApp` and asserts the deck,
  the on-screen state, the title and the log. With the fix reverted, 8 of its 10 load tests
  fail.
* `HostApp` gained test-time overrides for its settings and library paths and a switch for
  networking, so the fixture can run the real application without writing into the installed
  app's files or binding its ports.
* A four-stage load trace — button press, dispatched command, decode result, resulting deck
  state — each logged once, so the absence of a line localises a break in the chain. It is
  mirrored to Unity's log via `UnityLogBridge`, because an in-memory log cannot be read off a
  shipped `.app`.
* A **pointer trail**: every pointer that starts a gesture or is cancelled is logged with
  where it came from — `Mouse`, `Touch` or `Injected` — its position and the widget it landed
  on, and focus changes are logged too. Moves are not: a single drag is thousands of them, and
  the question being answered is always "what started this?". `Injected` is the test path, and
  nothing in the shipping application calls it: there is no network, IPC or scripting surface
  that reaches `TouchRouter.PointerDown`.
* The data folder is logged at startup, folded to `~`. An unsigned build has been observed
  changing `persistentDataPath` between rebuilds with no project setting changed, which makes
  a healthy library look empty.
* `TouchRouter.ProcessMouseState` and `SetFocus` make the mouse path testable: the rules exist
  because of what the operating system reports around a focus change, and `Input` cannot be
  driven from a test. A test's frames are logged as `Injected` and can never be mistaken for a
  real mouse.
* A **gesture trail through the jog**, start and end only: the touch count when it changes,
  the pointer down and up with source, position and widget, the scratch beginning and ending
  with the number of steps and the seconds they asked for, and the same two numbers again on
  the controller's send side and the host's receive side. A jog that does nothing can be
  placed on one side of the wire or the other from the two logs alone. There is no per-frame
  line anywhere in it.
* `TouchRouter.ProcessTouchState` makes the touch path testable the way `ProcessMouseState`
  made the mouse path testable: a phase, a finger id and a position in, a captured widget out
  — the same conversion `Update` feeds from `Input.touches`. The older tests called
  `PointerDown` directly, which skips exactly the step an iPad-only failure would live in.
* `UiFactory.ApplySafeArea` takes explicit values in a second overload, so the inset can be
  checked against a real iPad mini landscape safe area without a device.
* Tests: EditMode 333, PlayMode 98 (+1 opt-in soak), 0 failures.

### Added — Phase 4: builds and quality (2026-09-18)

* **Cue monitoring now does something.** The CUE buttons previously set a flag that changed
  nothing audible. A cued deck is now summed into a cue bus taken **before** the channel fader,
  the crossfader and MUTE — pre-fade listen, which is the whole point, since a DJ cues a track
  in order to hear it while it is still faded out. With one output device the only possible
  arrangement is split cue: left carries the cue, right carries the master. The mixer labels
  that state `SPLIT CUE (L)`, and the recorder is fed the master before the split, so a
  recording made while cueing contains the master only.
* **Waveform scrubbing** (FR-025). The waveform scrolls past a fixed playhead, so dragging it
  is the same gesture as moving a record under the needle. FR-025 previously had no control
  behind it at all — only the model supported seeking.

* **The iOS Xcode project now builds.** Generated from Unity and compiled with
  `xcodebuild` against the iOS 26.5 SDK to an arm64 `AIDeck.app`, zero errors. Only signing
  remains, which needs an Apple ID and a device. `docs/BUILD_IPAD.md` documents the
  no-signing compile check, because it separates "the project is wrong" from "my Team is wrong".
* `QualityPlayModeTests` covers the non-functional requirements that can only be shown by
  running the thing: audio continuing through a 300 ms main-thread stall (NFR-001), frames
  continuing through a decode (NFR-002), and — the most likely place for a path to leak — a
  recording failure keeping the full path out of the log and out of the broadcast snapshot
  (NFR-006).
* `SoakTests`: the 30-minute continuous two-deck run of NFR-005, checking every 250 ms that the
  audio thread is still producing, that neither deck has escaped its loop, that the master bus
  has not gone silent, and that the managed heap has not grown. It is **opt-in** through
  `AIDECK_SOAK_MINUTES` and reports *ignored* without it, never passed.
* A latency measurement in the integration tests, reported as an explicit **floor** because
  both ends run on one machine; the real figure is measured with the iPad in Phase 5.
* Tests at the end of Phase 4: **EditMode 332 passing, PlayMode 56 passing, 30-minute soak
  1 passing**, 0 failures and 0 compiler warnings. The soak result against this state was
  heap 19 MB → 19 MB (peak 21 MB) with 0.00 s of silence.
* `docs/BUILD_MAC.md` and `docs/BUILD_IPAD.md` lost their "not yet verified" notices and gained
  the settings actually observed in the produced binaries.

### Changed during Phase 4

* **Heartbeats moved from the fast channel to the reliable one.** Liveness must not depend on
  the lossy transport: on a network that drops UDP between clients — or behind a firewall that
  blocks the controller's inbound datagrams — the session would have timed out every three
  seconds and reconnected forever while the TCP connection was perfectly healthy. The split
  also gives a useful diagnosis, so when heartbeats arrive but snapshots do not, the controller
  says "Connected …, but not receiving updates" instead of silently showing state that stopped
  changing.

### Fixed during Phase 4

* The soak test hit Unity Test Framework's undocumented 180-second per-test timeout and was
  reported as a failure at three minutes. It now carries an explicit `Timeout`, and refuses a
  requested duration that would not fit rather than running into the ceiling.

### Added — Phase 3: networking (2026-09-18)

* `AIDeck.Net`: the transport described in `docs/NETWORK_PROTOCOL.md`.
  * `FrameReader` turns a TCP byte stream into frames, resynchronising on the magic bytes
    after damage rather than closing the connection, and consuming a frame with a bad version
    or bad flags instead of wedging on it.
  * `TcpLink` runs receive and send on their own threads and hands decoded frames to a queue
    the main thread drains, so application state is only ever mutated on the main thread. It
    sets `NoDelay`: a DJ command must not wait for Nagle to fill a segment.
  * `UdpLink` carries the fast channel and sends inline — queueing a datagram behind a thread
    would add the latency that channel exists to avoid.
  * `DiscoveryBroadcaster` beacons to each interface's directed broadcast address, not only to
    255.255.255.255, so a Mac on both Wi-Fi and Ethernet is found on either.
  * `HostSession` and `ControllerSession`: handshake, heartbeat, snapshot broadcast, library
    and waveform streaming, reconnection.
* `HostNetworkBridge` routes every decoded command into the **same** `HostCommands` the Mac
  window uses, so a rule added there applies identically to both surfaces. On losing the
  controller it releases every continuous control and applies the disconnect policy, whose
  default is the safe stop (§9, FR-066).
* `NetworkBackend` implements the `IControllerBackend` the UI was written against in Phase 2,
  so nothing in the controller screen changed when the network arrived. It assembles the
  streamed library and waveform chunks, and asks for a waveform once rather than every frame.
* The controller connects to a discovered host on its own when exactly one is being heard and
  the user has not chosen one — step 4 of the issue's completion definition — and lists them
  and waits when there is more than one.
* Only the connected controller's address may send on the fast channel; without that check any
  device on the LAN could move a crossfader.
* A second controller is refused with an explanation before the connection closes.
* Tests: PlayMode 45 passing (was 34), including all six §10.2 integration items run over real
  sockets rather than a mocked transport.

### Fixed during Phase 3

* `ControllerPlayModeTests` wrote a host address into the real settings file, so the installed
  app would start trying to reach a machine that never existed. The settings store is now
  injectable and the tests use a temporary file.

### Added — Phase 2: iPad controller (2026-09-18)

* `AIDeck.Controller`: the landscape control surface of §5.1–§5.5, composing the same
  `BrowserView`, `DeckPanelView` and `MixerPanelView` the Mac window uses so the two surfaces
  cannot drift apart. Deck B is laid out as a mirror image, as §5.5 requires.
* `IControllerBackend`: the controller is written against an interface rather than a socket.
  The network session implements it in Phase 3; `LocalHostBackend` implements it by binding
  the controller UI straight to an in-process host, which is how the iPad layout and every
  control are verified before there is an iPad — and is the shape the §10.2 integration tests
  will take with the network put back in.
* `ConnectPanel`: the connection sheet (FR-060, FR-061, FR-062). It covers the control surface
  whenever there is no link, because operating a DJ control connected to nothing is worse than
  being told you cannot. Manual address entry is offered from the start rather than hidden,
  since discovery fails on any network with client isolation.
* Safe-area inset applied to the content root and re-applied on rotation (FR-075), so every
  control is clear of the corners and the home indicator without each widget knowing about them.
* Backgrounding and focus loss release every held control and the host's continuous state
  (FR-074), and losing the link does the same (FR-066).
* Auto-sleep is suppressed only while a deck is actually playing, subject to the user setting
  (FR-076).
* `TouchRouter` now takes pointers through a public API rather than only from `Input`, which
  makes simultaneous multi-touch and cancellation testable without a touchscreen.
* `-aideck-role controller` and `-aideck-role controller-local`.
* Tests: PlayMode 34 passing (was 13), including a check that every primary deck control meets
  the 44 pt minimum of §5.1.

### Fixed during Phase 2

* Library rows ran their metadata underneath the load buttons; the line is now built from what
  is actually known (no "Unknown" filler) and clips instead of overflowing.
* The master meter sat against the mixer panel's bottom edge and was clipped. The master block
  is now ordered meter, label, fader, so the meter is never the element pushed off the edge.
* The connect sheet had a fixed height, leaving a hole in the middle when discovery had found
  nothing. It is now sized to its contents.

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
