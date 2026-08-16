using System.Collections.Concurrent;

namespace Remaestro.Sdk;

/// <summary>
/// The driver's own record of its conversation with a device — the wire exchange the hub can't see from the
/// outside. Captured only when the hub turns it on (it's costly and sees everything), and redacted at the
/// source, so a secret never leaves this process even into the hub.
/// <para>
/// Static because a driver <i>is</i> a process: one buffer per driver, shared by every transport the SDK
/// ships. That's the whole leverage — instrument <see cref="LineDevice"/>, <see cref="ByteLink"/> and the
/// HTTP handler once, and every driver built on them gets diagnostics with no work of its own.
/// </para>
/// </summary>
public static class Diag
{
    /// <param name="Endpoint">Where this happened, in the transport's own terms — an address, a port, a URL.</param>
    /// <param name="Hex">The bytes as they went past, when the transport knows them. Empty otherwise.</param>
    public readonly record struct Entry(
        long Seq, long TsMs, string DeviceId, string Transport, string Direction, string Text, string Detail,
        string Endpoint = "", string Hex = "");

    // A few minutes of chatty gear. Oldest dropped first — the interesting part is what just happened when
    // the user reproduced the problem, not an hour ago.
    const int Max = 4000;

    /// <summary>
    /// How much of a payload is kept as hex. A driver streaming artwork over HTTP would otherwise put
    /// megabytes a second through this buffer, and the first few dozen bytes are what identifies a frame.
    /// </summary>
    const int MaxHexBytes = 512;

    static readonly ConcurrentQueue<Entry> _records = new();
    static readonly HashSet<string> _on = new(StringComparer.Ordinal);

    /// <summary>
    /// Every secret this driver knows, mapped to the bytes it is on a wire.
    /// <para>
    /// The bytes are held rather than recomputed because <see cref="Bytes"/> has to find a secret in a
    /// payload, and it has to do that on every captured frame. Encoding a handful of short strings once is
    /// the difference between that being free and it being the reason capture is expensive.
    /// </para>
    /// </summary>
    static readonly ConcurrentDictionary<string, byte[]> _secrets = new(StringComparer.Ordinal);
    static readonly Lock _gate = new();
    static long _seq;

    /// <summary>
    /// Capture everything, whoever it belongs to.
    /// <para>
    /// The per-device switch can only name devices the hub already knows about, which quietly excludes the
    /// two cases you most want when something is broken: a driver that is still starting up, and traffic
    /// attributed to an id that was never registered. Tracing "everything" has to mean everything.
    /// </para>
    /// </summary>
    static volatile bool _everything;

    public static bool Everything
    {
        get => _everything;
        set => _everything = value;
    }

    public static bool Enabled(string deviceId)
    {
        if (_everything) return true;
        lock (_gate) return _on.Contains(deviceId);
    }

    public static void SetEnabled(string deviceId, bool on)
    {
        lock (_gate) { if (on) _on.Add(deviceId); else _on.Remove(deviceId); }
    }

    /// <summary>Register a value that must never appear in a record — a device's secrets, from its config.</summary>
    public static void RegisterSecret(string? value)
    {
        // Below a few characters it isn't a secret worth masking, and masking it would eat ordinary text.
        if (value is { Length: >= 4 }) _secrets[value] = System.Text.Encoding.UTF8.GetBytes(value);
    }

    /// <summary>
    /// One moment on the wire, with every registered secret gone from <b>every</b> field of it.
    /// <para>
    /// It used to be every field except two. <c>endpoint</c> and <c>hex</c> went into the record exactly as
    /// they arrived, and <see cref="Bytes"/> renders one payload twice — once as text and once as hex — so a
    /// driver sending a password over a line protocol had it masked in the readable column and printed in
    /// full, one column to the right. <c>WattboxDriver</c> does precisely that, and the hub copies every
    /// field of every record verbatim into the <c>trace.json</c> inside a support bundle somebody then
    /// emails. The guard that existed enumerated the record's string fields and stopped one short.
    /// </para>
    /// </summary>
    public static void Emit(string deviceId, string transport, string direction, string text, string detail = "",
        string endpoint = "", string hex = "")
    {
        if (!Enabled(deviceId)) return;
        _records.Enqueue(new Entry(
            Interlocked.Increment(ref _seq), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            deviceId, transport, direction, Redact(text), Redact(detail), Redact(endpoint), RedactHex(hex)));
        while (_records.Count > Max) _records.TryDequeue(out _);
    }

    public static void Tx(string deviceId, string transport, string text, string endpoint = "") =>
        Emit(deviceId, transport, "tx", text, endpoint: endpoint);

    public static void Rx(string deviceId, string transport, string text, string endpoint = "") =>
        Emit(deviceId, transport, "rx", text, endpoint: endpoint);

    public static void Open(string deviceId, string transport, string where) =>
        Emit(deviceId, transport, "open", where, endpoint: where);

