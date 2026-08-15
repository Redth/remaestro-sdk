namespace Remaestro.ProxyAgent;

/// <param name="Path">The event node to open — <c>/dev/input/event3</c>.</param>
/// <param name="Name">What the device calls itself, from sysfs.</param>
public sealed record InputDevice(string Path, string Name);

/// <summary>
/// Which input devices this machine has, and which of them a config entry means.
/// <para>
/// <b>Everything here is file reads, and that is a deliberate design choice rather than a convenience.</b>
/// The names could have come from an <c>EVIOCGNAME</c> ioctl, which is how most programs ask — and that
/// would have meant a P/Invoke, a struct layout, and a seam that can only be tested on Linux with a device
/// plugged in. sysfs answers the same question with a text file, so the whole of device discovery is
/// <see cref="Root"/> plus <see cref="Directory.EnumerateDirectories(string)"/>, and a temp directory is a
/// complete and honest fake.
/// </para>
/// </summary>
public sealed class InputDevices(string root = "/")
{
    /// <summary>
    /// Where the filesystem starts. Only ever something else in a test, where it is a directory laid out
    /// like a Pi's.
    /// </summary>
    public string Root { get; } = root;

    /// <summary>
    /// Every input device this machine has, in event-node order.
    /// <para>
    /// A device whose name can't be read is still listed, with an empty name. It can then be pointed at by
    /// path, which is the case that matters: something is plugged in, sysfs is being awkward about it, and
    /// the alternative is a picker that swears there is nothing there.
    /// </para>
    /// </summary>
    public IReadOnlyList<InputDevice> All()
    {
        var classDir = Path.Combine(Root, "sys", "class", "input");
        if (!Directory.Exists(classDir)) return [];

        var found = new List<(int Number, InputDevice Device)>();

        foreach (var entry in Directory.EnumerateDirectories(classDir))
        {
            var node = Path.GetFileName(entry);

            // Only the event nodes. /sys/class/input also carries mouseN and jsN, which are older interfaces
            // onto the same hardware — listing them would offer the same remote three times, and two of the
            // three cannot report a keypress.
            if (!node.StartsWith("event", StringComparison.Ordinal)) continue;
            if (!int.TryParse(node["event".Length..], out var number)) continue;

            found.Add((number, new InputDevice(
                Path.Combine(Root, "dev", "input", node),
                ReadName(entry))));
        }

        // Numerically rather than as text, so event10 doesn't sort between event1 and event2 and quietly
        // renumber which device an index refers to.
        return [.. found.OrderBy(f => f.Number).Select(f => f.Device)];
    }

    static string ReadName(string sysfsEntry)
    {
        try
        {
            var path = Path.Combine(sysfsEntry, "device", "name");
            return File.Exists(path) ? File.ReadAllText(path).Trim('\0', ' ', '\n', '\r') : "";
        }
        catch (Exception)
        {
            // An unreadable name is not a missing device. Returning blank keeps it selectable by path.
            return "";
        }
    }

    /// <summary>
    /// The device a selector means, or null.
    /// <para>
    /// A path is taken literally and is not required to be in <see cref="All"/> — a
    /// <c>/dev/input/by-id/…</c> symlink is the recommended way to write one and never appears in that list,
    /// because it is a name for an event node rather than an event node.
    /// </para>
    /// </summary>
    public InputDevice? Resolve(string? selector)
    {
        if (selector is not { Length: > 0 }) return null;

        var chosen = selector.Trim();
        if (chosen.Length == 0) return null;

        if (chosen[0] == '/')
        {
            var known = All().FirstOrDefault(d =>
                string.Equals(d.Path, chosen, StringComparison.Ordinal));

            return known ?? new InputDevice(chosen, "");
        }

        // By name. Case-insensitive substring, matching the hub's ProxyDeviceMatch — see the note there
        // about vendors' spelling.
        return All().FirstOrDefault(d =>
            d.Name.Contains(chosen, StringComparison.OrdinalIgnoreCase));
    }
}
