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
    static readonly ConcurrentDictionary<string, byte> _secrets = new(StringComparer.Ordinal);
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
        if (value is { Length: >= 4 }) _secrets[value] = 1;
    }

    public static void Emit(string deviceId, string transport, string direction, string text, string detail = "",
        string endpoint = "", string hex = "")
    {
        if (!Enabled(deviceId)) return;
        _records.Enqueue(new Entry(
            Interlocked.Increment(ref _seq), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            deviceId, transport, direction, Redact(text), Redact(detail), endpoint, hex));
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

        var kept = data.Length > MaxHexBytes ? data[..MaxHexBytes] : data;
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

    static string Redact(string text)
    {
        if (text.Length == 0 || _secrets.IsEmpty) return text;
        foreach (var secret in _secrets.Keys)
            if (text.Contains(secret, StringComparison.Ordinal))
                text = text.Replace(secret, "«redacted»");
        return text;
    }
}
