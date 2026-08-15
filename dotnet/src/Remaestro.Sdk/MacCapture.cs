using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Remaestro.Sdk;

/// <summary>
/// Where a stored MAC came from, kept beside the MAC itself.
/// <para>
/// A typed address and a learned one are not the same claim and must not be treated alike. Someone who
/// typed one has said something, and a driver that writes over it is a driver arguing with its owner; a
/// value we worked out ourselves is ours to correct the moment the device contradicts it. Nothing else
/// distinguishes them once they are both six bytes in a config dictionary, so the distinction has to be
/// written down.
/// </para>
/// <para>
/// <b>Absent means typed.</b> That is the migration: every device saved before any of this existed has a
/// MAC and no origin, and is therefore left alone forever — which is exactly right, because a person put
/// it there.
/// </para>
/// </summary>
public static class MacOrigin
{
    /// <summary>The config key the origin is saved under, beside <c>mac</c>.</summary>
    public const string Key = "macSource";

    /// <summary>Read off the device's own account of its wired interface.</summary>
    public const string Wired = "wired";

    /// <summary>Read off the device's own account of its wireless interface.</summary>
    public const string WiFi = "wifi";

    /// <summary>Read out of this machine's neighbour table, for a device that won't say.</summary>
    public const string Neighbour = "neighbour";

    /// <summary>
    /// Whether this is an origin we wrote, and may therefore write again. Anything unrecognised — including
    /// the empty string — is somebody's typing and is not ours to touch.
    /// </summary>
    public static bool Learned(string? origin) => origin is Wired or WiFi or Neighbour;

    /// <summary>How to say it on screen, in the same breath as the address.</summary>
    public static string Describe(string? origin) => origin switch
    {
        Wired => "learned from the device, over Ethernet",
        WiFi => "learned from the device, over Wi-Fi",
        Neighbour => "learned from this machine's neighbour table",
        _ => "typed in by hand",
    };
}

/// <summary>
/// The address we should store for a device, and where it came from — or nothing at all, with the reason
/// said out loud.
/// </summary>
/// <param name="Mac">Canonical <c>AA:BB:CC:DD:EE:FF</c>, or empty when there is nothing to store.</param>
/// <param name="Origin">One of <see cref="MacOrigin"/>'s values, or empty alongside an empty MAC.</param>
/// <param name="Why">
/// Why there is no address, in words somebody could act on. Null when there is one. This is the whole
/// point of the type: "we could not learn a MAC" and "we learned this MAC" are both answers, and the
/// failure that has no words is the one that gets stored anyway.
/// </param>
public readonly record struct MacFinding(string Mac, string Origin, string? Why)
{
    public bool Found => Mac.Length > 0;

    public static MacFinding None(string why) => new("", "", why);
}

/// <summary>An address this machine holds, and how much of it is the network part.</summary>
public readonly record struct Segment(IPAddress Address, int PrefixLength);

/// <summary>
/// Where a driver aims a wake-up, and where that address came from — held in one place so the three
/// drivers that wake a television by magic packet do the same thing with it.
/// <para>
/// <b>Two copies of a learned MAC, on purpose.</b> This one so the very next Power On uses it without
/// waiting for a restart, and the hub's so it outlives the process. Only the second is persisted, and only
/// <see cref="Take"/> decides what is worth persisting — which is how "never argue with a typed value"
/// stays one rule in one place rather than the same twenty lines written out per driver.
/// </para>
/// </summary>
/// <param name="mac">Whatever is configured now — typed or learned earlier, indistinguishable without…</param>
/// <param name="source">…this: one of <see cref="MacOrigin"/>'s values, or empty for somebody's typing.</param>
public sealed class WakeTarget(string mac, string source)
{
    volatile string _mac = mac ?? "";
    volatile string _source = source ?? "";

    /// <summary>The address a magic packet goes to, or empty when there isn't one.</summary>
    public string Address => _mac;

    /// <summary>Where it came from, in a person's words. Empty when there is no address to qualify.</summary>
    public string Provenance => _mac.Length > 0 ? MacOrigin.Describe(_source) : "";

    /// <summary>
    /// Take what a device has just told us, if it is ours to take, and hand back the config keys the hub
    /// should save — empty when there is nothing to save, which is nearly every connection.
    /// </summary>
    public IReadOnlyDictionary<string, string> Take(MacFinding finding)
    {
        var learned = MacCapture.WhatChanged(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["mac"] = _mac, [MacOrigin.Key] = _source },
            finding);

