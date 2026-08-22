# What a proxy should run on

The proxy concept is settled — a thing near the AV rack that dials out to the hub and speaks to equipment
the hub cannot reach. What it *runs on* is not, and this page is the survey behind that choice.

**The question that prompted it.** The ESP32 board in `hardware/proxy-board/` does serial, IR, Bluetooth and
Ethernet, and cannot do USB input. A Raspberry Pi does all five and drags a Linux distribution — with its
patching, its attack surface and its update story — into a device meant to be an appliance. Is there a third
thing that is neither?

**The short answer: no single part does all of it, and the split is not where you would guess.** It is not
"MCU is weaker". On IR the MCU is *better*. The line falls on USB host, and specifically on USB *audio*.

**What this is not.** Nothing here has been run. Every claim is sourced to a datasheet, a vendor doc, or
this repo, and says which. The ESP32 board itself has never been fabricated (`hardware/proxy-board/README.md`
§What this is not), so "what we have today" already means "on paper".

---

## 1. The finding that makes most of this cheap

Before the hardware: **`usb.input` is almost free on the hub side, whatever carries it.** That was not
obvious and it changes the cost of every option below.

`HidHostCodec` — written for `bt.host`, a Bluetooth remote relayed by an ESP32 — takes a frame of
`[reportId, ...the report exactly as the remote sent it]` and works out what was pressed **by sniffing the
report's shape**, not by parsing a report descriptor:

| Shape | Read as |
|---|---|
| 8 bytes, byte 1 zero | boot keyboard — up to six held usages on page 0x07 |
| 2 bytes | a consumer-page usage, little-endian — the media and transport keys |
| 1 byte | a consumer usage in one byte |
| anything else | `HID_R<id>_<hex>` — a stable name, bindable by pressing it |

A USB HID host emits exactly the same thing. So a `usb.input` role on *any* proxy — Pi, ESP32, anything —
can reuse `HidHostCodec`, `HidUsage` and the `inputproxy` driver **unchanged**, and a binding made against a
remote on one kind of proxy keeps working on the other. `HidUsage`'s own comment already argues the case:
a descriptor parser "would still get exotic remotes wrong, and this way strange buttons are learnable
rather than unusable."

Two consequences worth stating out loud:

- **The hub-side work is the config model, not the input path.** See `#220`.
- **Boot protocol is not enough.** The fallback means a boot-protocol-only host still *works*, but a remote's
  volume and play/pause keys live on the consumer page and a boot keyboard never reports them. Whatever does
  USB host must use the report protocol, or the buttons people actually press arrive as nothing.

---

## 2. What actually has to be true

| Requirement | Why it is on the list |
|---|---|
| **Multiple serial** | A rack has a projector and a receiver. Two is the current ceiling and the reason is the chip — `hardware/proxy-board/README.md` §Serial. |
| **IR TX + RX** | TX to drive, RX to learn. |
| **BT HID (device)** | Be a remote, for Apple TV / Android TV / Fire TV. |
| **BT host** | Relay a remote somebody already owns. |
| **USB input** | The one that started this: a 2.4 GHz receiver plugged in where a person is sitting, hub in a rack. |
| **USB audio** | **The hidden requirement.** `docs/voice-seam.md` line 1345 describes the reference rig as "a 2.4 GHz USB dongle presenting both the buttons *and the microphone*" — 16 kHz mono 16-bit. The voice remote is not a HID device, it is HID **plus** a UAC capture device, and `HidInputDriver` already has a `micDevice` field pairing the two. |
| **Ethernet / PoE** | One cable, no Wi-Fi credentials, no radio in a metal rack. |
| **Not a Linux distro to maintain** | The user's stated objection, and a fair one. |

The last two are where the survey gets interesting, and the **USB audio** row is where it ends.

---

## 3. The candidates

