"""The gRPC surface: everything `DriverHost` does in C#, done by hand.

Written by following `docs/driver-protocol.md` §5 in order. Where a step took more than the
checklist said, there is a comment saying so — those comments are the reason this file is a
sample rather than a script.
"""

import os
import queue
import sys
import threading
import time
from concurrent import futures

import grpc

import driver_pb2 as pb
import driver_pb2_grpc as pb_grpc

from .device import Lamp
from .diag import DIAG

TYPE_ID = "example.lamp"

# "The current version is the highest value in this enum. That is the definition rather than a
# convention, so a generated SDK can compute it instead of carrying a constant that drifts away
# from the file it came from." — so compute it. The .NET SDK's DriverProtocol.Current is the same
# expression, and there is a test on that side asserting the two agree.
PROTOCOL_CURRENT = max(pb.Protocol.values())

# The beat is coupled to command handling in this plugin — see `_beat` — so it is declared slow
# and dependent rather than fast and lying.
HEARTBEAT_INTERVAL_MS = 5000


def _config_schema():
    return [
        pb.ConfigField(key="host", label="Address", type="string", required=True,
                       help="The lamp's hostname or IP address."),
        # type="secret" is what makes this field a password box in the console *and* what this
        # plugin keys its own redaction off. Both readings matter and only one of them is the
        # hub's; see CreateDevice below.
        pb.ConfigField(key="password", label="Password", type="secret", required=True,
                       help="The lamp's local API password."),
        pb.ConfigField(key="pair_seconds", label="Pairing wait", type="number", advanced=True,
                       default_value="20", min="1", max="300",
                       help="How long the lamp waits for its button to be pressed."),
    ]


def _commands():
    return [
        pb.CommandDescriptor(id="power.on", label="Power On"),
        pb.CommandDescriptor(id="power.off", label="Power Off"),
        pb.CommandDescriptor(
            id="light.set_level", label="Set Brightness",
            parameters=[pb.ConfigField(key="level", label="Level (%)", type="number",
                                       min="0", max="100")]),
        # Deliberately not in CommandVocabulary. It appears on the device's own toolbox under this
        # label and resolves to no capability at all, so the assistant, the remotes and the
        # activity graph never see it. That is the documented design rather than a defect, and a
        # plugin author who does not know it will believe their plugin is broken.
        pb.CommandDescriptor(id="lamp.pair", label="Pair"),
    ]