        if (learned.TryGetValue("mac", out var mac)) _mac = mac;
        if (learned.TryGetValue(MacOrigin.Key, out var origin)) _source = origin;

        return learned;
    }
}

/// <summary>
/// Learning a device's MAC address instead of asking its owner to go and read one off a menu.
/// <para>
/// Wake-on-LAN is the only way to switch on a television that speaks over IP, and it needs a MAC that
/// nothing on the network will tell you unless you know where to look. Three drivers therefore took a
/// hand-typed one — webOS, Samsung, Android TV — and each of them opened with an instruction to go and
/// find it. This is that instruction being removed.
/// </para>
/// <para>
/// <b>Two ways to learn one, and they are not equal.</b>
/// </para>
/// <list type="number">
/// <item>
/// <b>The device's own account of itself.</b> webOS answers <c>getinfo</c> with the MAC of each interface
/// and <c>getStatus</c> with which of them holds which address; Samsung's REST document carries
/// <c>wifiMac</c> next to a <c>networkType</c> saying whether that is the interface in use. A value a
/// device states about itself survives routing, arrives already attributed to an interface, and can be
/// checked. Always preferred.
/// </item>
/// <item>
/// <b>This machine's neighbour table</b>, for a device that won't say — see <see cref="Neighbourhood"/>.
/// Weaker in a specific and dangerous way: across a routed segment the table answers with the
/// <i>router's</i> MAC, which is a wrong address that looks exactly like a right one. Sending a magic
/// packet to it does nothing, forever, with no error anywhere. So the table is only ever read for an
/// address inside one of this machine's own subnets, where the entry can only be the device's own card.
/// </item>
/// </list>
/// <para>
/// <b>Which MAC.</b> A television has two — wired and wireless — with different addresses, and waking the
/// one it isn't plugged into achieves nothing. Both routes above answer for the interface the device is
/// actually reachable on rather than for the device in general, which is the only version of the question
/// with a useful answer.
/// </para>
/// </summary>
public static class MacCapture
{
    /// <summary>
    /// The address in canonical form, or null if it isn't one worth keeping.
    /// <para>
    /// Accepts the forms a device or a person produces — colons, dashes, bare hex — via
    /// <see cref="WakeOnLan.TryParseMac"/>, so the two agree by construction about what parses at all.
    /// Then refuses three things that parse perfectly and cannot be a network card:
    /// </para>
    /// <list type="bullet">
    /// <item><b>All zeroes.</b> What <c>/proc/net/arp</c> writes for an entry it has asked about and not
    /// yet had answered, and what a device with an interface it isn't using reports for that interface.
    /// Stored, it is a wake that can never work.</item>
    /// <item><b>All ones.</b> The broadcast address. A magic packet aimed at it wakes nothing and asks
    /// every machine on the segment to look at it.</item>
    /// <item><b>Multicast</b> — the low bit of the first octet. No card is ever assigned one, so its
    /// presence means we are reading something that isn't a card's address.</item>
    /// </list>
    /// </summary>
    public static string? Normalise(string? mac)
    {
        if (!WakeOnLan.TryParseMac(mac, out var bytes)) return null;
        if (bytes.All(b => b == 0x00)) return null;
        if (bytes.All(b => b == 0xFF)) return null;
        if ((bytes[0] & 0x01) != 0) return null;

        return string.Join(":", bytes.Select(b => b.ToString("X2")));
    }

    /// <summary>
    /// What a device has just told us about itself, as something storable — or nothing, said out loud.
    /// </summary>
    /// <param name="mac">The address the device reported for the interface it is reachable on.</param>
    /// <param name="origin"><see cref="MacOrigin.Wired"/> or <see cref="MacOrigin.WiFi"/>.</param>
    public static MacFinding FromDevice(string? mac, string origin) =>
        Normalise(mac) is { } clean
            ? new MacFinding(clean, origin, null)
            : MacFinding.None("The device didn't report a usable address for the interface it's reachable on.");

