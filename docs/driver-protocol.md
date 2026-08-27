# The driver protocol

**What the hub and a plugin promise each other, and what happens when they disagree.**

`proto/driver.proto` is the contract. This page is the part that needs more than a field comment: the
version negotiation, the capability declaration, and the three things a heartbeat has to say before anyone
may read anything into silence.

Everything here is **protocol**, not .NET. The C# SDK fills all of it in for you; a plugin generated
straight from the proto has to do it by hand, and the sections below are written for that reader.

---

## 1. The compatibility promise

Four rules govern changes to `driver.proto`. They are in the file itself, at the top, and they are repeated
here because they are the reason a plugin published today still runs next year.

1. **A field number is never reused and never renamed.** Not even for a field nobody appears to send — the
   hub hashes numbers *and* names to decide whether a cached descriptor is still readable, "because a
   rename is a different meaning even at the same number".
2. **Fields are only added, at fresh numbers.** A removed field is `reserved`, never recycled.
3. **RPCs are only added.** A driver that has never heard of one answers `UNIMPLEMENTED`, and the hub reads
   that as "this plugin is older than this feature" rather than as a fault.
4. **The meaning of an existing field is never narrowed.** Widening — one more allowed string in a list
   whose unknown values are already ignored — is the only change that is safe in place.

Anything those four cannot cover is a **new protocol version**.

---

## 2. Version negotiation, in both directions

### 2.1 Why both

Every guard that existed before this protected *the hub from an old driver*: the descriptor contract hash,
the binary stamp, `UNIMPLEMENTED` tolerance. There was nothing at all in the other direction. A plugin built
against a newer contract than the hub understands would fill fields that proto3 silently drops — no error,
no log, and a feature that simply does not happen.

So negotiation is two integers going opposite ways on the one call that already exists.

| Where | Field | Meaning |
|---|---|---|
| `DescribeRequest` | `hub_protocol` | the highest `Protocol` value the **hub** knows; 0 = a hub older than negotiation |
| `DriverDescriptor` | `protocol_version` | the highest `Protocol` value the **driver** was built against |
| `DriverDescriptor` | `min_hub_protocol` | *optional.* The oldest hub this driver will work against |

`Protocol` is an enum in the proto and **the current version is its highest value**. That is the
definition rather than a convention, so a generated SDK can compute it instead of carrying a constant that
drifts away from the file it came from.

### 2.2 One integer, two places, on purpose

A packaged plugin carries a single integer called `abi`. **It is the same number as `protocol_version`.**
Two integers that could disagree would mean a plugin that installs and will not run, or, worse, one that
runs having claimed it would not. The manifest's job is to refuse an install; the descriptor's job is to
refuse a launch; they answer the same question at two moments.

**Two files carry it, and they are both called `plugin.json`.** The one inside the archive — §6, and the
only one a hub ever reads — and the registry submission, which lists every version and never comes near a
box. The registry's CI refuses an archive whose `abi` is not the one its submission published. Where this
page says "the manifest" without qualifying it, it means the one in §6.

The manifest carries no floor. That is not an omission and does not need fixing: **an undeclared floor
reads as `abi`**, which is exactly what unset `min_hub_protocol` means here. A floor can therefore only ever
be *added* later, and adding one can only ever **widen** the set of hubs a plugin runs on — so nothing
already published becomes wrong.

### 2.3 Unset is not zero

`min_hub_protocol` has **explicit presence** (`optional`), and the difference is the whole story:

- **unset** → the floor is `protocol_version`. "I need a hub at least as new as the contract I was built
  from." This is the safe reading and it is the default.
- **set** → the floor is what you said. Use it once you know your plugin never touches anything newer than
  some earlier version. That widens what you run on.

A floor may only move down over the life of a plugin. Raising it would break hubs that were already
running you, which is the thing this whole mechanism exists to prevent.

### 2.4 Who refuses

**The hub refuses; the driver never does.**

A driver receives `hub_protocol` and may use it to leave out fields the hub cannot read, or to log a line
its author will understand. It must still **answer `Describe`**. A driver that throws instead is a driver
the hub cannot name in the message it puts in front of a person — and the person is the only one who can
do anything about a mismatch.

So: one party has a screen, and that is the party that says no.

---

## 3. Capabilities: declaring instead of being discovered

### 3.1 The problem, stated exactly

Before this field, the hub learned what a driver implemented **by calling and reading the answer** — and
the answer was a `bool supported` that meant three different things:

- the hub asked about a device this driver does not hold;
- the device is simply not that kind of thing;
- reaching the hardware **threw**.

Six responses carried that boolean and five of them collapsed all three into `false`. The sixth,
`ListBridgedDevices`, answers `supported = true` on a throw, with the comment *"an unreachable bridge
shouldn't read as 'this isn't a bridge'"*. **That comment was the bug report for the other five.**

Two fields fix it, and they fix different halves. `capabilities` fixes the *question* — the structural
answer is knowable before anything is called. `Availability` fixes the *answer* — when something is called
and does not work, the caller can tell which kind of "no" it got.

### 3.2 `DriverDescriptor.capabilities`

