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
/// </summary>
public interface IEpgSource
{
    Task<EpgData> GetEpgAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
}