    /// <summary>
    /// Which config keys this finding contradicts — empty when there is nothing to write, which is nearly
    /// every reconnect and is therefore the case worth being cheap.
    /// <para>
    /// <b>The rule that makes this safe to call on every connection.</b> A MAC whose <see cref="MacOrigin"/>
    /// is absent or unrecognised was typed by a person, and this returns nothing for it no matter what the
    /// device says. Only a value we learned is ours to replace — which is what lets a set that moves from
    /// Wi-Fi to Ethernet correct itself, while a set whose owner typed something deliberate keeps it.
    /// </para>
    /// <para>
    /// A finding with no address never clears anything. Losing sight of a device is not evidence its
    /// address changed, and the stored value is the only chance of ever waking it again; see
    /// <c>CLAUDE.md</c> on <c>Upsert</c> patching, which agrees — there is deliberately no path here that
    /// needs <c>patchIfUpdate: false</c>.
    /// </para>
    /// </summary>
    /// <param name="config">What is saved for this device now.</param>
    public static IReadOnlyDictionary<string, string> WhatChanged(
        IReadOnlyDictionary<string, string> config, MacFinding finding)
    {
        var changed = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!finding.Found) return changed;

        var storedMac = config.GetValueOrDefault("mac", "");
        var storedOrigin = config.GetValueOrDefault(MacOrigin.Key, "");

        // Somebody typed that. It is not ours.
        if (storedMac.Length > 0 && !MacOrigin.Learned(storedOrigin)) return changed;

        if (!string.Equals(storedMac, finding.Mac, StringComparison.OrdinalIgnoreCase))
            changed["mac"] = finding.Mac;
        if (!string.Equals(storedOrigin, finding.Origin, StringComparison.Ordinal))
            changed[MacOrigin.Key] = finding.Origin;

        return changed;
    }

    /// <summary>
    /// Whether this address is inside one of the subnets <paramref name="ours"/> describes.
    /// <para>
    /// The whole guard on reading a neighbour table rests here. An address inside our own subnet is one we
    /// speak to directly, so the table's entry for it is that device's own card. An address outside it is
    /// reached through a router, and the table's entry is the <i>router's</i> card — six plausible bytes
    /// that will never wake anything.
    /// </para>
    /// </summary>
    public static bool Within(IPAddress address, IEnumerable<Segment> ours)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        foreach (var segment in ours)
        {
            var mine = segment.Address;
            if (mine.AddressFamily != address.AddressFamily) continue;
            if (segment.PrefixLength < 0) continue;

            var a = address.GetAddressBytes();
            var b = mine.GetAddressBytes();
            if (a.Length != b.Length || segment.PrefixLength > a.Length * 8) continue;

            var whole = segment.PrefixLength / 8;
            var spare = segment.PrefixLength % 8;

            if (!a.AsSpan(0, whole).SequenceEqual(b.AsSpan(0, whole))) continue;
            if (spare > 0)
            {
                var mask = (byte)(0xFF << (8 - spare));
                if ((a[whole] & mask) != (b[whole] & mask)) continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// The MAC <c>/proc/net/arp</c> holds for an address, or null.
    /// <para>
    /// Kept as a parse over text rather than a lookup so it can be exercised against real tables — the
    /// incomplete entry in particular, which is the one that matters and the one no test with a socket in
    /// it would reliably produce.
    /// </para>
    /// <para>
    /// The file looks like this, header row and all:
    /// </para>
    /// <code>
    /// IP address       HW type     Flags       HW address            Mask     Device
    /// 192.0.2.90       0x1         0x2         3c:cd:93:11:22:33     *        eth0
    /// 192.0.2.91       0x1         0x0         00:00:00:00:00:00     *        eth0
    /// </code>
    /// <para>
    /// <b>The flags column is not decoration.</b> <c>0x0</c> is an entry the kernel has asked about and not
    /// had answered, and it carries all zeroes — which <see cref="Normalise"/> refuses anyway, so the flag
    /// check is belt to that braces rather than the only guard.
    /// </para>
    /// </summary>
    public static string? InTable(string? table, IPAddress address)
    {
        if (string.IsNullOrEmpty(table)) return null;
        var wanted = address.ToString();

        foreach (var line in table.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var cells = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (cells.Length < 4) continue;
            if (!string.Equals(cells[0], wanted, StringComparison.Ordinal)) continue;

            // ATF_COM — the entry has actually been answered. Anything else is a question in flight.
            if ((Flags(cells[2]) & 0x2) == 0) return null;

            return Normalise(cells[3]);
        }

        return null;

        static int Flags(string cell) =>
            cell.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(cell.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var f) ? f : 0;
    }

    /// <summary>The subnets this machine is actually on, read off its own interfaces.</summary>
    /// <remarks>
    /// Looked up every time rather than cached. A hub on an appliance outlives DHCP leases, cables and
    /// docker restarts, and a cached answer would be a document disagreeing with a cable — which is the
    /// failure <c>#121</c> settled next door by making the machine look rather than remember.
    /// </remarks>
    public static IReadOnlyList<Segment> LocalSegments()
    {
        var segments = new List<Segment>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var ip in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ip.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    segments.Add(new Segment(ip.Address, ip.PrefixLength));
                }
            }
        }
        catch { /* an interface list we can't read is a capture we don't attempt, never a broken driver */ }

        return segments;
    }
}