A repeated string. The vocabulary is exactly the set of optional behaviours the hub asks about:

| String | The driver's devices answer |
|---|---|
| `inputs` | `ListInputs` with a real source list |
| `epg` | `GetEpg` — supersedes `supports_epg` |
| `apps` | `ListApps` |
| `device-remotes` | `GetRemote` — supersedes `supports_device_remotes` |
| `bridge` | `ListBridgedDevices` |
| `options` | `ListOptions` for a config field's `options_key` |
| `navigation` | `Browse`/`GetNode`/`SearchNodes`/`InvokeItem` — supersedes `supports_navigation` |
| `diagnostics` | `SetDiagnostics`/`GetDiagnostics`, with real captured traffic |
| `settings` | `ApplyPluginSettings` — **the driver itself**, not its devices; see §3.2.1 |

**An unknown string is ignored rather than refused.** That is rule 4 above, and it is what lets a later hub
name a capability this plugin has never heard of without breaking it.

**The reading rule.** An empty list is *not* "this driver does nothing" — it is a driver older than the
field, and the hub falls back to the three booleans. A non-empty list is authoritative, so a driver that
sends one **must include everything it does**, the three boolean-covered capabilities included. The C# SDK
folds those in for you, so a driver that sets `SupportsNavigation` and then declares one unrelated
capability cannot accidentally un-declare its navigation. If you are generating from the proto directly,
that fold is yours to do.

**Declare what you implement.** Declaring a capability whose RPC is missing is worse than declaring
nothing: the hub will call it, and the navigation path in particular has no exception handling at all, so
the user sees an error where an undeclared driver would have degraded quietly.

The three booleans keep being sent. They are not deprecated on the wire and they never will be — rule 1.

#### 3.2.1 `settings` is the one row about the driver rather than its devices

Every other string in that table is a promise about what a *device* will answer. `settings` is a promise
about the plugin: that it implements `ApplyPluginSettings` for the fields it declared in
`DriverDescriptor.settings_schema`. A plugin that makes no devices at all can still have something worth
configuring, which is the whole reason plugin settings exist.

**And unlike the rest of the table, declaring the schema and not the capability is a legitimate
arrangement rather than the mistake the paragraph above warns about.** The hub reads it as *keep my
settings and don't bother telling me*: the form draws on the plugin's page, the values are stored against
the person who typed them, the plugin's page says this build cannot be told about them, and nothing is
pushed. Declare `settings` when you want to be told; a driver that declares it and answers `UNIMPLEMENTED`
gets exactly the same treatment, so the cost of getting this one wrong is a line on a page rather than an
error in somebody's face.

#### One optional RPC is deliberately not in that table: `InvokeAssistantTool`

There is no `assistant-tools` capability string, and there should not be. **The declaration is the
capability**: a driver that fills `DriverDescriptor.assistant_tools` is saying it answers this call, and one
that leaves it empty is never asked. A second string would be a second way of saying the same thing, and the
two would eventually disagree.

The failure mode is still the one the paragraph above describes — declare what you implement — but it is a
soft one here rather than an error in somebody's face. A driver that declares tools and answers
`UNIMPLEMENTED` (which is what every driver built before this RPC existed does, and what the C# SDK sends
when you do not override `RunAssistantToolAsync`) gets a sentence in front of the model saying the plugin
declares the tool and this build of it cannot run one. That is deliberately the *same* answer for both,
because it is the same fact — and it is deliberately **not** the answer a tool nobody declared gets, because
"unsupported" and "broken" must not look alike.

**What the hub settles before you are called**, so you do not have to:

- the tool exists, you declared it, and the assistant asking is one of the `surfaces` you named. A tool you
  offered only on `console` is refused *at the call* when a model names it on the voice path — the plugin is
  not started and no RPC is made — so you never receive a call for a surface you did not opt into;
- `args` has been narrowed to the keys your tool declared, so it is a subset of your own `parameters` and
  never a superset. A key you declared may still be absent: a model is not obliged to fill an optional one,
  and the hub does not fill one in for it.

**And what you owe.** The call is made inside a turn, with somebody who has just spoken waiting for a reply,
so it carries a short deadline rather than the ordinary one-minute budget. When it passes, your
`CancellationToken` is cancelled and the hub tells the model the plugin did not answer in time — as a fact
about the hub having stopped waiting, never as an answer from you. Your `text` is prose a model reads and
nothing parses it; the hub bounds its length and truncates with a line saying so rather than refusing,
because half an answer beats an error to somebody standing in a room.

### 3.3 `Availability`

Carried alongside `supported` on all six responses.

| Value | Means | The hub may |
|---|---|---|
| `AVAILABILITY_UNSPECIFIED` | nothing said | read `supported` alone, as before |
| `AVAILABILITY_ANSWERED` | asked and answered — an empty list is a real, empty answer | believe it |
| `AVAILABILITY_UNSUPPORTED` | not that kind of device, permanently | cache it and stop asking |
| `AVAILABILITY_UNAVAILABLE` | it is, and it could not answer just now | ask again later; **never** conclude "this device has none" |
| `AVAILABILITY_UNKNOWN_DEVICE` | this driver does not hold that id at all | log it — the two ends disagree about what exists |