class LampDriver(pb_grpc.DriverServicer):
    def __init__(self):
        self._devices = {}
        self._events = queue.Queue()
        self._stop = threading.Event()
        # What we last told the hub each device could do. Checklist item 8: report only on change.
        # Get it wrong in one direction and every GetState reprints the whole command list; get it
        # wrong in the other and a device that learns a command never mentions it.
        self._sent_commands = {}
        self._sent_traits = {}
        self._busy = threading.Lock()

    # -- 1. Describe -------------------------------------------------------------------

    def Describe(self, request, context):
        # The hub says who it is. A driver may narrow what it sends; it may not refuse. Logged
        # rather than acted on here, because there is nothing in this descriptor an older hub
        # would choke on.
        if request.hub_protocol and request.hub_protocol < PROTOCOL_CURRENT:
            print(f"[lamp] hub speaks protocol {request.hub_protocol}, "
                  f"this plugin was built against {PROTOCOL_CURRENT}", file=sys.stderr, flush=True)

        return pb.DriverDescriptor(
            type_id=TYPE_ID,
            display_name="Example Lamp",
            description="A dimmable lamp, written in Python straight from driver.proto.",
            config_schema=_config_schema(),
            commands=_commands(),
            events=[pb.EventDescriptor(type="lamp.changed", description="The lamp's level moved.")],
            state_schema=[
                pb.StateField(key="power", type="string", description="on | off"),
                pb.StateField(key="brightness", type="number", description="0-100"),
            ],
            traits=["light"],

            protocol_version=PROTOCOL_CURRENT,
            # min_hub_protocol deliberately left unset: unset means "a hub at least as new as the
            # contract I was built from", which is the safe reading and the one to leave alone
            # until you know otherwise. Setting it to 0 is NOT the same thing — the field has
            # explicit presence, and an explicit 0 declares a floor of zero.

            # A non-empty list is authoritative: it must name everything, including anything the
            # three legacy booleans cover. Declare a capability whose rpc is missing and the hub
            # will call it.
            capabilities=["inputs", "diagnostics"],
            supports_navigation=False,
            supports_epg=False,
            supports_device_remotes=False,
        )

    # -- 2. Device lifecycle -----------------------------------------------------------

    def CreateDevice(self, request, context):
        try:
            # Checklist item 9, and the one with no wire-level equivalent. Every field the schema
            # declares `secret` gets registered before the device is built, because the device
            # logs its login line the moment it is.
            for field in _config_schema():
                if field.type == "secret":
                    DIAG.register_secret(request.config.get(field.key))

            device = Lamp(request.device_id, request.name, dict(request.config))
            self._devices[request.device_id] = device
            commands = _commands()
            self._sent_commands[request.device_id] = _command_signature(commands)
            self._sent_traits[request.device_id] = ("light",)
            return pb.CreateDeviceResponse(ok=True, commands=commands, traits=["light"])
        except Exception as ex:  # noqa: BLE001 — an exception here is an answer, not a crash
            return pb.CreateDeviceResponse(ok=False, error=str(ex))

    def DisposeDevice(self, request, context):
        self._devices.pop(request.device_id, None)
        self._sent_commands.pop(request.device_id, None)
        self._sent_traits.pop(request.device_id, None)
        return pb.DisposeResponse(ok=True)

    # -- 3. Commands and state ---------------------------------------------------------

    def ExecuteCommand(self, request, context):
        device = self._devices.get(request.device_id)
        if device is None:
            return pb.ExecuteCommandResponse(ok=False, error="no such device")

        if request.command_id == "lamp.pair":
            return self._pair(device)

        with self._busy:
            try:
                result = device.execute(request.command_id, dict(request.args))
                self._emit(device.device_id, "lamp.changed", result)
                return pb.ExecuteCommandResponse(ok=True, result=result)
            except Exception as ex:  # noqa: BLE001
                return pb.ExecuteCommandResponse(ok=False, error=str(ex))

    def _pair(self, device):
        """A wait that is deliberate, declared, and longer than the hub's patience."""
        seconds = device.pair_seconds
        hold_id = f"{device.device_id}:pair"
        self._hold(hold_id, device.device_id,
                   "waiting for the button on the lamp",
                   int((time.time() + seconds) * 1000))
        try:
            with self._busy:
                device.pair(seconds, self._stop.is_set)
            return pb.ExecuteCommandResponse(ok=True, result={"paired": "true"})
        except Exception as ex:  # noqa: BLE001
            return pb.ExecuteCommandResponse(ok=False, error=str(ex))
        finally:
            # Release every hold you begin, *including the ones that failed*. A hold left open is
            # indistinguishable from the wedge it was added to rule out.
            self._hold(hold_id, device.device_id, "", 0, released=True)

    def GetState(self, request, context):
        device = self._devices.get(request.device_id)
        if device is None:
            return pb.DeviceStateMessage(online=False)

        msg = pb.DeviceStateMessage(online=device.online, state=device.state())

        commands = _commands()
        signature = _command_signature(commands)
        if self._sent_commands.get(request.device_id) != signature:
            self._sent_commands[request.device_id] = signature
            msg.commands_changed = True
            msg.commands.extend(commands)

        traits = ("light",)
        if self._sent_traits.get(request.device_id) != traits:
            self._sent_traits[request.device_id] = traits
            msg.traits_changed = True
            msg.traits.extend(traits)

        return msg

    # -- 4. Optional answers -----------------------------------------------------------

    def ListInputs(self, request, context):
        device = self._devices.get(request.device_id)
        if device is None:
            # Declared, so it is answered — and the three flavours of "no" are told apart, which
            # is the whole reason Availability exists.
            return pb.InputListMessage(supported=False,
                                       availability=pb.AVAILABILITY_UNKNOWN_DEVICE)
        if not device.online:
            return pb.InputListMessage(supported=False,
                                       availability=pb.AVAILABILITY_UNAVAILABLE)
        # `supported` is a plain proto3 bool, so unset and false are byte-identical and there is
        # no compiler anywhere on this path. Set it explicitly, every time.
        return pb.InputListMessage(
            supported=True, availability=pb.AVAILABILITY_ANSWERED,
            inputs=[pb.InputSourceMessage(id="warm", label="Warm white"),
                    pb.InputSourceMessage(id="cool", label="Cool white")])

    # -- 5. Diagnostics ----------------------------------------------------------------

    def SetDiagnostics(self, request, context):
        if request.everything:
            DIAG.set_everything(request.enabled)
        elif not request.device_id:
            for device_id in list(self._devices):
                DIAG.set_enabled(device_id, request.enabled)
        else:
            DIAG.set_enabled(request.device_id, request.enabled)
        return pb.SetDiagnosticsResponse(ok=True)

    def GetDiagnostics(self, request, context):
        enabled = DIAG.everything or (
            any(DIAG.enabled(d) for d in self._devices)
            if not request.device_id else DIAG.enabled(request.device_id))
        msg = pb.DiagnosticsMessage(enabled=enabled)
        for r in DIAG.since(request.device_id, request.after_seq):
            msg.records.append(pb.DiagnosticRecord(
                seq=r["seq"], timestamp_unix_ms=r["ts"], device_id=r["device_id"],
                transport=r["transport"], direction=r["direction"], text=r["text"],
                detail=r["detail"], endpoint=r["endpoint"], hex=r["hex"]))
        return msg

    # -- 6. The event stream -----------------------------------------------------------

    def StreamEvents(self, request, context):
        beat = threading.Thread(target=self._beat, daemon=True)
        beat.start()
        try:
            while not self._stop.is_set() and context.is_active():
                try:
                    yield self._events.get(timeout=0.25)
                except queue.Empty:
                    continue
        finally:
            self._stop.set()

    def _emit(self, device_id, type_, data):
        self._events.put(pb.DeviceEventMessage(
            device_id=device_id, type=type_, data=data,
            timestamp_unix_ms=int(time.time() * 1000)))

    def _hold(self, hold_id, device_id, reason, until_ms, released=False):
        self._events.put(pb.DeviceEventMessage(
            # The hub routes this frame on the *presence of the `hold` field*, not on this string —
            # but send it anyway: it is what the protocol says, and a hub that later starts reading
            # it costs nothing to have been honest with.
            type="driver.hold",
            timestamp_unix_ms=int(time.time() * 1000),
            hold=pb.DriverHoldMessage(id=hold_id, device_id=device_id, reason=reason,
                                      until_unix_ms=until_ms, released=released)))

    def _beat(self):
        """The heartbeat, and the honest declaration that goes with it.

        `_busy` is the same lock `ExecuteCommand` takes. That makes this beat **not** independent:
        a long command stops it dead. The protocol's answer to that is not to fix the plugin — a
        single thread and one loop is the natural shape in most languages — but to *say so*, so
        the hub never reads this silence as death. Hence `heartbeat_independent=False`.

        The first frame goes out before any wait. That immediacy is load-bearing: it is what lets
        the hub tell a driver too old to send one from a driver that started a moment ago.
        """
        first = True
        while not self._stop.is_set():
            if not first:
                if self._stop.wait(HEARTBEAT_INTERVAL_MS / 1000.0):
                    return
            first = False

            with self._busy:
                pass  # nothing to read off a lamp; the lock is the coupling being declared

            self._events.put(pb.DeviceEventMessage(
                type="driver.heartbeat",
                # Not on the checklist and it should be: the hub takes the age of a beat from
                # *this* field, not from when the frame arrived. Leave it at proto3's zero and the
                # driver reads as last seen in 1970 — permanently silent while beating perfectly.
                timestamp_unix_ms=int(time.time() * 1000),
                runtime=pb.DriverRuntimeMessage(
                    heartbeat_interval_ms=HEARTBEAT_INTERVAL_MS,
                    heartbeat_independent=False,
                )))


def _command_signature(commands):
    return ";".join(f"{c.id}:" + ",".join(p.key for p in c.parameters) for c in commands)


def _address() -> str:
    """Where to listen.

    `REMAESTRO_DRIVER_URL` is the name a plugin author can guess. `ASPNETCORE_URLS` is what older
    hubs set and is still set today, so read both — in that order — and fall back to a fixed port
    so `python3 main.py` on a laptop does something rather than nothing.
    """
    url = (os.environ.get("REMAESTRO_DRIVER_URL")
           or os.environ.get("ASPNETCORE_URLS")
           or "http://127.0.0.1:5199")
    return url.split("//", 1)[-1].split(",")[0].rstrip("/")


def serve() -> int:
    server = grpc.server(futures.ThreadPoolExecutor(max_workers=8))
    driver = LampDriver()
    pb_grpc.add_DriverServicer_to_server(driver, server)
    # h2c: no TLS. The hub dials loopback and owns this process.
    server.add_insecure_port(_address())
    server.start()
    print(f"[lamp] listening on {_address()} (protocol {PROTOCOL_CURRENT})",
          file=sys.stderr, flush=True)
    try:
        server.wait_for_termination()
    except KeyboardInterrupt:
        pass
    driver._stop.set()
    return 0