    public static void Close(string deviceId, string transport, string why = "") =>
        Emit(deviceId, transport, "close", why);

    public static void Error(string deviceId, string transport, string message, string endpoint = "") =>
        Emit(deviceId, transport, "error", message, endpoint: endpoint);

    public static void Info(string deviceId, string transport, string message, string detail = "") =>
        Emit(deviceId, transport, "info", message, detail);

    /// <summary>
    /// A moment on a transport whose bytes are worth keeping verbatim.
    /// <para>
    /// The text rendering stays, because for the large share of AV gear that speaks ASCII it is the readable
    /// version and the hex is noise. But a rendering is a lossy view of a binary protocol — a Samsung frame
    /// or an HID report read as text is mostly dots — so both travel, and the view picks.
    /// </para>
    /// </summary>
    public static void Bytes(string deviceId, string transport, string direction, ReadOnlySpan<byte> data,
        string text = "", string endpoint = "")
    {
        // Checked before the hex is built: formatting a payload nobody asked for is the cost that made this
        // off by default in the first place.
        if (!Enabled(deviceId)) return;

        // Blotted *before* the cut, not after: a secret straddling the cap would otherwise survive as a
        // fragment, and half a password is a shorter password rather than a redacted one. Only the prefix
        // that can reach the hex is scanned, so this stays bounded on a payload of any size.
        var kept = Blot(data);
        var hex = Convert.ToHexString(kept);
        if (data.Length > MaxHexBytes) hex += $"… (+{data.Length - MaxHexBytes} bytes)";

        Emit(deviceId, transport, direction, text.Length > 0 ? text : $"{data.Length} bytes", endpoint: endpoint, hex: hex);
    }

    public static void TxBytes(string deviceId, string transport, ReadOnlySpan<byte> data, string text = "",
        string endpoint = "") => Bytes(deviceId, transport, "tx", data, text, endpoint);

    public static void RxBytes(string deviceId, string transport, ReadOnlySpan<byte> data, string text = "",
        string endpoint = "") => Bytes(deviceId, transport, "rx", data, text, endpoint);

    /// <summary>Records newer than <paramref name="afterSeq"/>, for a device or (empty id) all of them.</summary>
    public static IReadOnlyList<Entry> Since(string deviceId, long afterSeq) =>
        _records.ToArray()
            .Where(r => r.Seq > afterSeq && (deviceId.Length == 0 || r.DeviceId == deviceId))
            .ToList();

    /// <summary>What a blotted byte reads as — <c>*</c>, so a hex dump shows 2A where a secret was.</summary>
    const byte Blotted = 0x2A;

    /// <summary>
    /// The leading bytes of a payload, at most <see cref="MaxHexBytes"/> of them, with every registered
    /// secret overwritten.
    /// <para>
    /// Scanned over a window one byte short of a secret's length past the cap, because that is the whole of
    /// what could contribute to the hex — so a megabyte of artwork costs a bounded scan rather than a
    /// proportional one, which is the same reason the cap exists at all.
    /// </para>
    /// </summary>
    static byte[] Blot(ReadOnlySpan<byte> data)
    {
        var cut = Math.Min(data.Length, MaxHexBytes);
        if (_secrets.IsEmpty) return data[..cut].ToArray();

        var longest = 0;
        foreach (var bytes in _secrets.Values) longest = Math.Max(longest, bytes.Length);

        var window = data[..Math.Min(data.Length, MaxHexBytes + Math.Max(0, longest - 1))].ToArray();
        foreach (var needle in _secrets.Values)
        {
            if (needle.Length == 0) continue;
            for (var from = 0; from <= window.Length - needle.Length;)
            {
                var at = window.AsSpan(from).IndexOf(needle);
                if (at < 0) break;
                window.AsSpan(from + at, needle.Length).Fill(Blotted);
                from += at + needle.Length;
            }
        }

        return window[..cut];
    }

    /// <summary>
    /// A hex string with the hex of every registered secret blotted out. The backstop for anything that
    /// hands <see cref="Emit"/> a hex string it built itself rather than going through <see cref="Bytes"/>.
    /// </summary>
    static string RedactHex(string hex)
    {
        if (hex.Length == 0 || _secrets.IsEmpty) return hex;
        foreach (var bytes in _secrets.Values)
        {
            var needle = Convert.ToHexString(bytes);
            if (hex.Contains(needle, StringComparison.OrdinalIgnoreCase))
                hex = hex.Replace(needle, new string('*', needle.Length), StringComparison.OrdinalIgnoreCase);
        }
        return hex;
    }

    static string Redact(string text)
    {
        if (text.Length == 0 || _secrets.IsEmpty) return text;
        foreach (var secret in _secrets.Keys)
            if (text.Contains(secret, StringComparison.Ordinal))
                text = text.Replace(secret, "«redacted»");
        return text;
    }
}