**`supported` keeps its old meaning and is still sent.** That is what made this addable: a reader that has
never heard of the enum behaves exactly as it always did. When both are present, `availability` wins,
because it is the one that can tell the three cases apart.

---

## 4. The heartbeat

A driver emits a frame on `StreamEvents` with `type == "driver.heartbeat"`, an empty `device_id`, and a
`DriverRuntimeMessage` in `runtime`. **The first frame goes out immediately**, before any wait — that is
what lets the hub tell a driver too old to answer from a new one that has not ticked yet.

**Set `timestamp_unix_ms` on the frame.** This is the field most easily missed and it is not optional in
any useful sense: the hub takes the *age* of a beat from what the frame says, not from when it arrived.
Left at proto3's zero, your driver reads as last seen in 1970 — reported as having stopped, permanently,
while beating perfectly. The C# SDK fills it in and no generator will.

**The frame is routed by the field it carries, not by its type.** `runtime` present makes it a heartbeat;
`hold` present makes it a hold. Send the `type` string as well — it is what the protocol says and a later
hub may read it — but a frame that carries the type and forgets the submessage is not a heartbeat that got
lost. It is an ordinary device event with an empty `device_id`, and it would land in front of every rule in
the house at whatever rate you beat. Recent hubs drop that frame and say so once; older ones do not.

Three things had to be said before anyone could read anything into silence. All three are on the frame
rather than the descriptor, deliberately: a descriptor is *cached*, keyed on a stamp taken from one
entrypoint file, and a plugin whose entrypoint is a launcher script can change everything behind it without
that stamp moving. A promise served from a stale cache is worse than no promise. These cannot go stale, and
they cost a handful of bytes on a frame that was already going out.

### 4.1 Declare your interval — `heartbeat_interval_ms`

Without it, every hub-side threshold is a constant chosen against **one SDK's default**. The hub's is 30
seconds, which is fifteen of the .NET SDK's two-second intervals — and simply wrong for a plugin that beats
once a minute. That plugin gets reported as having stopped, thirty seconds into working perfectly.

Unset means "did not say", and the hub keeps its own default. **Set it.** A plugin that beats slowly and
does not declare it will be described, accurately and uselessly, as having stopped.

The hub's rule: silence is worth a sentence after `max(30s, 3 × your declared interval)`. Three missed
beats, with a floor so a fast beater is not reported for a momentary stall.

### 4.2 Declare whether the beat is independent — `heartbeat_independent`

**The protocol asks rather than requires, and the reasoning matters more than the field does.**

Requiring independence would be one sentence to write and nothing to enforce. A single-threaded plugin —
the natural shape in most languages, and precisely the population this seam exists to admit — would violate
it silently, and the hub would read "busy" as "stopped" with no way to know. *A rule nobody can check is
worse than a fact on the wire*, because it licenses the reader to trust something false.

So:

> **Independence is a SHOULD. Declaring the truth is a MUST.**

Emit the beat from a task, thread or timer that is not the one servicing `ExecuteCommand`, and set this
`true`. If you cannot — one thread, one loop — set it **`false`**, and the hub will never read anything into
your silence at all. `false` costs you nothing except a liveness signal you were never able to give in the
first place.

**Unset is a third answer, not a synonym for `false`.** A plugin that never sets it made no promise either
way, and the hub reads its silence exactly as it did before this field existed — collapsing the two would
take a working signal away from every driver that has not been rebuilt. `false` is a *positive* declaration,
and it is the one that buys you the protection. Saying nothing buys you nothing.

Anything built on the C# `DriverHost` is independent, and that is measured rather than assumed: with a
device stuck for ever inside `ExecuteAsync`, the frames kept arriving at their normal cadence throughout,
because the beat loop is its own task writing into the event channel and the event channel is drained on a
different HTTP/2 stream from the one a command blocks.

### 4.3 Declare a hold — `DriverHoldMessage`

The hub can see that a call has been outstanding for ten minutes. It **cannot** see the difference between
a driver that is wedged and a driver that is waiting, correctly, for somebody to walk over and press the
pairing button on a bridge. Those look identical from outside the process, and only the process knows.

Send a frame with `type == "driver.hold"` and `hold` set:

| Field | |
|---|---|
| `id` | stable for the life of one hold, so the frame that ends it names the same one that began it. **Required** — a hold with an empty id is dropped, silently, and there is nothing to see |
| `device_id` | which device; empty means the process itself |
| `reason` | one phrase for whoever is looking at the screen — *"waiting for the button on the bridge"* |
| `until_unix_ms` | when you expect to stop waiting; **0 means you do not know**, which is honest and common |
| `released` | this hold is over |

**Release every hold you begin, including the ones that failed.** A hold that is never released is
indistinguishable from the wedge it existed to rule out, which would turn this field into a way of hiding
exactly what it was added to reveal. The hub also drops every hold a process held when that process goes,
so a crash cannot leave a permanent excuse behind — but do not rely on that for a hold that merely ended
badly.

