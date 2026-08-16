namespace Remaestro.Sdk;

/// <summary>
/// A device that exposes a browsable, hierarchical content library — media (Jellyfin/DLNA), rooms and
/// entities (Home Assistant), files (NAS) — projected onto a common <see cref="LibraryNode"/> shape.
/// See docs/navigation-spec.md. Implement this on your <see cref="IRemaestroDevice"/> and set
/// <c>IRemaestroDriver.SupportsNavigation =&gt; true</c>; the host serves the navigation surface automatically.
/// </summary>
public interface INavigableDevice
{
    /// <summary>List the children of a node. <paramref name="nodeId"/> null/empty = the library root(s).</summary>
    Task<NodeListing> BrowseAsync(string? nodeId, BrowseOptions options, CancellationToken ct);

    /// <summary>Full detail for one node (metadata, assets, commands).</summary>
    Task<LibraryNode?> GetNodeAsync(string nodeId, CancellationToken ct);

    /// <summary>
    /// Search the library. A library that genuinely can't be searched still implements this and returns an
    /// empty listing — deliberately, in one line, with a comment saying so.
    /// <para>
    /// <b>There is no default implementation, on purpose.</b> There was one — <c>Task.FromResult(new
    /// NodeListing())</c> — and it cost HDHomeRun its search for the driver's whole existence: that driver
    /// spelled its override <c>SearchNodesAsync</c> while the member was called <c>SearchAsync</c>, so the
    /// misspelling compiled as an ordinary extra method, the default won, and every channel search answered
    /// "searched, found nothing" with no error anywhere. The rule it leaves behind, which the SDK's other
    /// ten defaults already keep: <b>a default interface member may state a negative — "this driver declares
    /// no events", "this device is not navigable" — but never a result.</b> "Found nothing" is a result, and
    /// only the driver is in a position to give it.
    /// </para>
    /// <para>
    /// Note which way the surprise ran: over the raw wire this case is <i>louder</i>, because an
    /// unimplemented rpc answers <c>UNIMPLEMENTED</c> and the hub shows the error. The C# convenience
    /// invented a silent failure the protocol itself does not have.
    /// </para>
    /// <para>
    /// <b>And the name is the proto's, plus the suffix .NET asks for.</b> Every member here is now
    /// <c>&lt;rpc name&gt;</c> + <c>Async</c> — <c>Browse</c>→<c>BrowseAsync</c>,
    /// <c>GetNode</c>→<c>GetNodeAsync</c>, <c>SearchNodes</c>→<c>SearchNodesAsync</c>
    /// (<c>driver.proto:47-49</c>). It used to be <c>SearchAsync</c>, the one member that dropped a word
    /// from its own rpc — and that is exactly the name the HDHomeRun author wrote instead, because they
    /// wrote what the wire says. An SDK generated for any other language carries the proto's names and
    /// cannot be talked out of them, so leaving the divergence would have made it permanent the moment a
    /// second language existed, while renaming costs three call sites today.
    /// </para>
    /// </summary>
    Task<NodeListing> SearchNodesAsync(string query, BrowseOptions options, CancellationToken ct);

    /// <summary>
    /// Invoke a per-item command (play/queue/toggle…). Returns a result and, by convention, emits an event
    /// (e.g. <c>library.play</c>) so rules can route the effect (see the spec's redirect pattern).
    /// <para>
    /// <b>Except for <see cref="NavItemCommand.Resolve"/>, which emits nothing.</b> It is a question the hub
    /// asks — what is this item, where does it stream from — and the hub routes the answer itself. Guard the
    /// <c>Emit</c> with <see cref="NavItemCommand.IsQuery"/>, and never let an id you don't recognise fall
    /// through to your play branch: that is not "unsupported", it is a second playback nobody asked for.
    /// </para>
    /// </summary>
    Task<CommandResult> InvokeItemAsync(string nodeId, string commandId, IReadOnlyDictionary<string, string> args, CancellationToken ct);
}

/// <summary>Paging / sort / filter options for a browse or search.</summary>
public sealed record BrowseOptions(int Offset = 0, int Limit = 100, string? SortBy = null, string? Filter = null);

/// <summary>A page of children plus the browsed node itself (for breadcrumbs) and the total child count.</summary>
public sealed record NodeListing
{
    public LibraryNode? Node { get; init; }
    public IReadOnlyList<LibraryNode> Items { get; init; } = [];
    public int Total { get; init; }

    /// <summary>
    /// How this page's items should be shaped, when they're all alike — so a driver returning two hundred
    /// films says "poster" once rather than on every one. An item that differs overrides it.
    /// See <see cref="NodeShape"/>.
    /// </summary>
    public string Shape { get; init; } = "";

    /// <summary>
    /// How large this page's cards should be — see <see cref="NodeSize"/>. A wall of channel logos wants
    /// compact; a shelf of four featured films wants large. Blank is the normal size for the shape.
    /// </summary>
    public string Size { get; init; } = "";
}

