namespace Remaestro.Sdk;

/// <summary>
/// One channel in a guide. <paramref name="Id"/> is what its programmes reference; <paramref name="StreamUrl"/>,
/// when set, is what the guide plays to watch it — the same redirect a library item uses.
/// </summary>
public sealed record EpgChannel(
    string Id, string Name, string? Logo = null, string? Number = null, string? StreamUrl = null);

/// <summary>
/// One programme on one channel, over a definite window. This is the part the navigation projection has no
/// room for — a library node is timeless, and a guide is nothing but time.
/// </summary>
public sealed record EpgProgramme(
    string ChannelId, DateTimeOffset Start, DateTimeOffset Stop, string Title,
    string? Subtitle = null, string? Description = null, string? Category = null,
    string? Image = null, string? Episode = null, bool IsNew = false);

/// <summary>A guide: the channels and the programmes on them, for whatever window was asked.</summary>
public sealed record EpgData(IReadOnlyList<EpgChannel> Channels, IReadOnlyList<EpgProgramme> Programmes)
{
    public static readonly EpgData Empty = new([], []);
}

/// <summary>
/// A device that can answer "what's on?" — a tuner with a cloud guide, an XMLTV feed, an Xtream account. The
/// hub asks for a window (a day or two, not the whole schedule) and merges every source into one guide grid.
/// Kept separate from <see cref="INavigableDevice"/> on purpose: browsing a library and reading a schedule are
/// different shapes, and forcing programmes into timeless nodes loses the one thing a guide is about.
///
/// <para>
/// <b>An empty guide is not one you couldn't read.</b> Return <see cref="EpgData.Empty"/> only for a source
/// that answered and has nothing to say — a document that parses with no channels in it, an account whose
/// line has lapsed, a provider that serves channels and no listings are all real, empty answers about
/// somebody's television. A feed you could not read is <see cref="DeviceUnreachableException"/>, the same
/// exception and the same rule as <see cref="INavigableDevice.BrowseAsync"/>, because an empty grid and an
/// empty shelf are the same mistake drawn on different furniture.
/// </para>
///
/// <para>
/// <b>Here it also poisons a cache, which is why the rule has to live in your driver rather than above
/// it.</b> The hub holds each source's guide for a window of minutes and writes an empty answer in like any
/// other, replacing whatever it had — so one manufactured empty blanks a guide that was working, and keeps
/// it blank after the feed comes back. The layer holding that cache <i>cannot tell a manufactured empty
/// from a real one</i>: both arrive as no channels and no programmes, and by then the difference is gone.
/// Your <c>GetEpgAsync</c> is the last place the two are still distinguishable, so it is the only place the
/// distinction can be made.
/// </para>
///
/// <para>
/// <b>And know what the throw carries, which is less than you would expect.</b> <see cref="DriverHost"/>
/// catches everything out of this method and answers <c>Availability.Unavailable</c> with no detail —
/// <c>EpgMessage</c> has no field for a reason, unlike the navigation rpcs, which carry the message on the
/// call's own failure. What the throw buys is the availability, and that is worth having on its own: the
/// hub serves the last good copy under a "can't reach" band instead of caching nothing-is-on. Two
/// consequences. Put your words somewhere the person can still see them — a <c>lastError</c> state key is
/// where this fleet's guide drivers put theirs, and it reaches the devices page. And expect no help telling
/// a bug in your driver from a feed that is down: a <c>NullReferenceException</c> in your parser and a
/// refused connection reach the hub as the identical answer, so an <c>Unavailable</c> you did not expect is
/// a reason to read your own logs before blaming the network.
/// </para>
/// </summary>
public interface IEpgSource
{
    Task<EpgData> GetEpgAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
}
