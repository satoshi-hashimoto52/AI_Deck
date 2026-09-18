# AI Deck Generator — Third-party inventory

The generator is deliberately kept out of the Unity binary. Its source, Python environment
and model files are downloaded into the ignored `.aideck-generator/` runtime directory and
are not redistributed by this repository.

| Component | Pinned identity | Declared licence | Purpose |
| --- | --- | --- | --- |
| ACE-Step 1.5 source | Git tag `v0.1.8` | MIT | REST service and inference pipeline |
| `acestep-v15-turbo` | resolved by pinned ACE-Step | Check the downloaded model card | Audio diffusion model |
| `acestep-5Hz-lm-0.6B` | resolved by pinned ACE-Step | MIT in its model card | Lightweight song planner |

Sources:

- <https://github.com/ACE-Step/ACE-Step-1.5>
- <https://huggingface.co/ACE-Step/acestep-v15-turbo>
- <https://huggingface.co/ACE-Step/acestep-5Hz-lm-0.6B>

The upstream model card describes the system as commercially ready and MIT-licensed. AI
Deck does not turn that statement into a warranty. Before generated music is sold,
published, used for advertising, or bundled with a distributed application, the exact
downloaded model revision and its then-current terms must be reviewed again.

Prompts must describe musical properties rather than request imitation of a named living
artist, and reference audio must be material the user has permission to adapt.
