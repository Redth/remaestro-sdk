namespace Remaestro.Sdk;

/// <summary>
/// The metadata keys that describe <b>a particular copy</b> of something, as opposed to the work itself.
/// <para>
/// <see cref="LibraryNode.Metadata"/> is deliberately free-form, which means anything can travel in it and
/// nothing has to. That works for display — a UI shows what it recognises — but it fails the moment
/// something has to <i>choose</i>: asked to play a film in a particular room, there's no way to tell the
/// 4K HDR remux from the 720p copy of the same title, because nobody agreed on what to call the fields.
/// </para>
/// <para>
/// So these are named. A driver fills in what its server knows and leaves the rest out; a missing key means
/// "not known", never "no". Deliberately absent are <c>year</c> and <c>runtimeSeconds</c>, which the spec
/// already defines and which describe the work rather than the copy.
/// </para>
/// </summary>
public static class MediaMeta
{
    // ---- Picture ------------------------------------------------------------------------------------

    /// <summary>Full frame size, e.g. <c>3840x2160</c>. Useful to show; <see cref="VideoHeight"/> is what to compare.</summary>
    public const string VideoResolution = "videoResolution";

    /// <summary>
    /// Height in pixels, e.g. <c>2160</c>. Separate from the resolution string because comparing copies is
    /// the whole point, and parsing "3840x2160" at every comparison invites getting it wrong once.
    /// </summary>
    public const string VideoHeight = "videoHeight";

    /// <summary>
    /// Dynamic range: <c>SDR</c>, <c>HDR10</c>, <c>HDR10+</c>, <c>DV</c>, <c>HLG</c>. The one that matters
    /// most for whether a room can play a copy properly — a display that can't do Dolby Vision may show a
    /// DV-only file washed out and green rather than refusing it.
    /// </summary>
    public const string VideoRange = "videoRange";

    /// <summary>Codec: <c>HEVC</c>, <c>AV1</c>, <c>H264</c>, <c>VC1</c>. Some players can't decode some of these.</summary>
    public const string VideoCodec = "videoCodec";

    /// <summary>Frames per second, e.g. <c>23.976</c>.</summary>
    public const string VideoFrameRate = "videoFrameRate";

    // ---- Sound --------------------------------------------------------------------------------------

    /// <summary>Codec: <c>TrueHD</c>, <c>DTS-HD MA</c>, <c>EAC3</c>, <c>AAC</c>, <c>FLAC</c>.</summary>
    public const string AudioCodec = "audioCodec";

    /// <summary>
    /// The object layer on top of the codec: <c>Atmos</c>, <c>DTS:X</c>. Separate from the codec because
    /// Atmos rides on TrueHD or EAC3, and "TrueHD" alone doesn't tell you whether it's there.
    /// </summary>
    public const string AudioProfile = "audioProfile";

    /// <summary>Channel layout, e.g. <c>7.1</c>, <c>5.1</c>, <c>2.0</c>.</summary>
    public const string AudioChannels = "audioChannels";

    /// <summary>The default track's language, ISO 639 where the server knows it.</summary>
    public const string AudioLanguage = "audioLanguage";

    // ---- The file itself ----------------------------------------------------------------------------

    /// <summary>Overall bitrate in kbps — the tie-break when two copies match on everything else.</summary>
    public const string BitrateKbps = "bitrateKbps";

    public const string FileSizeBytes = "fileSizeBytes";

    /// <summary>Container: <c>mkv</c>, <c>mp4</c>, <c>ts</c>.</summary>
    public const string Container = "container";

    /// <summary>Subtitle languages present, comma separated.</summary>
    public const string Subtitles = "subtitles";

    // ---- Which copy this is -------------------------------------------------------------------------

    /// <summary>
    /// The cut, where a server distinguishes one: <c>Director's Cut</c>, <c>Theatrical</c>, <c>Extended</c>.
    /// Worth carrying separately from the title, because "the extended one" is a thing people ask for.
    /// </summary>
    public const string Edition = "edition";

    /// <summary>
    /// How many copies of this item the source holds. A count above one is the signal that picking the
    /// right copy is a real decision rather than a formality.
    /// </summary>
    public const string VersionCount = "versionCount";

    /// <summary>Every key here, for a driver or a test that wants to check it emitted sensible ones.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        VideoResolution, VideoHeight, VideoRange, VideoCodec, VideoFrameRate,
        AudioCodec, AudioProfile, AudioChannels, AudioLanguage,
        BitrateKbps, FileSizeBytes, Container, Subtitles,
        Edition, VersionCount,
    ];

    // ---- Reading them back --------------------------------------------------------------------------

    /// <summary>
    /// A short human description of a copy — "4K DV · TrueHD Atmos 7.1". What a picker shows next to two
    /// copies of the same film, and what an assistant says when it explains which one it chose.
    /// </summary>
    public static string Describe(IReadOnlyDictionary<string, string> metadata)
    {
        var parts = new List<string>();

        if (Height(metadata) is { } h)
            parts.Add(h >= 2160 ? "4K" : h >= 1080 ? "1080p" : h >= 720 ? "720p" : $"{h}p");

        if (metadata.GetValueOrDefault(VideoRange) is { Length: > 0 } range && !range.Equals("SDR", StringComparison.OrdinalIgnoreCase))
            parts.Add(range);

        var sound = new List<string>();
        if (metadata.GetValueOrDefault(AudioCodec) is { Length: > 0 } codec) sound.Add(codec);
        if (metadata.GetValueOrDefault(AudioProfile) is { Length: > 0 } profile) sound.Add(profile);
        if (metadata.GetValueOrDefault(AudioChannels) is { Length: > 0 } channels) sound.Add(channels);
        if (sound.Count > 0) parts.Add(string.Join(" ", sound));

        if (metadata.GetValueOrDefault(Edition) is { Length: > 0 } edition) parts.Add(edition);

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// The copy's height, from <see cref="VideoHeight"/> or failing that the resolution string. Null when
    /// the source didn't say — which is different from small, and has to stay different.
    /// </summary>
    public static int? Height(IReadOnlyDictionary<string, string> metadata)
    {
        if (metadata.GetValueOrDefault(VideoHeight) is { Length: > 0 } h && int.TryParse(h, out var height))
            return height;

        if (metadata.GetValueOrDefault(VideoResolution) is { Length: > 0 } res)
        {
            var x = res.IndexOf('x');
            if (x > 0 && int.TryParse(res[(x + 1)..].Trim(), out var fromRes)) return fromRes;
        }
        return null;
    }

    /// <summary>Whether this copy carries high dynamic range of any flavour.</summary>
    public static bool IsHdr(IReadOnlyDictionary<string, string> metadata) =>
        metadata.GetValueOrDefault(VideoRange) is { Length: > 0 } range
        && !range.Equals("SDR", StringComparison.OrdinalIgnoreCase);
}
