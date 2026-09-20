# AI Deck Generator — Requirements

This document defines the post-V1 local music-generation feature. It does not amend or
silently extend Issue #1: the shipped DJ host/controller remains independently usable while
this feature is developed.

## 1. Goal

Generate an original WAV file on the Mac from a description and optional Japanese lyrics,
save it under `~/Music/AI Deck/Generated`, and make it available to the existing two-deck
library without depending on a paid music service or a service download button.

## 2. Requirements

| ID | Requirement | Phase |
| --- | --- | --- |
| GEN-001 | Generation runs locally after a one-time model installation. | 0 |
| GEN-002 | The supported Phase 0 machine is Apple Silicon with 16 GB unified memory or more. | 0 |
| GEN-003 | The engine is ACE-Step 1.5 `v0.1.8`, `acestep-v15-turbo`, with the 0.6B LM on MLX. | 0 |
| GEN-004 | A request controls title, description, lyrics, duration, BPM, key, language and optional seed. | 1 |
| GEN-005 | Input outside the upstream documented ranges is refused before generation. | 1 |
| GEN-006 | Output is validated as a WAV container and installed atomically. | 1 |
| GEN-007 | Every output has a JSON sidecar containing parameters, model identity, seed information and elapsed time. | 1 |
| GEN-008 | Partial, timed-out and failed jobs never appear in the music library. | 1 |
| GEN-009 | Generated files are written only below the configured generated-music directory. | 1 |
| GEN-010a | The Unity main thread and audio thread are never blocked by generation code: no HTTP, process start, file I/O, lock or allocation on either. | 2 |
| GEN-010b | Playback stays stable while a **real model** generates. Not claimed, not measured, and not to be checked before the soak. | 4 |
| GEN-011 | The Mac UI shows queued, generating, completed, failed and cancelled states. | 2 |
| GEN-012 | A completed song can be refreshed into the library and loaded to deck A or B. | 2 |
| GEN-013 | The iPad controller does not run the model and remains responsive while the Mac generates. | 2 |
| GEN-014 | The default Night Drive preset produces a DJ-friendly instrumental intro, break and outro at 118 BPM in A minor. | 3 |
| GEN-015 | No generated result is described as original, rights-cleared or commercially usable without a separate licence review. | all |
| GEN-016 | The server can be started, inspected, and stopped by scripts that never signal a process they did not start. | 1 |
| GEN-017 | "Starting" and "ready" are distinguishable without reading the log. | 1 |
| GEN-018 | Transport, readiness, HTTP, task, timeout, process-exit and invalid-audio failures are reported as separate kinds. | 1 |
| GEN-019 | A generation longer than one control timeout is not failed on the clock; the maximum wait is stated. | 1 |
| GEN-020 | An existing output file is never overwritten, and file names cannot escape the output directory. | 1 |
| GEN-021 | Memory and swap can be measured before starting, after loading, after generating and after stopping. | 1 |
| GEN-022 | Setup is re-runnable and re-downloads nothing that is already present. | 1 |
| GEN-023 | The language model in use is fixed by profile and visible at run time. | 1 |
| GEN-024 | Generation is refused while either deck is playing or fading out, while cue monitoring, or while recording, with the reason shown. | 2 |
| GEN-025 | AI Deck never starts the engine or loads a model without an explicit button press. | 2 |
| GEN-026 | Unity talks only to the AI Deck bridge contract, never to the engine's own API. | 2 |
| GEN-027 | The bridge binds to loopback only and refuses any other address. | 2 |
| GEN-028 | Each process stops only what it started; an externally started server survives. | 2 |
| GEN-029 | Cancelling really interrupts the work; the UI is never marked cancelled while generation continues. | 2 |
| GEN-030 | Prompts, lyrics and absolute paths are never written to a log. | 2 |

## 3. Phase 0 acceptance

Phase 0 passes only on the target M1 MacBook Air when all of the following are observed:

1. the pinned environment installs without changing the Unity environment;
2. the local health endpoint answers on `127.0.0.1:8001`;
3. a 30-second Japanese female-vocal request completes without memory-pressure termination;
4. `Neon Highway Phase 0.wav` is a non-empty, readable WAV;
5. AI Deck imports and plays it;
6. elapsed time and subjective vocal/music quality are recorded honestly.

Passing unit tests in another environment does not satisfy these hardware conditions.

## 4. Phase 1 acceptance

Phase 1 passes on the target M1 MacBook Air when all of the following are observed. All were
observed on 2026-09-20; the figures are in
[`GENERATOR_PHASE1.md`](GENERATOR_PHASE1.md).

1. `setup_macos.sh` runs a second time without re-downloading a model or replacing anything;
2. `start_macos.sh` prints the models, the PID, the log path and the checkpoint path, and
   refuses to start a second copy or to take a port already in use;
3. `status_macos.sh` distinguishes not-running, starting and ready, and names the loaded
   language model;
4. one 30-second track generates and is saved with a JSON sidecar;
5. AI Deck imports and plays it;
6. after `stop_macos.sh` no ACE-Step process remains and unrelated processes are untouched;
7. memory and swap are recorded before starting, after loading, after generating and after
   stopping;
8. the loaded language model is shown to be the 0.6B, not the 1.7B.

## 5. Phase 2 acceptance

Phase 2 passes when all of the following hold. All were observed on 2026-09-20 except where
the row says otherwise; figures are in [`GENERATOR_PHASE2.md`](GENERATOR_PHASE2.md).

1. **GENERATE** opens a sheet that does not overlap the deck controls;
2. the sheet refuses to generate while a deck plays, fades out, cues or records, and says why;
3. nothing starts a process or loads a model until a button is pressed;
4. one 30-second track generates from inside AI Deck and lands in the library by itself;
5. **LOAD TO A** / **LOAD TO B** put it on a deck, and it plays after the server is stopped;
6. "stop the AI server after generating" works, and no ACE-Step process or port 8001 remains;
7. an unrelated process is never signalled;
8. memory and swap are recorded either side of the run;
9. the iPad build contains no model and no Python runtime.

**Not part of Phase 2 acceptance:** generating while a deck plays. That is GEN-010b and waits
for the Phase 4 soak. Audio quality remains a listening judgement and is left undecided.

## 6. Non-goals for the first release

- Training a foundation model from scratch.
- Claiming Suno-equivalent vocal quality.
- Running generation on the iPad.
- Generating during a live set until the M1 soak test proves that playback is unaffected.
- Commercial distribution or an App Store submission.
- Cloning a real person's voice or imitating a named artist.
