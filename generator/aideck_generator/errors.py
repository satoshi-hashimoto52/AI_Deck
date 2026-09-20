"""What went wrong, told apart.

Phase 0 reported every transport problem as "Local generator is not reachable".  That
sentence is correct only for one of them, and it sent the user to restart a server that was
in fact running and still loading a 4.5 GB model.  Each failure below needs a different
action from the person reading it, so each one is a different type.

The taxonomy is deliberately about *what the user must do next*, not about which Python
exception was raised:

``GeneratorUnreachableError``   nothing is listening — start the server
``GeneratorNotReadyError``      it is listening and still loading — wait
``GeneratorHttpError``          it answered with a status code — read the detail
``GenerationFailedError``       the task ran and failed — change the request or read the log
``GeneratorTimeoutError``       no answer in the time allowed — wait longer or reduce duration
``GeneratorStoppedError``       the process we were talking to has gone — look at the log
"""

from __future__ import annotations

from typing import Optional


class GeneratorError(RuntimeError):
    """Base class: the local generator could not complete a request safely.

    ``kind`` is a stable machine-readable tag.  The Unity bridge in a later phase will map it
    to a screen message; nothing should parse the English text.
    """

    kind = "generator-error"

    def __init__(self, message: str, *, detail: Optional[str] = None) -> None:
        super().__init__(message)
        self.detail = detail

    def report(self) -> str:
        """One or two lines for a terminal, tagged so the class is visible in a log."""
        if self.detail:
            return f"[{self.kind}] {self} ({self.detail})"
        return f"[{self.kind}] {self}"


class GeneratorUnreachableError(GeneratorError):
    """Nothing accepted the connection: no server, wrong port, or it exited."""

    kind = "unreachable"


class GeneratorNotReadyError(GeneratorError):
    """The server is up but has not finished loading its models.

    Not a failure. The first start loads roughly 10 GB from disk and legitimately takes
    minutes on an M1 with 16 GB, and treating that as an error is what made Phase 0 look
    broken when it was merely slow.
    """

    kind = "not-ready"


class GeneratorHttpError(GeneratorError):
    """The server answered with a non-success status code."""

    kind = "http-error"

    def __init__(self, status: int, message: str, *, detail: Optional[str] = None) -> None:
        super().__init__(message, detail=detail)
        self.status = status


class GenerationFailedError(GeneratorError):
    """The task was accepted, ran, and failed. The server knows why; ask it."""

    kind = "task-failed"


class GeneratorTimeoutError(GeneratorError):
    """No answer within the time allowed.

    Carries the limit it hit so the message can say what to raise, rather than leaving the
    user to guess which of several timeouts expired.
    """

    kind = "timeout"

    def __init__(self, message: str, *, waited_seconds: float, limit_seconds: float) -> None:
        super().__init__(message)
        self.waited_seconds = waited_seconds
        self.limit_seconds = limit_seconds


class GeneratorStoppedError(GeneratorError):
    """The server process we were talking to is no longer running.

    Distinguished from ``GeneratorUnreachableError`` because it means something killed a
    server that had been working — an out-of-memory kill is the likely one on 16 GB — and the
    answer is in its log rather than in starting it again.
    """

    kind = "server-stopped"


class InvalidGeneratedAudioError(GeneratorError):
    """The server returned a file that is not usable audio.

    A zero-length or header-only WAV is a failed generation that reported success, and
    counting it as a win is how a silent track reaches the deck.
    """

    kind = "invalid-audio"
