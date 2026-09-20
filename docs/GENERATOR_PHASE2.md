# Local music generation — Phase 2

Generating a track from inside AI Deck, and getting it onto a deck.

Phase 1 made the generator startable and measurable from a terminal. Phase 2 puts it behind a
button and feeds the result into the library the deck already knows how to play. It changes
nothing about how the DJ application behaves when the sheet is closed.

## Using it

1. Stop both decks. Stop recording. Turn cue monitoring off.
2. Press **GENERATE** in the top bar.
3. Press **START AI SERVER** and wait. The first start loads about 10 GB and takes minutes;
   the sheet says `Starting — loading models` until it is genuinely ready.
4. Fill in a title and a style description, adjust length, BPM, key, language and seed.
5. Press **GENERATE**. A 30-second track takes about 85 seconds on the M1.
6. The finished track appears in the library by itself. **LOAD TO A** / **LOAD TO B** put it
   on a deck.
7. **STOP THE AI SERVER AFTER GENERATING** is on by default, so the models unload as soon as
   the track is done. The file stays where it is.

## Why the decks must be stopped

The button is greyed, with a reason, while either deck is playing, while one is still fading
out, while a deck is being cued into the headphones, and while the master output is being
recorded.

This is not a claim that generating during playback breaks the audio. It is that **it has not
been measured**, and the measurement is Phase 4 work. What has been measured is the cost: one
30-second track takes the swap file on this 16 GB machine from nothing to 19 GB. Until a soak
test says otherwise, generation is something done *before* a set, and `GenerationSafetyGate`
is what makes that the default rather than a note in a document.

A deck mid fade-out counts as playing. "Not playing" and "silent" are twelve milliseconds
apart, and the gate uses the second one.

## How it is put together

```
AI Deck (Unity)  --owns-->  bridge (Python)  --owns-->  ACE-Step server
   IGeneratorBridge          127.0.0.1:8765         stop_macos.sh (PID-validated)
```

Unity never speaks ACE-Step's API. It speaks one small contract — `/v1/state`,
`/v1/server/start`, `/v1/server/stop`, `/v1/generate`, `/v1/cancel` — and every upstream
quirk stops at `generator/aideck_generator/bridge.py`: the unified-repository download, the
task-list shape of `query_result`, the missing cancel.

| State | Meaning |
| --- | --- |
| `not-installed` | `setup_macos.sh` has never been run |
| `stopped` | installed, no models in memory |
| `starting` | loading — minutes on a cold start |
| `ready` | a request would be accepted |
| `queued` / `generating` | one request in flight; a second is refused |
| `completed` | a validated WAV exists |
| `failed` / `cancelled` | nothing is offered to the library |
| `cancelling` | the engine is being stopped |

### Logging, and why it cannot fail an operation

The bridge is started as a child of AI Deck. If AI Deck goes away, the bridge can be left
running with a standard output whose reader has closed — and then `print` raises
`BrokenPipeError`. Because `start_server` logged before it did anything, pressing **START AI
SERVER** answered `internal: BrokenPipeError` and the engine was never started at all.

Three things changed, and the order matters:

1. `BridgeLogger` guards every write, abandons a broken stream permanently rather than
   retrying it per line, and keeps a copy in `.aideck-generator/logs/bridge.log`. It also
   points the interpreter's own `stdout` at `os.devnull` once the stream breaks, because
   CPython flushes it again on exit and a failure there ends the process with status 120.
2. `GeneratorBridge._log` swallows anything a logger does, so a logger injected from outside
   cannot fail a request either.
3. AI Deck **no longer redirects** the bridge's `stdout`/`stderr`. Redirecting them and never
   reading them gave two failures: a 16 KB pipe that would eventually fill and block the
   bridge mid-write, and the closed-reader case above. Reading them asynchronously would also
   work, but the callbacks arrive on a thread-pool thread where no Unity API may be touched,
   and the bridge already keeps its own log. No pipe is the simplest thing that cannot break.

Nothing personal reaches that log: the prompt and lyrics are recorded as lengths and the home
directory is folded to `~`. A real line looks like

```
2026-09-20 22:38:51 [bridge] queued: {"title": "Neon Highway Phase 2", "prompt_length": 198,
"lyrics_length": 0, "duration_seconds": 30, "bpm": 118, ...}
```

### Process ownership

Each arrow is an ownership **only if that side started the other**.

* AI Deck starts the bridge if nothing is listening on 8765, and stops that bridge when it
  quits. A bridge that was already running is used and left alone.
* The bridge starts the engine when **START AI SERVER** is pressed, and stops it on shutdown.
  An engine that was already running is adopted for the session and not stopped.
* Stopping always goes through `stop_macos.sh`, which validates the PID against the process's
  own command line. There is no `pkill` and no name matching anywhere in the chain — an
  unrelated `uvicorn` belonging to another project survived every test run.
