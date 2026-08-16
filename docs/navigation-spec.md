# Remaestro Content Navigation — Projection Layer Spec (v1)

A **common projection** that lets any driver expose its content as a **browsable, hierarchical library**
of nodes — each with metadata, media assets, and per-item commands — so Remaestro can render one unified
"remote-control" surface over wildly different backends: Jellyfin libraries, Home Assistant rooms/devices,
DLNA/UPnP servers, NAS/file shares, and more.

The projection is deliberately thin: a driver maps *its* model onto a small set of node shapes; the hub and
UI never learn a backend's specifics. Item commands flow through the **existing event bus + rules engine**,
which is what makes the marquee scenario work (navigate Jellyfin → press Play → a rule redirects playback to
the CoreELEC box because the "Projector Movies" activity is active).

---

## 1. Model

### 1.1 `LibraryNode`

The single unit of the projection. A node is either a **container** (browse into it), a **leaf** (an item),
or both (a playable folder). Everything below is a node: a library, a collection, a movie, a season, an
episode, a person, a room, a light, a file.

| Field | Type | Notes |
|---|---|---|
| `id` | string | Opaque, **driver-scoped**, stable. The hub never parses it. |
| `parentId` | string? | For breadcrumbs / "up". Null at a root. |
| `kind` | string | From the **kind vocabulary** (§1.4). Drives icons/layout; not behaviour. |
| `title` | string | Primary label. |
| `subtitle` | string? | e.g. `"2019 · 2h 14m"`, `"Season 3 · 10 episodes"`, `"Living Room"`. |
| `overview` | string? | Long description / synopsis. |
| `isContainer` | bool | Can be passed to `Browse`. |
| `isPlayable` | bool | Has a play-kind command. |
| `childCount` | int? | If known (containers). |
| `metadata` | map<string,string> | Flexible, **namespaced** key/values (§1.5). |
| `images` | ImageRef[] | Poster/backdrop/thumb/logo/icon (§1.2). |
| `commands` | ItemCommand[] | Per-item functions (§1.6). |
| `shape` | string? | How this item is drawn (§1.3). Blank inherits the page's, then the kind's default. |
| `size` | string? | How big, relative to the page (§1.3). Blank inherits the page's. |
| `group` | string? | Section heading this item sits under (§1.8). Blank for a flat listing. |

A node is intentionally flat except for `parentId` + `Browse`; the hierarchy is discovered by browsing, not
by embedding children (so huge libraries page lazily).

### 1.2 `ImageRef`

| Field | Type | Notes |
|---|---|---|
| `kind` | string | `poster` \| `backdrop` \| `thumb` \| `logo` \| `banner` \| `icon` |
| `url` | string | Absolute, hub-reachable. Drivers proxy/sign as needed (see §5). |
| `width` / `height` | int | 0 if unknown. |
| `blurHash` | string? | Optional low-res placeholder. |
| `aspect` | string? | The shape this image *is* (§1.3), when known. Lets the UI pick the one that fits the card instead of cropping. |

### 1.3 Shape and size

`kind` says what a thing *is*; `shape` says what it should *look* like. They usually agree, but not always —
a Jellyfin library's artwork is a 16:9 banner and a collection's is a still, so drawing either at poster
aspect letterboxes a landscape image into a portrait frame. That was the bug this exists to fix.

**Shapes.** `poster` (2:3, films and series) · `wide` (16:9, libraries, collections, episodes) ·
`square` (1:1, albums and artists) · `banner` (very wide, channel and network art).

A shape decides two things: the aspect the card is drawn at, **and how wide the card wants to be** — a 16:9
card at poster width is a stamp, so the grid's column width follows the shape.

**Sizes.** `compact` · `normal` · `large`. Deliberately coarse: a driver says an item matters more than its
neighbours, and the UI decides what that's worth on the screen it's actually on. `large` on a single item
lets it span two columns on a grid wide enough to spare one.

**Resolution order**, applied hub-side at the wire boundary so the UI never falls back:

```
item.shape → listing.shape → default for item.kind
item.size  → listing.size  → normal
```

State it on the listing when a page is uniform (a season of episodes is all `wide`) and on the item only
when it differs from the page around it. `NodeShape.ForKind` in the SDK — mirrored by `NavShape.ForKind` in
the hub — holds the per-kind defaults, so most drivers never set either field.

