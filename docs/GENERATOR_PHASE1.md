# Local music generation — Phase 1

Running the generator on an M1 MacBook Air with 16 GB: start it, see what it is doing,
generate a track, stop it, and measure what it cost.

Phase 0 proved a track could be made at all. Phase 1 is about being able to do it again
tomorrow without guessing. Nothing here changes the DJ application; see
[`GENERATOR_PHASE0.md`](GENERATOR_PHASE0.md) for what came before and
[`GENERATOR_REQUIREMENTS.md`](GENERATOR_REQUIREMENTS.md) for the phase plan.

## The commands

```bash
./generator/scripts/setup_macos.sh        # safe to re-run; downloads nothing twice
./generator/scripts/start_macos.sh        # foreground; Ctrl+C stops it cleanly
./generator/scripts/status_macos.sh       # not running / starting / ready
./generator/scripts/phase1_generate.sh    # one 30-second track, with memory readings
./generator/scripts/stop_macos.sh         # stops only what start_macos.sh started
./generator/scripts/memory_report.sh NAME # one labelled memory + swap snapshot
```

`status_macos.sh` exits `0` ready, `3` not running, `4` starting, `5` HTTP error, so a script
can branch without reading the text.

## Why the 1.7B language model was downloaded

Phase 0 asked for the 0.6B language model and got a 3.5 GB 1.7B one as well. Traced through
ACE-Step v0.1.8, there are two independent causes, and neither is the `--lm-model-path`
argument, which worked correctly.

**1. The generation model lives in a bundle that contains the 1.7B LM.**

`acestep/api/model_download.py` maps the *generation* model to the unified repository:

```python
MODEL_REPO_MAPPING = {
    "acestep-v15-turbo": "ACE-Step/Ace-Step1.5",
    "acestep-5Hz-lm-1.7B": "ACE-Step/Ace-Step1.5",
    ...
    "acestep-5Hz-lm-0.6B": "ACE-Step/acestep-5Hz-lm-0.6B",
}
```

and `download_from_huggingface` special-cases that repository by snapshotting **the whole
thing into the checkpoints root**, with no `allow_patterns`:

```python
if is_unified_repo:
    download_dir = local_dir          # the checkpoints root, not a per-model folder
snapshot_download(repo_id=repo_id, local_dir=download_dir, local_dir_use_symlinks=False)
```

`ACE-Step/Ace-Step1.5` bundles `acestep-v15-turbo`, `vae`, `Qwen3-Embedding-0.6B` **and**
`acestep-5Hz-lm-1.7B`. So asking for the turbo generation model necessarily fetches the 1.7B
LM. The 0.6B lives in its own repository and is fetched separately, which is why it appears
only after the LM step runs.

The tell-tale on disk is the repository's own root files — `README.md`, `.gitattributes`,
`config.json` — sitting next to the model folders in the checkpoints directory. That is a
whole-repository snapshot, not a per-model download.

**This cannot be avoided without patching the pinned upstream**, which Phase 1 does not do.
The 1.7B is already on disk and is left alone. It costs disk, not memory.

**2. Phase 0 set a variable the API server ignores, so it downloaded into two places.**

`acestep/api/startup_model_init.py` hardcodes its checkpoint directory:

```python
checkpoint_dir = os.path.join(project_root, "checkpoints")
```

with `project_root` being the installed package's parent. It never reads
`ACESTEP_CHECKPOINTS_DIR`. Phase 0's `start_macos.sh` exported that variable pointing at
`.aideck-generator/models`, which *other* code paths (`model_downloader.get_checkpoints_dir`)
do honour. The result was two checkpoint trees, each with its own copy of the 1.7B:

| Directory | Size | Used by the API server |
| --- | --- | --- |
| `.aideck-generator/ACE-Step-1.5/checkpoints` | 11 GB | **yes** |
| `.aideck-generator/models` | 9.5 GB | no |

