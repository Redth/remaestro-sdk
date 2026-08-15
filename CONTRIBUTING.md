# Contributing

Thanks for looking. This repository is the public boundary of a product whose hub is closed, which makes a
few things work differently from a normal open-source project. Worth two minutes before you open a PR.

## What lives here, and what does not

**Here:** the wire contract, the .NET SDK, the reference proxy agent, four sample drivers, and the
specifications behind them.

**Not here:** the hub, the web console, the mobile app, and the other forty-odd drivers. Those are in a
private repository. If you hit something that looks like a hub bug, open an issue here anyway and describe
what you observed — someone will carry it across.

**The `Remaestro.Sdk` and `Remaestro.Grpc` sources are shared with that private repository**, where they are
the foundation of every shipping driver. A change here has to be a change there too, so:

- Keep changes to those two projects **minimal and behaviour-preserving** unless the change is the point.
- **Do not reformat.** A whitespace-only diff across `DriverHost.cs` costs a manual reconciliation and buys
  nothing.
- Comments are load-bearing. Several of them are the only written record of a rule that has no schema — the
  heartbeat's first frame being immediate, why `ListBridgedDevices` answers `Supported = true` from its
  catch, absent-means-offline. If you are moving code, move the comment with it.

## Building and testing

```bash
dotnet build Remaestro.Sdk.slnx
dotnet test  Remaestro.Sdk.slnx
```

You need the .NET 10 SDK; `global.json` pins the version.

**Read the exit code, not the summary line.** A test host that dies mid-run can leave a green-looking log
with most of an assembly missing — `Failed: 0` is true and meaningless when three thousand tests never ran.
The exit code is the whole verdict.

**Tests must not touch the network.** Not a style preference: tests here that dialled real addresses once
hung an entire suite. Everything in the proxy conformance suite runs over in-memory pipes and a fake device,
and `ProxySession` takes a `Stream` and a delegate specifically so that it can.

## Changing `driver.proto`

This is the part that needs care, because the file is a published contract with consumers we cannot see.

- **Field numbers are never reused. Fields are never renamed. RPCs are only added.** A rename is a different
  meaning at the same number, and the hub's own cache invalidation hashes both numbers and names for exactly
  that reason.
- **New fields are fine** — proto3 drops what it does not know, so an old plugin sends nothing and the hub
  reads a default. New RPCs are fine for the same reason: a plugin that has never heard of one returns
  `UNIMPLEMENTED`, which is what the hub already expects.
- **A change in what a stable field number means is not fine, and nothing will catch it.** If you find
  yourself wanting one, open an issue first.
- Prefer explicit presence (`optional`) over a bare `bool` for anything where "unset" and "false" are
  different answers. There is a live example of why in the README.

Changes to the proto will usually need a matching change in the hub, so expect a proto PR to take longer than
an SDK one.

## Writing a proxy in another language

The conformance suite in `dotnet/tests/Remaestro.ProxyAgent.Tests` is written so this is possible:
[`HubWire.cs`](dotnet/tests/Remaestro.ProxyAgent.Tests/HubWire.cs) holds the hub's half as literals — op
bytes, header layout, JSON documents, one whole frame in hex — rather than as a reference to hub code. Port
the assertions. If you find the specification underspecified somewhere, that is a documentation bug and a
good issue.

## Style

The prose in this codebase has a voice and it is not accidental. Comments say **why**, and specifically why
something that looks wrong is right; they are aimed at the person who will next be surprised. If a comment
would only restate the code, leave it out.

Commit subjects are lowercase, `area: what changed in plain language`, describing the effect rather than the
mechanics. No ticket numbers, no `feat:`/`fix:` prefixes.

```
sdk: a device that never spoke reads as offline rather than connected
proto: a driver can say which protocol it was built against
samples: the screen driver stops implying it knows where the screen is
```

## Security

**Do not open a public issue for a security problem.** Email the address on
[remaestro.app](https://remaestro.app) instead.

Please read the *"What installing a plugin means"* section of the README before reporting that plugins are
unsandboxed. They are, deliberately and on the record, and the trust model is a product decision rather than
an oversight.

## Licence

Contributions are accepted under the MIT licence in [`LICENSE`](LICENSE).