**Picking the image to match.** Set `aspect` on each `ImageRef` and order them best-first for the node's own
shape. The UI takes the first image whose `aspect` equals the card's shape before falling back to
`kind == poster`/`thumb`. Where the backend can render at a size, ask it for the shape you're going to draw:
Jellyfin's `fillWidth=640&fillHeight=360` for `wide` beats fetching a poster and cropping it in the browser.

### 1.8 Grouping

A listing is a flat list on the wire; `group` is what turns it into sections on screen. Set it to the
heading an item belongs under — `"Collections"`, `"Movies"`, `"Season 2"`, `"Specials"` — and the hub
gathers items with the same value together.

**Sections appear in the order their first member does.** There is no sort key, deliberately: the driver
already controls the order it returns items in, and a second ordering could only ever contradict the first.
Return collections before movies and they appear above them. Sorting the headings by name would put
"Season 10" above "Season 2".

**Label freely.** The hub shows headings only when a listing has more than one, so a group name on every
item costs nothing — opening a collection full of films doesn't grow a lone "Movies" banner. This is what
lets a driver label by kind without checking what else is in the listing.

An item with no group in an otherwise grouped listing keeps its position and gets no heading, rather than
being swept into an invented "Other".

**Grouping can replace a level of browsing.** Jellyfin lists a series as its episodes grouped by season
rather than as a row of season folders: the seasons are still there — they're the headings — and the
episode you came for is one tap closer. Do this when the intermediate level carries no information beyond
the heading it would become.

### 1.4 Kind vocabulary

**See `Remaestro.Sdk.NavKind`**, which is now the definition rather than this list. Still an open set —
drivers may add and the UI falls back sensibly — and `kind` is still a **hint**, never a switch on behaviour:
`isContainer`/`isPlayable`/`commands` decide what's possible.

- **Containers:** `library` `collection` `folder` `series` `season` `album` `artist` `playlist` `genre`
  `person` `room` `area` `category` `channel-list`
- **Playable leaves:** `movie` `episode` `track` `song` `video` `clip` `photo` `channel` `file` `stream`
- **Control leaves (HA-style):** `device` `sensor` `switch` `light` `scene` `climate` `media-player`

These moved into code because something started depending on them being spelled consistently:
`MediaPlayback` (§1.7) says which kinds a device will accept, and a kind spelled two ways matches nothing
while looking entirely correct. It's the same failure the metadata keys had before `MediaFacts`.

### 1.7 What a device can be handed to play

A device declares `MediaPlayback` — the command that plays something, which of its parameters carries the
item, and the kinds it accepts.

The hub used to work this out by looking for a command parameter named something like `url`. That heuristic
offered a Zidoo reached over RS-232 as somewhere to send a film — unarguably a media player, and equally
unable to be given one — and the activity was then generated with no play step and no complaint. The sniff
survives as a fallback for drivers that haven't declared yet; a declaration is believed outright.

**There are three answers, not two, and the third is why.** A driver that leaves `Playback` null hasn't
said anything, and the sniff still runs — which is right for a projector or a lamp, since nothing about
them looks like a media handoff to begin with. But an Xbox's `launch_app` takes a title `uri` and a
webhook's `request` takes a `url`, and neither is a film going anywhere; those declare
**`MediaPlayback.Nothing`**, meaning *asked and answered*, and the hub believes it and stops looking.
Without that third answer a device with a URI-shaped parameter can never get off the destination list.

`kinds` is the part the sniff could never do. A Sonos and a projector both "can play"; only one of them
should be offered as the destination for a film. A device that declares no kinds accepts anything, which is
the old behaviour and the right default — better to offer a target that might not work than to hide one
that would.

**A wrong `kinds` list is worse than no list**, and the failure is invisible: it removes a working
destination from a room with nothing anywhere saying why. Kodi's list omitted `channel` on the reasoning
that a channel isn't something you hand a player a URL for — but HDHomeRun's tuner emits `channel` nodes
over HTTP, and the one box in the house that plays a transport stream was silently missing from the places
you could watch live TV. Leave a kind out only when you know the device refuses it.

**The declaration names a command; what that command does is the driver's business.** One command with one
parameter is the whole contract, and it is deliberately narrower than what devices need underneath:

- A **Sonos** takes two calls. `SetAVTransportURI` on an ordinary URI *loads* it and leaves the transport
  where it was, so a set with no `Play` after it is silence that looks exactly like a speaker ignoring you.
  The driver's `open` is the pair; the hub never learns there were two.
