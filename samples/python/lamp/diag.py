"""The driver's own record of its conversation with the lamp.

This file exists because of one line in the protocol checklist:

    Redact your own secrets before anything reaches GetDiagnostics. The hub cannot do it for you
    and there is no wire-level equivalent.

It is worth being precise about how sharp that is. The hub turns capture on, the driver answers
with whatever it recorded, and the hub writes those records **verbatim** into `trace.txt` and
`trace.json` inside a support bundle that a person then emails. Nothing between here and there
masks anything. A plugin that logs its own login line and never calls `register_secret` ships the
device's password to whoever reads the bundle, and no test anywhere goes red.

So: every value the config schema marks `type: "secret"` is registered here at `CreateDevice`
time, and every string leaving this module is passed through `redact` on the way in.
"""

import threading
import time

_MAX = 4000
_MIN_SECRET = 4  # below this, masking eats ordinary text rather than protecting anything


class Ring:
    def __init__(self):
        # Reentrant, and not by taste: `emit` holds this while calling `redact`, which needs it
        # too. A plain Lock there deadlocks the ring on the first captured record and every
        # later diagnostics call blocks for ever — no error, no log, and the hub's own
        # GetDiagnostics poll simply times out. The C# SDK never meets this because its secret
        # set is a ConcurrentDictionary and its capture set is a separate lock.
        self._lock = threading.RLock()
        self._records = []
        self._seq = 0
        self._on = set()
        self._everything = False
        self._secrets = set()

    # -- capture scope -----------------------------------------------------------------

    def set_enabled(self, device_id: str, on: bool) -> None:
        with self._lock:
            if on:
                self._on.add(device_id)
            else:
                self._on.discard(device_id)

    def set_everything(self, on: bool) -> None:
        with self._lock:
            self._everything = on

    def enabled(self, device_id: str) -> bool:
        with self._lock:
            return self._everything or device_id in self._on

    @property
    def everything(self) -> bool:
        with self._lock:
            return self._everything

    # -- redaction ---------------------------------------------------------------------

    def register_secret(self, value) -> None:
        """Never let this value appear in a record again.

        Called for every config field whose declared type is `secret`, and for anything the
        driver learns later that is secret-shaped — a session token handed back by the device is
        every bit as leakable as the password that obtained it, and only this process ever sees it.
        """
        if isinstance(value, str) and len(value) >= _MIN_SECRET:
            with self._lock:
                self._secrets.add(value)

    def redact(self, text: str) -> str:
        if not text:
            return text
        with self._lock:
            secrets = tuple(self._secrets)
        for s in secrets:
            text = text.replace(s, "***")
        return text

    # -- recording ---------------------------------------------------------------------

    def blot(self, payload: bytes) -> bytes:
        """The payload with every secret's bytes overwritten, ready to be rendered as hex.

        **This is the half that is easy to miss, and missing it undoes the other half.** A record
        carries the same moment twice — once as words and once as bytes — and redacting only the
        words leaves the password masked in the readable column and printed in full, one column to
        the right. It is not a hypothetical: the .NET SDK shipped exactly this bug, its own guard
        enumerated the record's string fields by hand and stopped one short of `hex`, and this
        sample reproduced it faithfully by porting the design without the fix.

        Blotted at the byte level rather than by string-replacing the hex, because a payload is
        usually capped before it is hexed and a secret straddling that cap would otherwise survive
        as a fragment. Half a password is a shorter password, not a redacted one.
        """
        with self._lock:
            secrets = tuple(self._secrets)
        for s in secrets:
            payload = payload.replace(s.encode(), b"*" * len(s.encode()))
        return payload

    def emit(self, device_id, transport, direction, text, detail="", endpoint="", payload=b"") -> None:
        if not self.enabled(device_id):
            return
        with self._lock:
            self._seq += 1
            self._records.append(
                {
                    "seq": self._seq,
                    "ts": int(time.time() * 1000),
                    "device_id": device_id,
                    "transport": transport,
                    "direction": direction,
                    "text": self.redact(text),
                    "detail": self.redact(detail),
                    # Every string field, not the two obvious ones. An endpoint can be a URL with a
                    # token in its query, and nothing stops it.
                    "endpoint": self.redact(endpoint),
                    "hex": self.blot(payload).hex().upper(),
                }
            )
            if len(self._records) > _MAX:
                del self._records[: len(self._records) - _MAX]

    def since(self, device_id: str, after_seq: int):
        with self._lock:
            return [
                r
                for r in self._records
                if r["seq"] > after_seq and (not device_id or r["device_id"] == device_id)
            ]


DIAG = Ring()
