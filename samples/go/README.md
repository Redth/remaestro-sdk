# The Example Metronome — a plugin in Go

A working driver written from `proto/driver.proto` with stock `protoc`, cross-compiled to one static
binary per architecture, packaged as a signed `tar.gz`, installed into a hub by URL, and launched by it.
Nothing here imports anything of ours; there is no Go SDK, and this sample is what one would be made of.

It is the **second** non-.NET plugin. [`samples/python/`](../python/) is the first, and the two are
deliberately the other shape from each other in the places the protocol asks about:

| | `samples/python` — the Example Lamp | `samples/go` — the Example Metronome |
|---|---|---|
| `runtime` | `python3` — an interpreter, plus a vendored `grpcio` | `native` — one static binary, nothing else |
| archive / unpacked | 12.6 MB / 42 MB | **3.9 MB / 9.4 MB** (linux-arm64) |
| `heartbeat_independent` | `false`, and demonstrates why that is a real answer | `true`, and demonstrates it under a blocking command |
| optional rpcs | answers `ListInputs` with a real `Availability` | has never heard of it — `UNIMPLEMENTED`, for free |
| a hold | on a pairing wait | on a calibration wait |
| verified on | the machine that built it | **linux/arm64 and linux/amd64, in a `FROM scratch` container with a read-only root** |

---

## Run it on your laptop

```sh
protoc -I ../../proto \
  --go_out=gen      --go_opt=module=example.com/metronome/gen \
  --go_opt=Mdriver.proto=example.com/metronome/gen/maestro \
  --go-grpc_out=gen --go-grpc_opt=module=example.com/metronome/gen \
  --go-grpc_opt=Mdriver.proto=example.com/metronome/gen/maestro \
  driver.proto

REMAESTRO_DRIVER_URL=http://127.0.0.1:5199 go run .
```

It answers `Describe` and everything else on that port. There is no hardware: the metronome is simulated,
and the "wire" exists so the sample can show what a captured diagnostic contains.

`go run ./cmd/verify http://127.0.0.1:5199` drives it the way the hub does and prints what came back.

## Build a package

```sh
./package.sh linux-arm64                       # dist/com.example.metronome-1.0.0-linux-arm64.tar.gz
./package.sh linux-arm64 my-publisher-key.pem  # …and the signature and public key to install it with
```

That prints the four values install-by-URL needs: the URL you host it at, the SHA-256, the signature, and
your public key. There is no registry in that path, deliberately.

## Prove it runs where the hub runs

```sh
./verify.sh linux-arm64      # the appliance's shape
./verify.sh linux-x64        # the cloud's shape
```

Needs Docker. It builds a `FROM scratch` image containing nothing but the one binary `package.sh` produced,
runs it with `--read-only`, and points `cmd/verify` at it from outside. A binary that starts in an image
with no libc, no shell and no `/etc` is not depending on anything the appliance might not have.

**This is the step that a proof usually skips, and skipping it is what makes a proof worth less than it
looks.** The hub is arm64 Linux on an appliance and amd64 Linux in a container; neither is anybody's laptop,
and "it worked when I ran it" is a statement about the machine that compiled it.

---

## The ten things that cost more than the checklist said

Every one of these was hit building this sample, in this order. Seven of them are not in
[`docs/driver-protocol.md`](../../docs/driver-protocol.md) §5 as it stood, and the ones that are now are
there because of this list.

**1. `protoc-gen-go` used to refuse to run at all.** The file carried `option csharp_namespace` and no
`option go_package`, and Go treats a missing import path as *fatal* where every other generator defaults
something:

```
protoc-gen-go: unable to determine Go import path for "driver.proto"
--go_out: protoc-gen-go: Plugin failed with status code 1.
```

Nothing generated, at step one, from a file whose own preamble says it is the whole contract *in any
language*. The proto now declares `go_package`, so the stock one-line command works. The `M…` flags in
`package.sh` are still there and are **layout** rather than a workaround: they put the stubs inside *this*
module. Any plugin with a module path of its own needs them whatever the proto says.

**2. The two environment variables carry a URL, not a `host:port`.** `REMAESTRO_DRIVER_URL` and
`ASPNETCORE_URLS` are both set to the same value, `http://127.0.0.1:53412`, because the name they were
modelled on is ASP.NET Core's. `net.Listen` — and every other language's listener — wants the other form.
The `http://` is also the only place it is said that the hub speaks **cleartext h2c**: serve TLS and the
hub cannot talk to you.

**3. Exactly one rpc is required for your plugin to launch, be catalogued and be offered: `Describe`.**
Everything else can answer `UNIMPLEMENTED`, including `StreamEvents`. Go's generated
`UnimplementedDriverServer` gives you that for free, which is the protocol's rule 3 arriving as a language
default rather than as a decision you have to make.

**4. Your process is started, asked one question, and killed — before any device exists.** The first time
a hub sees your plugin it runs a first-run introspection sweep: launch, `Describe`, record, `Process.Kill`.
Then it launches you *again* when something actually wants a device. Measured — two pids, seconds apart, on
the first boot after install. So `Describe` must be cheap and must not depend on anything you did in
`CreateDevice`, and any expensive startup you do is paid at least twice.

**5. You never get asked to stop.** Measured: a handler on `SIGTERM`, `SIGINT`, `SIGHUP` and `SIGQUIT`
across a full install-launch-drive-stop cycle **fired for neither process**. The hub ends a driver with
`Process.Kill(entireProcessTree: true)` — `SIGKILL` — so there is no graceful shutdown, no flush, and no
last write. Anything you want to survive has to be durable at the moment it is true.