/// <summary>
/// One unit of the projection — a container (browse into it), a leaf (an item), or both. Everything is a
/// node: a library, a collection, a movie, a season, an episode, a person, a room, a light, a file.
/// </summary>
public sealed record LibraryNode
{
    public string Id { get; init; } = "";
    public string? ParentId { get; init; }
    public string Kind { get; init; } = "item";          // see the spec's kind vocabulary
    public string Title { get; init; } = "";
    public string? Subtitle { get; init; }
    public string? Overview { get; init; }
    public bool IsContainer { get; init; }
    public bool IsPlayable { get; init; }
    public int? ChildCount { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<ImageRef> Images { get; init; } = [];
    public IReadOnlyList<ItemCommand> Commands { get; init; } = [];

    /// <summary>
    /// The section this item belongs under — "Collections", "Season 2" — or blank for a flat listing.
    /// <para>
    /// A name rather than an ordering, because the driver already controls the order it returns items in
    /// and a second ordering field could only ever disagree with the first. Sections appear in the order
    /// their first member does, so a driver that wants collections above movies returns them above movies.
    /// </para>
    /// </summary>
    public string Group { get; init; } = "";

    /// <summary>
    /// The shape this item wants to be shown at — see <see cref="NodeShape"/>. Blank inherits the page's
    /// shape, and failing that the sensible default for its kind. Set it when an item differs from the
    /// page around it: a library or a collection is a wide card even in a grid of posters.
    /// </summary>
    public string Shape { get; init; } = "";

    /// <summary>
    /// How big this item's card should be relative to the rest of the page — see <see cref="NodeSize"/>.
    /// Blank means the page's size. Use it to make one item the feature of a shelf, not to micro-manage
    /// pixels: the UI owns the actual dimensions, this only says "bigger than its neighbours".
    /// </summary>
    public string Size { get; init; } = "";
}

/// <summary>
/// What a node <i>is</i>. An open set — a driver may invent one and the UI falls back sensibly — but these
/// are the ones the console ships affordances for, and the ones anything filtering by kind can rely on.
/// <para>
/// Named here rather than left in the spec because the spec's prose version already drifted once: the
/// descriptive metadata keys lived in §1.5 as a list, two drivers spelled them differently, and the same
/// episode on two servers stopped looking like the same episode. <c>MediaFacts</c> closed that; this closes
/// the same gap for kinds, which <c>MediaPlayback</c> now depends on to say what a device will accept.
/// </para>
/// <para>
/// <c>kind</c> is a <b>hint, never a switch on behaviour</b> — <c>IsContainer</c>, <c>IsPlayable</c> and the
/// item's own commands decide what's actually possible.
/// </para>
/// </summary>
public static class NavKind
{
    // ---- Containers: things you browse into ---------------------------------------------------------

    public const string Library = "library";
    public const string Collection = "collection";
    public const string Folder = "folder";
    public const string Series = "series";
    public const string Season = "season";
    public const string Album = "album";
    public const string Artist = "artist";
    public const string Playlist = "playlist";
    public const string Genre = "genre";
    public const string Person = "person";
    public const string Room = "room";
    public const string Area = "area";
    public const string Category = "category";
    public const string ChannelList = "channel-list";

    // ---- Leaves: things you play ---------------------------------------------------------------------

    public const string Movie = "movie";
    public const string Episode = "episode";
    public const string Track = "track";
    public const string Song = "song";
    public const string Video = "video";
    public const string Clip = "clip";
    public const string Photo = "photo";
    public const string Channel = "channel";
    public const string File = "file";
    public const string Stream = "stream";

    // ---- Leaves you control rather than play ---------------------------------------------------------

    public const string Device = "device";
    public const string Sensor = "sensor";
    public const string Switch = "switch";
    public const string Light = "light";
    public const string Scene = "scene";
    public const string Climate = "climate";
    public const string MediaPlayer = "media-player";

    public static readonly IReadOnlyList<string> Containers =
    [
        Library, Collection, Folder, Series, Season, Album, Artist, Playlist, Genre, Person,
        Room, Area, Category, ChannelList,
    ];

    public static readonly IReadOnlyList<string> Playable =
    [
        Movie, Episode, Track, Song, Video, Clip, Photo, Channel, File, Stream,
    ];

    public static readonly IReadOnlyList<string> Controls =
    [
        Device, Sensor, Switch, Light, Scene, Climate, MediaPlayer,
    ];

    public static readonly IReadOnlyList<string> All = [.. Containers, .. Playable, .. Controls];

    /// <summary>
    /// Whether this is one the console ships affordances for. False is not an error — the vocabulary is
    /// open — it only means nothing can be assumed about it.
    /// </summary>
    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind);
}

/// <summary>
/// How large a card is drawn, relative to what its shape normally gets. Deliberately coarse — a driver
/// says an item matters more, and the UI decides what that's worth on the screen it's actually on.
/// </summary>
public static class NodeSize
{
    /// <summary>Denser than usual — a wall of channel logos or icons.</summary>
    public const string Compact = "compact";

