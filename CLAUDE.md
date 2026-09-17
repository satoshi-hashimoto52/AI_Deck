# Working in this repository

Conventions for anyone — human or agent — changing AI Deck.

## The specification

[Issue #1](https://github.com/satoshi-hashimoto52/AI_Deck/issues/1) is the single source of
requirements. If code and Issue disagree, the Issue wins, or the Issue gets a comment
explaining the deliberate change. Requirement IDs (`FR-023`, `NFR-009`) are referenced in
code comments and in [`docs/REQUIREMENTS.md`](docs/REQUIREMENTS.md); keep those references
accurate when you move code.

## Build and test

```bash
UNITY=/Applications/Unity/Hub/Editor/6000.3.23f1/Unity.app/Contents/MacOS/Unity

# EditMode — fast, no audio device needed
"$UNITY" -batchmode -nographics -projectPath AIDeckUnity \
         -runTests -testPlatform EditMode \
         -testResults /tmp/editmode-results.xml -logFile /tmp/editmode.log

# PlayMode
"$UNITY" -batchmode -nographics -projectPath AIDeckUnity \
         -runTests -testPlatform PlayMode \
         -testResults /tmp/playmode-results.xml -logFile /tmp/playmode.log
```

Exit code 0 means every test passed, 2 means at least one failed. Read the XML for detail;
`grep "error CS" /tmp/editmode.log` for compile errors.

Unity must not be open in the editor while a batch-mode run is in progress — it holds a lock
on the project.

## Layering

```
Core  ←  Audio, Net, UI  ←  Host, Controller  ←  App
```

`AIDeck.Core` has `noEngineReferences: true` and **must keep it**. It is what makes the deck
logic, DSP, protocol and persistence testable in seconds with no scene and no audio device,
and it is what makes the audio thread safe to write.

If you find yourself wanting `UnityEngine.Time` or `Debug.Log` in Core, the logic belongs one
layer up, or the value should be passed in as a parameter.

## Rules that are not negotiable

These come from §9 and §7 of the Issue, and every one of them has a test.

1. **No NaN, Infinity or out-of-range value reaches an audio parameter.** Route it through
   `AudioSafety`. New parameters sanitise in the setter, not at the call site.
2. **The audio thread allocates nothing, locks nothing and does no I/O.** `OnAudioFilterRead`
   and anything it calls. The recorder's `Submit` writes to a lock-free ring; the file is
   written on another thread.
3. **No gain changes in a step.** Play, pause, cue, load, eject, disconnect and quit all
   fade over ~12 ms. A one-sample cut is a click at full output level.
4. **Continuous controls release when their input disappears.** Touch cancel, backgrounding
   and disconnect all call `Cancel()` / `ReleaseContinuousControls()`.
5. **The user's music is read-only.** No code path writes, moves or deletes a source file.
6. **Nothing personal in logs or on the wire.** File names, not paths. No account
   information, no file contents.
7. **Exceptions are surfaced, not swallowed.** The user gets a short sentence; the detail goes
   to `DiagnosticLog`. `catch { }` with an empty body needs a comment saying why.

## Reporting work

From §14 and §15 of the Issue:

* **Never report a test as passing unless it was run.** "Not run" and "failed" are different
  things and must be said differently.
* **Never mark a checklist item complete with a known problem hidden.** Record it in
  [`docs/KNOWN_LIMITATIONS.md`](docs/KNOWN_LIMITATIONS.md) instead.
* A feature that had to be simplified is documented as simplified, not as done.
* Commit per feature. The message says what changed and why.
* Post a phase report to the Issue in the §15 format at the end of each phase.

## Prohibited

* Paid assets, paid APIs, or code whose licence is unclear. Everything non-trivial in Core —
  JSON, wire codec, DSP, WAV writer, BPM analyser — is written here for this reason.
* `git reset --hard`, `git clean -fd`, `sudo`, deleting anything outside this repository.
* Changing other Unity projects, Apple account settings, or the user's home configuration.
* Closing Issue #1, or submitting to the App Store, without the user asking.

## Style

* Four-space indent, Allman braces, `_camelCase` private fields, file-scoped namespaces are
  **not** used (the codebase uses block namespaces consistently).
* XML doc comments on public types and any member whose behaviour is not obvious from its
  name.
* Comments explain **why**, not what. A comment that restates the code is noise; a comment
  that records why a threshold is 0.15 or why SYNC refuses rather than clamps is the point.
* Tests are named as sentences describing the behaviour —
  `EnableSync_RefusesWhenEitherTempoIsUnknown` — and assert behaviour, not implementation.