- An **Apple TV** needs a constant beside the URL — AirPlay is `launch_app` with `app=remaestro.stream`
  *and* `url=…`. That is a driver command with one parameter, not a second field on this record. If a
  command needs a constant to *mean* "play this", the device doesn't have a play command; it has a general
  one the driver configures, and the driver is the only thing that knows the constant.
- **Home Assistant**'s `media_player.play_media` wants a `media_content_type` beside the id, and it isn't
  fixed — which is why fixed arguments would not have been the general answer they look like.

Where a device's own vocabulary doesn't line up with §1.4, **say so rather than force it**. Home Assistant's
`media_content_type` looks like a kind list and mostly isn't: `movie`, `episode`, `track` and the rest name
items in an integration's *browse tree*, where the content id is that integration's id. Handing over a URL
only has three buckets — audio, video, picture — so four of our kinds collapse onto `video` and two onto
`music`. Their `channel` is a tuner's channel id where ours is a node whose address is a transport stream,
so it goes over as what it physically is. That is a distinction being dropped on purpose, written down at
the mapping.

### 1.5 Metadata namespacing

`metadata` is free-form but keys should be **namespaced** so the UI can pick what it understands and drivers
can pass through anything:

- Common: **see `Remaestro.Sdk.MediaFacts`**, which is now the definition rather than this list. These keys
  used to live here as prose while the stream keys below lived in code, and prose drifted exactly as you'd
  expect: Jellyfin emitted its own API's `indexNumber` / `parentIndexNumber` for episode and season, Plex
  emitted a runtime and nothing else, and the same episode on two servers didn't look like the same episode.
- Driver-specific: prefix with the driver id, e.g. `jellyfin:seriesId`, `ha:entityId`, `dlna:objectClass`.

#### Work keys — describing *the thing itself*

`MediaFacts` names what a work is, where it sits in a series, and who made it. A driver fills in what its
server knows and leaves the rest out; **a missing key means "not known", never "no"**.

| Group | Keys |
|---|---|
| The work | `year` `releaseDate` `runtimeSeconds` `genres` `officialRating` `communityRating` `tagline` `studio` `originalTitle` |
| In a series | `seriesTitle` `seasonNumber` `episodeNumber` `episodeTitle` `airDate` `seriesStatus` |
| Who made it | `cast` `directors` `writers` — comma separated, billing order, cast capped |
| Music | `artists` `albumArtist` `album` `trackNumber` `discNumber` |
| Where you got to | `positionSeconds` `played` `favorite` |

**`indexNumber` is gone.** It meant an episode's number on one item and a season's on another, so a single
item could never carry both — which made "season 3, episode 5" unaskable. It is now `seasonNumber` and
`episodeNumber`, and a test asserts neither old name comes back.

A `library` node also carries **`libraryKind`** — `movies` · `tv` · `music` · `photos` · `live` · `other` —
so a search can be scoped to the right shelves and a caller knows which of the above to expect. Asking a
music library for a season number is a question with no answer.

#### Media stream keys — describing *this copy*

The keys above describe the **work**; these describe the **file**. They exist because something has to be
able to *choose*: asked to play a film in a particular room, there is otherwise no way to tell the 4K HDR
remux from the 720p copy of the same title, since nobody agreed what to call the fields.

A driver fills in what its server knows and leaves the rest out. **A missing key means "not known", never
"no"** — a copy with no `videoRange` is not thereby SDR. See `Remaestro.Sdk.MediaMeta` for the constants.

| Key | Example | Notes |
|---|---|---|
| `videoResolution` | `3840x2160` | For display |
| `videoHeight` | `2160` | For comparing — don't re-parse the resolution string |
| `videoRange` | `SDR`, `HDR10`, `HDR10+`, `DV`, `HLG` | The flavour matters: a display that does HDR10 may not do Dolby Vision |
| `videoCodec` | `HEVC`, `AV1`, `H264` | Some players can't decode some of these |
| `videoFrameRate` | `23.976` | |
| `audioCodec` | `TrueHD`, `DTS-HD MA`, `EAC3` | |
| `audioProfile` | `Atmos`, `DTS:X` | Separate from the codec — Atmos rides on TrueHD *or* EAC3 |
| `audioChannels` | `7.1` | The layout, not the raw count |
| `audioLanguage` | `eng` | The default track |
| `bitrateKbps` | `48000` | The tie-break when two copies match otherwise |
| `fileSizeBytes` | `64424509440` | |
| `container` | `mkv` | |
| `subtitles` | `eng, fra` | Languages present |
| `edition` | `Director's Cut` | The cut, where the server distinguishes one |
| `versionCount` | `2` | Above one means picking a copy is a real decision |

