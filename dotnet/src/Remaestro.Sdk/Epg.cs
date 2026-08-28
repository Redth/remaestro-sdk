namespace Remaestro.Sdk;

/// <summary>
/// One channel in a guide. <paramref name="Id"/> is what its programmes reference; <paramref name="StreamUrl"/>,
/// when set, is what the guide plays to watch it — the same redirect a library item uses.
///
/// <para>
/// <b><paramref name="Group"/> is the section heading this channel belongs under</b> — "Sports", "Movies" —
/// and null or blank means ungrouped, which is what every driver in this fleet but Xtream will always send.
/// One group per channel: an upstream that says a channel is in several (Xtream's <c>category_ids</c> does)
/// wants one of them picked, because a line-up that repeats a channel under every category it belongs to
/// has a row count that is not a count of anything, and a grid like that cannot be sized or scrolled.
/// </para>
///
/// <para>
/// <b>You do not have to order anything for this to work.</b> The contract requires a source's groups to be
/// contiguous in its own order, and <see cref="EpgChannelOrder.Sorted"/> — which <see cref="DriverHost"/>
/// applies to every .NET driver's line-up before it crosses the wire — is what satisfies that. Set the
/// label and the host does the rest.
/// </para>
/// </summary>
public sealed record EpgChannel(
    string Id, string Name, string? Logo = null, string? Number = null, string? StreamUrl = null,
    string? Group = null);

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
///
/// <para>
/// <b>This one method is also what your driver's search reaches, and that is worth knowing before somebody
/// asks why.</b> <see cref="DriverHost"/> answers <c>SearchEpg</c> for you — every .NET guide source gained
/// a searchable guide the day that rpc landed, with no change to any of them — by filtering exactly what
/// this call returns. The contract says a search covers the whole guide the plugin holds; the whole guide
/// this shim holds is the window it was just asked for, because there is no way to ask you for more. So a
/// .NET source is conformant and it is also the narrowest conformant thing there is: a search cannot reach
/// a programme outside the window the guide is open on. Widening it means <see cref="IEpgSource"/> growing
/// a way to say how much guide you have, which is a change to this interface and has not been made.
/// </para>
/// </summary>
public interface IEpgSource
{
    Task<EpgData> GetEpgAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
}

/// <summary>
/// What order a guide source's channels come out in, and therefore what <c>EpgRequest.offset</c> addresses.
///
/// <para>
/// <b>Why this is in the SDK rather than in the hub.</b> The grid is the concatenation of each source's own
/// line-up — the hub never compares a channel from one source against a channel from another, which is what
/// makes a range query over several sources arithmetic rather than a merge. So a section's order is the
/// source's, the hub does not sort and cannot check, and the only place a shipped driver can be given the
/// order a person expects for free is the host shim every one of them goes through.
/// <c>docs/plugins/design-guide-sections.md</c> §5 is the argument.
/// </para>
///
/// <para>
/// <b>A channel number is not a decimal.</b> "3.1" is sub-channel 1 of channel 3, so the parts are compared
/// as two integers rather than folded into one <c>double</c> by <c>major + minor / 1000.0</c>. That fold is
/// exact for minors under 1000 and wrong above it — "3.1000" comes out as <c>4.0</c>, <b>equal to channel
/// 4</b> — and widening the divisor moves the cliff rather than removing it. Three tails, ordinal rather
/// than sentinel: a number, a number that will not parse, no number at all. Sentinels near
/// <c>double.MaxValue</c> converge, because the ULP up there is about 2·10²⁹².
/// </para>
///
/// <para>
/// The parse is deliberately small: split on the first '.' or '-', parse the whole leading part (so "12A"
/// does not read as 12), a missing or unreadable minor is 0, anything past the second part ignored.
/// </para>
/// </summary>
public static class EpgChannelOrder
{
    const int Numbered = 0, Unreadable = 1, Numberless = 2;

    /// <summary>
    /// A channel's group label, normalised the one way this contract compares labels: trimmed, and equal
    /// ignoring case. Blank for a channel in no group.
    /// <para>
    /// <b>One rule, used in two places on purpose.</b> The shim uses it to decide which channels form a
    /// run; the hub uses it to decide whether this source's "Sports" and another source's are one heading.
    /// Nothing fuzzier than that — "Sport" and "Sports" are two groups, and a provider who spells a
    /// category differently from your aerial gets two headings. Anything cleverer is a promise a plugin in
    /// another language cannot keep.
    /// </para>
    /// </summary>
    public static string Label(EpgChannel channel) => channel.Group?.Trim() ?? "";

    /// <summary>Where a channel number sorts. Public so a driver that pages for itself can agree with the shim.</summary>
    public static (int Bucket, int Major, int Minor) Of(string? number)
    {
        if (string.IsNullOrWhiteSpace(number)) return (Numberless, 0, 0);
        var parts = number.Split('.', '-');
        if (!int.TryParse(parts[0], out var major)) return (Unreadable, 0, 0);
        var minor = parts.Length > 1 && int.TryParse(parts[1], out var m) ? m : 0;
        return (Numbered, major, minor);
    }