**6. `StreamEvents` must never return, and returning is silent.** Sabotaged and measured: end the stream
after the first frame and the plugin goes on looking **completely healthy** — `GetState` answers, commands
succeed, diagnostics work, and the liveness reading is a green `00:00:01`. What is gone is everything the
stream carries: `EventBus saw 0 events` where it had seen 28, and a `driver.hold` published during a
twelve-second command arrived as `id=''`. **Nothing reconnects and nothing is logged.** The natural shape
in most languages — a `for range` over a channel that closes, a generator that runs out — returns.

**7. `GetState` must return the *whole* state map, every time.** The hub does
`rt.Reported = s.State.ToDictionary(…)` — a wholesale replacement, not a merge — so a key you leave out is
a key that stops existing. This is easy to get backwards because the two fields immediately beside it in
the same message, `commands_changed` and `traits_changed`, are explicitly *only when it changes*. One
message, two opposite rules, and only one of them is written down.

**8. Nothing hub-side validates a config value against the schema you declared.** Not `required`, not a
range, not membership in `options`. The console's form is the only thing that asks, and `POST /api/devices`
takes an arbitrary dictionary. Measured: `bpm: "9000"` reaches `CreateDevice` untouched. Check your own,
every time, and refuse in `CreateDeviceResponse.error` rather than by throwing.

**9. `ok: false` and a gRPC error are different facts and the hub keeps them apart.** A device that
declines is `ExecuteCommandResponse{ ok: false, error: "…" }` and the person is shown your sentence. A gRPC
status is read as *the driver* having failed, and a `DEADLINE_EXCEEDED` in particular becomes "the driver
didn't answer within 60s". Use the response for anything the device said. The hub's deadline is on the
call's context, which is where a plugin in any language can read it — `Describe` 10 s, an ordinary command
60 s, a pairing command 150 s, `GetEpg` 5 min. None of those numbers is on the wire as a field.

**10. `traits` is a closed vocabulary that the contract does not contain.** The proto says
`repeated string traits = 11; // what this type is for (ir.emitter, bridge, display…)` — three of the
thirteen, and an ellipsis. The list lives in the hub's own `DeviceTraits.cs`, which you cannot see, and an
unknown trait is **accepted, labelled as itself, and does nothing**: no grouping, no icon, no activity
generation. This sample said `"speaker"` until somebody went and looked; the word it wanted was `"audio"`.
There is no error and no log line, so the only way to find out is to have the hub's source open.

---

## What this sample is really for

### `heartbeat_independent = true`, demonstrated rather than described

The lamp declares `false` and shows why that is a real answer. This one declares `true` and shows what the
declaration is worth when it is honest: the beat is its own goroutine writing into a channel the stream
drains, so `metronome.calibrate` blocking for twelve seconds does not stop it. Measured through a real hub,
with the command outstanding:

```
[11] runtime interval=00:00:05 independent=True silence=00:00:01.51
[12] hold while calibrating: id=metro1:calibrate reason='counting a reference minute against the studio clock'
     silence with a command outstanding: 00:00:00.52
```

The silence staying small *while a command is blocked* is the claim being true. Declaring `true` and then
beating from the command loop is the one shape the protocol cannot catch you at, and it is the shape that
gets a working plugin reported as dead.

### `timestamp_unix_ms`, measured rather than warned about

The checklist says a zero here reads as 1970. Sabotaged, through the hub, with the plugin beating perfectly
every five seconds:

```
[11] runtime interval=00:00:05 independent=True silence=20687.03:20:10
```

Fifty-six years of silence, from a process that had been alive for two seconds.

### The secret, and why this sample logs one on purpose

`CreateDevice` sends `HELLO <name> <studio_token>` on the fake wire and records it. Redaction is the one
obligation a C# author gets invisibly — `DriverHost` registers declared secrets for it — and every other
author gets nothing at all for. There is no wire-level equivalent, because the hub cannot know which of
your bytes are a password.

`metronome/diag.go` blots the **bytes** and then renders, rather than masking the text and passing the
payload through. A `DiagnosticRecord` carries the same moment twice, in `text` and in `hex`, and `endpoint`
is a third place a credential can sit; the .NET SDK shipped exactly that bug and `samples/python` inherited
it by being ported faithfully. Through the hub's real capture:

```
[15] diagnostics records=2
     tx 'HELLO Bench ***' hex=48454C4C4F2042656E6368202A2A2A
     LEAKED = 0
```

### A plugin id and a type id are different names, with different rules

`plugin.json` says `com.example.metronome`. The descriptor says `example-metronome`. They are not the same
name and they are not checked the same way:

- **the plugin id** is matched case-insensitively, must be reverse-DNS to pass the registry, and is refused
  outright if it collides with a driver the hub ships;
- **the type id** is what actually decides which driver serves a device. Nothing validates it — not its
  characters, not its emptiness, and **not a collision with a shipped driver's type id**, which is an
  ordinal dictionary write that the last driver to start wins.

Pick something that is obviously yours.

### `metronome.tempo` is invisible to the assistant, on purpose

A command id the hub's `CommandVocabulary` does not know resolves to no capability at all. It appears on
the device's own toolbox under its label and is invisible to the assistant, to remotes, to activities and
to physical-remote routing. `power.on` and `power.off` are vocabulary ids and behave normally. This is the
documented design rather than a defect, and *"my button does nothing in the assistant"* is otherwise
unexplainable.
