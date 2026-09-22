"""HTTP front for :mod:`bridge`, bound to loopback.

The contract Unity sees, and the only one it should ever learn:

``GET  /v1/state``           what the bridge is doing
``POST /v1/server/start``    load the models (never automatic)
``POST /v1/server/stop``     unload them; ``{"force": true}`` stops an adopted server too
``POST /v1/generate``        queue one generation
``POST /v1/cancel``          stop the running one — see ``GeneratorBridge.cancel``

Every response is ``{"ok": bool, "data"|"error": …}``. Errors carry a ``kind`` so the deck can
branch without matching on English.

Written on :mod:`http.server` rather than a framework: the bridge must not add a dependency
to a project whose whole licence argument is that it wrote its own JSON, codec and DSP.
"""

from __future__ import annotations

import argparse
import json
import os
import signal
import socketserver
import sys
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer
from typing import Any, Dict, Optional, Tuple

from .bridge import (
    BRIDGE_HOST,
    BRIDGE_PORT,
    BridgeBusyError,
    BridgeError,
    GeneratorBridge,
    redact,
)

MAX_BODY_BYTES = 256 * 1024


class _Handler(BaseHTTPRequestHandler):
    bridge: GeneratorBridge = None  # type: ignore[assignment]
    server_version = "AIDeckGeneratorBridge/1"
    sys_version = ""

    # http.server answers HTTP/1.0 by default, even to an HTTP/1.1 request. curl copes by
    # treating the connection as closing; Unity's UnityWebRequest sends keep-alive and then
    # waits for a response that never comes, so the deck's poll hung on its first request and
    # a finished track was never noticed. Every response here carries an accurate
    # Content-Length, which is what HTTP/1.1 needs to keep a connection honest.
    protocol_version = "HTTP/1.1"

    # -------------------------------------------------------------- plumbing

    def log_message(self, format: str, *args: Any) -> None:
        """Quieter and safer than the default.

        The default prints the request line, which for this server would be harmless, but the
        rule is that nothing in the generator writes a path or a prompt to a log and the
        cheapest way to keep it is not to print request detail at all.
        """
        return

    def _send(self, status: int, payload: Dict[str, Any]) -> None:
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        # A browser on this machine must not be able to drive the generator from a web page.
        self.send_header("Access-Control-Allow-Origin", "null")
        self.end_headers()
        self.wfile.write(body)

    def _ok(self, data: Dict[str, Any]) -> None:
        self._send(200, {"ok": True, "data": data})

    def _fail(self, status: int, message: str, kind: str, detail: str = "") -> None:
        self._send(status, {"ok": False, "error": {
            "message": message, "kind": kind, "detail": redact(detail)
        }})

    def _body(self) -> Dict[str, Any]:
        length = int(self.headers.get("Content-Length") or 0)
        if length <= 0:
            return {}
        if length > MAX_BODY_BYTES:
            raise ValueError("The request body is too large.")
        raw = self.rfile.read(length)
        try:
            parsed = json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise ValueError("The request body was not valid JSON.") from exc
        if not isinstance(parsed, dict):
            raise ValueError("The request body must be a JSON object.")
        return parsed

    # --------------------------------------------------------------- routing

    def do_GET(self) -> None:  # noqa: N802 - name fixed by BaseHTTPRequestHandler
        if self.path.split("?")[0] == "/v1/state":
            self._dispatch(lambda _body: self.bridge.state())
            return
        self._fail(404, "Unknown endpoint.", "not-found")

    def do_POST(self) -> None:  # noqa: N802
        route = self.path.split("?")[0]
        actions = {
            "/v1/server/start": lambda _body: self.bridge.start_server(),
            "/v1/server/stop": lambda body: self.bridge.stop_server(
                force=bool(body.get("force", False))
            ),
            "/v1/generate": lambda body: self.bridge.generate(body),
            "/v1/cancel": lambda _body: self.bridge.cancel(),
            "/v1/shutdown": lambda _body: self._shutdown(),
        }
        action = actions.get(route)
        if action is None:
            self._fail(404, "Unknown endpoint.", "not-found")
            return
        self._dispatch(action)

    def _shutdown(self) -> Dict[str, Any]:
        """Stop the engine we own, then end this process.

        AI Deck asks for this when it quits, over the same contract it already speaks, rather
        than signalling the process. Sending SIGKILL — which is what .NET's ``Process.Kill``
        does — skips Python's cleanup entirely and would leave a bridge-owned ACE-Step server
        holding several gigabytes with nothing left to stop it.

        An engine the bridge merely adopted is left running: it was not ours to start.
        """
        state = self.bridge.state()
        self.bridge.shutdown()

        # Answer first, exit after: the caller gets a clean response rather than a dropped
        # connection, and the wait is short enough not to hold up a quit.
        threading.Timer(0.5, lambda: os._exit(0)).start()
        return {"state": state.get("state", "stopped"), "stopping": True}

    def _dispatch(self, action) -> None:
        try:
            body = self._body()
        except ValueError as exc:
            self._fail(400, str(exc), "bad-request")
            return

        try:
            self._ok(action(body))
        except BridgeBusyError as exc:
            self._fail(409, str(exc), exc.kind, exc.detail)
        except BridgeError as exc:
            self._fail(400, str(exc), exc.kind, exc.detail)
        except ValueError as exc:
            self._fail(400, str(exc), "invalid-input")
        except Exception as exc:  # never leave the deck waiting on a 200 that never comes
            # The type alone is not diagnosable: "internal: NameError" says something is
            # wrong and nothing about what. The message is included, redacted, so the deck's
            # log names the fault instead of its category.
            self._fail(500, "The bridge failed to handle that request.",
                       "internal", f"{type(exc).__name__}: {exc}")


