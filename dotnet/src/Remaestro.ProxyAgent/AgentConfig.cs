using System.Text.Json;
using System.Text.Json.Serialization;

namespace Remaestro.ProxyAgent;

/// <summary>
/// The document the hub writes to this proxy — the same one an ESP32 gets, read by the other language.
/// <para>
/// Its shape is a contract with <c>ProxyConfigJson.Write</c>, and a test asserts the field names still line
/// up. Unknown keys are ignored rather than rejected, which is what lets a newer hub configure an older
/// proxy: a role this build has never heard of arrives, fails to start, and says so, instead of the whole
/// document failing to parse and the proxy doing nothing at all.
/// </para>
/// </summary>
public sealed record AgentConfig
{
    public string Name { get; init; } = "";

    /// <summary>Where the hub is, as a URL. The tunnel is dialled at that host on <see cref="TunnelWire.Port"/>.</summary>
    public string Hub { get; init; } = "";

    /// <summary>The shared secret minted when this proxy was adopted. Presented on every connection.</summary>
    public string Token { get; init; } = "";

    public List<AgentPin> Pins { get; init; } = [];

    /// <summary>
    /// The host to dial, out of whatever shape the hub's address arrived in.
    /// <para>
    /// Lenient because this value is written by the hub from its own configuration and has been a bare host,
    /// a <c>http://host:5006</c> and a trailing-slash version of both. A proxy that refused one of them
    /// would go quiet with a perfectly good config on it and no screen to say why.
    /// </para>
    /// </summary>
    public string? HubHost()
    {
        var text = Hub.Trim();
        if (text.Length == 0) return null;

        if (Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Host.Length > 0) return url.Host;

        // No scheme. Take everything before a port or a path.
        var host = text.Split('/', 2)[0].Split(':', 2)[0];
        return host.Length > 0 ? host : null;
    }

    /// <summary>The <paramref name="index"/>-th entry of a role, counted the way the hub counts.</summary>
    /// <remarks>
    /// Per role and in config order, which is the same rule <c>ProxyHardware</c> uses to number the entries
    /// it puts in the picker and <c>ProxyWiring.Describe</c> uses to describe them. All three have to agree
    /// or a channel opens onto the wrong peripheral — the kind of fault that looks like a broken remote.
    /// </remarks>
    public AgentPin? Find(string role, int index) =>
        Pins.Where(p => p.Role == role).Skip(index).FirstOrDefault();

    public static AgentConfig? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<AgentConfig>(json, Options); }
        catch (JsonException) { return null; }
    }

    public static async Task<AgentConfig?> ReadAsync(string path, CancellationToken ct = default)
    {
        try { return Parse(await File.ReadAllTextAsync(path, ct)); }
        catch (Exception) { return null; }
    }

    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        // A hub newer than this agent will send roles and keys it doesn't know. Ignoring them is the whole
        // compatibility story; throwing would take the proxy off the network over a field it didn't need.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };
}

/// <summary>One thing this proxy has been set up to do. Mirrors <c>ProxyPin</c>.</summary>
public sealed record AgentPin
{
    public string Role { get; init; } = "";

    /// <summary>Always present in the document, and always meaningless here. A Pi has no GPIO to be on.</summary>
    public int Pin { get; init; } = -1;

    public int Pin2 { get; init; } = -1;

    public string Name { get; init; } = "";

    /// <summary>
    /// Which device this is — a name to match, or an absolute path. See the hub's <c>ProxyDeviceMatch</c>,
    /// which is where the rule about names outliving event numbers is written down.
    /// </summary>
    public string Device { get; init; } = "";

    public Dictionary<string, string> Settings { get; init; } = [];
}

/// <summary>The roles this agent can actually start. Mirrors the Linux half of <c>ProxyBoards.Roles</c>.</summary>
public static class AgentRole
{
    public const string UsbInput = "usb.input";

    /// <summary>
    /// What this build handles. Everything else in the hub's vocabulary is a role a Pi could do and this
    /// agent doesn't yet — serial, IR through LIRC, Bluetooth through BlueZ, and the microphone half of the
    /// dongle. Each is a handler here and a line in <c>ProxyBoards.Roles</c>, and none of them is a change
    /// to this file's shape.
    /// </summary>
    public static IReadOnlyList<string> All => [UsbInput];
}