/// <summary>
/// This machine's neighbour table, and the rule about when it may be believed.
/// <para>
/// <b>Read as a file, never a process.</b> <c>/proc/net/arp</c> is one read; <c>arp -a</c> is a fork, and
/// a driver that forks per lookup is a driver that forks per reconnect, forever, on an appliance.
/// </para>
/// <para>
/// <b>It is allowed not to work.</b> The hub runs in a container with host networking, where this file is
/// the host's own table and the answer is right; it runs as a plain process on an appliance, where the
/// same is true; and it runs on a developer's Mac, where the file does not exist at all. The third case
/// returns a <see cref="MacFinding"/> that says so rather than one that guesses — and so does a container
/// <i>without</i> host networking, by a different door: the device's address then falls outside the
/// bridge's subnet and <see cref="MacCapture.Within"/> refuses it.
/// </para>
/// </summary>
/// <param name="ours">The subnets this machine is on. Injected so the rule can be tested without one.</param>
/// <param name="table">The neighbour table as text, or null where there is no such thing to read.</param>
public sealed class Neighbourhood(Func<IReadOnlyList<Segment>> ours, Func<string?> table)
{
    /// <summary>The real thing: this machine's interfaces, and this machine's <c>/proc/net/arp</c>.</summary>
    public static Neighbourhood System { get; } = new(MacCapture.LocalSegments, ReadProcNetArp);

    const string ProcNetArp = "/proc/net/arp";

    static string? ReadProcNetArp()
    {
        try { return File.Exists(ProcNetArp) ? File.ReadAllText(ProcNetArp) : null; }
        catch { return null; }
    }

    /// <summary>
    /// What is at this address on our own segment — or nothing, and why not.
    /// <para>
    /// The order of the two refusals is deliberate. Being on another subnet is checked <i>first</i>,
    /// because that is the case where an answer exists and is wrong; a missing table is merely a case
    /// where no answer exists. Reporting the second when the first is true would suggest that installing
    /// something would fix it.
    /// </para>
    /// </summary>
    public MacFinding At(IPAddress address)
    {
        if (!MacCapture.Within(address, ours()))
            return MacFinding.None(
                "The device isn't on any network this hub is on, so the only address we could read for it "
                + "here would be the router's. A magic packet doesn't cross subnets either — waking it "
                + "needs a MAC typed in, and a hub on its network to send from.");

        if (table() is not { } text)
            return MacFinding.None("This machine doesn't publish a neighbour table we can read.");

        return MacCapture.InTable(text, address) is { } mac
            ? new MacFinding(mac, MacOrigin.Neighbour, null)
            : MacFinding.None("Nothing in this machine's neighbour table answers for that address yet.");
    }

    /// <summary>
    /// The same, for a device configured by name or address. A name is resolved, which is why this is the
    /// async one; an address costs nothing.
    /// </summary>
    public async Task<MacFinding> AtAsync(string host, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host))
            return MacFinding.None("This device has no address to look up.");

        if (IPAddress.TryParse(host, out var literal)) return At(literal);

        try
        {
            var resolved = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct);
            return resolved.Length > 0
                ? At(resolved[0])
                : MacFinding.None($"'{host}' doesn't resolve to an address on this network.");
        }
        catch
        {
            return MacFinding.None($"'{host}' doesn't resolve to an address on this network.");
        }
    }
}
