# The plugin platform — the plan

What this is: the synthesis of five audits (`audit-drivers-proxies.md`, `audit-media-libraries.md`,
`audit-remote-templates.md`, `audit-ai-and-settings.md`, `audit-packaging-marketplace.md`, 4,416 lines
between them) into a phase order, plus the decisions the user took while they ran.

Read the audits for evidence. This page is only for *what to do, in what order, and why that order*.

> **A note for readers of the public repository.** This page is published essentially unedited, because a
> roadmap that has been sanded down is not worth reading. Three consequences:
>
> - **The five audits it cites are not published.** They are internal engineering notes that walk the hub's
>   source line by line, and most of what they say would be unreadable without it. Where this page cites one,
>   the citation is a pointer you cannot follow; the claim it supports is stated here in full.
> - **`#nnn` is an issue in a private tracker**, and file paths like `src/Remaestro.Hub/…`,
>   `docs/cloud.md` or `site/privacy.html` are in repositories you do not have. They are left in rather than
>   stripped so that the reasoning stays checkable by the people who *can* follow them.
> - **Where an item has since grown a long internal note, this copy carries the short form and a pointer
>   into `docs/`** — Phase 0's items 1, 2 and 5 are abridged that way, and so is **Phase 6's status note**,
>   which is the longest of them and is written for a plugin author rather than for the hub's own
>   maintainers. They are shorter, not different: no claim on this page is weaker than the internal one
>   except the sentence named below.
>
> **One sentence in §6 is softened on purpose, and it is the only claim anywhere that differs**: it describes
> the privilege a plugin runs with, without naming the specific deployment hardening it comes from. The claim
> it makes is unchanged and is the one that matters — a plugin can do anything the hub can.
>
> §6 is the part to read if you only read one. It is the honest statement of what installing a plugin costs,
> and it is deliberately not reassuring.

---

## 1. The finding that reshapes the request

The ask was framed as "a whole new area of the product". It is mostly not. **The out-of-process,
any-language seam already exists and works.**

- `DriverProcessManager` launches every driver as **its own process over gRPC h2c on loopback**, with the
  design intent stated in its own comment: *"A crashing driver can't take the hub down."*
- `driver.proto` — the only proto in the repo, 537 lines, one service, 18 RPCs — already carries **media
  library `Browse`/`GetNode`/`SearchNodes`/`InvokeItem`**, **`RemoteTemplateMessage`**,
  **`ConfigField`/`show_when`**, options, EPG, apps and diagnostics.
- `DriverManifest` already caches **self-describing descriptors**, versioned by a *reflective hash of the
  protobuf contract itself* plus the driver binary's stamp.
- **`src/Remaestro.Sdk` already exists** — 3,624 lines, used by all 43 drivers. Measured by reflection it
  references `Remaestro.Grpc` and **nothing else of ours**: no hub types, no internals. **Publishable as-is
  modulo metadata.** The untangling everyone would budget for is not there.
- **`src/Remaestro.ProxyAgent` is already a third-party-shaped proxy** — merged, zero project references,
  with a 273-line conformance suite. Counting the C++ firmware there are three implementations of that
  protocol.

And the claim was tested rather than argued. **Two agents independently built a Python driver from
`driver.proto` with stock `protoc` and drove it through the hub's real `ResolveDriver`, real
`ProcessStartInfo` and real `DriverConnection`.** `Describe`, `CreateDevice`, `ExecuteCommand`, `GetState`,
`StreamEvents` with heartbeats, `DisposeDevice` — all green, **with no hub change**, and a missing RPC
correctly returned `UNIMPLEMENTED`. Two facts only running could produce: **cwd is inherited from the hub**,
and **the shebang must be an absolute interpreter path** (so `#!/usr/bin/env python3` cannot select a
virtualenv, and Windows has no shebang at all).

So the work is **generalise, harden, package and document** — not build.

### The genuinely missing half

Getting a plugin *onto* the box is the obvious gap. The non-obvious one, and the one that actually blocks
everything: **the hub cannot find a plugin that is already there.** The driver list is 43 hard-coded entries
in `appsettings.json:9-53`, and *nothing anywhere enumerates `REMAESTRO_DRIVERS_DIR`*. A correctly built
plugin in the correct folder is simply never launched.

---

## 2. Decisions taken

Taken by the user during the audits. Each is settled; the reasoning is kept because the reasoning is what
makes them re-derivable later.