    /// <summary>
    /// One source's line-up in group order, then channel number, then name, then id.
    /// <para>
    /// <b>Total, and that is the requirement rather than a nicety.</b> <c>offset</c> has to mean the same
    /// thing on two consecutive calls or a paged grid repeats rows and drops others, so any tie left for
    /// <c>OrderBy</c>'s stability to settle is a tie settled by whatever order the upstream happened to
    /// return this time. The id is unique within a source by construction — programmes reference it — so
    /// the three keys together cannot tie.
    /// </para>
    /// <para>
    /// <b>Group first, and that is what makes a grouped line-up legal.</b> The contract requires a source
    /// that sets <see cref="EpgChannel.Group"/> to keep each group contiguous in its own order, because
    /// <c>offset</c> addresses that order and a section is a range inside it. Ordering by group before
    /// number is how this shim keeps that promise on every .NET driver's behalf, so a driver that sets a
    /// label owes nothing further.
    /// </para>
    /// <para>
    /// <b>Groups come out in the order the source emitted them, not alphabetically.</b> A provider chose
    /// its category order and every other client shows it; alphabetically, "Season 10" sorts above
    /// "Season 2" and "Channel 10 Sports" above "Channel 2 Sports". <c>NavSections.Of</c> in the hub
    /// argues the same thing about a library listing, and this is that argument on a line-up.
    /// </para>
    /// <para>
    /// <b>A line-up with no groups in it is sorted exactly as it was before groups existed.</b> Every
    /// channel then has the same blank label, so the first key is constant and the remaining three decide
    /// everything — which is the property that lets this ship before any driver has grown a group. An
    /// ungrouped channel in a line-up that does have groups keeps its place rather than being swept into
    /// an invented "Other": blank is a run like any other and falls where it first appears.
    /// </para>
    /// </summary>
    public static IReadOnlyList<EpgChannel> Sorted(IReadOnlyList<EpgChannel> channels)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in channels)
        {
            var label = Label(c);
            if (!seen.ContainsKey(label)) seen[label] = seen.Count;
        }

        return [.. channels
            .OrderBy(c => seen[Label(c)])
            .ThenBy(c => Of(c.Number))
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Id, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Where each group begins in an already-<see cref="Sorted"/> line-up, and how long it is —
    /// <c>EpgMessage.groups</c>, computed once over the whole selection rather than per page.
    /// <para>
    /// <b>Empty when nothing is grouped</b>, which is the wire's "not said" and is the honest answer for
    /// every source in this fleet today. A single run covering everything would be a heading over the
    /// whole grid, which tells a person nothing they could not see, and the hub would have to unpick it.
    /// </para>
    /// </summary>
    public static IReadOnlyList<EpgGroupRun> RunsOf(IReadOnlyList<EpgChannel> ordered)
    {
        var runs = new List<EpgGroupRun>();
        var grouped = false;

        for (var i = 0; i < ordered.Count;)
        {
            var label = Label(ordered[i]);
            if (label.Length > 0) grouped = true;

            var j = i;
            while (j < ordered.Count && string.Equals(Label(ordered[j]), label, StringComparison.OrdinalIgnoreCase))
                j++;

            runs.Add(new EpgGroupRun(label, i, j - i));
            i = j;
        }

        return grouped ? runs : [];
    }
}

/// <summary>One run of channels sharing a group label: the label, where it starts, and how long it is.</summary>
public readonly record struct EpgGroupRun(string Label, int First, int Count);

/// <summary>
/// The one predicate this contract promises about a guide search: <b>case-insensitive substring, no
/// ranking</b>. A channel is selected when its name or its number contains the query, or when one of its
/// programmes has a title that does; a programme is a match when its title does.
///
/// <para>
/// <b>It is deliberately the largest promise that can be kept in every language.</b> Anything cleverer —
/// stemming, synonyms, a relevance order — is a promise the hub cannot check and a plugin author in Go or
/// Python has no shared library to inherit. <c>docs/plugins/design-guide-storage.md</c> §1.2 is the
/// argument; this class is so that the .NET half has exactly one implementation of it.
/// </para>
///
/// <para>
/// <b>An empty query selects everything and matches nothing.</b> Not a special case bolted on: "contains
/// the empty string" is true of every title in the guide, so a list of every programme is what the naive
/// reading returns, and that is not a search result — it is the guide again with a different name on it.
/// </para>
/// </summary>
public static class EpgSearch
{
    /// <summary>Does this text contain the query, ignoring case? False for an empty query.</summary>
    public static bool Hits(string? text, string query)
        => query.Length > 0 && text is not null && text.Contains(query, StringComparison.OrdinalIgnoreCase);

    /// <summary>Does this channel's own name or number contain the query?</summary>
    public static bool Selects(EpgChannel channel, string query)
        => Hits(channel.Name, query) || Hits(channel.Number, query);
}