Note that `runtimeSeconds` and `year` are **not** repeated here — they describe the work and are already
defined above. Emitting a second key for the same thing is how Plex items ended up with no runtime showing
in the console for months.

### 1.6 `ItemCommand`

A function invocable on a node. Distinct from device commands (which are static per device type); item
commands are **per-node and dynamic**.

| Field | Type | Notes |
|---|---|---|
| `id` | string | Node-scoped command id, e.g. `play`, `resume`, `queue`, `shuffle`, `toggle`. |
| `label` | string | e.g. "Play", "Resume from 34:12", "Play on…". |
| `kind` | string | `play` \| `resume` \| `queue` \| `shuffle` \| `toggle` \| `open` \| `custom` |
| `params` | ConfigField[]? | Optional inputs (e.g. a target, a position). |

### 1.6.1 Reserved command ids — `resolve`

Everything in §1.6 is a node's *own* vocabulary: the driver invents the ids, lists them on the node, and is
the only thing that has to understand them. **Reserved ids are the exception — the hub sends them, and no
node ever advertises them.** There is one today.

| Id | Means | Advertised on a node? |
|---|---|---|
| `resolve` | "What is this item, and where does it stream from?" | **No.** The hub sends it regardless. |

`resolve` is a **question**. It exists because the hub, not the driver, decides where something plays: the
Library page's "Play on…", `POST /api/nav/{deviceId}/play-with-activity`, and the assistant's `play_media`
all ask the source what an item is, then route the answer themselves — into an activity that sets a room up,
or straight at a box in another room. See `Library.razor`, `ApiEndpoints.cs`, `AssistantTools.cs`.

**What a driver must return.** The media facts, in `CommandResult`'s result map: at minimum `streamUrl` and
`mediaType` for anything playable, plus `title` and `positionSeconds` where they exist. The key spellings are
the ones in §1.5 and in `MediaMeta`; the caller reads them by name.

**What a driver must *not* do — the part that is the whole reason this section exists:**

- **Emit no event.** Not `library.play`, not `library.queue`, not any other event that a rule could route.
- **Start no playback**, on the server or on any client it can reach.
- **Change nothing** a second `resolve` would see differently. It is safe to call twice, and callers do.

That is not fussiness about purity. §4's redirect pattern means a `library.play` from a source *is* a
playback instruction to every rule the user has written against it. A driver that announces a resolve
therefore plays the item **once via the rule** — wherever that rule points — and the caller then plays it
**again** at the destination the person actually asked for. One press, two playbacks, and the wrong one
first.

The worst case is not the duplicate, it is the **denial**. Every caller resolves *before* it asks the
activity gate, so when the gate refuses — the room is already in use — the person is told "the activity
wouldn't start" while the phantom has already put the film on somewhere else. Spoken, that is an assistant
saying nothing happened, in a room where something did. `PlexDriver` and `HdHomeRunDriver` did exactly this
from the day each was written, because this section did not exist and only `JellyfinDriver` had guessed
right.

**If you don't support it**, fail the command. `CommandResult.Fail` is reported to the user as "couldn't work
out how to play that", and the item is then visibly unroutable rather than invisibly mis-routed. **Do not let
an unrecognised id fall through to your play branch** — a `default:` that plays is how both drivers above
got it wrong, and it is why the id is named here rather than left to convention.

The SDK spells it `NavItemCommand.Resolve`, with `NavItemCommand.IsQuery(commandId)` as the guard to put in
front of an `Emit`. On the wire it is an ordinary `InvokeItem` with `command_id = "resolve"`
(`driver.proto`, `InvokeItemRequest.command_id`).

**What this section is worth, stated honestly.** It is prose, and prose is all that stands between a new
driver and the bug above: `resolve` arrives through the same method as `play`, so an `InvokeItem` whose
`default:` branch plays will play — and the hub cannot tell that apart from a driver that resolved
correctly and routed as well. A dedicated method would make the guarantee structural instead, because a
resolve could not reach a play branch at all, and a driver that had never implemented it would say so in
the protocol's own words rather than by doing the wrong thing quietly. That is a change to the wire
contract and is not made here. Until it is, **the guarantee is only as good as this page** — which is why
the rule above is written with its consequence attached rather than as a bare MUST NOT.

---

## 2. API — what a driver implements