Phase 1 stops setting `ACESTEP_CHECKPOINTS_DIR` and points every script at the directory the
server actually uses. **Neither tree is deleted** — see [Reclaiming disk](#reclaiming-disk).

## Was the 1.7B ever loaded?

No, and on this machine it cannot be. Measured with the installed runtime:

```
$ uv run python -c "from acestep.gpu_config import *; c = get_gpu_config(); print(c.tier, c.available_lm_models)"
macOS MPS detected (11.8 GB unified memory, tier=tier4)
tier4 ['acestep-5Hz-lm-0.6B']
```

`available_lm_models` contains only the 0.6B, and `startup_llm_init.py` validates the
requested model against that list. Even its fallback — `get_recommended_lm_model`, which
returns the *largest* available model — resolves to the 0.6B here, because it is the only
one. The 1.7B is 3.5 GB of disk that is never mapped into memory.

The evidence at run time is in the server log and in `/health`:

```
[API Server] Using LM model: acestep-5Hz-lm-0.6B
```

```bash
curl -s http://127.0.0.1:8001/health | python3 -m json.tool
# "loaded_lm_model": "acestep-5Hz-lm-0.6B"
```

`status_macos.sh` prints that field on every run and warns if it is ever not the 0.6B.

## The M1 Safe profile

Defined once, in `generator/scripts/common.sh`:

| Setting | Value | Why |
| --- | --- | --- |
| `ACESTEP_CONFIG_PATH` | `acestep-v15-turbo` | the smallest generation model with usable quality |
| `ACESTEP_LM_MODEL_PATH` | `acestep-5Hz-lm-0.6B` | the only LM this tier accepts |
| `ACESTEP_LM_BACKEND` | `mlx` | Apple Silicon path; `resolve_lm_backend` confirms it |
| `ACESTEP_INIT_LLM` | `true` | without the LM there is no prompt understanding |
| `ACESTEP_NO_INIT` | `false` | see below |
| `ACESTEP_CHECKPOINTS_DIR` | *not set* | the API server ignores it; setting it split the downloads |

`ACESTEP_NO_INIT` defaults to `True` upstream, meaning models load on the first request
rather than at startup. That makes `/health` report `models_initialized: false` for ever and
buries ten gigabytes of loading inside the first generation, where it looks like a hang.
Phase 1 loads eagerly so that "starting" and "ready" are real states.

## Memory and swap

Generation on 16 GB is a heavy job and the machine will swap. The scripts **measure** this;
they do not claim to fix it.

```bash
./generator/scripts/memory_report.sh before-start
./generator/scripts/memory_report.sh after-model-init
./generator/scripts/memory_report.sh after-generate
./generator/scripts/memory_report.sh after-stop
```

Each run appends to `.aideck-generator/logs/memory.log`.

### Measured, 2026-09-20, one 30-second track on a 16 GB M1 MacBook Air

| Point | Swap file | Swap used | Generator RSS |
| --- | --- | --- | --- |
| before start | 0 MB | 0 MB | — |
| after model load | 16384 MB | 15239 MB | 1405 MB |
| after generating | 19456 MB | 18847 MB | 1035 MB |
| after stop | 10240 MB | 3788 MB | — |

Read these as *this machine, this run*, from `.aideck-generator/logs/memory.log`. The swap
file grew to 19 GB, and the resident set of the server itself never exceeded about 1.4 GB —
the pressure is the unified-memory working set of a 4.5 GB model on a machine with 16 GB,
not a leak in the server process.

**Stopping the server releases some swap but not all of it.** In the run above, `used` fell
from 18.8 GB to 3.8 GB and the swap file itself shrank from 19 GB to 10 GB, but neither
returned to the zero it started at; that only happens on restart. So a large
`sysctl vm.swapusage` some time after generating is expected and does not mean something is
still holding memory.

Nothing in this repository reduces swap, and no change here should be read as having reduced
it unless a measurement in `memory.log` says so. The Phase 0 observation of 42 GB was taken
with no before-reading to compare against, which is the reason these four points exist.

## Output files

Tracks are written to `~/Music/AI Deck/Generated/`, a sub-folder of the music folder AI Deck
scans, so **ADD FILES** on `~/Music/AI Deck` imports them. (`Recordings/` is the only
sub-folder the scanner skips.)

* The title is sanitised: path separators and leading dots are removed, so a title can never
  write outside the output folder.
* An existing file is **never overwritten** — a second `Neon Highway` becomes `Neon Highway 2`.
* A `.json` of the same name records the exact prompt, models, seed, duration and timing.
* The WAV is parsed before it counts as a success: header, frame count, a length of at least
  half what was asked for, and a non-zero peak. A silent or truncated file is deleted and
  reported as a failure, so a broken generation never reaches the deck.

## What the errors mean

Phase 0 reported every transport problem as "Local generator is not reachable", including a
server that was up and loading. Each case is now separate, with its own exit code:

| Exit | Kind | Meaning | What to do |
| --- | --- | --- | --- |
| 3 | `unreachable` | nothing is listening | `start_macos.sh` |
| 4 | `not-ready` | up, still loading | `wait`, or watch `status_macos.sh` |
| 5 | `http-error` | answered with a status code | read the detail in the message |
| 6 | `task-failed` | the task ran and failed | the server's reason is in the message |
| 7 | `timeout` | no answer within the limit | raise `--max-wait`, or shorten the track |
| 8 | `server-stopped` | the process has gone | read `logs/server.log`; usually memory |
| 9 | `invalid-audio` | the WAV holds no audio | nothing was saved; generate again |

Timeouts are split. A control request gets 30 seconds, a download gets 600, and the
generation itself gets `--max-wait` (default 1800). A single 30-second limit would have failed
the measured 86.7-second Phase 0 run.

## Reclaiming disk

Roughly 9.5 GB in `.aideck-generator/models` is an orphan of the Phase 0 configuration, and
3.5 GB of the active tree is the 1.7B LM this profile never loads. **Phase 1 deletes
neither.** If you want the space back, delete them yourself:

```bash
du -sh .aideck-generator/models                                  # look first
rm -rf .aideck-generator/models                                  # orphaned tree
rm -rf .aideck-generator/ACE-Step-1.5/checkpoints/acestep-5Hz-lm-1.7B
```

The second one will be re-downloaded as part of the unified repository if the generation model
is ever fetched again, for the reason explained above.

## Known limitations

See [`KNOWN_LIMITATIONS.md`](KNOWN_LIMITATIONS.md) §6.
