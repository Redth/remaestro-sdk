using System.Net.NetworkInformation;
using System.Reflection;

namespace Remaestro.ProxyAgent;

/// <param name="Id">The MAC-derived id the hub knows this proxy by, and mDNS announces.</param>
/// <param name="Chip">Which board this is, in the hub's vocabulary — see <c>ProxyBoards</c>.</param>
public sealed record BoardIdentity(string Id, string Chip, string Firmware)
{
    /// <summary>What every proxy's announced id starts with. Mirrors <c>ProxyNaming.AnnouncedPrefix</c>.</summary>
    public const string Prefix = "remaestro-";

    public static BoardIdentity Detect() => new(DetectId(), DetectChip(), Version());

    /// <summary>
    /// <c>remaestro-</c> and twelve hex digits of MAC, which is what an ESP32 does and therefore what the
    /// hub's store, mDNS and every saved port name already expect.
    /// <para>
    /// The lowest MAC among real interfaces rather than the first, because "first" is enumeration order and
    /// moves when a USB Ethernet adapter is plugged in — which would give the same Pi a second identity, an
    /// unadopted one, and leave every device pointing at a proxy that no longer exists.
    /// </para>
    /// </summary>
    public static string DetectId()
    {
        try
        {
            var macs = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
                .Select(n => n.GetPhysicalAddress().GetAddressBytes())
                .Where(b => b.Length == 6 && b.Any(x => x != 0))
                .Select(Convert.ToHexStringLower)
                .Order(StringComparer.Ordinal)
                .ToList();

            if (macs.Count > 0) return Prefix + macs[0];
        }
        catch (Exception) { /* fall through — an identity is worth more than an exception */ }

        return Prefix + "unknown";
    }

    /// <summary>
    /// Which Pi this is, from the device tree — the same file <c>HostBoard</c> reads, normalised into the
    /// short ids the hub matches on.
    /// <para>
    /// Anything unrecognised is <c>linux</c> rather than a guess. That is a complete answer, not a
    /// degradation: the hub branches on the <i>family</i> and never on the model, so an unknown Pi and a
    /// mini PC both work, and only lose the marketing name on a card.
    /// </para>
    /// </summary>
    public static string DetectChip(string? model = null)
    {
        model ??= ReadModel();

        if (model.Length == 0) return "linux";

        // Longest first. "Raspberry Pi 4" is a substring of nothing here, but "Pi 5" and "Pi 500" are the
        // shape of mistake this ordering exists to prevent as models are added.
        if (Has(model, "Zero 2")) return "pi-zero-2w";
        if (Has(model, "Compute Module 5")) return "pi-cm5";
        if (Has(model, "Compute Module 4")) return "pi-cm4";
        if (Has(model, "Raspberry Pi 5")) return "pi-5";
        if (Has(model, "Raspberry Pi 4")) return "pi-4";
        if (Has(model, "Raspberry Pi 3")) return "pi-3";

        return "linux";
    }

    static bool Has(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    static string ReadModel()
    {
        try
        {
            // NUL-terminated, as HostBoard also notes — the trailing byte shows up as a stray character in
            // anything that prints it.
            if (File.Exists("/proc/device-tree/model"))
                return File.ReadAllText("/proc/device-tree/model").Trim('\0', ' ', '\n', '\r');
        }
        catch (Exception) { /* unreadable is not fatal; see DetectChip */ }

        return "";
    }

    /// <summary>
    /// What this agent reports as its version. Compared against nothing — see the hub's
    /// <c>ProxyUpdateVerdict.UpdatesItself</c>, which is the whole point: this machine is not flashed from
    /// the hub, so the version is for a bug report rather than for an update decision.
    /// </summary>
    static string Version() =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0]
        ?? "unversioned";
}
