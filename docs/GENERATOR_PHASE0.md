# AI Deck Generator — Phase 0 on the M1 Mac

Phase 0 proves the model on the actual target hardware before the Unity UI is built around
it. It keeps all third-party source, Python packages and model weights in the ignored
`.aideck-generator/` directory. Nothing is installed inside `AIDeckUnity`.

ACE-Step's core models require roughly 10 GB of free disk space. The first server start
downloads the weights; later starts reuse them.

## 1. Set up

From the repository root:

```bash
brew install uv
./generator/scripts/setup_macos.sh
```

The setup is pinned to ACE-Step 1.5 `v0.1.8`; it does not silently pull a newer model or
source revision.

## 2. Start the local engine

```bash
./generator/scripts/start_macos.sh
```

Keep this Terminal window open. The endpoint is loopback-only (`127.0.0.1`) and is not
reachable from another machine on the LAN.

The first start is not ready when the process merely prints its port. Wait until model
loading has completed, then use a second Terminal window:

```bash
cd /Users/hashimoto/vscode/AI_Deck
python3 -m generator.aideck_generator health
```

## 3. Generate the fixed acceptance sample

In the second Terminal:

```bash
./generator/scripts/phase0_generate.sh
```

The output is saved under:

```text
~/Music/AI Deck/Generated/
```

Press **ADD FILES** in AI Deck with `~/Music/AI Deck` selected. Load the generated row to
deck A, listen once without effects, then check FILTER, ECHO, LOOP and the jog wheel.

## 4. Report back

Copy the command output and report:

- generation elapsed time;
- whether macOS showed yellow or red memory pressure;
- whether the voice sounds intelligibly Japanese;
- whether the music is enjoyable enough to continue integrating;
- whether ordinary AI Deck playback remained stable while the generator was open.

Do not mark Phase 0 complete from the presence of a file alone. The audible result and the
effect on real-time playback are acceptance criteria.

## 5. Remove the experiment

Phase 0 never removes files automatically. If you later decide not to keep it, the runtime
is entirely contained in `.aideck-generator/`; generated songs remain in the music folder
until you choose to remove them.