    /// <summary>What the shape normally gets.</summary>
    public const string Normal = "normal";

    /// <summary>Bigger than its neighbours — a feature row, or a short shelf worth dwelling on.</summary>
    public const string Large = "large";
}

/// <summary>
/// The shapes a browsable item can be shown at. A shape decides both the aspect the artwork is cropped to
/// and how wide the card wants to be, because a 16:9 card at poster width is unreadably small.
/// </summary>
public static class NodeShape
{
    /// <summary>2:3 — film and series artwork, the shape those posters are actually drawn at.</summary>
    public const string Poster = "poster";

    /// <summary>16:9 — libraries, collections, episodes, anything whose art is a still or a banner.</summary>
    public const string Wide = "wide";

    /// <summary>1:1 — albums and artists.</summary>
    public const string Square = "square";

    /// <summary>Very wide, for a row of channel or network art.</summary>
    public const string Banner = "banner";

    /// <summary>What a kind looks like when nobody said. Keeps drivers from having to state the obvious.</summary>
    public static string ForKind(string kind) => kind switch
    {
        "library" or "collection" or "folder" or "playlist" or "genre" => Wide,
        "episode" or "video" or "channel" or "photo" => Wide,
        "album" or "artist" or "track" or "person" => Square,
        _ => Poster,
    };
}

/// <summary>A media asset for a node. <c>Kind</c>: poster | backdrop | thumb | logo | banner | icon.</summary>
/// <param name="Aspect">
/// The shape this particular image is, when it's known — so a caller wanting a wide card can choose the
/// still over the poster rather than cropping one into the other. See <see cref="NodeShape"/>.
/// </param>
public sealed record ImageRef(string Kind, string Url, int Width = 0, int Height = 0, string? BlurHash = null, string Aspect = "");

/// <summary>A per-node function. <c>Kind</c>: play | resume | queue | shuffle | toggle | open | custom.</summary>
public sealed record ItemCommand(string Id, string Label, string Kind = "custom", IReadOnlyList<ConfigField>? Params = null);

/// <summary>
/// The ids that arrive at <see cref="INavigableDevice.InvokeItemAsync"/>. Most of them are a node's own —
/// whatever it listed in <see cref="LibraryNode.Commands"/>, which the driver invented and the driver
/// understands. <b><c>Resolve</c> is not one of those</b>: it is reserved, the hub sends it, and no node
/// ever offers it.
/// <para>
/// That asymmetry is the whole reason this class exists. <c>resolve</c> was load-bearing in three hub call
/// sites — the Library page's "Play on…", <c>/api/nav/{id}/play-with-activity</c>, and the assistant's
/// <c>play_media</c> — while being written down in no proto, no spec and no interface. Two of the three
/// drivers that had to answer it had never heard of it, so it fell through to their play branch and a
/// <i>question</i> about an item announced itself as a <i>press</i>. See <c>docs/navigation-spec.md</c> §1.6.1.
/// </para>
/// </summary>
public static class NavItemCommand
{
    // ---- The ones a node advertises (see ItemCommand.Kind) ---------------------------------------------

    public const string Play = "play";
    public const string Resume = "resume";
    public const string Queue = "queue";
    public const string Shuffle = "shuffle";
    public const string Toggle = "toggle";
    public const string Open = "open";

    // ---- Reserved: the hub sends these whether or not a node lists them ---------------------------------

    /// <summary>
    /// "What is this item, and where does it stream from?" — a question, answered and nothing more.
    /// <para>
    /// The caller is going to route the answer itself: start an activity with it, or hand the URL to a box
    /// in another room. A driver answering this <b>must not</b> emit <c>library.play</c>, <c>library.queue</c>
    /// or any other routing event, must not start playback anywhere, and must leave nothing behind that a
    /// second resolve would see differently. Return the facts — at minimum <c>streamUrl</c> and
    /// <c>mediaType</c> for anything playable — in the <c>CommandResult</c>.
    /// </para>
    /// <para>
    /// The failure this forbids is not theoretical and it is not visible: the stray event fires every
    /// redirect rule the user has written against that source, and <i>then</i> the caller starts the
    /// activity as well. One press, two routings, and the first one goes wherever the rule says rather than
    /// where the person asked.
    /// </para>
    /// </summary>
    public const string Resolve = "resolve";

    /// <summary>
    /// Whether this id asks a question rather than doing something — the guard to put in front of an
    /// <c>Emit</c> in <see cref="INavigableDevice.InvokeItemAsync"/>.
    /// <para>
    /// Written as a call rather than as <c>commandId != "resolve"</c> at each site so that a second reserved
    /// query, if there is ever one, does not need three drivers to be found and edited again — which is the
    /// way this one was missed.
    /// </para>
    /// </summary>
    public static bool IsQuery(string commandId) => commandId == Resolve;
}
