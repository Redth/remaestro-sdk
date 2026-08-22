# Hello World — a reMaestro plugin

A rubber duck. It is not there, and that is the point.

**If you are writing your first plugin, this is the directory to copy.** It is the smallest thing that
still proves the whole path, and it is deliberately the opposite end from [`../go`](../go), which is the
same language exercising every optional corner of the contract. This one is the floor: one device, two
commands, one event, no capabilities.

It is in Go because `#427` proved that path end to end first and left a working mould. Nothing about the
plugin contract is Go-specific, and nothing here imports anything of reMaestro's — the only reMaestro
artefact in the module is generated from [`../../proto/driver.proto`](../../proto/driver.proto) by stock
`protoc` on every build, and is not committed.

Install it and a device appears. Turn it on and it says hello every ten seconds, and counts. That count is
the whole feature, and it is deliberately the most boring thing in the product: **a number that moves on
its own**, so "did the plugin install, launch, connect, and get read?" has an answer you can look at rather
than one you have to infer.

Set the greeting to whatever you like. It comes back out in the device's state, which is how you know your
value went all the way through — the console, the hub, the gRPC seam, this process, and back.

It talks to no hardware, reaches no network, holds no credentials and writes no files.

---

## "Does not do much" is a constraint, not an absence

This is the one thing worth getting right about a hello world, and it is easy to get wrong in the charming
direction. **A plugin that does *nothing* cannot tell installed-and-working apart from installed-and-broken**
— which is the only question it exists to answer. A device card that merely appears proves that a
descriptor was cached; it proves nothing about the process still being alive, the event stream still being
read, or a value you typed reaching the far side.

So there is exactly one effect, and it has two halves that answer two different questions:

| | answers |
|---|---|
| `power.on` says hello **immediately**, and returns the count | did my command reach the far side of the gRPC call, *now* |
| an awake duck goes on saying hello **on a timer** | is this process still alive and is anybody still reading its stream |

The second cannot be produced by pressing a button, which is exactly why it is there. `#427` measured what
a broken event stream looks like from outside: **every unary call still answers and the liveness reading
stays green.** A probe that only presses buttons cannot see that; a count that stops moving can.

---

## Build it

You need Go, `protoc`, and the two protobuf plugins:

```sh
go install google.golang.org/protobuf/cmd/protoc-gen-go@latest
go install google.golang.org/grpc/cmd/protoc-gen-go-grpc@latest

./package.sh linux-arm64                       # the appliance
./package.sh linux-x64 publisher.pem           # the cloud, signed
```

Out comes one `.tar.gz` per architecture, plus the SHA-256, the signature and the public key — the three
values a hub needs to install it from a URL with no registry anywhere in the path.

Run it on your laptop with no hub at all:

```sh
REMAESTRO_DRIVER_URL=http://127.0.0.1:5199 go run .
```

### Then check it somewhere that is not your laptop

```sh
./verify.sh linux-arm64        # unpacks the archive into a FROM scratch container and drives it
```

**A plugin that has only ever run on the machine that compiled it has not been shown to run on a hub.** A
hub is arm64 Linux on an appliance with a read-only root filesystem, and amd64 Linux in the cloud. The
container `verify.sh` builds has **one file in it** — no libc, no shell, no `/etc`, no CA bundle — which is
the only way to check what `CGO_ENABLED=0` claims short of a real box.

---

## The traps, in the order they bite

Every one of these is measured rather than reasoned, most of them by `#427`, which walked this path first
in Go and wrote up ten of them. If you read one thing before writing a plugin, read
`docs/plugins/phase-3-a-stranger-in-go.md` in the hub's tree.

**1. `StreamEvents` must never return, and returning is silent.** It is opened once and read until the
*hub* cancels. Returning from it — cleanly, at the end of a loop, on a timer — takes every device event and
every hold off the bus for the life of the process, while every unary call keeps answering and the
liveness reading stays green. Nothing reconnects and nothing is logged. This is the natural shape in most
languages (a `for` over a channel that closes, a generator that runs out) and it is the sharpest trap in
the contract.

**2. The address is a URL, not a `host:port`, and its scheme is load-bearing.** `REMAESTRO_DRIVER_URL` is
`http://127.0.0.1:53412`; `net.Listen` wants the second half of that. The `http://` says **cleartext h2c**,
so a plugin that serves TLS because that is its framework's default cannot be talked to at all.

**3. Nothing ever asks you to stop.** A hub ends a driver with `Process.Kill(entireProcessTree: true)` —
SIGKILL. No rpc, no signal, no grace period. A lock file cleaned up on exit is never cleaned up and a
"graceful shutdown" branch is dead code. Anything you want to survive has to be durable at the moment it is
true.