| # | Decision | Why it went that way |
|---|---|---|
| Trust | **A plugin is trusted, exactly like an in-repo driver.** No sandbox, no capability grants. | Self-hosted: you chose what to install. The counter-argument — that a marketplace of third-party binaries makes "you chose it" do a lot of work — was put and overruled. |
| Shape | **Plan plus a working proof**: a non-.NET plugin built to prove the contract before the SDK ships. | Converts every .NET assumption from a footnote into a blocker on something real. It already paid: the cwd and shebang findings came from running, not reading. |
| SDK version | **The SDK versions independently**, and the proto carries the compatibility statement. | A NuGet package pinned to the hub's version is unusable to a third party. Breaks `Directory.Build.props`'s one-version rule for exactly one component, knowingly. |
| Negotiation | **Fix version negotiation before the SDK ships.** | After third parties exist, *adding* negotiation is itself a breaking change — the exact problem it solves. |
| AI reach | **Acting tools on `console` by default; the remote/voice path is opt-in per tool**, with the consequence printed on the plugin's page. | The same argument that let the console keep `system.send_raw` when the remote lost it (`ai-safety.md` §8.5). On the wire: `acts: true` + `"remote"` in `surfaces` — no new field needed. |
| Identity | **A plugin learns user id *and* display name.** | Chosen deliberately over the opaque-id recommendation. Consequence recorded in §6. |
| Registry key | **A plugin-registry signing key may live in CI.** | `operations.md:181-183` names this exact revisit condition — a second trust anchor compromisable without reaching stable boxes. The index key signs no hub, no OS, no driver, and is rotatable by an app release. **We still do not sign other people's plugins**: a signature from us over a third-party binary reads as a warranty we cannot give. |
| Marketplace | **A GitHub repo, submission by pull request.** Not the cloud. | A marketplace on `cloud.remaestro.app` puts an account-bearing paid service in the install path for a free plugin, breaking `docs/cloud.md`'s *"cloud replaces effort, never capability"*. |

Accepted on the auditors' recommendation, as engineering rather than product calls: plugin settings live in a
new `[Document]` collection keyed `(userId, pluginId)` (which gets the backup-coverage build guard for free);
plugins get a `/plugins/{id}` console page now and the `Settings.razor` tab registry is a **later refactor,
explicitly not a prerequisite**; package format is a signed `tar.gz` plus `plugin.json`, one archive per
architecture.

### Why not containers

Not preference. **The hub cannot run containers on two of its three deployment shapes** — there is no
runtime on the appliance at all, and under Docker the socket is on the *supervisor*, not the hub.
`docs/local-services.md:23-36` rejected this same design once already. Measured: a .NET plugin is
**15.5 MB** self-contained/single-file/trimmed and **116 MB** without those flags; framework-dependent is
impossible because the appliance has no shared `dotnet`.

---

## 3. Phase order

The ordering principle: **anything that is a breaking change once strangers exist comes first.** Everything
in Phase 0 is cheap now and expensive-to-impossible later.

### Phase 0 — things that must land before a single package is published

1. ~~**Version negotiation, both directions.**~~ **Shipped.** `DescribeRequest` carries `hub_protocol`;
   `DriverDescriptor` carries `protocol_version` and an optional `min_hub_protocol`. Every guard that
   existed before it protected *the hub from an old driver*; this is the other direction, which is the one a
   published SDK creates. See [`docs/driver-protocol.md`](../driver-protocol.md) §2.
2. ~~**A declared capability list.**~~ **Shipped**, in two halves, because the three `supports_*` descriptor
   booleans and the six runtime `supported` bools were two different bugs. `repeated string capabilities`
   fixes the *question* — what a driver implements is knowable before anything is called — and an
   `Availability` enum beside every `supported` bool fixes the *answer*, which used to mean "unknown
   device", "not implemented" and "it threw" all at once. The single exception was `ListBridgedDevices`,
   which returns `Supported = true` on a throw with the comment *"an unreachable bridge shouldn't read as
   'this isn't a bridge'"* — **that comment was the bug report for the other five**. See
   [`docs/driver-protocol.md`](../driver-protocol.md) §3.
3. **Kill the silent default.** `INavigableDevice.SearchAsync` returns an empty listing by default, which is
   how HDHomeRun's search came to return nothing forever (`#255`). Removing it after the first publish is a
   breaking change to strangers. Pair with an **SDK startup assertion** that warns when declaration and
   implementation disagree — the shape the codebase already uses in `WarnAboutTemplatedDefaults`.
4. **Resolve the proto/SDK naming divergence.** The proto says `SearchNodes`/`Browse`/`GetNode`; the SDK says
   `SearchAsync`/`BrowseAsync`/`GetNodeAsync`. **Every generated non-.NET SDK will carry the proto's names**,
   so a second language makes this worse, not better.
