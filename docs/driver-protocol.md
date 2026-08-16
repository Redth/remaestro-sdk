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

The registry manifest — `plugin.json` — carries a single integer called `abi`. **It is the same number as
`protocol_version`.** Two integers that could disagree would mean a plugin that installs and will not run,
or, worse, one that runs having claimed it would not. The manifest's job is to refuse a download; the
descriptor's job is to refuse a launch; they answer the same question at two moments.

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
| `id` | stable for the life of one hold, so the frame that ends it names the same one that began it |
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
that is the whole of it. Nothing loops and nothing kills.

The three fields above are what a watchdog that *acts* would need first. They are not a commitment that one
is coming.

---

## 5. Checklist for a plugin generated straight from the proto

The C# SDK does all of this. Nothing else will.

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
- [ ] Release every hold you begin.
- [ ] Redact your own secrets before anything reaches `GetDiagnostics`. The hub cannot do it for you and
      there is no wire-level equivalent — this is the one obligation a C# author gets invisibly and every
      other author gets nothing at all.
