namespace Remaestro.Sdk;

/// <summary>
/// The metadata keys that describe <b>the work</b> — what it is, when it was made, who is in it, and where
/// it sits in a series. The counterpart to <see cref="MediaMeta"/>, which describes a particular copy.
/// <para>
/// These existed as prose in the navigation spec and nowhere in code, and they drifted exactly as you would
/// expect: Jellyfin emits its own API's <c>indexNumber</c> / <c>parentIndexNumber</c> for episode and season,
/// while Plex emits neither. So "season 3, episode 5" was expressible on one server and not the other, and
/// the same episode on two servers didn't look like the same episode. Naming them here is what lets a search
/// filter on a fact rather than on a string, and what lets two libraries agree.
/// </para>
/// <para>
/// A driver fills in what its server knows and leaves the rest out. <b>A missing key means "not known",
/// never "no"</b> — an item with no <see cref="Cast"/> is not thereby unpeopled.
/// </para>
/// </summary>
public static class MediaFacts
{
    // ---- The work -----------------------------------------------------------------------------------

    /// <summary>Release year, e.g. <c>1985</c>. The one fact people volunteer unprompted: "the 80s one".</summary>
    public const string Year = "year";

    /// <summary>Full release date, ISO <c>yyyy-MM-dd</c>, where the server is that precise.</summary>
    public const string ReleaseDate = "releaseDate";

    /// <summary>How long the work runs, in seconds. Describes the work, not the file — see <see cref="MediaMeta"/>.</summary>
    public const string RuntimeSeconds = "runtimeSeconds";

    /// <summary>Comma separated: <c>Science Fiction, Adventure</c>. Comma because the spec already made that choice for genres.</summary>
    public const string Genres = "genres";

    /// <summary>The certificate: <c>PG-13</c>, <c>15</c>, <c>TV-MA</c>. Whose scheme it is depends on the region.</summary>
    public const string OfficialRating = "officialRating";

    /// <summary>An aggregate score, however the source scales it. Compare within a source, not across them.</summary>
    public const string CommunityRating = "communityRating";

    /// <summary>One line of marketing, where a server carries it. Not the overview.</summary>
    public const string Tagline = "tagline";

    /// <summary>Production company or label.</summary>
    public const string Studio = "studio";

    /// <summary>The title in its own language, when the listed one is translated.</summary>
    public const string OriginalTitle = "originalTitle";

    // ---- Where it sits in a series ------------------------------------------------------------------

    /// <summary>
    /// The show an episode belongs to. Carried on the episode itself so a result is self-describing: a
    /// search hit reading "Episode 5" is useless without it, and the parent may not have been fetched.
    /// </summary>
    public const string SeriesTitle = "seriesTitle";

    /// <summary>
    /// Which season, as a number. Named for what it is rather than for a server's API — this is the key
    /// Jellyfin was calling <c>parentIndexNumber</c> and Plex wasn't emitting at all.
    /// </summary>
    public const string SeasonNumber = "seasonNumber";

    /// <summary>Which episode within the season.</summary>
    public const string EpisodeNumber = "episodeNumber";

    /// <summary>The episode's own title, when it has one distinct from the node's title.</summary>
    public const string EpisodeTitle = "episodeTitle";

    /// <summary>First broadcast, ISO <c>yyyy-MM-dd</c>.</summary>
    public const string AirDate = "airDate";

    /// <summary><c>Continuing</c> or <c>Ended</c>, for a series.</summary>
    public const string SeriesStatus = "seriesStatus";

    // ---- Who made it --------------------------------------------------------------------------------

    /// <summary>
    /// Billed cast, comma separated and in billing order where the server preserves it. Capped rather than
    /// exhaustive — this travels on every item in a listing, and nobody asks for the fourteenth name.
    /// </summary>
    public const string Cast = "cast";

    public const string Directors = "directors";
    public const string Writers = "writers";

    // ---- Music --------------------------------------------------------------------------------------

    /// <summary>Performing artists, comma separated.</summary>
    public const string Artists = "artists";

    /// <summary>The album's artist, which is not always the track's — a compilation has one of each.</summary>
    public const string AlbumArtist = "albumArtist";

    public const string Album = "album";
    public const string TrackNumber = "trackNumber";

    /// <summary>Which disc, on a release that has more than one.</summary>
    public const string DiscNumber = "discNumber";

    // ---- Where the viewer got to --------------------------------------------------------------------

    /// <summary>Resume position in seconds.</summary>
    public const string PositionSeconds = "positionSeconds";

    /// <summary><c>true</c> when it's been watched through.</summary>
    public const string Played = "played";

    /// <summary><c>true</c> when the user has marked it a favourite.</summary>
    public const string Favorite = "favorite";