5. **Liveness at the driver boundary (`#152`).** ~~Nothing restarts a crashed driver today.~~ The framing
   here was wrong: nothing *restarts*, and nothing should — the dangerous case is the hang rather than the
   crash, and a hub that kills a driver on a guess costs a room going dark. What was missing was that a
   driver which stops working produced **no signal of any kind**, and the hub now reports one.
   <br>**The protocol half is shipped**: the heartbeat declares its interval, so a threshold is no longer a
   constant taken from one SDK's default; it declares whether it is independent of command handling, which
   the protocol *asks* rather than requires because a rule nobody can check invites the reader to trust
   something false; and a driver can declare a **hold** — "I am deliberately waiting, until *T*". See
   [`docs/driver-protocol.md`](../driver-protocol.md) §4.
   <br>~~Still open, and hub-side rather than protocol: no driver call carries a deadline.~~ **Shipped** —
   one interceptor on the channel rather than an edit at each of 25 call sites, so the twenty-sixth is
   bounded without anybody remembering. Streaming is deliberately untouched: a deadline on `StreamEvents`
   would kill every healthy driver in the house on a timer.
6. **A secret-redaction obligation that survives leaving C#.** `DriverHost.cs:255` registers config secrets
   for redaction automatically, so a C# plugin gets it invisibly and **a Python plugin gets nothing** — skip
   it and every captured diagnostic ships the device's password. There is no wire-level equivalent. This is
   the item a plugin author would never think of unaided, and under a trust model with no sandbox it is the
   sharpest asymmetry in the whole any-language promise.

### Phase 1 — make the hub able to find and launch a plugin

~~Enumerate `REMAESTRO_DRIVERS_DIR` instead of reading 43 hard-coded entries.~~ **Shipped.** This paragraph
was written before any of it was built, and **two of its sentences are wrong**, corrected here rather than
left to be copied out by somebody reading the plan on its own:

- The variable is **`REMAESTRO_PLUGINS_DIR`**, and it overrides the root a hub looks for plugins under. The
  43 configured drivers are still read from configuration; installed plugins are appended to that list, and
  configuration wins a name collision.
- `plugin.json` carries **`exec` alone, as a full argv** — not `exec` + `args`. One field is better anyway:
  two invite the question of whether `exec` may contain spaces, which is the question an argv exists to
  delete. **The normative contract is
  [`docs/plugin-manifest.schema.json`](../plugin-manifest.schema.json)** — published, versioned with the
  proto, and what the hub is tested against; [`docs/driver-protocol.md`](../driver-protocol.md) §6 is the
  prose beside it. This page is a plan and loses to both.

The rest of the line stands and shipped as written: set `WorkingDirectory` on launch and pass a
neutrally-named `REMAESTRO_DRIVER_URL` alongside `ASPNETCORE_URLS`; fix `StampFor`, which stamps one file's
size and mtime — **a multi-file plugin with a launcher entrypoint serves a stale descriptor forever,
silently**, which is precisely the failure that comment was written about.

Plugins live at `<REMAESTRO_DATA>/plugins/<id>/<version>/` — **specifically not under `app/`**, because
`SystemdDeployment.ForgetPreviousAsync` deletes all but the newest two version directories.

### Phase 2 — publish the SDK

Metadata only, plus discipline: `PackageId`, version, licence, repository, authors. Independent semver per
the decision above. **First release is the only cheap moment to seal, hide or rename anything**, so do the
public-surface pass before publishing rather than after. Note `net10.0`-only is a real constraint on who can
consume it, and that **GitHub Packages requires auth even to restore public packages** — verified. That makes
GH feeds fine for dogfooding and *not* a public channel; NuGet.org is the public one.

**The NuGet.org publish is deferred indefinitely, and the reason matters more than the status.** The gate
set earlier was *hold until the working proof is cashed*. `#264` cashed it — a Python plugin built from the
published proto, signed, installed by URL and driven by the hub — and the decision on reading that was to
keep holding: *"we can defer nuget publish much longer yet."*

The reason is what makes this a decision rather than a delay: **while nothing is on NuGet.org, removing or
renaming anything in the SDK is free.** Three changes since that gate was set would each have been a
breaking change to strangers the day after publication — `#255` deleted a default interface member and
renamed a method to match its rpc, `#262` reshaped the descriptor, and `#265` changed what `plugin.json`
says. **That churn is evidence the deferral is right, not evidence the SDK is unstable**: every one of the
three was a correction, and a published package would have turned each into a major version, a compatibility
shim, or a wrong thing kept because removing it was no longer allowed.

