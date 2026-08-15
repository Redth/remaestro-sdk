namespace Remaestro.Drivers.Screen;

/// <summary>
/// The serial protocol nearly every motorised projection screen speaks.
/// <para>
/// Screens look like a fragmented market and aren't: the tubular motor controllers inside them come from
/// a handful of OEMs, and most of the industry ships the same five-byte protocol at 2400 baud. Dragonfly's
/// own control document and Grandview's Skyshow manual give byte-identical tables, which is what you'd
/// expect given who manufactures for whom.
/// </para>
/// <para>
/// It is strictly one-way. Both documents say so outright — "no status is available back to the automation
/// controller". A screen cannot be asked where it is, which is the single most important thing to be
/// honest about, because a UI that shows a position implies it measured one.
/// </para>
/// </summary>
public static class ScreenProtocol
{
    /// <summary>2400 8N1. Slow, and not negotiable — these controllers run one speed.</summary>
    public const int Baud = 2400;

    /// <summary>
    /// The default address. The frame is <c>FF</c>, three address bytes, then the action — so the middle
    /// three identify which screen on a shared bus, and <c>EEEEEE</c> is what they leave the factory as.
    /// </summary>
    public const string DefaultAddress = "EEEEEE";

    public const byte Header = 0xFF;

    public const byte ActionUp = 0xDD;
    public const byte ActionStop = 0xCC;
    public const byte ActionDown = 0xEE;

    /// <summary>
    /// Build a command frame: header, address, action.
    /// </summary>
    /// <param name="address">Six hex characters — three bytes. Blank means the factory default.</param>
    public static byte[] Frame(byte action, string? address = null)
    {
        var bytes = AddressBytes(address);
        return [Header, bytes[0], bytes[1], bytes[2], action];
    }

    /// <summary>
    /// Parse an address into its three bytes, falling back to the factory default. Anything malformed
    /// falls back rather than throwing: a screen on the default address is overwhelmingly the common case,
    /// and refusing to start over a typo in an advanced field helps nobody.
    /// </summary>
    public static byte[] AddressBytes(string? address)
    {
        var s = (address ?? "").Trim().Replace(" ", "").Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        if (s.Length != 6 || !s.All(Uri.IsHexDigit)) s = DefaultAddress;

        return
        [
            Convert.ToByte(s[..2], 16),
            Convert.ToByte(s[2..4], 16),
            Convert.ToByte(s[4..6], 16),
        ];
    }

    /// <summary>Readable form of a frame, for the device's own state and for a log worth reading.</summary>
    public static string Describe(byte[] frame) => string.Join(" ", frame.Select(b => b.ToString("X2")));

    /// <summary>
    /// Where the screen is, as far as anything here can tell. Deliberately not called "position" without
    /// qualification anywhere it's shown: the protocol is one-way, so this is inferred from what was last
    /// sent and how long ago, not measured. Someone pressing the wall switch makes it wrong immediately.
    /// </summary>
    public static class Believed
    {
        public const string Up = "up";
        public const string Down = "down";
        public const string Moving = "moving";

        /// <summary>Before anything has been sent — and after a stop, which lands somewhere in between.</summary>
        public const string Unknown = "unknown";
    }

    /// <summary>
    /// Said plainly in the device's own state, because a screen that never reports its position looks
    /// broken next to every other device in the list.
    /// </summary>
    public const string OneWayNote =
        "Screen controllers only listen — the protocol has no way to ask where the screen is. "
        + "The position shown is worked out from the last command and how long the screen takes to travel, "
        + "so the wall switch or the handset will make it wrong.";
}
