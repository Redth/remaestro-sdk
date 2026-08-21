# reMaestro SDK

**The open architecture surface of [reMaestro](https://remaestro.app) — the contract a plugin speaks, the
libraries that make speaking it easy, and working examples of both.**

reMaestro is a self-hosted hub that drives the equipment in a house: televisions, receivers, projectors,
screens, media libraries, lights. Its drivers do not live inside it. **Every driver is its own process,
talking to the hub over gRPC on loopback** — so a driver can be written in any language, crash without
taking the hub with it, and be installed without rebuilding anything.

This repository is that boundary. The hub itself is not open source; the contract it speaks is, and so is
everything you need on your side of it.

---

## What is in here

| | |
|---|---|
| **[`proto/driver.proto`](proto/driver.proto)** | **The contract.** One service, eighteen RPCs. Everything else in this repository is downstream of this file. |
| **[`dotnet/src/Remaestro.Sdk`](dotnet/src/Remaestro.Sdk)** | The C# SDK. Implement two interfaces, call one method, and the gRPC is handled. |
| **[`dotnet/src/Remaestro.Grpc`](dotnet/src/Remaestro.Grpc)** | The generated .NET client and server for the proto. |
| **[`samples/dotnet`](samples/dotnet)** | Four real drivers, chosen to cover four different shapes of device. These ship in the product. |
| **[`dotnet/src/Remaestro.ProxyAgent`](dotnet/src/Remaestro.ProxyAgent)** | A reference *proxy* — the other boundary, for hardware the hub cannot reach over the network. |
| **[`dotnet/tests`](dotnet/tests)** | A conformance suite for that proxy protocol, written as literal wire vectors so it is portable to any language. |
| **[`samples/python`](samples/python)** | A driver in Python, generated from the proto with stock `protoc`, packaged, signed and installed the way a stranger would. Nothing in it imports anything of ours. |
| **[`docs/driver-protocol.md`](docs/driver-protocol.md)** | **The negotiation, the capability list and the heartbeat's obligations** — the parts of the contract a plugin in another language has to implement by hand. |
| **[`docs/plugin-manifest.schema.json`](docs/plugin-manifest.schema.json)** | **The `plugin.json` inside a plugin archive**, as a JSON Schema. The file that decides whether your plugin runs. |
| **[`docs/`](docs)** | The specifications behind the parts of the contract that need more than a comment. |

### Why the proto is not under `dotnet/`

.NET is the first SDK, not the only intended one. `proto/driver.proto` sits at the repository root because a
Node SDK, a Python plugin author and a Go plugin author all generate from that same file, and a contract
parked inside one language's directory tells every other language it is a guest. The .NET build points at it
across the tree rather than keeping a copy — one file, no drift.

The `Remaestro.Plugins.Protocol` package also ships the `.proto` itself as content, so you can get the exact
file the hub was built from without cloning anything.

---

## The contract, in one page

A plugin is **a process that serves gRPC**. The hub starts it, hands it a loopback address, and dials it.

1. **Listen for gRPC h2c** (cleartext HTTP/2 — no TLS, no auth; it never leaves loopback) on the address in
   `ASPNETCORE_URLS`, and serve the `maestro.Driver` service from `driver.proto`.
2. **Answer `Describe` promptly.** The hub retries for about ten seconds, then gives up on you.
3. **Exit on `SIGTERM`.** The hub owns your process and reads your liveness from it.
4. **Do not fork or daemonise.**

That is all of it. A ninety-line Python script has been run through the hub's real launch path doing exactly
this — full device lifecycle, commands, state, the event stream and the heartbeat — with no change to the hub.

**Not every RPC is required.** Six are effectively mandatory (`Describe`, `CreateDevice`, `ExecuteCommand`,
`GetState`, `StreamEvents`, `DisposeDevice`); the rest are opt-in. **Say which you implement** in
`DriverDescriptor.capabilities` — `inputs`, `epg`, `apps`, `device-remotes`, `bridge`, `options`,
`navigation`, `diagnostics` — rather than leaving the hub to find out by calling. Anything you did not
declare, it does not ask for; anything you did declare, it will. Declaring `navigation` in particular is a
promise with teeth: that path has no exception handling, so declaring it without implementing all four RPCs
surfaces an error to the user where an undeclared driver would have degraded quietly.

There are still three ways to say "not this one", and they answer at different moments:

- **the capability list**, which stops the hub asking at all;
- **`supported` + `availability` in the response** — `ListInputs`, `GetEpg`, `ListApps`, `GetRemote`,
  `ListBridgedDevices`, `ListOptions`. `supported: false` is always safe; `availability` says *which* kind
  of no it was, so an unreachable device is never read as a device that has nothing;
- **returning `UNIMPLEMENTED`**, which the hub tolerates everywhere.

> **A trap for anyone generating from the proto directly.** The six `supported` fields are plain proto3
> `bool`s, so an unset field and an explicit `false` are byte-identical on the wire. Implement `GetEpg`
> perfectly, forget `supported = true`, and you have silently built nothing. There is no compiler anywhere on
> that path. The C# SDK sets it for you; nothing else will.

**Both ends declare a version.** The hub sends `hub_protocol` on `DescribeRequest`; you send
`protocol_version` on the descriptor, and optionally `min_hub_protocol`. The hub is the party that refuses a
mismatch — you always answer `Describe`, because the hub is the one with a screen to explain it on.

**Your heartbeat has to describe itself.** Send `heartbeat_interval_ms` on every frame, or the hub is
guessing your cadence from someone else's default; and send `heartbeat_independent` truthfully, which is the
one place a false claim costs a user something. A single-threaded plugin says `false` and loses nothing but
a signal it could never have given.

**[`docs/driver-protocol.md`](docs/driver-protocol.md) is the whole of it** — negotiation, capabilities,
availability, the heartbeat's three declarations, and a checklist for a plugin generated straight from the
proto.

---

## Writing a plugin in C#

```csharp
// Program.cs — this is the whole file, in every driver that ships with the product.
using Remaestro.Sdk;

await DriverHost.RunAsync(new MyDriver(), args);
```

```csharp
public sealed class MyDriver : IRemaestroDriver
{
    public string TypeId => "acme.lamp";
    public string DisplayName => "Acme Lamp";
    public string Description => "A lamp that answers HTTP.";

    public IReadOnlyList<ConfigField> ConfigSchema { get; } =
        [new("host", "Host / IP", Required: true)];

    public IReadOnlyList<CommandInfo> Commands { get; } =
        [new("power.on", "On", "Turn the lamp on")];

    public Task<IRemaestroDevice> CreateAsync(DeviceContext context, CancellationToken ct) =>
        Task.FromResult<IRemaestroDevice>(new MyDevice(context));
}
```

`DriverHost` hosts the Kestrel gRPC server, assembles your descriptor, keeps the device registry, runs the
event stream and the two-second heartbeat, turns exceptions into failed command results, and works out which
optional RPCs you support **by looking at which interfaces your device implements**. Add `IEpgSource` and
`GetEpg` starts answering; there is nothing to register. It also fills in the protocol version, folds your
`Supports*` flags into the capability list, declares the heartbeat's interval and independence, and picks the
right `availability` at each of the six optional answers — see
[`docs/driver-protocol.md`](docs/driver-protocol.md) for what that means and why a plugin in another language
has to do it by hand.

What it cannot infer is what you *have not implemented yet*. Set `Capabilities` for anything the interfaces
cannot express — a driver that captures its own traffic for `GetDiagnostics`, say — and set
`HeartbeatIndependent` to `false` if you have taken the beat into your own hands and coupled it to your
command loop.

`DeviceBase` gives you a thread-safe state bag, change-debounced command and trait reporting, and `Online`
derived from state where **absent means offline** — a lesson this project learned the hard way, when drivers
that only wrote a state key reported "Connected" for devices nothing could reach.

`LineDevice` and `TcpLineDevice` absorb the entire connect / reconnect / frame / trace problem for gear that
speaks a line protocol over TCP or RS-232, which is most AV equipment ever made.

```
dotnet add package Remaestro.Plugins.Sdk
```

> Your project needs `<Project Sdk="Microsoft.NET.Sdk.Web">`. The SDK takes a framework reference on
> `Microsoft.AspNetCore.App`, because a driver genuinely *is* a Kestrel gRPC server.
>
> **`net10.0` only**, deliberately. A plugin is a standalone process rather than a component inside your
> application, so this asks you to build one small executable on .NET 10 — not to move anything you already
> have onto it.

### Writing one in something else

Generate from `proto/driver.proto` with stock `protoc` and serve `maestro.Driver`. That is the entire
integration; there are no custom options, no interceptors, no metadata requirements. Read the four process
rules above, read the `supported`-bool trap above twice, and then work through the checklist at the end of
[`docs/driver-protocol.md`](docs/driver-protocol.md) — every item on it is something the C# SDK does for a
C# author and nothing does for you.

---

## The samples

Four drivers that ship in the product, picked because each one shows a different shape.

| Sample | Lines | What it shows |
|---|---|---|
| **[`Remaestro.Drivers.Http`](samples/dotnet/Remaestro.Drivers.Http)** | 191 | The smallest useful driver. `DeviceBase` plus an `HttpClient` — start here. |
| **[`Remaestro.Drivers.Lutron`](samples/dotnet/Remaestro.Drivers.Lutron)** | 447 | `TcpLineDevice`: a text line protocol over a socket, with reconnection handled for you. |
| **[`Remaestro.Drivers.Screen`](samples/dotnet/Remaestro.Drivers.Screen)** | 421 | `ByteLink`: a **binary** protocol — five bytes at 2400 baud over RS-232 — and a device that is strictly write-only. Worth reading for how it declines to invent a position it cannot measure. |
| **[`Remaestro.Drivers.Jellyfin`](samples/dotnet/Remaestro.Drivers.Jellyfin)** | 1192 | The large end: `INavigableDevice` for a browsable media library, plus `ListenerSupervisor`. |

They are the real files, not reductions. Each is a directory you can copy.

*Lines* is the driver's own C# without its four-line `Program.cs`. All four had drifted — Lutron's
said 159 against a file that has not been that size for a long time — so they are worth recounting
rather than trusting; nothing checks them.

**[`samples/dotnet/README.md`](samples/dotnet/README.md) explains how a driver actually starts** — which is
not `dotnet run`, and which is why none of these projects has a launch profile.

---

## Proxies, and the other protocol

Some equipment cannot be reached over a network at all — RS-232, infrared, a USB remote receiver that has to
sit where a person is sitting. A **proxy** is a small machine near the rack that dials out to the hub and
relays those. It speaks a different, much smaller protocol: a binary tunnel over TCP, four bytes of header
and a body.

- **[`Remaestro.ProxyAgent`](dotnet/src/Remaestro.ProxyAgent)** is a complete Linux implementation of the
  board side. It shares no types with the hub, on purpose — the third implementation of this protocol is
  ESP32 firmware written in C++, which could never share one, so agreement has to be tested rather than
  compiled.
- **[The conformance suite](dotnet/tests/Remaestro.ProxyAgent.Tests)** is how. The hub's half is written out
  as literals — op bytes, header layout, JSON documents, one whole frame in hex — in
  [`HubWire.cs`](dotnet/tests/Remaestro.ProxyAgent.Tests/HubWire.cs). **Read that file as the specification**,
  and port its assertions if you are building a proxy in another language.

[`docs/proxy-hardware.md`](docs/proxy-hardware.md) is the survey of what a proxy should run on, and why the
line between a microcontroller and a small Linux machine falls where it does.

---

## Documentation

- **[`docs/driver-protocol.md`](docs/driver-protocol.md)** — version negotiation, the capability list, and
  what a heartbeat has to declare before anyone may read anything into silence. **Read this first** if you
  are generating from the proto rather than using the C# SDK. §6 is packaging.
- **[`docs/plugin-manifest.schema.json`](docs/plugin-manifest.schema.json)** — the schema for the
  `plugin.json` inside a plugin archive, with every field's meaning in its `description`. Validate your
  manifest against it with any draft 2020-12 validator; a hub that will not start your plugin is usually
  this file.
- **[`docs/navigation-spec.md`](docs/navigation-spec.md)** — the projection that lets any driver expose its
  content as a browsable library. Read this before implementing `INavigableDevice`.
- **[`docs/driver-remotes.md`](docs/driver-remotes.md)** — how a driver draws its own remote control layout,
  and the (longer) list of drivers that should not.
- **[`docs/proxy-hardware.md`](docs/proxy-hardware.md)** — the proxy hardware survey.

---

## Building

```bash
dotnet build Remaestro.Sdk.slnx
dotnet test  Remaestro.Sdk.slnx
```

Requires the .NET 10 SDK (`global.json` pins 10.0.302 with `latestFeature` roll-forward).

---

## Versioning

**The SDK versions independently of the hub, on plain semver.** The product ships hub and drivers as one unit
with one version number, on the reasoning that it is the number a user reads out to you over the phone. That
rule is right for something you install as a unit and wrong for something strangers compile against: a plugin
built against SDK 1.2 has to keep working on hub 1.7, because moving apart is the entire point of a published
contract.

So compatibility is a question about **the protocol**, not about the package version — and it is answered on
the wire. See [`dotnet/eng/SdkVersion.props`](dotnet/eng/SdkVersion.props), which carries the reasoning next
to the number.

---

## What installing a plugin means

Said plainly, because a platform owes people this rather than a reassuring badge:

**A plugin is trusted exactly as a driver that ships in the product is.** There is no sandbox, no capability
grant and no permission manifest. A plugin process runs with the hub's own privileges and can do anything the
hub can do — read every database and credential it holds, and reach anything on your network.

That is a deliberate choice for self-hosted software, taken with the counter-argument on the table: you chose
what to install. But it means no amount of packaging, signing or review makes a plugin *safe*. Provenance —
knowing who published something and that later versions came from the same person — is the only control that
exists, and it is a different claim from safety.

---

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md). Bug reports and fixes against the SDK, the samples, the proxy agent
and the docs are all welcome. Changes to `driver.proto` are a different matter and that file explains why.

## Licence

MIT. See [`LICENSE`](LICENSE).