Authors implement `INavigableDevice` on their `IRemaestroDevice` (SDK), and set
`IRemaestroDriver.SupportsNavigation => true`. The SDK's `DriverHost` then serves the gRPC nav surface; the hub
exposes it over HTTP. Everything is **per device instance** (a configured Jellyfin server, a specific HA hub).

```csharp
public interface INavigableDevice
{
    // List the children of a node. nodeId null/empty = the library root(s).
    Task<NodeListing> BrowseAsync(string? nodeId, BrowseOptions options, CancellationToken ct);

    // Full detail for one node (metadata, assets, commands, maybe related).
    Task<LibraryNode?> GetNodeAsync(string nodeId, CancellationToken ct);

    // Search across the library. Required, and named for its rpc (SearchNodes) like the two above.
    // A library that can't be searched returns an empty listing itself and says so in a comment:
    // this had a default doing that invisibly, and HDHomeRun inherited it by misspelling the name.
    Task<NodeListing> SearchNodesAsync(string query, BrowseOptions options, CancellationToken ct);

    // Invoke a per-item command. Returns a result AND (by convention) emits an event for rules (§4).
    Task<CommandResult> InvokeItemAsync(string nodeId, string commandId,
        IReadOnlyDictionary<string, string> args, CancellationToken ct);
}

public sealed record BrowseOptions(int Offset = 0, int Limit = 100, string? SortBy = null, string? Filter = null);
public sealed record NodeListing(LibraryNode? Node = null, IReadOnlyList<LibraryNode> Items = default!, int Total = 0);
```

### 2.1 gRPC wire contract (`driver.proto`)