| | ESP32-S3 (today's board) | ESP32-P4 + C6 | Pi Zero 2 W | Pi 4 / 5 / CM4 |
|---|---|---|---|---|
| Serial | 2 (chip ceiling) | **5 + 1 LP** | 1 honest | **6** on Pi 4 |
| IR | **RMT — hardware timed** | **RMT** | kernel LIRC, soft PWM | kernel LIRC |
| BT HID device | BLE only | BLE only (via C6) | **classic + BLE** | **classic + BLE** |
| BT host | BLE only | BLE only | **classic + BLE** | **classic + BLE** |
| USB input | **no** — board revision | **yes**, HS, own PHY | yes | yes |
| USB audio | component exists, no PSRAM on this board | plausible | **yes, it's ALSA** | **yes** |
| Ethernet | W5500 over SPI (6 GPIO) | **native EMAC** | **none** | native |
| PoE | designed, Olimex front end | possible | via a USB dongle it cannot spare | PoE HAT |
| OS to maintain | **none** | **none** | a distro | a distro |
| Fit with this repo | **it is this repo** | ESP-IDF port | reuses the appliance | reuses the appliance |

### ESP32-S3 — what exists, and why USB host is a board revision not a firmware change

Two facts kill the easy version:

1. **The S3's USB-OTG and USB-Serial-JTAG controllers share a single PHY — only one can run at a time**
   ([Espressif](https://docs.espressif.com/projects/esp-idf/en/stable/esp32s3/api-reference/peripherals/usb_host.html)).
   The board's USB-C is how `/flash` installs firmware and how Improv hands over Wi-Fi credentials, over the
   same cable. Taking the PHY for host mode takes that away.
2. **A host has to source VBUS.** `NETLIST.md` wires J1 as a sink — 5.1 kΩ on both CC lines, which is
   precisely what makes it *not* a host port.

So it wants a second connector, a 5 V load switch, and a decision about which controller owns the PHY. That
is a rev-2 board. Then the ceilings: full speed only; external hubs need `CONFIG_USB_HOST_HUBS_SUPPORTED`
and each downstream device consumes a host channel, so "plug in a hub" has a small hard limit; and the
WROOM-1-**N8** on the BOM has **no PSRAM**, which is the wrong module to buffer an audio stream on.

**What it is excellent at, and should keep doing:** IR. `RMT` is a hardware timing peripheral. Linux
`gpio-ir-tx` bit-bangs with interrupts disabled. The MCU is the better IR device and it is not close.

### ESP32-P4 + ESP32-C6 — the "best of all worlds" part, and its bill

On paper this is the answer, and it is worth being precise about why
([datasheet](https://www.espressif.com/en/products/socs/esp32-p4)): dual RISC-V at 400 MHz, **USB 2.0
high-speed OTG with an embedded PHY** *and* a separate USB Serial/JTAG controller — so the §3 conflict
above simply does not exist — a **native 10/100 Ethernet MAC** (no W5500, six GPIOs back, one less SPI
bus), and **5 UARTs plus a low-power UART**, which retires the two-port ceiling outright.

The bill:

- **No radio at all.** Wireless is a companion — a C6 over SDIO via ESP-Hosted. That is two chips, a
  second flash, and a hosted-network layer between the firmware and Wi-Fi.
- **BLE only**, through that companion. No classic Bluetooth.
- **Arduino support is beta.** The P4 Core Board landed in arduino-esp32 3.3.6 (January 2026) and the
  state of it in April 2026 was "basic functionality, use ESP-IDF for serious work". This firmware is
  Arduino end to end — ArduinoJson, IRremoteESP8266 pinned at 2.9.0, NimBLE pinned at 1.4.3, Improv,
  ESP32-BLE-Keyboard from git. A P4 build is plausibly an **ESP-IDF port of the whole sketch**.
- 768 KB SRAM and no PSRAM unless the module has it.

**Verdict: the right chip, at the wrong moment.** It is the natural rev-3, and the reason to keep the
firmware's role abstraction honest in the meantime.

### Raspberry Pi — and the OS objection, which is smaller than it looks

The objection is real but this repo has already paid most of it. `docs/appliance.md`: A/B root slots, RAUC,
**read-only root**, `/etc` as an overlay, and a boot marked good only when the application actually answers.
A proxy image is that machinery minus the hub. And the proxy's security posture is unusually good for a
Linux box, because **the tunnel dials out** — a proxy needs no listening port, no forwarded port, and no
sshd.

What survives the objection, honestly: kernel CVEs are still yours, `docs/appliance.md` says **no appliance
has ever installed a bundle**, and a bundle carries the root slot only — so a kernel is still reflash-only.
That is a real gap, but it is a gap the appliance already has and already tracks.

**Which Pi matters more than people expect.** For this job the Zero 2 W is the *weakest* Pi, not the
cheapest good one:

- **No Ethernet, so no PoE.** Adding it costs a USB dongle on the single USB port — the port whose entire
  purpose here is the remote receiver.
- **One micro-USB OTG port**, supplying little current. Anything past one dongle is a powered hub.
- **One honest UART.** It is a Pi 3-era part: one PL011, one mini-UART, and the onboard Bluetooth is holding
  one of them. `disable-bt` frees the good one and costs `bt.hid` outright; `miniuart-bt` keeps both and
  demotes BT to the mini-UART, whose baud follows the VPU core clock and drifts unless the clock is pinned.
  Fine at 9600, not at 115200.

A **Pi 4 / 5 / CM4 with a PoE HAT** has six UARTs, real Ethernet, PoE, and USB ports to spare. The Zero 2 W's
case is size and price for a Wi-Fi, one-dongle, in-the-room proxy — which is a real case, and the one that
was actually asked for. Both should be supported; they are the same image.

---

## 4. The thing that decides it

**USB audio.** Everything else has two answers; this has one.

The dongle this product ships with presents buttons *and* a microphone, and `docs/voice-seam.md` §7 is a
long argument about endpointing its 16 kHz stream. On a Pi that stream is ALSA and `arecord`, which is what
the existing code already does. On an MCU it is `usb_host_uac` (UAC 1.0,
[component registry](https://components.espressif.com/components/espressif/usb_host_uac)) — which exists,
and then has to push ~32 KB/s continuously through a tunnel that frames at 4 kB **with no flow control**,
interleaved with a room's live control traffic, on a module with no PSRAM to buffer it.

`docs/proxy-ota.md` §2 already refused to push a firmware image down that tunnel for exactly these reasons,
and audio is the same argument with a deadline attached.

**So: a voice remote needs a Linux proxy. A plain remote does not.** That is the line, and it is a clean
one — it splits the lineup by what the peripheral is, not by what the customer bought.

---

## 5. What was considered and rejected

| Option | Why not |
|---|---|
| **USB/IP or VirtualHere** — export the dongle, hub attaches it as a local USB device | Needs `vhci-hcd` on the *customer's* Docker host, which contradicts `docs/supported-hardware.md`'s whole claim that any Linux box works. Moves a kernel dependency onto the one machine we do not control, and fails in ways nobody can diagnose over a message. |
| **A second MCU doing USB host into the main board over UART** | Two firmwares, two update paths, and a UART between them, to avoid one board revision. |
| **RP2350 / Pico 2 W, TinyUSB PIO host** | USB host by PIO is a bit-banged full-speed stack. Wireless is a CYW43 with BTstack. All of it is more DIY than the ESP32 path and better at none of it. |
| **Off-the-shelf: Waveshare ESP32-S3-ETH + PoE module, Olimex ESP32-POE-ISO** | Genuinely good boards — Olimex's PoE front end is already copied part-for-part into our netlist. Neither has a USB host port, so they solve the half we already solved. Worth keeping as the "buy one today" answer. |
| **Global Caché iTach Flex / IP2SL** | IR and serial over Ethernet, and `GlobalCacheTransport` already speaks to them — so this is a *supported path today*. No USB input, no Bluetooth, and it is what the proxy board competes with on price. |
| **Full hub on the proxy** | 512 MB on a Zero 2 W, and it is a proxy. |

---

## 6. Recommendation: a lineup, not a winner

The user's instinct — "the right answer will be all of the above" — is correct, and the reason is §4: the
split is a property of the peripheral, not of the customer.

| Tier | Hardware | For | State |
|---|---|---|---|
| **1** | ESP32-S3 board / any ESP32 | IR, serial, GPIO, BLE HID, Harmony RF. PoE. No OS. | firmware exists, board designed, **never fabricated** |
| **2** | Pi image — Zero 2 W, 3, 4, 5, CM4 | everything in tier 1 *except* IR precision, **plus** USB input, USB audio, classic Bluetooth, many serial | **`usb.input` works**; the rest is §8 |
| **3** | ESP32-P4 + C6 | tier 1 plus USB input, native Ethernet, five UARTs, still no OS | rev-3, when the Arduino core is not beta |

Three rules that make it a lineup rather than three products:

1. **One tunnel protocol.** A Pi proxy is a second implementation of the board side of `TunnelFrame`, not a
   second subsystem. The console, adoption, `ProxyLoopback` and the hardware pickers must not learn which
   kind they are talking to except where it genuinely differs.
2. **One role vocabulary.** `usb.input` means the same frames on every tier — §1 is what makes that free.
   A role a tier cannot do is absent from its config, not a different feature.
3. **Roles are advertised, not assumed.** The hub already refuses a GPIO an ESP32 chip does not have
   (`EspPins`). It needs the same honesty across tiers: a Zero 2 W with `disable-bt` has no `bt.hid` and the
   console should say so, rather than offering a role that silently never fires.

Rule 3 is the one that will be got wrong first. It is also the one `docs/proxy-ota.md` §5 already models —
a table of verdicts, each of which is a sentence about why something is refused.

---

## 7. What tier 2 actually is, now that it exists

`usb.input` is built and tested end to end. The rest of tier 2 is not, and the shape of what is missing is
the useful part of this section.

### The agent

The **`Remaestro.ProxyAgent`** project, which ships in the SDK repository — a .NET console app that is **a
second implementation of the board side of `TunnelFrame`**, exactly as §6 rule 1 asks. It dials out, says
hello, serves channels, answers pings, and relays button presses. It holds its own copy of every wire
constant rather than referencing the hub's, and a drift test asserts each one still matches — the same
bargain the C++ firmware makes, because a constant shared by a compiler for the Pi and not for the board is
the worst of both.

Why .NET and not Python or Go: the appliance is already a .NET image, and — the deciding reason — the whole
of the protocol can then be tested in the same suite with no network and no device. `CLAUDE.md` is explicit
that tests must not reach the network, and a proxy agent that could only be tested by plugging in a remote is
a proxy agent nobody would test.

**It reads evdev, not hidraw, and that is a correction to §1 rather than an exception to it.** §1 argues that
a USB HID host emits `[reportId, ...raw report]` and that `HidHostCodec` can sniff the usage page out of the
report's shape — true, and the right design for an MCU, which has no descriptor parser and cannot afford one.
A Pi is not writing a USB HID host. The kernel already is one, and it has already done the descriptor parse
that §1 goes out of its way to avoid: `usbhid → evdev` is the path `docs/supported-hardware.md` lists as *the
supported one* for USB remotes. Synthesising a report from a keycode so the hub could infer its way back to
the same answer would be a lossy round trip through the one step Linux exists to have already done — and it
would lose exactly the buttons §1 warns about, because shape-sniffing is a guess and the kernel's answer
isn't.

So the agent sends the keycode, as `OP_DATA` carrying `[HidHostOp.Evdev, codeLo, codeHi, value]`, and the hub
names it. The proxy still holds no vocabulary: it relays the number the kernel gave it. `HidHostOp.Report` is
still accepted on the same role, so a future MCU USB host needs no hub change and §1's claim holds in full.

**The names are a mirror, and this was the sharpest thing found.** `HidUsage` says it is "deliberately the
Linux `KEY_*` vocabulary rather than a new one", because a remote paired straight to the hub arrives through
evdev. That vocabulary is not abstract — it is `KeyCodes` in `src/Remaestro.Drivers.HidInput/`, which is what
actually names a remote plugged into the hub, and it is what every existing binding was made against.
`EvdevKeyNames` in the hub is a copy of that table with a drift test that reads the driver's source and fails
on any disagreement, in either direction. A first draft of it was written from the kernel headers instead and
disagreed in five places — it called keycode 385 `KEY_RADIO` where the driver calls it `KEY_DVD`, and fell
back to `EVDEV_766` where the driver falls back to `KEY_766`. Every one of those would have produced a button
that is bindable on a Pi, bindable on the hub, and portable between neither — which is precisely the failure
§1's whole argument exists to prevent, arriving through the door nobody was watching.

### The config model

The pin model does not transfer, and the decision was **one `ProxyDevice` with a second validator**, not a
shape of its own. A separate shape would have meant a second `ProxyStore`, `ProxyDiscovery`, `ProxyRoll`,
`ProxyTunnelServer`, `ProxyHardware` and `ProxyLoopback` — the "second subsystem" rule 1 forbids, paid for
twice.

`ProxyBoards` answers *which family*, out of the `Chip` field that already rides mDNS, `/info`,
`TunnelHello` and the persisted `ProxyGlimpse`, so nothing gained a field to carry it. Family — `esp` or
`linux` — selects the validator; **nothing branches on which Pi it is**, which is the line that stops this
becoming the support matrix `HostBoard` warns about. A `ProxyPin` gained a `Device`: a selector that is a
name to match, or an absolute path. A name is the better answer and the path is the escape hatch, which is
the opposite of what it looks like — `/dev/input/event3` is handed out in enumeration order, so unplugging a
keyboard renumbers it and a proxy quietly starts listening to nothing with a config that still looks right.

### Rule 3, applied

An ESP32's role list does not contain `usb.input`; a Pi's contains nothing else *yet*. Both absences are the
hub's, both are said in words when a config asks for the wrong one, and neither tier is offered a role that
would silently never fire.

---

## 8. What tier 2 still needs, in the order it gets cheaper

Each of these is a handler in the agent plus a line in `ProxyBoards.Roles`. None is a change to the tunnel,
the config model, or the hub's picker — which is the claim this slice was built to make true.

| Role | What it takes |
|---|---|
| `serial` | Open the device path with `System.IO.Ports`, and relax `ChannelOpen.Problem()`'s baud table, which is an ESP32 UART's list and not a USB adapter's. The codec is already passthrough. |
| `ir.tx` / `ir.rx` | `/dev/lirc0` and the `LIRC_MODE_PULSE` ioctls — the one place the agent needs a P/Invoke, and the one role where §3 is right that the MCU is simply better. |
| `bt.host` | BlueZ over D-Bus. Emits `HidHostOp.Report` frames, which already work on this codec, so the hub side is done. |
| `bt.hid` | BlueZ again, in the other direction, against `HidCodec`. |
| **USB audio** | The one that isn't a role. §4 is the argument that this tier exists *for* the microphone, and the tunnel is the wrong pipe for it — 4 kB frames, no flow control. It needs a decision about a second channel or a second transport before it needs any code. |

Two smaller things this slice left honest rather than fixed:

- **A gamepad button reports as a key.** `KeyCodes.Modality` calls `BTN_*` a pad and `KEY_*` a key; the
  codec emits `EVT KEY` for both. Harmless for a remote, wrong for a controller.
- **The console has no picker for any of this.** `Proxies.razor` takes `Chip` from a network sighting and
  has never had a chip control, so a Pi adopts and validates correctly today but the wiring dialog still
  draws a GPIO field for a machine that returns no pins. `PinChoices.For` returns empty for a Linux proxy,
  so it draws an empty picker rather than a wrong one — honest, and not yet good.

---

## 9. Open

- **Whether tier 1 gets USB host in a rev-2 board**, or waits for the P4 at tier 3. Cost is a connector, a
  load switch and the PHY decision; the gain is buttons-only USB remotes without a Linux box, and the loss
  is flashing over the port that used to do it.
- **Which Pi is the image's tested target.** Zero 2 W is what was asked for and the weakest for it; a Pi 4
  with a PoE HAT is the one that meets every row of §2 at once. `docs/supported-hardware.md` already has the
  vocabulary for this — one tested, the rest community.
- **Whether the proxy image is the appliance image minus the hub**, sharing RAUC, slots and `BootConfirm`,
  or something smaller. Sharing is the obvious answer and inherits a known gap: bundles carry the root slot
  only, so a kernel is reflash-only.

Sources: [ESP-IDF USB Host (S3)](https://docs.espressif.com/projects/esp-idf/en/stable/esp32s3/api-reference/peripherals/usb_host.html) ·
[ESP-IDF USB Host (P4)](https://docs.espressif.com/projects/esp-idf/en/stable/esp32p4/api-reference/peripherals/usb_host.html) ·
[ESP32-P4 product page](https://www.espressif.com/en/products/socs/esp32-p4) ·
[ESP32-P4 datasheet](https://documentation.espressif.com/esp32-p4_datasheet_en.html) ·
[usb_host_uac](https://components.espressif.com/components/espressif/usb_host_uac) ·
[esp-idf HID host example](https://github.com/espressif/esp-idf/blob/master/examples/peripherals/usb/host/hid/README.md) ·
[arduino-esp32](https://github.com/espressif/arduino-esp32) ·
[Olimex ESP32-POE-ISO](https://www.olimex.com/Products/IoT/ESP32/ESP32-POE-ISO/open-source-hardware) ·
[Waveshare ESP32-S3-ETH](https://www.waveshare.com/wiki/ESP32-S3-ETH)