**4. Exactly one rpc is required, and it is `Describe`.** Everything else, including `StreamEvents`, is
called on demand. `Describe` must answer cold, with nothing set up and nothing connected, within about ten
seconds — and your process is launched, asked it once, and killed, *before any device exists*, so any
expensive startup is paid at least twice and the first time is thrown away.

**5. `GetState` replaces; it does not merge.** Send the whole map every time. A key you leave out is a key
that stops existing. The two fields beside `state` on the same message have the *opposite* rule and say so
in their own comments, which is what makes this easy to get backwards.

**6. Nothing hub-side validates your config against the schema you declared.** Not `required`, not a range,
not membership in `options`, not `default_value`. The console's form is the only thing that asks and the
HTTP API takes an arbitrary dictionary. **Every driver validates its own config, every time**, and refuses
in `CreateDeviceResponse.error`.

**7. A device's refusal and a driver's failure are different answers.** `ok: false` with a sentence is the
device declining, and the person is shown your words. A gRPC status is read as *the driver* having failed,
and a `DEADLINE_EXCEEDED` is rendered as the hub having given up — which reads, to somebody standing in a
room, as a completely different fact.

**8. `traits` is a closed vocabulary of thirteen, and the contract publishes three of them and an
ellipsis.** An unknown one is accepted, labelled as itself, and does nothing anywhere — no error, no log
line. `#427`'s sample said `"speaker"` and worked perfectly while being absent from every grouping the
trait exists to drive.

**9. Your command ids should come from the hub's `CommandVocabulary`.** An invented one works — it appears
on the device's own toolbox — and is invisible to the assistant, to remotes and to activity generation.
Silently. This one uses `power.on` and `power.off`, which resolve.

**10. `chmod +x` before `tar`.** An archive without the execute bit **installs successfully** and then
fails at `Process.Start` with a message about permissions and nothing at all about a tar.

**11. `rid` is a .NET runtime identifier**, even here. `linux-x64`, not `linux/amd64`.

**12. `"abi": "1"` — the string — reads as 0 on a hub, silently, with the install-time compatibility check
switched off.** A registry catches that; install-by-URL does not. It is an integer.

---

## A plugin id and a type id are different names, and only one of them is protected

`plugin.json` says `io.github.redth.helloworld`. The driver says `redth-hello-duck`. They are used for
different things and they have very different protection:

- **An `id` collision is refused, loudly**, by the registry. Ids are allocated once and never reassigned.
- **A `type_id` collision is not checked anywhere at all** — not by the registry, not by the hub, not at
  install. Two plugins claiming `hello-duck` produce one device type, and the person who installed the
  second is never told which of them they got.

So make your `type_id` obviously, unmistakably yours. Here it is stamped at build time by `package.sh`,
because this one source is published under two publisher identities; hard-coding yours is the ordinary
thing to do.

---

## What is reproducible, and why that is worth the trouble

`package.sh` pins the tar's owner, group and every timestamp, and tells gzip not to write a name or an
mtime of its own. Two builds of the same source two seconds apart are **byte-identical**, which matters
because once a version is published **the bytes at its URL may never change** — so being able to rebuild
them and get the same SHA-256 is the difference between "here is the source" and "here is the source,
check me".

**It has one consequence nobody would guess, and it cost a green run.** BuildKit decides whether it can
reuse a cached snapshot of a build context from the files' *metadata* — name, size, mode, mtime — and not
from their contents. Two builds of this plugin under two publisher identities differ only in a
linker-stamped string, so they are the same size, the same mode and (because of the pinning) the same
mtime. BuildKit read the second as the first and the container served the **other identity's binary**, and
`verify.sh` printed VERIFIED. Measured: **`--no-cache` does not fix it** — that flag invalidates the
instruction cache and not the context snapshot. Moving the mtime by one second fixed it immediately.

Hence both halves of the fix, and the second is the general one: `verify.sh` builds from a fresh context
with fresh timestamps, **and** `cmd/verify --expect-type` asserts that the thing answering is the thing
that was meant to be verified. A verifier that never says which artefact it verified cannot tell you it
verified the wrong one.

---

## Layout

| | |
|---|---|
| `main.go` | the address, the listener, and why there is no shutdown handler |
| `hello/driver.go` | the whole driver — `Describe`, four device rpcs, the event stream |
| `cmd/verify/` | a hub-shaped client, for `verify.sh` |
| `package.sh` | codegen from `../../proto/driver.proto`, build, manifest, tar, sign |
| `verify.sh` | run the packaged archive on the architecture it claims |

`gen/` is generated on every build and is not committed: a checked-in copy of a generated file is a second
copy of the contract, and it is the one that goes stale.

MIT. Copy it.