Added to the existing `Driver` service (all no-ops on drivers that don't support navigation):

```proto
rpc Browse     (BrowseRequest)     returns (NodeListingMessage);
rpc GetNode    (NodeRefMessage)    returns (LibraryNodeMessage);
rpc SearchNodes(SearchNodesRequest) returns (NodeListingMessage);
rpc InvokeItem (InvokeItemRequest) returns (ExecuteCommandResponse);
```

`DriverDescriptor` gains `bool supports_navigation = 9;`. Messages mirror the model 1:1
(`LibraryNodeMessage`, `ImageRefMessage`, `ItemCommandMessage`, `NodeListingMessage`).

### 2.2 Hub HTTP API

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/nav/{deviceId}/browse?node={id}&offset=&limit=&sort=&filter=` | Children of a node (root if `node` omitted). |
| GET | `/api/nav/{deviceId}/node/{nodeId}` | One node's full detail. |
| GET | `/api/nav/{deviceId}/search?q=&limit=` | Search. |
| POST | `/api/nav/{deviceId}/invoke` `{ nodeId, command, args }` | Invoke an item command. |
| GET | `/api/nav/sources` | Devices where `supportsNavigation == true`. |

Paging: `offset`/`limit` with `total` in the response. Errors: 404 (unknown device/node), 501 (device
doesn't support navigation), 400 (bad command).

---

## 3. Backend mappings (illustrative)

| Projection | **Jellyfin** | **Home Assistant** | **DLNA/UPnP** | **NAS / file share** |
|---|---|---|---|---|
| root children | user Views (libraries) | Areas (rooms) | ContentDirectory root | share roots |
| container kinds | library, collection, series, season, boxset, genre, person | room, device group | container objects | folders |
| leaf kinds | movie, episode, audio, photo | light, switch, sensor, media_player | item (video/audio/image) | files |
| `id` | Jellyfin ItemId | `entity_id` / `area_id` | UPnP `@id` | path |
| images | `/Items/{id}/Images/{type}` | entity picture / icon | `<upnp:albumArtURI>` / thumb | generated/typed icon |
| play command | `play` → stream URL | `toggle`/`turn_on` (control leaves) | `play` → res URI | `open` → file URL |
| metadata | year, runtime, rating, overview, indexNumber… | state, attributes | dc:*/upnp:* | size, modified, mime |

The **same** four methods serve all of them; only the mapping differs.

---

## 4. The redirect pattern (why item commands are events)

`InvokeItemAsync` does two things: it returns a `CommandResult` (so a caller/UI gets data like a stream URL),
**and** the device emits an event onto the bus. That event is what the rules engine can intercept —
so the *effect* of "Play" is policy, not hard-wired.

**Scenario — "Projector Movies" redirects Jellyfin playback to CoreELEC:**

1. User browses the Jellyfin device's library in the nav UI, opens a movie, presses **Play**.
2. Hub calls `InvokeItem(jellyfin, movieId, "play")`. The Jellyfin device resolves a playable stream URL and
   **emits** `library.play` with data `{ nodeId, title, streamUrl, mediaType:"video", positionSeconds }`.
   (It may also start playback on a default target, or do nothing itself — driver's choice.)
3. A **rule**, enabled only while the **"Projector Movies" activity** is active, triggers on
   `library.play` from the Jellyfin device and runs `kodi.open` with `url = ${event.data.streamUrl}` against
   the CoreELEC box — so the film plays on the projector, not wherever Jellyfin would default.
4. Change the active activity (say "Bedroom TV") and a *different* rule routes the same Play to a different
   target. The navigation surface never changes; the routing is activity + rules.

This reuses everything already shipped: events (`Emit`), the `EventBus`, `RulesEngine` triggers, token
templating (`${event.data.streamUrl}`), and activities as state. Navigation adds only the *browse* surface.

Recommended standard events: `library.play`, `library.queue`, `library.resume`, `library.open`
(data always includes `nodeId`, `title`, and — for playables — `streamUrl` + `mediaType`).

---

## 5. Semantics & rules for implementers

- **Ids are opaque and stable.** Encode whatever you need; never assume the hub parses them. Prefer
  `kind:backendId` internally if it helps you route within the driver.
- **Roots.** `Browse(null)` returns the top level. A driver may have multiple roots (Jellyfin: several
  libraries) — return them as the first listing with `Node == null`.
- **Lazy + paged.** Never inline children. Honour `offset`/`limit`; set `Total` so the UI can page/scroll.
- **Images must be hub-reachable.** If the backend needs auth, either embed a token in the URL or expose a
  proxy route; the UI just does `<img src>`.
- **Playables resolve at invoke time.** Don't put stream URLs in `Browse` payloads (they expire / are big).
  Resolve on `InvokeItem`/`GetNode`, and hand them back via the event + `CommandResult`. This is also why
  `resolve` (§1.6.1) is its own command rather than being folded into `GetNode`: a detail sheet can sit open
  for minutes before anyone presses Play, and a URL fetched to draw it would be stale by then.
- **An item command either does something or answers something, never both.** The reserved `resolve`
  (§1.6.1) answers; everything else does. Guard the `Emit` in `InvokeItemAsync` with
  `NavItemCommand.IsQuery`, and never let an id you don't recognise reach your play branch.
- **Control leaves** (HA light/switch) use `toggle`/`custom` commands and carry live state in `metadata`;
  they're the same projection, just not "playable".
- **Capability discovery.** `supports_navigation` on the descriptor + `isContainer`/`isPlayable`/`commands`
  per node fully describe what's possible; the UI never needs backend knowledge.
- **Versioning.** This is v1. Additive fields only within v1; breaking changes bump the projection version
  advertised on the descriptor.

---

## 6. Rollout

1. ✅ **SDK contract + models** (`INavigableDevice`, `LibraryNode`, `NodeListing`, `ImageRef`, `ItemCommand`,
   `BrowseOptions`) — the surface drivers implement. → `src/Remaestro.Sdk/Navigation.cs`
2. ✅ **gRPC** nav rpcs + `DriverHost` serving them (no-op when a device isn't `INavigableDevice`).
   → `driver.proto` (`Browse`/`GetNode`/`SearchNodes`/`InvokeItem`, `supports_navigation`), `DriverHost.cs`
3. ✅ **Hub** `NavigationService` + `/api/nav/*` endpoints; `InvokeItem`'s emitted event flows to the bus over
   the existing event stream (so rules can redirect). → `src/Remaestro.Hub/Navigation/NavigationService.cs`
4. ✅ **Jellyfin driver** implements the projection (root Views → folders → movies/series → seasons/episodes),
   with `play` emitting `library.play` carrying the resolved stream URL. → `JellyfinDriver.cs`
   *Verified end-to-end against a live Jellyfin: browse, search, node detail, and `play` → `library.play`
   on the bus with a resolved stream URL.*
5. ✅ **UI** — a "library remote" (`/library[/{deviceId}]`, `Components/Pages/Library.razor`): breadcrumbed
   drill-down, poster grid (watched badges, resume progress, hover-play), a detail sheet (backdrop, overview,
   metadata, per-item Play/Resume/Queue → toast), search, source switcher; entry point on the Home remote.
   *Verified in-browser against a live Jellyfin: browse → drill-down → detail → Play → `library.play` on the
   bus, plus search, light/dark, and mobile.*
