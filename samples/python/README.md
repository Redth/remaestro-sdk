# The Example Lamp — a plugin in Python

A working driver written from `proto/driver.proto` with stock `grpc_tools.protoc`, packaged as a signed
`tar.gz`, installed into a hub by URL, and launched by it. Nothing here imports anything of ours; there is
no Python SDK, and this sample is what one would be made of.

It is also the file to read **beside** [`docs/driver-protocol.md`](../../docs/driver-protocol.md). That page
is the contract. This is what following it actually costs.

---

## Run it on your laptop

```sh
python3 -m venv .venv && .venv/bin/pip install grpcio grpcio-tools
.venv/bin/python -m grpc_tools.protoc -I ../../proto --python_out=. --grpc_python_out=. ../../proto/driver.proto
REMAESTRO_DRIVER_URL=http://127.0.0.1:5199 python3 main.py
```

It answers `Describe` and everything else on that port. There is no hardware: the lamp is simulated, and
the "wire" exists so the sample can show what a captured diagnostic contains.

## Build a package

```sh
./package.sh linux-arm64                       # dist/com.example.lamp-1.0.0-linux-arm64.tar.gz
./package.sh linux-arm64 my-publisher-key.pem  # …and the signature and public key to install it with
```

That prints the four values install-by-URL needs: the URL you host it at, the SHA-256, the signature, and
your public key. There is no registry in that path, deliberately.

---

## The seven things that cost more than the checklist said

Every one of these was hit building this sample, in this order.

**1. The generated stubs cannot live in a package directory.** `grpc_tools.protoc` writes
`import driver_pb2` — a top-level import — into `driver_pb2_grpc.py`. Put the two files in `lamp/` and the
first import is `ModuleNotFoundError: No module named 'driver_pb2'`. They live at the package root here, and
`package.sh` generates them there.

**2. `python3 main.py`, not `python3 -m lamp`.** Python puts *the script's directory* on `sys.path`, and
does not put the working directory there. A script entrypoint therefore finds its own code no matter where
it was started from, which is what makes this sample work on a hub that predates the working directory being
set at all. (The claim in the audit that a Python plugin needs
`sys.path.insert(0, dirname(__file__))` to import its own stubs is not true for a script entrypoint —
measured; `sys.path[0]` is already that directory.)

**3. `"runtime": "python3"` gets you an interpreter and nothing else.** `grpcio` is on neither the
appliance nor in the hub container, and it is a compiled wheel. So a Python plugin vendors it — which makes
`python3` **exactly as architecture-bound as `native`**, and about three times the size:

| | unpacked | archive |
|---|---|---|
| this plugin, `_vendor/` included | 42 MB | 12.6 MB |
| a .NET plugin, self-contained/single-file/trimmed | 15.5 MB | — |

The data partition on the appliance is 3.0 GiB and does not grow.

**4. Every config value is a string.** `map<string, string>`, no types on the wire. `type: "number"` is a
hint to the console's input box; parsing, ranging and defaulting are yours, every time.

**5. `capabilities` is all-or-nothing.** A non-empty list is authoritative and case-sensitive, and the three
legacy `supports_*` booleans are then ignored completely. Declare one capability and forget to re-declare
navigation and you have silently turned navigation off. The C# SDK folds the booleans in for you. Nothing
does that here.

**6. `timestamp_unix_ms` is not optional on a heartbeat, and no checklist says so.** The hub takes the age
of a beat from that field, not from when the frame arrived. Leave it at proto3's zero and your plugin reads
as last seen in 1970 — reported as silent, permanently, while beating perfectly.

**7. Redacting a secret means redacting its *bytes*.** See below.

---

## What this sample is really for

### `heartbeat_independent = false`, demonstrated rather than described

This plugin's heartbeat takes the same lock `ExecuteCommand` does. That makes it **not** independent: the
`lamp.pair` command stops the beat for as long as it runs. The protocol's answer is not to fix the plugin —
one thread and one loop is the natural shape in most languages, and it is the population this whole seam
exists to admit — but to declare it, so the hub never reads that silence as death.

Measured against a real hub, with a 40-second pairing wait and a 5-second declared interval:

```
runtime interval=00:00:05 independent=False
hold while pairing: id=lamp1:pair reason='waiting for the button on the lamp'
  pulse=Quiet  note while busy -> 'waiting: waiting for the button on the lamp'
  with the hold set aside:  pulse=Quiet  note=''
  had it claimed independence: note='no heartbeat for 35s'
```

The last line is the point. The same plugin, having claimed independence it did not have, is reported as
having stopped — while working perfectly.

### The secret, and why this sample logs one on purpose

The lamp sends `LOGIN <host> <password>` on connect, and records it. That is the one obligation a C# author
gets invisibly and every other author gets nothing at all for.

**This sample got it wrong first, and the way it got it wrong is worth more than the fix.** The redaction
was ported faithfully from the C# SDK: mask the text, pass the bytes through. The result, through the hub's
real capture:

```
tx 'LOGIN 10.0.0.9 ***' hex=4C4F47494E2031302E302E302E39206C346D702D70726F62652D70617373776F7264
                                                  └─ "l4mp-probe-password", in clear
```

Masked in the readable column, printed in full one column to the right — and the hub writes every field
verbatim into the `trace.json` inside a support bundle somebody then emails. The .NET SDK had the same bug,
found the same way, and its own guard had enumerated the record's string fields by hand and stopped one
short of `hex`.

So `lamp/diag.py` blots the *bytes* before rendering them, and redacts every string field rather than the
two obvious ones. Read that file before you write your own.

### `lamp.pair` is invisible to the assistant, on purpose

A command id the hub's `CommandVocabulary` does not know resolves to no capability at all. It still appears
on the device's own toolbox under its label, and it is invisible to the assistant, to remotes, to activities
and to physical-remote routing. `light.set_level` and `power.on` are vocabulary ids and behave normally;
`lamp.pair` is not, and does not. This is the documented design rather than a defect, and *"my button does
nothing in the assistant"* is otherwise unexplainable.