So the sentence above — *first release is the only cheap moment to seal, hide or rename anything* — is still
true, and its practical form today is that **every** moment is that moment. The public-surface pass is not
urgent while nothing is published; it becomes the gate on the day a publication date exists. Nothing else in
this phase changes: GitHub Packages remains the dogfooding feed, `net10.0`-only remains a real constraint on
who could consume it, and NuGet.org remains the public channel whenever it is opened.

### Phase 3 — the proof, as a real deliverable

A non-.NET plugin, end to end, installed the way a user would install it. Both audits left a reproduction
recipe in an appendix, so this starts from a working base rather than from scratch.

### Phase 4 — packaging and install

Signed `tar.gz` + `plugin.json`, one archive per architecture (the appliance data partition is **3.0 GiB and
does not grow** — see `#256`). Per-publisher keypairs pinned by the registry on first publication, so every
later version must be signed by the same key. **Install-by-URL must work with no registry at all, and should
be built first.**

### Phase 5 — the registry

A GitHub repo, submission by PR, CI-signed index. The index carries names, versions, URLs and SHA-256s —
**not a signature from us over anyone's binary.**

### Phase 6 — the per-area extensions

Now, and not before, the things each audit designed: media types declared on the descriptor with
`plays_as` as the load-bearing field; remote-template resources on disk under the plugin layout;
AI tools with `acts`/`surfaces`; generalised `ConfigField`; the `/plugins/{id}` console page.

> **Audited 2026-08-20, and four of the five were already built.** This paragraph read as though none of it
> had happened while §4 below said *"Settled by `#268`"* about the first item — one document contradicting
> itself in two sections. The standard applied was *a field a proto declares and nothing reads is not
> done*: each "done" was traced to the code that consumes it, not to the line that declares it. Abridged
> here per the note at the top; the long form is `docs/plugins/plan.md`.

**1. Media types and `plays_as` — done.** §4 below is the argument. It is not merely declared: the hub
translates it in one place and asks the destination list about the result at a single production call site,
so a plugin's invented kind reaches every player that already accepts what it plays as, and nothing below
the routing boundary has heard of a media type. Two edges remain — validation and matching disagree about
case, so `plays_as: "Video"` is refused though it would have matched everything; and the value is checked
against the whole `NavKind` set rather than the playable part of it. The Python sample has no media-type
support, so a Python plugin cannot declare one today.

**2. Remote-template resources — done.** A plugin ships pictures under its own `assets/` folder and names
them `plugin:<path>`; the hub resolves the reference into the one form that draws on every surface, bounded
at 32 KiB a file and 256 KiB a package and refusing rather than truncating. The audit's recommendation was
departed from in one place, argued rather than dropped: a hub-origin URL does not resolve on the phone,
whose web view is not on the hub's origin and authenticates with a header an `<img>` cannot send. Fonts are
refused (licensing) and sounds deferred (no consumer exists).

**3. AI tools with `acts`/`surfaces` — done, and the two fields have different answers.**

`surfaces` is **enforced twice, and the second is the guarantee.** A plugin's tools are added to the
catalogue the model is given, per assistant, rebuilt every round; they are filtered on the way out and then
**refused again at the call, before your process is started** — because a model can name a tool it was not
offered, which makes filtering necessary and insufficient. Declaring no surface means offered nowhere.

`acts` **gates nothing at runtime, and is not meant to.** It is read only by a screen and a log line. That
is the settled decision rather than an unfinished gate: the hub cannot check what a tool does, because the
doing is on your side of the call — so `acts: true` beside `remote` in `surfaces` is your opt-in, and the
product's job is to make it legible to the person who installed you rather than to overrule it. Read the
`AssistantToolSpec` doc comments before assuming a hub-side veto exists.

Open around it: there is a per-tool and per-plugin size ceiling but none on the sum across plugins on one
surface, and there is no user-confirmation primitive on the assistant path for any tool, ours or yours.

**4. Generalised `ConfigField` — partly done.** The message carries everything the audit asked for, and
`ConfigField` is now one grammar across four contexts rather than one — device config, command parameters,
assistant-tool parameters and media-type facts. Per field, though, what the hub actually *honours* is
uneven, and a plugin author should know which is which before relying on one:

