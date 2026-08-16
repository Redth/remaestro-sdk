"""The lamp itself, and the pretend wire it sits on.

There is no real hardware and no socket — a sample that dials a real address is a sample that
cannot be run in a test suite. What is real is the *shape*: a login exchange that carries a
password, a command exchange, and a pairing wait that takes long enough for the hub to notice.
"""

import threading
import time

from .diag import DIAG

ENDPOINT_PORT = 4499


class Lamp:
    """One lamp. Every method here takes `_lock`, and that is the point — see host.py."""

    def __init__(self, device_id: str, name: str, config: dict):
        self.device_id = device_id
        self.name = name
        self.host = config.get("host", "")
        self.password = config.get("password", "")
        self.endpoint = f"{self.host}:{ENDPOINT_PORT}"
        # Every config value arrives as a string — `map<string, string>`, with no types on the
        # wire at all. A `type: "number"` field is a hint to the console's input box and nothing
        # more, so parsing and defaulting is the plugin's job, every time.
        try:
            self.pair_seconds = max(1.0, min(300.0, float(config.get("pair_seconds") or 20)))
        except ValueError:
            self.pair_seconds = 20.0

        self._lock = threading.Lock()
        self._state = {"power": "off", "brightness": "0"}
        self._online = False

        self._login()

    # -- the pretend wire --------------------------------------------------------------

    def _tx(self, line: str) -> None:
        # The raw bytes are handed over rather than a hex string built here, so the one place that
        # knows what a secret is gets to blot them before they are rendered. See Ring.blot.
        DIAG.emit(self.device_id, "tcp", "tx", line, endpoint=self.endpoint, payload=line.encode())

    def _rx(self, line: str) -> None:
        DIAG.emit(self.device_id, "tcp", "rx", line, endpoint=self.endpoint, payload=line.encode())

    def _login(self) -> None:
        DIAG.emit(self.device_id, "tcp", "open", f"connecting to {self.endpoint}",
                  endpoint=self.endpoint)
        # The line that makes this sample worth having: the password crosses the wire, so it
        # crosses the trace. `Ring.redact` is the only thing standing between it and a support
        # bundle, and it only works because host.py registered it first.
        self._tx(f"LOGIN {self.host} {self.password}")
        self._rx("OK ready")
        self._online = True

    # -- state -------------------------------------------------------------------------

    @property
    def online(self) -> bool:
        with self._lock:
            return self._online

    def state(self) -> dict:
        with self._lock:
            return dict(self._state)

    # -- commands ----------------------------------------------------------------------

    def execute(self, command_id: str, args: dict) -> dict:
        with self._lock:
            if command_id == "power.on":
                self._tx("SET POWER ON")
                self._rx("OK")
                self._state["power"] = "on"
                if self._state["brightness"] == "0":
                    self._state["brightness"] = "100"
            elif command_id == "power.off":
                self._tx("SET POWER OFF")
                self._rx("OK")
                self._state["power"] = "off"
            # `light.set_level`, and not a name of this driver's choosing. A command id the hub's
            # CommandVocabulary does not know resolves to no capability at all: it still appears on the
            # device's own toolbox, and it is invisible to the assistant, to remotes, to activities and to
            # physical-remote routing. `lamp.pair` below is deliberately in that state; this one is not.
            elif command_id == "light.set_level":
                level = args.get("level", "")
                if not level.isdigit() or not 0 <= int(level) <= 100:
                    raise ValueError(f"brightness must be 0-100, got '{level}'")
                self._tx(f"SET LEVEL {level}")
                self._rx("OK")
                self._state["brightness"] = level
                self._state["power"] = "on" if int(level) > 0 else "off"
            else:
                raise ValueError(f"this lamp has no command '{command_id}'")
            return dict(self._state)

    def pair(self, seconds: float, cancelled) -> None:
        """Wait for somebody to walk over and press the button on the lamp.

        Holds `_lock` throughout, deliberately: this is the single-threaded shape the protocol's
        `heartbeat_independent = false` exists to describe, and a sample that quietly avoided it
        would prove nothing.
        """
        with self._lock:
            self._tx("PAIR BEGIN")
            deadline = time.monotonic() + seconds
            while time.monotonic() < deadline and not cancelled():
                time.sleep(0.05)
            self._rx("OK paired")