* AI Deck asks the bridge to stop over `POST /v1/shutdown` rather than signalling it. .NET's
  `Process.Kill` is SIGKILL, which skips Python's cleanup entirely and would leave a
  bridge-owned ACE-Step server resident with nothing left that knows how to stop it. The
  bridge also handles `SIGTERM` by unwinding through the same path as Ctrl+C. Killing is the
  last resort, after the polite request has had eight seconds.

**AI Deck never starts the engine by itself.** Launching a DJ application must not load ten
gigabytes of weights, so the model load is always a button press.

### What cancel actually does

**ACE-Step v0.1.8 has no cancellation endpoint.** Its HTTP surface is `health`, `v1/audio`,
`v1/lora/*`, `v1/model_inventory`, `v1/models`, `v1/stats`, `create_random_sample`,
`format_input`, `query_result`, `release_task`, `v1/create_sample`, `v1/init` and
`v1/reinitialize`; its job store can mark a job running, succeeded or failed and has nothing
that stops work already handed to a worker.

So **cancel stops the engine process**, by validated PID. Consequences, stated plainly:

* the generation really does stop — this is not a UI relabelling;
* the models are unloaded, so the next generation needs **START AI SERVER** again;
* if the track happens to finish during the cancellation, the file stays on disk but is
  **not** added to the library, because the user asked for it not to happen.

Marking the UI cancelled and letting the work continue was the one option ruled out. It would
leave a 16 GB machine grinding on a job nobody is waiting for, and a track would appear
minutes later from a generation the user believed they had stopped.

## What reaches the library

Only a completed, validated WAV inside `~/Music/AI Deck/Generated/`, and only that one file.

* The path is resolved in full before it is accepted, so a reported path cannot climb out of
  the generated folder.
* The `.json` sidecar is never offered — it sits beside the track and is not audio.
* The file goes through `TrackImporter`, the same one **ADD FILES** uses, so duplicate
  detection, BPM analysis and the waveform cache are the tested ones rather than a second
  copy. Re-scanning the whole folder would also work and is what the obvious implementation
  does, but it re-reads every track the user owns to add one, and reports "skipped 12" on a
  success.
* A failed, cancelled or invalid generation adds nothing.
* No file the user already had is read for writing, moved or deleted.

## Validation

Checked in the panel *and* in the bridge. The panel's job is to stop a bad request being sent
and to say which box is wrong while everything typed is still there; the bridge's job is to
stop a bad request being run, whatever sent it.

| Field | Rule |
| --- | --- |
| Title | required |
| Style / description | required |
| Length | 10–600 seconds, finite |
| BPM | 30–300, finite |
| Time signature | 2, 3, 4 or 6 |
| Seed | empty, or a whole number |

`NaN` and `Infinity` are refused explicitly: `float.TryParse` accepts both, and a NaN duration
would otherwise reach the engine before anything noticed.

## Threads

* Every bridge call is a `UnityWebRequest` coroutine. Nothing blocks the main thread.
* Nothing in the generation path is reachable from `OnAudioFilterRead` — no HTTP, no process
  start, no file I/O, no lock, no allocation.
* Importing runs on the importer's existing coroutine, which is the one **ADD FILES** uses.

This is GEN-010a — *the code does not block those threads*. GEN-010b — *playback is stable
while a real model generates* — is *not* claimed here, has not been measured, and is Phase 4.

## Privacy

The prompt and the lyrics are the user's writing and are never logged: the bridge records
their **lengths**. Absolute paths are folded to `~` before anything is written down. The
completed track's full path is used once, to hand one file to the importer, and is not logged.

## Not in Phase 2

* No iPad generation screen. The iPad runs no model and gained no new screen.
* No Night Drive preset. The Phase 1 defaults are the starting values (GEN-014, Phase 3).
* No generation during playback (Phase 4).

## The sheet's type scale

The panel does **not** use `Theme`'s font sizes. Those are tuned for a dense deck surface that
is read at a glance and largely recognised by shape and colour; at 10–13 points a *form* is
unreadable, which is what the first build was reported as. Changing `Theme` would have moved
every label on the deck, which was not what was wrong.

| Element | Points |
| --- | --- |
| Title | 23 |
| State | 18 |
| Body and guidance | 16 |
| Warning | 15 |
| Input labels | 14 |
| Input text | 16 |
| Button text | 15 |

These are reference-resolution points at 1440×900 with the canvas matching height, so at that
size they are literal. A test asserts every label on the sheet is at least 14.

The warning is measured rather than given a fixed height — it is two lines at one card width
and three at another — and the first input is placed below whatever it needed. When the form
is taller than the card, which happens on a short window, the card scrolls.

## Known limitations

See [`KNOWN_LIMITATIONS.md`](KNOWN_LIMITATIONS.md) §7.