| field | state |
|---|---|
| `options` | **Honoured** — a chooser, with an escape to free text. |
| `managed` | **Honoured** — kept out of the create form, and shown in a group that names the owner. |
| `show_when` | **Honoured** — genuinely conditional. One key, `=`, `\|`-ORed values; no ANDing. Hiding a field does not drop its stored value. |
| `sensitivity` | **Honoured by the console's forms** — a declaration beats the hub's guesses and the value is never rendered back. `WRITE_ONLY` is **not** yet honoured at rest: it is stored like any other value. Do not rely on it to mean "not kept". |
| `advanced` | **Half** — a disclosure in the create form, ignored in the edit form. |
| `min` / `max` | **Advisory.** They pick a slider when the type is `number` and *both* are present, and nothing revalidates a bound on save. |
| `options_key` | **Works for command arguments, not for config.** No config form asks for the list today. |

**There is no hub-side validation of declared metadata** — not `required`, not a range, not membership in
`options`. Validate your own config when you are asked to create a device; that is the boundary that holds,
and it is the one the design intends to hold.

**5. The `/plugins/{id}` console page — this was the only item genuinely not started, and it is built now**,
at `/plugins/installed/{id}`. It shows one plugin at length: where it is installed and what argv is
launched, the media kinds it declares and what each plays as, the pictures it ships and any it refused, its
whole tool list, and its pinned publisher with the record of any override.

> **It is not yet the settings screen the design calls for**, and the gap is worth stating plainly because
> it is larger than the page: **there is no plugin-level settings schema on this wire at all.**
> `ConfigField` hangs off a device type, a command or a tool — never off the plugin — and there is no rpc
> to read or write plugin settings and no store behind one. A plugin has a page and nothing to configure on
> it. That is a phase of its own.

---

## 4. The one that is backwards, and the decision Phase 6 took

From the media audit, and it is the sharpest single finding in the set:

> **The better-declared a house's playback devices are, the harder a new media kind fails.**

`MediaPlayback.Accepts` is an **allow-list on the destination side**. An undeclared device accepts anything;
Kodi declares 10 kinds, Sonos 2. So a plugin inventing a media kind works *only* on drivers that have not
done their homework, and a playable leaf with no destination renders **zero buttons**. That is exactly
backwards, and it directly threatens the user's standing constraint that media items stay playable.

**Settled by `#268`, and the decision was the smaller of the two available.** The allow-list is untouched:
no device driver changed, `MediaPlayback.Accepts` is the same method, and nothing below the routing boundary
has heard of a media type. What changed is that a driver may now *declare* its kind on the descriptor with a
`plays_as`, and the hub asks the destination list about that instead — so `recipe` plays as `video` and
reaches every box that already accepts one.

The alternative, widening the allow-list so an undeclared kind is accepted by default, would have made
`kinds` mean nothing and put a film back on a Sonos. The cost of the route taken is **two vocabularies where
there was one**: `kind` open and `NavKind` closed, meeting at `plays_as` and nowhere else. A driver may
invent a kind and may not redefine one. See `docs/navigation-spec.md` §1.4.1.

---

## 5. Documentation debt that is owed regardless

These are published sentences that are already imprecise or become false. They are not blocked on any phase.

- **`docs/ai-safety.md` §3 guarantees are all about a capability, a page, a value or a prompt — not one is
  about a tool**, because the tool set was a compile-time constant. The failure mode is not a guarantee going
  false; it is *"`do` cannot name a command the device has not got"* **staying literally true while ceasing
  to be the boundary**, because a plugin tool is not `do`. A guarantee that is technically true and
  practically hollow is worse than one that is plainly wrong, because nobody re-reads it. The audit names
  each sentence with replacement wording.
- **`site/privacy.html` says the update check is the only request the box makes and "there is no other
  request".** A plugin index fetch makes that false. **It must change in the same commit as the fetcher.**
- **The privacy pages' method is enumeration, and a plugin cannot be enumerated in advance.** This bites
  harder now that display names cross the boundary.

---

## 6. Stated plainly, because the user should be able to see what was chosen

**Installing a plugin is running an arbitrary binary with the hub's privileges** — everything the hub can
reach, read and write, it can — **and no packaging, signing or marketplace choice changes that.** Provenance
is the only control that exists, which is why signing is about *who published this* and never about *this is
safe*.

**Household member display names reach every installed third-party binary**, unbounded, by the identity
decision. A plugin can log them, store them, or send them anywhere.

Both follow from the trusted model, which was chosen with the counter-argument on the table. They are
recorded here so the marketplace can present risk honestly rather than reassuringly.

---

## 7. Deliberately deferred

Paid plugins (decide nothing that forecloses it; the cloud takes no payment today). The `Settings.razor` tab
registry. Sounds in remote templates — **no consumer exists anywhere**, so designing a format now would be
designing for a feature that does not exist. A Node SDK, which is the second language and should be built
from the mould the first one proves.
