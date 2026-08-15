# Sample drivers

Four drivers that ship in reMaestro, copied here unmodified. They are the real files rather than reductions,
so what you read is what runs in a house.

| Sample | Lines | What it shows |
|---|---|---|
| **[`Remaestro.Drivers.Http`](Remaestro.Drivers.Http)** | 189 | The smallest useful driver. `DeviceBase` plus an `HttpClient`. **Start here.** |
| **[`Remaestro.Drivers.Lutron`](Remaestro.Drivers.Lutron)** | 159 | `TcpLineDevice` — a text line protocol over a socket, with connect, reconnect and framing handled for you. |
| **[`Remaestro.Drivers.Screen`](Remaestro.Drivers.Screen)** | 410 | `ByteLink` — a **binary** protocol, five bytes at 2400 baud over RS-232. Also worth reading for a device that is strictly write-only and declines to invent a position it cannot measure. |
| **[`Remaestro.Drivers.Jellyfin`](Remaestro.Drivers.Jellyfin)** | 1004 | The large end: `INavigableDevice` for a browsable media library, plus `ListenerSupervisor`. |

Each is a directory you can copy. Change the `TypeId`, the config schema and the commands, and you have a
plugin.

---

## How a driver actually starts

**Not with `dotnet run`, and there is deliberately no launch profile in any of these projects.**

A driver is not a web application you visit. **The hub starts your process** — it picks a free port in the
18000–20000 band, puts it in the child's environment, and dials it:

```
ASPNETCORE_URLS=http://127.0.0.1:19204
ASPNETCORE_ENVIRONMENT=Production
DOTNET_ENVIRONMENT=Production
```

`DriverHost.RunAsync` reads that variable, serves gRPC h2c on it, and answers `Describe`. Your `Program.cs`
is one line and there is nothing to configure.

Three consequences that catch people out:

- **There is no browser page.** Opening the URL yourself gets you nothing useful; the only thing on the other
  end is a gRPC service.
- **The working directory is the hub's, not yours.** Anything you resolve relative to the current directory
  will not be where you left it. Use the directory of your own assembly.
- **`ASPNETCORE_URLS` is a .NET name on a language-neutral contract**, and it is what the hub sets today. If
  you are writing a plugin in another language, that is the variable to read — one `split("//")` gets you the
  address.

A `Properties/launchSettings.json` would describe a way of starting a driver that the product never uses, so
the two samples that had one (scaffolding from `dotnet new`) have had it removed. The `appsettings.json`
files went with it for the same reason — configuration reaches a driver through its config schema and
`DeviceContext`, not through a file beside the binary.

### So how do you test one?

Not by running it. Build it, then drive it as the hub does — the plugin SDK's own
[conformance suite](../../dotnet/tests/Remaestro.ProxyAgent.Tests) is the pattern for the proxy protocol, and
for a driver the equivalent is to open a `GrpcChannel` against the address you started it on and call
`Describe` yourself. Everything in `driver.proto` is reachable that way with no hub present, which is how the
cross-language proofs of this contract were done.