class _LoopbackServer(socketserver.ThreadingMixIn, HTTPServer):
    """Threaded so a slow /v1/state cannot block a cancel."""

    daemon_threads = True
    allow_reuse_address = True


def serve(
    host: str = BRIDGE_HOST,
    port: int = BRIDGE_PORT,
    bridge: Optional[GeneratorBridge] = None,
) -> Tuple[_LoopbackServer, threading.Thread]:
    """Start the bridge on loopback and return it, already serving.

    ``host`` is checked rather than trusted: binding a model server to 0.0.0.0 would put an
    unauthenticated generator on the LAN, and no configuration mistake should be able to do
    that silently.
    """
    if host not in {"127.0.0.1", "localhost", "::1"}:
        raise ValueError(
            f"The generator bridge only binds to loopback; refused to bind {host!r}."
        )

    _Handler.bridge = bridge or GeneratorBridge()
    server = _LoopbackServer((host, port), _Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True, name="aideck-bridge")
    thread.start()
    return server, thread


def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="AI Deck local generator bridge")
    parser.add_argument("--host", default=BRIDGE_HOST)
    parser.add_argument("--port", type=int, default=BRIDGE_PORT)
    arguments = parser.parse_args(argv)

    try:
        server, _thread = serve(arguments.host, arguments.port)
    except ValueError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2
    except OSError as exc:
        print(f"ERROR: could not bind {arguments.host}:{arguments.port}: {exc}", file=sys.stderr)
        return 3

    # A terminated bridge must still stop the engine it started, so SIGTERM unwinds through
    # the same finally as Ctrl+C instead of ending the process where it stands.
    def _on_terminate(_signum: int, _frame: Any) -> None:
        raise KeyboardInterrupt

    signal.signal(signal.SIGTERM, _on_terminate)
    signal.signal(signal.SIGINT, _on_terminate)

    try:
        print(f"AI Deck generator bridge listening on http://{arguments.host}:{arguments.port}",
              flush=True)
        print("This endpoint is loopback-only and is not reachable from the LAN.", flush=True)
    except (BrokenPipeError, ValueError, OSError):
        # Started with no readable standard output. Not a reason to refuse to run.
        pass
    try:
        while True:
            _thread_join(_thread)
    except KeyboardInterrupt:
        print("\nStopping the bridge.", flush=True)
    finally:
        bridge = _Handler.bridge
        if bridge is not None:
            # Only a server this bridge started is stopped; an adopted one is left running.
            bridge.shutdown()
        server.shutdown()
        server.server_close()
    return 0


def _thread_join(thread: threading.Thread) -> None:
    thread.join(timeout=1.0)


if __name__ == "__main__":
    raise SystemExit(main())
