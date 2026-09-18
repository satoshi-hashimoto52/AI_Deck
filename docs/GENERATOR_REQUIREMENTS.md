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
| GEN-010 | The Unity main thread and audio thread never block on model loading, generation, polling or file writes. | 2 |
| GEN-011 | The Mac UI shows queued, generating, completed, failed and cancelled states. | 2 |
| GEN-012 | A completed song can be refreshed into the library and loaded to deck A or B. | 2 |
| GEN-013 | The iPad controller does not run the model and remains responsive while the Mac generates. | 2 |
| GEN-014 | The default Night Drive preset produces a DJ-friendly instrumental intro, break and outro at 118 BPM in A minor. | 3 |
| GEN-015 | No generated result is described as original, rights-cleared or commercially usable without a separate licence review. | all |

## 3. Phase 0 acceptance

Phase 0 passes only on the target M1 MacBook Air when all of the following are observed:

1. the pinned environment installs without changing the Unity environment;
2. the local health endpoint answers on `127.0.0.1:8001`;
3. a 30-second Japanese female-vocal request completes without memory-pressure termination;
4. `Neon Highway Phase 0.wav` is a non-empty, readable WAV;
5. AI Deck imports and plays it;
6. elapsed time and subjective vocal/music quality are recorded honestly.

Passing unit tests in another environment does not satisfy these hardware conditions.

## 4. Non-goals for the first release

- Training a foundation model from scratch.
- Claiming Suno-equivalent vocal quality.
- Running generation on the iPad.
- Generating during a live set until the M1 soak test proves that playback is unaffected.
- Commercial distribution or an App Store submission.
- Cloning a real person's voice or imitating a named artist.