In C#, `DeviceBase.Hold(reason, until)` returns a token that releases on dispose:

```csharp
using var hold = Hold("waiting for the button on the bridge", DateTimeOffset.UtcNow.AddSeconds(30));
await WaitForPairingAsync(ct);
```

`samples/dotnet/Remaestro.Drivers.Screen` is the worked example: a projection screen can be configured to
take up to five minutes to travel, which is well past the two minutes at which the hub starts saying a
driver has stopped answering.

### 4.4 What the hub does with all this

**It reports, and it restarts nothing.** A driver holds device connections, learned credentials and
in-flight commands, and nothing outside it can see that it has stopped — so the System page carries a
sentence (`no heartbeat for 2 min`, `ExecuteCommand unanswered for 10 min`, or a hold's own reason) and
that is the whole of it. Nothing is restarted and nothing is killed for being quiet — the one thing
the hub re-asks for is the event stream, §7.2.

The three fields above are what a watchdog that *acts* would need first. They are not a commitment that one
is coming.

---

## 5. Checklist for a plugin generated straight from the proto

The C# SDK does all of this. Nothing else will.

Two samples are this checklist worked through end to end — codegen, packaging, signing, install and
launch — each with a note at every step that cost more than the line here suggests.
[`samples/python/`](../samples/python/) is an interpreter and a vendored wheel;
[`samples/go/`](../samples/go/) is one static binary, and is the one that also *runs* itself on the
two architectures a hub actually is, rather than on the machine that built it.

- [ ] Set `protocol_version` on the descriptor to the highest `Protocol` enum value you generated.
- [ ] Leave `min_hub_protocol` unset unless you have a reason; if you set one, it may only ever go down.
- [ ] Answer `Describe` even when `hub_protocol` is older than you like. Refusing is the hub's job.
- [ ] Fill `capabilities` with **everything** you implement, including the three the old booleans cover —
      and keep sending those booleans.
- [ ] Set `supported = true` on every optional response you actually answer. It is a plain proto3 `bool`,
      so unset and `false` are byte-identical, and there is no compiler anywhere on that path.
- [ ] Set `availability` alongside it — `ANSWERED`, `UNSUPPORTED`, `UNAVAILABLE` or `UNKNOWN_DEVICE`.
- [ ] Send `heartbeat_interval_ms` on every frame.
- [ ] Send `heartbeat_independent`, and send it **truthfully**. Omitting it is not the same as `false`.
- [ ] Set `timestamp_unix_ms` on every frame you send, heartbeats included. Zero means 1970, and the hub
      believes you.
- [ ] Put `runtime` on a heartbeat and `hold` on a hold. The type string alone routes nothing.
- [ ] Give every hold an `id`, and release every hold you begin.
- [ ] Redact your own secrets before anything reaches `GetDiagnostics`. The hub cannot do it for you and
      there is no wire-level equivalent — this is the one obligation a C# author gets invisibly and every
      other author gets nothing at all.
- [ ] **And redact the bytes, not only the words.** A `DiagnosticRecord` carries the same moment twice —
      `text` and `hex` — and `endpoint` is a third place a credential can sit. Masking the readable field
      and passing the payload through leaves the password in full, one column to the right, in a
      `trace.json` the hub writes into a support bundle somebody then emails. This is not hypothetical:
      the .NET SDK shipped exactly that bug, and its own guard enumerated the record's string fields by
      hand and stopped one short of `hex`. Blot the payload **before** you truncate it for rendering — half
      a password is a shorter password, not a redacted one. `samples/python/lamp/diag.py` is the worked
      example.
- [ ] **Never return from `StreamEvents`.** Serve it until the *hub* ends it. Everything you publish
      between an end and the hub's reopen is lost — every event, every hold, your own heartbeat — and an
      older hub does not reopen at all. Be ready to serve the call more than once — §7.2.
- [ ] **Put a reference in an event, never a payload.** A frame over 4,194,304 bytes is refused and gone —
      no cursor, no retry, nothing buffered. Send one at the top of every stream and the driver never
      reports anything at all. §7.2a.
- [ ] **Answer `GetState` with your whole state map, every time.** It replaces rather than merges. The two
      fields beside it in the same message are the opposite rule and say so; this one is not and does not.
- [ ] **Validate your own config.** Nothing hub-side checks a value against the schema you declared — not
      `required`, not a range, not membership in `options`. Refuse in `CreateDeviceResponse.error`.
- [ ] **Say a device's "no" in the response, not as a gRPC status.** `ok: false` plus `error` is the
      device declining, and the person reads your sentence. A status code is read as the *driver* having
      failed — §7.3.
- [ ] Expect to be launched, asked `Describe`, and killed, before any device exists — §7.1. Keep
      `Describe` cheap and independent of anything `CreateDevice` does.
- [ ] Expect **`SIGTERM`, two seconds, then `SIGKILL`** — and no shutdown rpc on any hub, plus an older
      hub that sends no signal at all. Anything you need to survive still has to be durable at the moment
      it is true; the two seconds are a chance to be tidy — §7.4.
- [ ] Ship a `plugin.json` that validates against
      [`plugin-manifest.schema.json`](plugin-manifest.schema.json) — §6.

---

## 6. The manifest inside the archive

A plugin is a gzipped tar with a **`plugin.json` at its root**. That file is the only thing the hub reads to
learn what to run, and it is checked against
[**`docs/plugin-manifest.schema.json`**](plugin-manifest.schema.json), which is normative: every field, what
it means, and what the hub does with it is written there rather than here, so that there is one copy of it.

```jsonc
{
  "id": "com.example.lamp", "version": "1.0.0", "abi": 1,
  "kind": "driver", "runtime": "python3", "rid": "linux-arm64",
  "exec": ["python3", "main.py"]
}
```

`samples/python/package.sh` writes exactly that file, and is the worked example of everything below.

### 6.1 Two files, one name

There is a second `plugin.json` — the **registry submission**, one per plugin, listing every version and
every architecture, at `plugins/<id>/plugin.json` in the extensions registry. It has its own schema, its own
required fields, and **the opposite rule about unknown fields**: it refuses one, because a field the registry
cannot check is a claim a human reviewer is about to read. This one **ignores** unknown fields, because a hub
too old to know a field must still run a plugin carrying one — otherwise every field ever added is a flag day
for every box in the field.

The registry's CI reads the manifest out of your archive and refuses the submission if the two disagree on
`id`, `version`, `abi` or `rid`. Nothing on a hub can do that check, because a hub only ever has one of the
two files.

### 6.2 What launching it means

| | |
|---|---|
| Program | `exec[0]` — resolved against the package root if a file of that name is in it, otherwise left for the OS to find on `PATH` |
| Arguments | `exec[1..]`, passed as argv. Nothing is parsed and nothing needs quoting |
| Working directory | **the package root** |
| Address to serve on | `REMAESTRO_DRIVER_URL`, and `ASPNETCORE_URLS` with the same value |
| Somewhere to write | `REMAESTRO_DRIVER_STATE_DIR` — a directory of your own, made for you before you start, and the only place you may keep anything. §7.5 |

**The working directory is the one that catches people**, because the hub used to pass its own and a .NET
driver never noticed — `AppContext.BaseDirectory` does not care. Anything resolving a path relative to itself
does.

**And it is not where you put a file you mean to keep.** The package root is *version*-scoped —
`<data>/plugins/<id>/<version>/` — so a database written beside your code is thrown away by your own next
release. `REMAESTRO_DRIVER_STATE_DIR` is the answer to that and §7.5 is the whole of it.

`REMAESTRO_DRIVER_URL` and `ASPNETCORE_URLS` are two names for one fact rather than a migration: every driver
in the field reads the second, and nobody writing Go should have to read a variable named after a .NET web
framework to learn which port they were given.

### 6.3 Where the hub is more forgiving than this schema

The schema describes a manifest that is *correct*. The hub's parser is deliberately looser, so that it never
refuses a file it could have understood — and the looseness is worth knowing about, because in two places it
means your plugin runs having quietly said something you did not mean:

- **`abi` absent, or present but not an integer** — `"abi": "1"` is a string — reads as **0**, silently. The
  only `abi` check is *newer than this hub's protocol*, so 0 is never refused: your plugin runs with its
  install-time compatibility check switched off, having declared nothing. **This is the one on the list that
  costs you something**, and the registry is what catches it; a hub installing from a URL does not.
- **`kind` is compared case-insensitively**, so `"Driver"` runs. The schema names only `driver` because that
  is what to write.
- **`rid` empty** reads the same as absent — "runs anywhere".
- **`exec` entries that are empty or are not strings are dropped**, rather than making the manifest invalid.
  The manifest is only refused if nothing usable is left. The registry's CI is stricter and requires every
  element to be a non-empty string, so a manifest relying on this will pass a hub and fail a submission.
- **Unknown fields are ignored** — §6.1, and that one is not looseness but the rule.

Two things the schema and the hub agree on, listed here because they read like looseness and are not:
**`kind` absent** means `driver` and **`rid` absent** means "runs anywhere". Both are defaults, both are in
the schema, and saying either explicitly is what stops *we forgot* looking like *we decided*.

None of the looseness above is a promise to keep being true, and the registry refuses most of it. Validate
against the schema.

---

## 7. The process you are launched as

**Everything in this section is a fact about your process rather than about a message, which is why none of
it is in the proto — and why the C# SDK's authors never had to learn any of it.** All of it was measured by
`#427` while building `samples/go/`, against a real hub.

### 7.1 You are started twice, and killed in between

The first time a hub sees your plugin it runs a **first-run introspection sweep**: launch the process, call
`Describe`, record the descriptor, and kill it again. Then, when something actually wants a device of your
type, it launches you a second time.

Measured — two pids, one second apart, on the first boot after an install. So:

- `Describe` must be answerable **cold**, out of fields you already hold, with nothing set up. It has a 10
  second deadline and the hub retries it for about thirty.
- Anything expensive you do at startup is paid at least twice, and the first time is for nothing.
- Once the descriptor is cached, later boots do **not** start you at all until a device of your type
  exists. The cache is keyed on your declared version plus a stamp over your whole directory, so bumping
  the version in `plugin.json` invalidates it whatever the bytes did.

### 7.2 `StreamEvents` must never return, and a stream that ends is reopened

The hub calls it immediately after `Describe` and reads it for the life of the process. **Serve it until the
hub's own cancellation, and treat your own exit from that method as a bug.** Everything below is what ending
it costs and what the hub does about it, and none of it makes ending it acceptable.

**A driver that returns cleanly from `StreamEvents` looks completely healthy.** Sabotaged and measured: end
the stream after one frame and unary calls still work, `GetState` still answers, commands still succeed,
diagnostics still capture, and the liveness reading is a green one second. What is gone is everything the
stream carries — every device event, and every hold, including one published from inside a command that was
running at the time.

This is worth stating because **returning is the natural shape in most languages**: a `for` over a channel
that closes, a generator that runs out, a callback loop whose condition goes false. In C# it is an
`IAsyncEnumerable` that completes.

**The hub notices, says so, and opens the stream again.** A stream that ends for any reason — your clean
return, an `RpcException`, a fault on the hub's own side of the read — is written to the hub's log as a
warning naming your driver and the wait, and then reopened as a fresh call on the channel that is already up:

| | |
|---|---|
| The wait | doubles from **1 s** and is clamped at **30 s** — 1, 2, 4, 8, 16, 30, 30, … |
| The count | is ends *in a row*, not a tally. A stream that stays up for **30 s** clears it, and the next end starts again at 1 s |
| The ceiling | has no attempt limit above it, deliberately. A driver that comes back after an hour is streamed again |
| It gives up | only when the hub is shutting your driver down, or when your **process has already exited** — there is nothing to reopen against a process that is gone, and that case is logged as itself |

It is also reported to people, in one sentence with two shapes: `event stream ended 4s ago` while it is a
single end, `event stream ended 6 times, last 4s ago` after that. The hub's own System page shows it for
every end. A **device** row shows it only once the wait has walked all the way out to the ceiling — six ends
and thirty-one seconds of a stream that will not stay up — and then marks that device **"Not reporting"** in
front of whoever owns it. Deliberately not "offline": your unary calls are still being answered, which is
the whole trap this section is about.

**Three things follow, and they are obligations on you rather than on the hub.**

1. **You may be asked for `StreamEvents` more than once in one process, and every call must be served like
   the first.** This is the one genuinely new requirement here — before, a second call could not happen. A
   plugin that refuses a second `StreamEvents`, or that hands its event channel to a single consumer and
   has nothing left for the caller after it, turns one transient fault into a permanent one, and the hub
   will go on asking every thirty seconds for as long as the process lives.
2. **A cancelled stream is that one call ending, not your process being stopped.** The hub cancels the
   previous call before it opens the next, so you are never asked to serve two at once — and a cancellation
   arriving on `StreamEvents` says nothing about whether you are about to be shut down. §7.4 is what that
   looks like, and it looks nothing like this.
3. **Nothing is restarted.** The hub re-asks and never kills: your process, its devices, its connections
   and anything it has learned are all exactly as you left them.

**None of that repeals the advice, and this is the part to take away.** *Everything you publish between the
end and the reopen is gone* — every device event, every hold, and your own heartbeat, which rides this same
stream. Nothing is buffered hub-side and nothing is replayed, so a plugin that ends its stream once an hour
still loses whatever happened in that second. A plugin that returns immediately is worse than it sounds:
the schedule walks it out to the ceiling within half a minute, after which it is streaming for a few
milliseconds out of every thirty seconds and is marked as not reporting. **The hub recovering is a floor
under the damage, never permission to end the stream.**

**And a hub older than this one does neither half.** It caught the `RpcException` and returned, so a stream
that ended ended that hub's interest in your driver for the rest of the process — with nothing logged, and
`GetState`, commands, diagnostics and the heartbeat all still answering. This behaviour arrived with the
hub, not with the contract, so a plugin published today may be run by such a hub — which is one more reason
the paragraph above is the load-bearing one.

### 7.2a An event carries scalars, and a frame that is too large is gone

**A frame must fit in 4,194,304 bytes** — what a stock gRPC channel will receive, and the same figure
`GetEpg` and `GetDiagnostics` are paged against. Those two are unary and have a cursor, so a payload that
is too big can be asked for again in pieces. **An event has neither.** There is no request field per frame
and nothing hub-side buffers what it could not read, so an oversized event is not a page to re-ask for; it
is a frame that is gone.

What one costs, measured through a real hub:

| | |
|---|---|
| The oversized frame | dropped, never resent |
| Anything you wrote after it **on that call** | dropped with it — the refusal aborts the whole call, not one message |
| Everything before it | delivered normally |
| The hub | logs a warning naming your driver and the limit, and reopens on §7.2's schedule |
| The next stream | delivers normally, including anything you queued while it was down |

So **one** oversized frame costs one frame. **But send one at the top of every stream and you deliver
nothing at all, for as long as your process lives** — the reopen hits the same frame and ends the same
way, measured at 68 opens in two seconds. "Publish current state when a consumer connects" is an ordinary
shape, and it is the one that turns a lost frame into a driver that never reports. This is the sharpest
edge on this page: §7.2's reopen is a floor under the damage from a frame you send *once*, and no floor at
all under one you send *always*.

**Put a reference in `data`, not the payload.** A snapshot, a poster, a listing, a log file — an event can
point at any of them with a URL, an id, or a path into `Browse`. None of them is a thing an event should
carry. For scale, so this reads as a real bound rather than a caution: a 2 MiB 4K image base64s to
2,796,239 bytes and **fits**; a 10,000-item listing at 400 bytes each is 4,000,037 and **fits**. What goes
over is genuinely a payload.

**There is no chunking envelope and there is not going to be one**, so that you can build against this
rather than wait for it. Two reasons, both in `driver.proto` beside `DeviceEventMessage.data`: splitting a
frame would put the slicing decision on the sender, which is the arrangement `GetDiagnosticsRequest.limit`
exists to avoid — the party that knows the limit is the receiver. And the hub buffers the last 400 events
whole and hands each one to every subscriber and every connected browser, so letting these through is a
change to the hub's memory before it is a change to the wire: a full buffer of 4 MiB events measures
**3.13 GiB**, against the 7.87 GiB the shipped appliance has in total.

### 7.3 A device's refusal and a driver's failure are different answers

`ExecuteCommandResponse { ok: false, error: "…" }` is **the device declining**, and your sentence is what
the person is shown. A gRPC status is **the driver having failed**, and the hub words it that way — a
`DEADLINE_EXCEEDED` becomes *"the driver didn't answer within 60s"*, which reads to somebody in a room as
the hub having given up rather than as the television having said no.

The deadlines are not fields on any message. They arrive as the call's own gRPC deadline, which every
generated server surfaces on the request context, and they are: `Describe` **10 s**, an ordinary call
(command, state, listing, browse, create) **60 s**, `pair_begin`/`pair_finish` **150 s**, `GetEpg`
**5 min**. `StreamEvents` has none, deliberately.

### 7.4 You are asked to stop, and then killed — `SIGTERM`, two seconds, `SIGKILL`

**There is still no shutdown rpc**, and there is no message on the wire that means "I am going away".
`DisposeDevice` is about one device and is not a signal that the process is ending. What you get is the
signal your operating system already has:

1. the hub **drops the gRPC channel**, which ends `StreamEvents` and every call in flight;
2. it sends **`SIGTERM`** to the process it started — the one, not the tree;
3. it waits **two seconds**;
4. if you are still there it calls `Process.Kill(entireProcessTree: true)` — **`SIGKILL`**, and this one
   does take anything you forked.

So a shutdown handler runs, a flush happens, and a lock file you remove on the way out is removed.
Measured through a real hub with a Go plugin sabotaged both ways: one that handles `SIGTERM` and deletes a
lock file it holds is gone in **10 ms** with the file cleaned; the same build killed outright leaves it
behind. One that installs a handler and deliberately ignores the signal spends the whole grace and is
killed at **2021 ms**, and the hub writes a warning naming it.

**Two seconds is the number and it is not generous.** It was chosen against a measurement of every driver
the hub ships — 43 of them, 387 timings, ambient and under load — whose whole distribution is p50 24 ms and
max 472 ms. If your shutdown work does not fit in two seconds it is work that should not be at shutdown.

**None of that repeals the old advice, and this is the part to take away.** *Anything you want to survive
still has to be durable at the moment it is true.* Four things still end your process with no warning at
all: a crash, the hub itself being killed, a `SIGKILL` from the container runtime, and — the one you
control — **your own refusal to exit**, which costs every stop of you the full grace and then kills you
anyway. Treat the signal as a chance to be tidy, never as the moment your state becomes durable.

**And a hub older than this one sends nothing.** This behaviour arrived with the hub, not with the
contract, so a plugin published today may be run by a hub that still uses `SIGKILL` alone — which is one
more reason the paragraph above is the load-bearing one. A handler is safe on every hub: on an older one it
simply never fires, which is where this section started.

### 7.5 The address, the working directory, where you may write, and where your logging goes

| | |
|---|---|
| Address | `REMAESTRO_DRIVER_URL` and `ASPNETCORE_URLS`, both set to the same **URL** — `http://127.0.0.1:53412`. Not a `host:port`, because the variable it was modelled on is ASP.NET Core's; strip the scheme yourself. The `http://` is load-bearing: the hub speaks **cleartext h2c** and cannot talk to a driver serving TLS |
| Port | chosen by the hub, which binds it, closes it, and hands you the number. Losing that race is possible; die loudly if you cannot bind, because the hub has a guard for a driver that exited while something else answered on its address |
| Working directory | the package root — §6.2. **Read-mostly**: it is version-scoped, so anything you write here goes with your next release |
| State directory | `REMAESTRO_DRIVER_STATE_DIR` — an absolute path to a directory that is yours alone, already made, mode **0700**. Not version-scoped, so it survives your upgrades; deleted when somebody uninstalls you |
| stdout / stderr | **not redirected.** They are the hub's own, so a line you print lands in the hub's console or its container log, interleaved with everything else. There is no per-plugin log file |

#### `REMAESTRO_DRIVER_STATE_DIR`, in full

**Everything you want to still have next time goes in here, and there is nowhere else.** That is not advice,
it is the shape of the box you are running on: an appliance hub runs under `ProtectSystem=strict` with one
writable path, and `/tmp` there is a 256 MB tmpfs *in RAM* which is also where `PrivateTmp=` puts you. Your
own package directory is writable and is the wrong answer for a different reason — it carries your version
number, so your next release is a different directory and your data is in the old one.

- **The hub makes it before it starts you.** You never `mkdir`, you never handle the race, and you do not
  choose the mode. Open your file and go.
- **It is yours.** One directory per driver, named after your plugin id. No other plugin is given a path
  inside it. It is `0700` — owner only — because what a driver keeps is often a token it was issued, and on
  an appliance there are other local users. Note the honest limit: every plugin runs *as the hub's own user*,
  so this stops other people on the box and not other plugins. Nothing on this hub does — §6 of the plugin
  plan says so in as many words.
- **It survives an upgrade and does not survive an uninstall.** The path has no version in it, so installing
  your 2.0 over your 1.0 leaves the directory exactly as your 1.0 left it — *you* own migrating what is in
  it, and the hub will never rewrite or clear it. An uninstall deletes it, along with your code and your
  settings, because a store belonging to a plugin that is not on the box is a leak nobody will ever come
  back for.
- **Commit as you go.** Two seconds of `SIGTERM` grace and then a kill (§7.4), a crash, or the hub itself
  being killed all end you with whatever you had not flushed. A store that is only durable at shutdown is a
  store that is not durable.
- **One file, one writer — yours.** Do not put anything in the hub's own database and do not expect the hub
  to read anything here. This directory is opaque to it.

**It may be unset, and you must survive that.** Two real cases: you are being run by hand out of a build tree
with no hub at all — which is exactly what this repository's samples tell you to do — and the hub could not
make the directory, on which it starts you anyway rather than taking every device in the house off the box
for a full disk. **Unset means you have nowhere durable to write.** Degrade: hold it in memory, refetch next
time, or say plainly that a feature needs storage. Do not invent a path; every one you could guess is either
read-only, in RAM, or about to be deleted.

```go
dir := os.Getenv("REMAESTRO_DRIVER_STATE_DIR")
if dir == "" {
    // No hub, or the hub had nowhere to put us. Run without a store rather than guessing at one.
    log.Println("no state directory; the guide will be re-fetched every time")
} else {
    db, err = sql.Open("sqlite", filepath.Join(dir, "guide.db"))
}
```

**Two things about it that will not be what you assume, and both are somebody else's rules rather than
yours.**

- **It is not in the hub's backup.** The portable bundle is JSON, goes to the cloud, and carries what a
  *person* typed — devices, remotes, activities, accounts, and **your settings**, keyed by your plugin id. It
  does not carry your code and it does not carry this directory. So a household that restores onto new
  hardware gets your settings back and an empty state directory. **If you hold something irreplaceable —
  something a person cannot retype and you cannot re-fetch — a setting is where it belongs, not here.**
  Everything else, treat this as a cache that happens to persist.
- **There is no quota yet, and there will be.** The hub cannot refuse your `write(2)`, so nothing is enforcing
  a ceiling on this directory today. That is a gap being closed, not a licence: the data partition an
  appliance guarantees is 3.0 GiB, shared with the hub's database, two unpacked app versions, a ~150 MB
  speech model and every other plugin. A guide store measured at 483 MB is already 16 % of it. **Bound what
  you keep — by time, by row count, or by bytes — and do it now**, because the alternative to your ceiling is
  somebody else's.

### 7.6 Two names, two rules

`plugin.json` carries an **id** and your descriptor carries a **`type_id`**, and they are not the same name:

- the **id** is matched case-insensitively, must be reverse-DNS with at least three labels to pass the
  registry, and is **refused outright** if it collides with a driver the hub ships;
- the **`type_id`** is what decides which driver serves a device. Nothing validates it — not its
  characters, not its emptiness, and **not a collision with a shipped driver's `type_id`**, which is an
  ordinal dictionary write that the last driver to start wins.

Pick a `type_id` that is obviously yours.

### 7.7 `traits` is a closed vocabulary that is not in this contract

`DriverDescriptor.traits` and `CreateDeviceResponse.traits` decide how a device is grouped, which icon it
draws and what an activity is generated from. The proto names three of the thirteen and then an ellipsis.
The list is: `ir.emitter`, `ir.receiver`, `input.source`, `input.switcher`, `bridge`, `media.library`,
`media.player`, `display`, `audio`, `power`, `lighting`, `proxy`, `cover`.

**An unknown trait is accepted, labelled as itself, and does nothing.** No error and no log line, so the
only way to find out you guessed wrong is to read the hub's source, which you do not have.
