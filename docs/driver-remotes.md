# Remotes a driver draws

A driver knows its device far better than any template does. A Denon knows which sources that receiver has
and what its owner renamed them to; a Roku knows whether it's a television with volume keys or a stick
without. This is how a driver says so, and how the answer reaches the screen.

**Not having one is normal.** Most of the forty-odd drivers should never implement this. See
[Which drivers should draw one](#which-drivers-should-draw-one) — the list of ones that shouldn't is longer,
and it's a list of decisions, not a backlog.

---

## The three kinds of remote

There are now three ways a layout can reach a screen, and it's worth keeping them apart:

| | Where it lives | Varies by | Joins the gallery |
|---|---|---|---|
| **Built-in catalogue** | `src/Remaestro.Hub/Views/TemplateCatalog.*.cs` | nothing — one layout per real handset | yes |
| **Driver template** (`RemoteTemplates`) | the driver, fixed at build time | nothing — one layout for the whole device type | yes |
| **Driver-drawn** (`GetRemoteAsync`) | the driver, computed per device | **the device**: its model, its inputs, what it reported | no |

The first two are declarations. The third is an answer to a question about one unit, so it is never offered
as a template for the next one — handing someone the previous house's input names would be worse than
handing them nothing.

## What a driver author does

Three edits, all in the driver.

### 1. Say the type can

```csharp
public sealed class DenonDriver : IRemaestroDriver
{
    /// <summary>Every unit draws its own — the sources this receiver has, under its owner's names.</summary>
    public bool SupportsDeviceRemotes => true;
```

This is a hint, not a promise. The console reads it to decide which device cards open a remote; it can't ask
thirty devices over gRPC on every redraw. A type that says `true` and a device that then answers `null` is
fine and expected.

### 2. Implement `IRemoteSurfaceDevice` on the **device**

```csharp
internal sealed class DenonDevice : DeviceBase, IInputSourceDevice, IRemoteSurfaceDevice
{
    public Task<RemoteTemplateSpec?> GetRemoteAsync(CancellationToken ct)
    {
        ...
        return Task.FromResult<RemoteTemplateSpec?>(new RemoteTemplateSpec(
            Name, elements,
            Id: "denon.device",
            Description: $"{Name} — its own sources.",
            Icon: "ti:device-speaker", Category: "avr", Brand: "Denon",
            Width: 260, Height: y));
    }
```

On the device, not the driver, because that's where the knowledge is: config, live state, whatever the
hardware has said since it connected.

### 3. Place the elements

`RemoteElementSpec` is one control, in design-surface pixels on a `Width`×`Height` canvas.

```csharp
new RemoteElementSpec(
    X, Y, W, H,
    Kind: "button",        // button | label | dpad | rocker
    Capability: "input.select",
    Shape: "rounded",      // rounded | pill | circle
    Label: "Xbox",         // overrides the vocabulary's label; the text of a `label` element
    Icon: "",              // overrides the vocabulary's icon
    Fill: "#6d3fd4",       // colour override — use for trade dress, not decoration
    Variant: "",           // dpad: cross | ring | round | disc
    Plus: "", Minus: "",   // rocker: the capabilities for + and −
    Args: new Dictionary<string, string> { ["input"] = "GAME" },
    FontSize: 12)          // px; 0 keeps the default 15
```

`Capability` is a canonical id from `CommandVocabulary` — `power.on`, `nav.ok`, `volume.up`,
`transport.play_pause`, `input.select`, `app.launch`, `avr.zone_select`… The hub resolves each to *this*
device's real command, which is what lets the same layout be retargeted when an activity borrows it.

`Args` is what makes a per-device remote possible at all. The vocabulary names about a dozen inputs
discretely (`input.hdmi1`, `input.tuner`) and no real receiver's source list is any of them.
`input.select` + `{["input"] = "GAME"}` is one source key; `app.launch` + `{["app"] = "12"}` is one app key.

---

## The rules that matter

**Draw only what the device can actually do.** Every Denon handset has a row of sound-mode keys. The Denon
driver's remote doesn't, because the driver has no command behind them — and a key that resolves to nothing
is worse than a key that isn't there. If you want the key, add the command first.

**Return `null` rather than guessing.** The Roku driver draws nothing until the box has answered
`query/device-info` once, because before that it doesn't know whether it's a TV. Guessing wrong is either a
volume rocker that does nothing or a television with no volume. `null` sends the hub to the fallback, and the
device picks up its own layout the moment it's talking — the hub asks fresh each time.

**Let absence be the answer.** Not implementing the interface is a decision, and worth writing down where
someone will look for it. `SmartPlugDriver` does exactly that.

**Never throw.** The host swallows exceptions and reports "no remote", so a broken layout costs the device
its remote rather than its existence — but don't rely on it. Errors here are silent by design.

**Vary on what the hardware said, not on config.** Config is what someone typed; state is what the device
reported. `_renames`, `_zones`, `is-tv` are the interesting axes. Which serial port it's on is not.

---

## Where it ends up

```
device draws it  →  GetRemote (gRPC)  →  DriverTemplates.ForDevice  →  RemoteTemplate
                                                                            │
                        ┌───────────────────────────────────────────────────┴────────┐
                        │                                                            │
             a device's own remote                                  an activity's remote
             (Home tap, Add-device offer)                     (ActivityRemotes.Choose, below)
                        │                                                            │
             RemoteTemplates.Instantiate                        ActivityRemoteGenerator.Generate
                        │                                                            │
                    RemoteView ──────────────────────► saved, opened at /v/{id}
```

### An activity's remote — the resolution order

`ActivityRemotes.Choose` (`src/Remaestro.Hub/Activities/ActivityRemotes.cs`), three answers in order:

1. **The activity's own custom remote**, if someone made one — `RemoteView.Custom`. Nothing regenerates over
   it, including a save that clears every control role.
2. **The remote the NAVIGATION device's driver draws**, if it draws one.
3. **A generated remote** from the gallery, which is what every activity got before.

Step 2 still goes through `ActivityRemoteGenerator`, so the borrowed layout is retargeted by role: volume
keys go to the volume device, transport to the transport device, everything else to the navigation device.
An Apple TV's own remote has a volume key on it and the Apple TV is not what makes the noise.

An activity with no device set for volume, navigation *or* transport gets no remote at all — unless it has a
custom one, which outlives that.

**The known cost of step 2.** A borrowed layout only has the keys its own device's remote has. If the
navigation device is a receiver whose remote carries no transport row, the activity remote loses play/pause —
even though the activity names a transport device that could take it. The generic archetype it replaced had
those keys. This is the trade the order asks for: a remote that fits the box you're looking at, instead of one
that covers every role generically. If it bites, the answer is either "make it custom" or a driver that draws
the missing row; a hub-side merge of the two layouts would put keys somewhere nobody placed them.

### A device's own remote

`Home.OpenRemote` asks the driver first, then falls back to `TemplateStore.BestFor` (the type's template,
then a generic archetype by trait, then nothing). `AddDeviceDialog` does the same on the way in — though a
device created a second ago may not have talked to its hardware yet, so what it draws then is the dull
version. It's asked anyway, because once a view is saved for a device nothing asks again.

---

## Compatibility

Drivers are separate processes. An old driver binary against a new hub answers `GetRemote` with
`UNIMPLEMENTED` from its generated base, which `DeviceRegistry.GetDeviceRemoteAsync` catches by status code
and reads as "no remote" — the same answer most devices give anyway. A new driver against an old hub is never
asked. `DriverRemoteTests.A_driver_built_before_this_rpc_existed_reports_unimplemented` pins it.

New fields on existing messages (`RemoteElementMessage.args`, `.font_size`,
`DriverDescriptor.supports_device_remotes`) are additive and default to empty on a driver that doesn't set
them.

---

## Which drivers should draw one

Judged on one question: **does this driver know something about a particular unit that a fixed layout
cannot?** Not "is it a nice device" — the built-in catalogue already has beautiful transcribed handsets for
Apple TV, Roku, Fire TV, Shield, Xbox, PS5 and five AV receivers, and a driver that would only redraw one of
those should ship a `RemoteTemplates` entry instead.

### Done

| Driver | What varies |
|---|---|
| `Denon` | source list + owner's names (SSFUN), zones that answered |
| `Roku` | TV vs stick (`is-tv`), installed apps |
| `SmartPlug` | *nothing — deliberately draws none* |

### Should draw one

| Driver | What varies per unit |
|---|---|
| `Anthem` | zone count and named inputs, like Denon |
| `Eiscp` (Onkyo/Integra) | source list differs wildly by model; the protocol enumerates it |
| `Yamaha` | zones, scene buttons — a unit reports its own scene names |
| `Heos` / `SoundTouch` / `Sonos` | the user's own presets are the remote; nothing else on it matters |
| `WebOs` / `Samsung` / `Bravia` | installed apps and live HDMI labels ("Xbox" not "HDMI 2") |
| `AndroidTv` / `FireTv` | installed apps; whether the box has volume at all |
| `AppleTv` | installed apps; 1st-gen remote has no power or mute key |
| `Kodi` / `Jellyfin` / `Plex` / `Vlc` | which of transport / subtitles / audio-track this build actually exposes |
| `Hubitat` / `HomeAssistant` / `Hue` | a child's capabilities are only known after asking the hub — the single strongest case in the tree |
| `Tivo` / `DirecTv` | subscribed channels and DVR presence |
| `Zidoo` | serial vs network changes which half the commands exist |
| `Ir` | the learned codeset *is* the layout — draw exactly the keys that were learned |
| `Epson` / `BenQ` / `PjLink` | lens memories and inputs vary by model; PJLink reports its input list |
| `Xtream` / `XmlTv` / `HdHomeRun` | favourite channels, if anything |

### Should not

| Driver | Why |
|---|---|
| `SmartPlug`, `Wattbox`, `Lutron`, `Hue`*, `HueSync` | one to three controls. The device card is a better surface than a screen with three keys on it. |
| `Screen`, `InputProxy`, `HidProxy`, `HidInput` | not things a person points a remote at |
| `Activity` | it *is* the remote |
| `Http` | arbitrary by definition; the user's own view is the only sensible layout |
| `PlayStation`, `Xbox` | the catalogue's transcribed media remotes are already right, and nothing about a particular console changes them |

\* `Hue` as a *bridge* draws nothing; a Hue *child* with colour and scenes is arguably in the first list.
Left in the second until someone wants it, on the grounds that a light with a colour picker is a control
panel and not a remote.

The rest of the list is mechanical: pick one from the first table, add the flag, implement the interface,
place the keys. `DenonDevice.GetRemoteAsync` is the reference — the layout is computed top-down from a
running `y`, so a receiver with one more source or one fewer zone doesn't need every coordinate rewritten.