    /// <summary>Every key here, for a driver or a test that wants to check what it emitted.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Year, ReleaseDate, RuntimeSeconds, Genres, OfficialRating, CommunityRating, Tagline, Studio, OriginalTitle,
        SeriesTitle, SeasonNumber, EpisodeNumber, EpisodeTitle, AirDate, SeriesStatus,
        Cast, Directors, Writers,
        Artists, AlbumArtist, Album, TrackNumber, DiscNumber,
        PositionSeconds, Played, Favorite,
    ];

    // ---- Reading them back ---------------------------------------------------------------------------

    /// <summary>Split a comma-separated list key — cast, genres, artists — into its parts.</summary>
    public static IReadOnlyList<string> List(IReadOnlyDictionary<string, string> facts, string key) =>
        facts.GetValueOrDefault(key) is { Length: > 0 } value
            ? [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            : [];

    /// <summary>Read a numeric fact. Null when absent or unparseable, which are the same thing to a caller.</summary>
    public static int? Number(IReadOnlyDictionary<string, string> facts, string key) =>
        facts.GetValueOrDefault(key) is { Length: > 0 } value && int.TryParse(value, out var n) ? n : null;

    /// <summary>
    /// <c>S3E5</c>, when both are known. The form people say out loud and the form a search matches against —
    /// worth deriving in one place rather than in every driver's subtitle builder.
    /// </summary>
    public static string? EpisodeCode(IReadOnlyDictionary<string, string> facts) =>
        Number(facts, SeasonNumber) is { } season && Number(facts, EpisodeNumber) is { } episode
            ? $"S{season}E{episode}"
            : null;

    /// <summary>
    /// Whether any of the comma-separated names under <paramref name="key"/> matches — "kurt russell" finding
    /// "Kurt Russell". Substring rather than equality because people give one name of two.
    /// </summary>
    public static bool NameMatches(IReadOnlyDictionary<string, string> facts, string key, string query) =>
        query.Length > 0
        && List(facts, key).Any(name => name.Contains(query, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// What a library holds. A <c>library</c> node says which of these it is, so a search can be scoped to the
/// right shelves and so a caller knows which <see cref="MediaFacts"/> to expect on what comes back — asking
/// a music library for a season number is a question with no answer.
/// </summary>
public static class LibraryKind
{
    /// <summary>The metadata key a library node carries this under.</summary>
    public const string Key = "libraryKind";

    public const string Movies = "movies";
    public const string Tv = "tv";
    public const string Music = "music";
    public const string Photos = "photos";

    /// <summary>Broadcast or streamed channels rather than a catalogue.</summary>
    public const string Live = "live";

    /// <summary>Books, podcasts, home video — a shelf that isn't one of the above.</summary>
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All = [Movies, Tv, Music, Photos, Live, Other];

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind);
}


/// <summary>
/// What a device can be handed to play, and how to hand it over.
/// <para>
/// Declared rather than guessed. The hub used to work this out by looking for a command parameter named
/// something like <c>url</c>, which is a heuristic: it offered a Zidoo reached over RS-232 as somewhere to
/// send a film — unarguably a media player, and equally unable to be given a URL — and the activity was
/// then generated with no play step at all. No error, just a scene that never starts the film.
/// </para>
/// <para>
/// <paramref name="Kinds"/> is what makes the difference between "can play" and "can play <i>this</i>".
/// A Sonos takes a track and not a film; a photo frame takes neither. Empty means it doesn't distinguish.
/// Values come from the navigation spec's kind vocabulary — <c>movie</c>, <c>episode</c>, <c>track</c>,
/// <c>video</c>, <c>photo</c>, <c>channel</c>, <c>stream</c>.
/// </para>
/// </summary>
/// <param name="CommandId">The command that plays something.</param>
/// <param name="UrlParam">Which of that command's parameters carries the thing to play.</param>
public sealed record MediaPlayback(string CommandId, string UrlParam, IReadOnlyList<string>? Kinds = null)
{
    /// <summary>
    /// The answer for a device that <i>was</i> asked and cannot be handed anything: an Xbox whose only
    /// URI-shaped command launches an app, a generic HTTP endpoint, a Zidoo on a serial cable.
    /// <para>
    /// Not the same as leaving <c>Playback</c> null. Null means the driver hasn't been through this yet,
    /// and the hub still falls back to sniffing command parameters for something called <c>url</c> — the
    /// heuristic this whole record exists to retire. A device with a URI parameter that isn't a media
    /// handoff has no other way to say so, and the sniff will keep offering it as somewhere to send a film.
    /// </para>
    /// </summary>
    public static readonly MediaPlayback Nothing = new("", "");

    /// <summary>
    /// Whether this declaration names a command at all — false for <see cref="Nothing"/>. Both halves are
    /// needed: a command with no parameter to put the item in is no more usable than no command.
    /// </summary>
    public bool CanPlay => CommandId.Length > 0 && UrlParam.Length > 0;

    /// <summary>Whether an item of this kind can be sent here. An undeclared kind list accepts anything.</summary>
    public bool Accepts(string? kind) =>
        CanPlay
        && (Kinds is not { Count: > 0 }
            || kind is not { Length: > 0 }
            || Kinds.Contains(kind, StringComparer.OrdinalIgnoreCase));
}
