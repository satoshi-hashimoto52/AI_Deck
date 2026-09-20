"""Client tools for the local ACE-Step music generation service."""

from .client import (
    MAC_DIT_MODEL,
    MAC_LM_BACKEND,
    MAC_LM_MODEL,
    AceStepClient,
    GenerationRequest,
    GenerationResult,
    ServerHealth,
    safe_filename,
)
from .errors import (
    GenerationFailedError,
    GeneratorError,
    GeneratorHttpError,
    GeneratorNotReadyError,
    GeneratorStoppedError,
    GeneratorTimeoutError,
    GeneratorUnreachableError,
    InvalidGeneratedAudioError,
)
from .wave_check import WaveSummary, WaveValidationError, inspect_wave

__all__ = [
    "AceStepClient",
    "GenerationRequest",
    "GenerationResult",
    "ServerHealth",
    "safe_filename",
    "MAC_DIT_MODEL",
    "MAC_LM_MODEL",
    "MAC_LM_BACKEND",
    "GeneratorError",
    "GeneratorUnreachableError",
    "GeneratorNotReadyError",
    "GeneratorHttpError",
    "GenerationFailedError",
    "GeneratorTimeoutError",
    "GeneratorStoppedError",
    "InvalidGeneratedAudioError",
    "WaveSummary",
    "WaveValidationError",
    "inspect_wave",
]
