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

    /// <summary>Search the library. Return an empty listing if unsupported.</summary>
    Task<NodeListing> SearchAsync(string query, BrowseOptions options, CancellationToken ct)
        => Task.FromResult(new NodeListing());

    /// <summary>
    /// Invoke a per-item command (play/queue/toggle…). Returns a result and, by convention, emits an event
    /// (e.g. <c>library.play</c>) so rules can route the effect (see the spec's redirect pattern).
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
