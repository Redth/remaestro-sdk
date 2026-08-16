using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Remaestro.Sdk;

namespace Remaestro.Drivers.Jellyfin;

/// <summary>
/// A Jellyfin server as a source device: reachability, library search, and stream-URL resolution — and,
/// when it's been told which app to talk to, a way of putting something on that app's screen.
/// </summary>
public sealed partial class JellyfinDriver : IRemaestroDriver
{
    /// <summary>
    /// What a Jellyfin item should be shown as. Mostly the kind's default; the exceptions are the ones
    /// Jellyfin gives landscape artwork to — a library's banner, a collection's still, an episode's frame.
    /// </summary>
    public static string ShapeFor(string kind, string type) => type switch
    {
        "CollectionFolder" or "UserView" or "BoxSet" or "Playlist" => NodeShape.Wide,
        "Episode" or "Video" or "TvChannel" or "Photo" => NodeShape.Wide,
        "MusicAlbum" or "MusicArtist" or "Audio" or "Person" => NodeShape.Square,
        "Movie" or "Series" or "Season" or "Trailer" => NodeShape.Poster,
        _ => NodeShape.ForKind(kind),
    };

    /// <summary>
    /// Asks the server for the image already cropped to the shape we're going to draw it at, rather than
    /// fetching a poster and cropping it in the browser — smaller over the wire and no surprise letterbox.
    /// </summary>
    public static string Fit(string shape) => shape switch
    {
        NodeShape.Wide => "fillWidth=640&fillHeight=360",
        NodeShape.Square => "fillWidth=440&fillHeight=440",
        NodeShape.Banner => "fillWidth=800&fillHeight=200",
        _ => "fillHeight=480",
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public string TypeId => "jellyfin";
    public string DisplayName => "Jellyfin";
    public string Description => "A Jellyfin media server: search the library and resolve direct-play stream URLs (e.g. to open on Kodi).";

    // Jellyfin servers answer a "who is JellyfinServer?" UDP broadcast on :7359 — the hub's UDP prober handles it.
    public IReadOnlyList<string> DiscoveryServices => ["udp:jellyfin"];

    /// <summary>What this type is for, so a list can scope itself to the job. See <see cref="DeviceTrait"/>.</summary>
    public IReadOnlyList<string> Traits { get; } = [DeviceTrait.MediaLibrary];

    public IReadOnlyList<ConfigField> ConfigSchema { get; } =
    [
        new("serverUrl", "Server URL", Required: true, Help: "e.g. https://jelly.example.net"),
        new("apiKey", "API Key", Type: "secret", Required: true, Help: "Jellyfin → Dashboard → API Keys"),
        new("userId", "User Id", Help: "Optional; used to scope search results"),
        new("castTo", "Play on", Help: "The name of a Jellyfin app signed in to this server, e.g. \"Living Room TV\". "
            + "Fill this in to make the server somewhere you can send things, rather than only somewhere to pick them from."),
    ];

    public IReadOnlyList<CommandInfo> Commands { get; } =
    [
        new("search", "Search", "Find an item by name; returns the top match's id + name", [new("query", "Query", Required: true)]),
        new("stream_url", "Stream URL", "Build a direct-play URL for an item id", [new("itemId", "Item Id", Required: true)]),
        new("sessions", "Clients", "Which apps signed in to this server could be handed something to play"),
        new("play_on", "Play on a client", "Start something on a Jellyfin app that's already running",
        [
            new("item", "Item", Required: true, Help: "An item id, or a stream URL this server issued"),
            new("client", "Client", Help: "Its name — blank uses the one this device is configured with"),
            new("positionSeconds", "Start at", Type: "number"),
        ]),
    ];

    public IReadOnlyList<EventSchema> Events { get; } =
    [
        new("connection.changed", "Server reachability changed", [new("online", "bool"), new("serverName", "string")]),
        new("library.play", "A library item was played — route this to a playback target with a rule",
            [new("nodeId"), new("title"), new("streamUrl"), new("mediaType"), new("positionSeconds", "number")], HasExtraData: true),
        new("library.queue", "A library item was queued", [new("nodeId"), new("title"), new("streamUrl")]),
        new("session.changed", "A client on the server started or changed what it's playing",
            [new("title"), new("client"), new("paused", "bool"), new("positionSeconds", "number")], HasExtraData: true),
        new("session.stopped", "A client on the server stopped playing", [new("client")])
    ];

    public IReadOnlyList<StateField> StateSchema { get; } =
    [
        new("online", "bool"), new("serverName", "string"),
        new("nowPlaying", "string", "Title playing on any client, if any"),
        new("castTo", "string", "The client this server sends things to, if one was named")
    ];

    // This server exposes a browsable content library — see docs/navigation-spec.md.
    public bool SupportsNavigation => true;

    public Task<IRemaestroDevice> CreateDeviceAsync(string deviceId, string name, IReadOnlyDictionary<string, string> config, CancellationToken ct)
    {
        var server = config.GetValueOrDefault("serverUrl", "").TrimEnd('/');
        return Task.FromResult<IRemaestroDevice>(new JellyfinDevice(deviceId, name, server,
            config.GetValueOrDefault("apiKey", ""), config.GetValueOrDefault("userId"),
            config.GetValueOrDefault("castTo", ""), Commands, Http));
    }

    /// <summary>
    /// The item id inside one of this server's own stream URLs — see <see cref="JellyfinDevice.ItemOf"/>.
    /// Jellyfin writes ids both bare and hyphenated depending on the endpoint, so both are matched.
    /// </summary>
    [GeneratedRegex(@"/(?:Videos|Items|Audio)/([0-9a-fA-F]{8}-?(?:[0-9a-fA-F]{4}-?){3}[0-9a-fA-F]{12})",
        RegexOptions.IgnoreCase)]
    internal static partial Regex ItemIdInUrl();
}

internal sealed class JellyfinDevice : DeviceBase, INavigableDevice
{
    private readonly string _server;
    private readonly string _apiKey;
    private readonly string? _userId;
    private readonly string _castTo;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();
    private readonly ListenerSupervisor _poll;
    private readonly ListenerSupervisor _socket;
    private bool? _lastOnline;
    private string? _serverName;
    private string? _resolvedUserId;
    private string _lastNowPlaying = "";

    public JellyfinDevice(string id, string name, string server, string apiKey, string? userId, string castTo, IReadOnlyList<CommandInfo> commands, HttpClient http) : base(id, name)
    {
        _server = server; _apiKey = apiKey; _userId = userId; _castTo = castTo.Trim(); _http = http; Commands = commands;
        SetState("online", "false");
        SetState("castTo", _castTo);

        // A server with somewhere to send things is a player as well as a shelf, and the media role picker
        // asks for the trait before it asks whether anything can be handed over. Stated per device rather
        // than on the driver because it is a property of *this* server's configuration: the same driver
        // running against a headless backup server is a library and nothing else.
        Playback = _castTo.Length > 0 ? CanCast : MediaPlayback.Nothing;
        if (_castTo.Length > 0) SetTraits(DeviceTrait.MediaLibrary, DeviceTrait.MediaPlayer);

        // Two supervisors cooperate: the poll is the authoritative reachability + server-name signal; the
        // WebSocket is the always-on listener that turns online true the instant it connects and streams
        // live now-playing from every client. Both funnel through SetOnline so the state never flaps.
        _poll = new ListenerSupervisor(
            ReachabilityAsync,
            idle: TimeSpan.FromSeconds(5),
            retryMin: TimeSpan.FromSeconds(3),
            retryMax: TimeSpan.FromSeconds(20),
            linkedToken: _cts.Token).Start();

        _socket = new ListenerSupervisor(
            SocketAsync,
            idle: TimeSpan.FromSeconds(2),
            retryMin: TimeSpan.FromSeconds(3),
            retryMax: TimeSpan.FromSeconds(30),
            onConnectedChanged: connected => { if (connected) SetOnline(true); },
            linkedToken: _cts.Token).Start();
    }

    public override IReadOnlyList<CommandInfo> Commands { get; }

    /// <summary>
    /// What a Jellyfin server can be handed, which is <b>not the same question as what it can play</b> —
    /// it has no screen. It <i>routes</i>: the thing that ends up playing is a Jellyfin app already
    /// running on a television, and the server tells it what to open. So a library server stops being only
    /// a place to pick from and becomes a place to send to, and the television's own driver — if it even
    /// has one — never hears about it.
    /// <para>
    /// <b>The target is a session, and <see cref="MediaPlayback"/> cannot say which.</b> The record is one
    /// command and one parameter, and that parameter is spent on the item. Casting needs two coordinates:
    /// what, and to whom. The way out is the one <c>AppleKind.Open</c> took for AirPlay's
    /// <c>app=remaestro.stream</c> — if a command needs a second value to <i>mean</i> "play this", the
    /// device hasn't got a play command, it has a general one the driver configures. So the client is
    /// configuration (<c>castTo</c>) and <c>play_on</c> takes the item alone. Widening the SDK record was
    /// considered and rejected for the same reason it was rejected then: every other driver would carry a
    /// field only this one and Plex could ever fill.
    /// </para>
    /// <para>
    /// <b>Which makes the declaration conditional, and that is the honest part.</b> A server nobody has
    /// named a client on has nowhere to put a film, and says <see cref="MediaPlayback.Nothing"/> — asked
    /// and answered — rather than staying silent and letting the sniff offer it. Name one and it can.
    /// </para>
    /// <para>
    /// <b>No kinds, deliberately.</b> The constraint on a Jellyfin session isn't the kind of thing, it's
    /// where the thing came from: a client can open anything in this server's library and nothing outside
    /// it. <c>Kinds</c> has no way to say "must be an item on this server", and a list of leaf kinds would
    /// be a wrong answer to a question nobody asked — it would drop <c>trailer</c> and <c>musicvideo</c>,
    /// which this driver's own projection emits, and still let a Kodi URL through. So the kind list stays
    /// open and <see cref="PlayOnAsync"/> enforces provenance where it can actually be checked, in words.
    /// </para>
    /// </summary>
    static readonly MediaPlayback CanCast = new("play_on", "item");

    /// <inheritdoc cref="CanCast"/>
    public override MediaPlayback? Playback { get; }

    // Reachability probe (also the source of server name). A throw marks offline via SetOnline.
    async Task ReachabilityAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync($"{_server}/System/Info/Public", ct);
            if (!resp.IsSuccessStatusCode) { SetOnline(false); return; }
            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("ServerName", out var sn)) { _serverName = sn.GetString(); SetState("serverName", _serverName); }
            SetOnline(true);
        }
        catch (OperationCanceledException) { throw; }
        catch { SetOnline(false); }
    }

    void SetOnline(bool online)
    {
        if (_lastOnline == online) return;
        _lastOnline = online;
        SetState("online", online ? "true" : "false");
        if (!online) { SetState("nowPlaying", ""); _lastNowPlaying = ""; }
        Emit("connection.changed", new Dictionary<string, string> { ["online"] = online ? "true" : "false", ["serverName"] = _serverName ?? "" });
    }

    // ---- WebSocket listener: Jellyfin's /socket streams live session (now-playing) updates ----
    async Task SocketAsync(CancellationToken ct)
    {
        var scheme = _server.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        var host = _server.Replace("https://", "", StringComparison.OrdinalIgnoreCase).Replace("http://", "", StringComparison.OrdinalIgnoreCase);
        var uri = new Uri($"{scheme}://{host}/socket?api_key={_apiKey}&deviceId={DeviceId}");

        using var ws = new ClientWebSocket();
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(8));
            await ws.ConnectAsync(uri, connect.Token);
        }
        DiagSockets.WsOpened(DeviceId, _server);
        SetOnline(true);   // connected — flip online instantly rather than waiting for the next poll

        // Subscribe to session updates (push interval / throttle in ms).
        await SendAsync(ws, """{"MessageType":"SessionsStart","Data":"0,1500"}""", ct);

        // Jellyfin closes an idle socket, so keep it warm with periodic KeepAlive frames.
        using var hb = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = KeepAliveLoop(ws, hb.Token);
        try
        {
            var buffer = new byte[64 * 1024];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var msg = await ReceiveTextAsync(ws, buffer, ct);
                if (msg is null) break;      // peer closed
                HandleSocketMessage(msg);
            }
        }
        finally
        {
            hb.Cancel();
            try { await heartbeat; } catch { }
        }
    }

    async Task KeepAliveLoop(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
                await SendAsync(ws, """{"MessageType":"KeepAlive"}""", ct);
            }
        }
        catch { /* the read loop owns reconnection */ }
    }

    void HandleSocketMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("MessageType", out var mt)) return;
            switch (mt.GetString())
            {
                case "Sessions" when doc.RootElement.TryGetProperty("Data", out var data):
                    ApplySessions(data);
                    break;
                // Server asks us to prove we're alive within N seconds — the heartbeat already covers it.
                case "ForceKeepAlive":
                    break;
            }
        }
        catch { /* ignore malformed frames */ }
    }

    // The primary now-playing across all clients → a single session.changed / session.stopped signal.
    void ApplySessions(JsonElement sessions)
    {
        if (sessions.ValueKind != JsonValueKind.Array) return;
        foreach (var s in sessions.EnumerateArray())
        {
            if (!s.TryGetProperty("NowPlayingItem", out var item) || item.ValueKind != JsonValueKind.Object) continue;
            var title = item.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
            var client = s.TryGetProperty("Client", out var c) ? c.GetString() ?? "" : "";
            var paused = s.TryGetProperty("PlayState", out var ps) && ps.TryGetProperty("IsPaused", out var ip) && ip.ValueKind == JsonValueKind.True;
            long ticks = s.TryGetProperty("PlayState", out var ps2) && ps2.TryGetProperty("PositionTicks", out var pt) && pt.ValueKind == JsonValueKind.Number ? pt.GetInt64() : 0;

            var key = $"{client}|{title}";
            if (key != _lastNowPlaying)
            {
                _lastNowPlaying = key;
                SetState("nowPlaying", title);
                Emit("session.changed", new Dictionary<string, string>
                {
                    ["title"] = title, ["client"] = client,
                    ["paused"] = paused ? "true" : "false",
                    ["positionSeconds"] = (ticks / 10_000_000).ToString()
                });
            }
            return; // first client with something playing wins
        }
        // Nothing playing on any client now.
        if (_lastNowPlaying.Length > 0)
        {
            var wasClient = _lastNowPlaying.Split('|')[0];
            _lastNowPlaying = "";
            SetState("nowPlaying", "");
            Emit("session.stopped", new Dictionary<string, string> { ["client"] = wasClient });
        }
    }

    // Instance rather than static so the socket can be traced. The endpoint is the server rather than the
    // socket URL, which carries the API key as a query parameter.
    async Task SendAsync(ClientWebSocket ws, string text, CancellationToken ct)
        => await ws.SendTracedAsync(DeviceId, _server, text, ct);

    async Task<string?> ReceiveTextAsync(ClientWebSocket ws, byte[] buffer, CancellationToken ct)
    {
        var sb = new StringBuilder();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close)
            {
                DiagSockets.WsClosed(DeviceId, _server, "the server closed the socket");
                return null;
            }
            sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
            if (r.EndOfMessage)
            {
                var message = sb.ToString();
                DiagSockets.WsReceived(DeviceId, _server, message);
                return message;
            }
        }
    }

    public override async Task<CommandResult> ExecuteAsync(string commandId, IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        try
        {
            switch (commandId)
            {
                case "search":
                    var query = args.GetValueOrDefault("query", "");
                    if (string.IsNullOrEmpty(query)) return CommandResult.Fail("query is required");
                    return await SearchAsync(query, ct);
                case "stream_url":
                    var itemId = args.GetValueOrDefault("itemId", "");
                    if (string.IsNullOrEmpty(itemId)) return CommandResult.Fail("itemId is required");
                    return CommandResult.Success(new Dictionary<string, string> { ["url"] = StreamUrl(itemId) });
                case "sessions":
                    return await SessionsAsync(ct);
                case "play_on":
                    return await PlayOnAsync(
                        args.GetValueOrDefault("item", "").Trim(),
                        args.GetValueOrDefault("client", "").Trim(),
                        args.GetValueOrDefault("positionSeconds", "").Trim(),
                        ct);
                default:
                    return CommandResult.Fail($"Unknown command '{commandId}'");
            }
        }
        catch (Exception ex) { return CommandResult.Fail(ex.Message); }
    }

    async Task<CommandResult> SearchAsync(string query, CancellationToken ct)
    {
        var url = $"{_server}/Search/Hints?searchTerm={Uri.EscapeDataString(query)}&limit=1&api_key={_apiKey}";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
        if (doc.RootElement.TryGetProperty("SearchHints", out var hints) && hints.ValueKind == JsonValueKind.Array && hints.GetArrayLength() > 0)
        {
            var hit = hints[0];
            var id = hit.TryGetProperty("ItemId", out var i) ? i.GetString() : (hit.TryGetProperty("Id", out var i2) ? i2.GetString() : null);
            var name = hit.TryGetProperty("Name", out var n) ? n.GetString() : null;
            return CommandResult.Success(new Dictionary<string, string>
            {
                ["itemId"] = id ?? "",
                ["name"] = name ?? "",
                ["streamUrl"] = id is null ? "" : StreamUrl(id)
            });
        }
        return CommandResult.Fail("No results");
    }

    string StreamUrl(string itemId)
        => $"{_server}/Videos/{itemId}/stream?static=true&mediaSourceId={itemId}&api_key={_apiKey}";

    // ============================================================
    // Casting: the server as a router rather than a shelf
    // ============================================================

    /// <summary>One app signed in to this server, as far as being handed something is concerned.</summary>
    private readonly record struct Client(string SessionId, string Name, string App, string User, bool Controllable)
    {
        /// <summary>How a person would name it. The device name is the one written on the television.</summary>
        public string Label => Name.Length > 0 ? Name : App.Length > 0 ? App : SessionId;
    }

    /// <summary>
    /// Everything signed in right now. <c>SupportsRemoteControl</c> is carried rather than filtered out
    /// here so that <see cref="PlayOnAsync"/> can tell "there is no such client" from "that client won't
    /// take instructions", which are different problems and read identically as an empty list.
    /// </summary>
    async Task<IReadOnlyList<Client>> ClientsAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"{_server}/Sessions?api_key={_apiKey}", ct);
        resp.EnsureSuccessStatusCode();
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

        var found = new List<Client>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            string Str(string p) => el.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var id = Str("Id");
            if (id.Length == 0) continue;

            // This driver's own WebSocket is a session too, and it has no screen. A session that can't be
            // remote-controlled is never a destination, so ours drops out of the list by the same rule
            // that keeps a second hub out of it — no special case for our own name.
            found.Add(new Client(id, Str("DeviceName"), Str("Client"), Str("UserName"),
                el.TryGetProperty("SupportsRemoteControl", out var rc) && rc.ValueKind == JsonValueKind.True));
        }
        return found;
    }

    async Task<CommandResult> SessionsAsync(CancellationToken ct)
    {
        var clients = await ClientsAsync(ct);
        var controllable = clients.Where(c => c.Controllable).ToList();
        return CommandResult.Success(new Dictionary<string, string>
        {
            ["clients"] = string.Join(", ", controllable.Select(c => c.Label)),
            ["count"] = controllable.Count.ToString(),
            ["ignored"] = string.Join(", ", clients.Where(c => !c.Controllable).Select(c => c.Label)),
        });
    }

    /// <summary>
    /// Put something on a client's screen. <b>Two calls, and the first is what makes the second possible.</b>
    /// <para>
    /// Jellyfin addresses a target by <i>session</i> id, which is minted when an app signs in and gone when
    /// it signs out — so it can't be configuration. What can is the name on the television, which is why
    /// <c>castTo</c> holds a name and this resolves it against the live session list every time. A cached
    /// id would work all afternoon and then quietly play a film into a session that ended.
    /// </para>
    /// <para>
    /// The resolve is also the only place a client's refusal is visible. A session reporting
    /// <c>SupportsRemoteControl: false</c> answers <c>POST /Sessions/{id}/Playing</c> with 204 and does
    /// nothing — success, by every measure the HTTP layer has. That is precisely the shape of failure this
    /// codebase keeps being bitten by, so it is turned into an error here, before the POST, by name.
    /// </para>
    /// </summary>
    async Task<CommandResult> PlayOnAsync(string item, string client, string position, CancellationToken ct)
    {
        if (item.Length == 0) return CommandResult.Fail("Nothing to play was given.");

        var wanted = client.Length > 0 ? client : _castTo;
        if (wanted.Length == 0)
            return CommandResult.Fail("No client to play on — set “Play on” on this server, or name one in the command.");

        if (ItemOf(item) is not { Length: > 0 } itemId)
            return CommandResult.Fail(
                $"“{item}” isn't something this server can start. A Jellyfin client opens items out of its own "
                + $"library, so this needs an item id or a stream URL from {_server} — not an address elsewhere.");

        var clients = await ClientsAsync(ct);

        var match = clients.FirstOrDefault(c =>
            c.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)
            || c.App.Equals(wanted, StringComparison.OrdinalIgnoreCase)
            || c.SessionId.Equals(wanted, StringComparison.OrdinalIgnoreCase));

        if (match.SessionId is not { Length: > 0 })
            return CommandResult.Fail(clients.Count == 0
                ? $"Nothing is signed in to this server, so “{wanted}” isn't there to play on."
                : $"No client called “{wanted}”. Signed in right now: {string.Join(", ", clients.Select(c => c.Label))}.");

        if (!match.Controllable)
            return CommandResult.Fail($"“{match.Label}” is signed in but won't take instructions from the server — "
                                      + "its remote control is off, or it's an app that doesn't do it.");

        var url = $"{_server}/Sessions/{Uri.EscapeDataString(match.SessionId)}/Playing"
                  + $"?playCommand=PlayNow&itemIds={Uri.EscapeDataString(itemId)}";

        // Jellyfin counts in ticks of 100ns. Sent only when asked for, so a resume isn't invented for a
        // film someone wanted from the beginning.
        if (long.TryParse(position, out var seconds) && seconds > 0)
            url += $"&startPositionTicks={seconds * 10_000_000L}";

        using var resp = await _http.PostAsync($"{url}&api_key={_apiKey}", null, ct);
        if (!resp.IsSuccessStatusCode)
            return CommandResult.Fail($"{match.Label} refused it — the server answered {(int)resp.StatusCode}.");

        return CommandResult.Success(new Dictionary<string, string>
        {
            ["client"] = match.Label,
            ["sessionId"] = match.SessionId,
            ["itemId"] = itemId,
        });
    }

    /// <summary>
    /// The item id in whatever was handed over — a bare id, or one of this server's own stream URLs.
    /// <para>
    /// Recovering an id from a URL looks like a trick and isn't: the hub's play path carries exactly one
    /// value, the stream address, because that is what every other destination needs. A Jellyfin client
    /// needs the opposite — the library id, so the server can decide how to serve it to that particular
    /// screen. The address is where the id is; this reads it back out. Kodi already does the same thing
    /// in reverse to work out which item it's playing.
    /// </para>
    /// <para>
    /// A URL pointing anywhere but this server is refused rather than reached into. The regex would match
    /// a second Jellyfin's address happily, and the failure — a session told to open an id its own library
    /// has never heard of — would be a client sitting there doing nothing.
    /// </para>
    /// </summary>
    internal string? ItemOf(string item)
    {
        if (!item.Contains('/')) return item;               // already an id
        if (!Uri.TryCreate(item, UriKind.Absolute, out var given)) return null;
        if (!Uri.TryCreate(_server, UriKind.Absolute, out var mine)) return null;
        if (!given.Authority.Equals(mine.Authority, StringComparison.OrdinalIgnoreCase)) return null;

        return JellyfinDriver.ItemIdInUrl().Match(given.AbsolutePath) is { Success: true } m ? m.Groups[1].Value : null;
    }

    // ============================================================
    // Navigation projection (docs/navigation-spec.md)
    // ============================================================

    const string ItemFields = "Overview,ProductionYear,PremiereDate,RunTimeTicks,Genres,CommunityRating,OfficialRating,ChildCount,IndexNumber,ParentIndexNumber,Status,MediaType,UserData,MediaSources,MediaStreams,Path,People,Studios,Taglines,OriginalTitle,AlbumArtist,Artists";

    public async Task<NodeListing> BrowseAsync(string? nodeId, BrowseOptions options, CancellationToken ct)
    {
        var uid = await UserIdAsync(ct);
        if (string.IsNullOrEmpty(nodeId))
        {
            // Root = the user's Views (libraries).
            var views = await GetItemsAsync($"{_server}/Users/{uid}/Views?api_key={_apiKey}", ct);
            return new NodeListing { Items = views, Total = views.Count, Shape = PageShape(views) };
        }

        var node = await GetNodeAsync(nodeId, ct);

        // A show opens on its episodes, headed by season, rather than on a row of season folders. The
        // seasons are still there — as the headings — so nothing is lost, and the episode you came for is
        // one tap closer.
        if (node?.Kind == "series") return await EpisodesAsync(nodeId, uid, node, options, ct);

        var url = $"{_server}/Users/{uid}/Items?ParentId={nodeId}&SortBy=IsFolder,SortName&Fields={ItemFields}" +
                  $"&StartIndex={options.Offset}&Limit={options.Limit}&api_key={_apiKey}";

        var (items, total) = await GetItemsPagedAsync(url, ct);
        return new NodeListing { Node = node, Items = items, Total = total, Shape = PageShape(items) };
    }

    /// <summary>
    /// Every episode of a show, in season-then-episode order.
    /// <para>
    /// Ordered here rather than by the server, because the server doesn't: this endpoint returned 30 Rock as
    /// season 3, then 5, then 1, and ignored <c>SortBy</c> when asked otherwise. Since a heading appears
    /// where its first episode does, that order is what someone would have read down the page.
    /// </para>
    /// <para>
    /// Which is also why the whole show is fetched before paging: sorting one page of an unordered feed
    /// puts each page in order and the pages in none, so season 3 would still open the show.
    /// </para>
    /// </summary>
    async Task<NodeListing> EpisodesAsync(
        string seriesId, string? uid, LibraryNode node, BrowseOptions options, CancellationToken ct)
    {
        var all = await GetItemsAsync(
            $"{_server}/Shows/{seriesId}/Episodes?userId={uid}&Fields={ItemFields}&api_key={_apiKey}", ct);

        var ordered = all
            .OrderBy(e => Num(e, MediaFacts.SeasonNumber))
            .ThenBy(e => Num(e, MediaFacts.EpisodeNumber))
            .ToList();

        var page = ordered.Skip(options.Offset).Take(options.Limit > 0 ? options.Limit : ordered.Count).ToList();
        return new NodeListing { Node = node, Items = page, Total = ordered.Count, Shape = PageShape(page) };
    }

    /// <summary>A numbered fact, or last in its bracket when the item hasn't got one.</summary>
    static int Num(LibraryNode n, string key) =>
        n.Metadata.TryGetValue(key, out var v) && int.TryParse(v, out var i) ? i : int.MaxValue;

    public async Task<LibraryNode?> GetNodeAsync(string nodeId, CancellationToken ct)
    {
        var uid = await UserIdAsync(ct);
        using var resp = await _http.GetAsync($"{_server}/Users/{uid}/Items/{nodeId}?Fields={ItemFields}&api_key={_apiKey}", ct);
        if (!resp.IsSuccessStatusCode) return null;
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
        return MapItem(doc.RootElement);
    }

    /// <summary>
    /// Named for the rpc it answers (<c>SearchNodes</c>) rather than for the word "search", which this class
    /// also uses for its own <c>search</c> command — the two used to be overloads of one name.
    /// </summary>
    public async Task<NodeListing> SearchNodesAsync(string query, BrowseOptions options, CancellationToken ct)
    {
        var uid = await UserIdAsync(ct);
        var url = $"{_server}/Users/{uid}/Items?searchTerm={Uri.EscapeDataString(query)}&Recursive=true" +
                  $"&IncludeItemTypes=Movie,Series,Episode,MusicAlbum,Audio&Fields={ItemFields}&Limit={options.Limit}&api_key={_apiKey}";
        var (items, total) = await GetItemsPagedAsync(url, ct);
        return new NodeListing { Items = items, Total = total, Shape = PageShape(items) };
    }

    public async Task<CommandResult> InvokeItemAsync(string nodeId, string commandId, IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        var node = await GetNodeAsync(nodeId, ct);
        if (node is null) return CommandResult.Fail($"Unknown item '{nodeId}'.");
        var stream = StreamUrl(nodeId);
        var pos = node.Metadata.GetValueOrDefault("positionSeconds", "0");

        var data = new Dictionary<string, string>
        {
            ["nodeId"] = nodeId,
            ["title"] = node.Title,
            ["streamUrl"] = stream,
            ["mediaType"] = node.Metadata.GetValueOrDefault("mediaType", "Video"),
            ["positionSeconds"] = commandId == NavItemCommand.Resume ? pos : "0"
        };

        // "resolve" just hands back the media info (for a parameterized activity to consume); play/queue
        // additionally emit an event so a rule can route playback — policy stays outside the driver.
        //
        // This driver has always been right about that, and was the only one: the rule lived in this comment
        // rather than anywhere a second author could read it. It now goes through NavItemCommand, so the
        // reference implementation and the contract are the same line of code.
        if (!NavItemCommand.IsQuery(commandId))
            Emit(commandId == NavItemCommand.Queue ? "library.queue" : "library.play", data);
        return CommandResult.Success(data);
    }

    // ---- helpers ----
    async Task<string?> UserIdAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_userId)) return _userId;
        if (_resolvedUserId is not null) return _resolvedUserId;
        try
        {
            using var resp = await _http.GetAsync($"{_server}/Users?api_key={_apiKey}", ct);
            resp.EnsureSuccessStatusCode();
            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                _resolvedUserId = doc.RootElement[0].GetProperty("Id").GetString();
        }
        catch { /* fall through */ }
        return _resolvedUserId;
    }

    async Task<IReadOnlyList<LibraryNode>> GetItemsAsync(string url, CancellationToken ct)
        => (await GetItemsPagedAsync(url, ct)).Items;

    async Task<(IReadOnlyList<LibraryNode> Items, int Total)> GetItemsPagedAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
        var root = doc.RootElement;
        var arr = root.ValueKind == JsonValueKind.Array ? root
                : root.TryGetProperty("Items", out var its) ? its : default;
        var list = new List<LibraryNode>();
        if (arr.ValueKind == JsonValueKind.Array)
            foreach (var el in arr.EnumerateArray()) list.Add(MapItem(el));
        var total = root.TryGetProperty("TotalRecordCount", out var t) ? t.GetInt32() : list.Count;
        return (list, total);
    }

    /// <summary>
    /// The shape the page as a whole wants, so a grid of films sizes its columns for posters and a season
    /// of episodes sizes them for stills. Whatever most items are wins; the odd one out keeps its own.
    /// </summary>
    static string PageShape(IReadOnlyList<LibraryNode> items)
        => items.Count == 0 ? "" : items.GroupBy(i => i.Shape).OrderByDescending(g => g.Count()).First().Key;

    /// <summary>
    /// Jellyfin returns cast and crew in one <c>People</c> array tagged by role. Split into the three the
    /// vocabulary names, in billing order, and capped — this travels on every item of every listing, and
    /// nobody searches for the fortieth name in the credits.
    /// </summary>
    static void AddPeople(JsonElement el, Dictionary<string, string> meta)
    {
        if (!el.TryGetProperty("People", out var people) || people.ValueKind != JsonValueKind.Array) return;

        List<string> cast = [], directors = [], writers = [];

        foreach (var person in people.EnumerateArray())
        {
            if (!person.TryGetProperty("Name", out var n) || n.GetString() is not { Length: > 0 } who) continue;

            switch (person.TryGetProperty("Type", out var t) ? t.GetString() : null)
            {
                case "Director": directors.Add(who); break;
                case "Writer": writers.Add(who); break;
                case "Actor" or null when cast.Count < 12: cast.Add(who); break;
            }
        }

        if (cast.Count > 0) meta[MediaFacts.Cast] = string.Join(", ", cast);
        if (directors.Count > 0) meta[MediaFacts.Directors] = string.Join(", ", directors);
        if (writers.Count > 0) meta[MediaFacts.Writers] = string.Join(", ", writers);
    }

    LibraryNode MapItem(JsonElement el)
    {
        string? Str(string p) => el.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? Int(string p) => el.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        bool Bool(string p) => el.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;

        var id = Str("Id") ?? "";
        var type = Str("Type") ?? Str("CollectionType") ?? "Item";
        var mediaType = Str("MediaType");
        var isFolder = Bool("IsFolder");
        var kind = Kind(type, Str("CollectionType"));
        var playable = mediaType is "Video" or "Audio" || type is "Movie" or "Episode" or "Audio" or "MusicVideo" or "Trailer";

        var meta = new Dictionary<string, string> { ["jellyfin:type"] = type };
        if (mediaType is not null) meta["mediaType"] = mediaType;
        if (Int("ProductionYear") is { } yr) meta["year"] = yr.ToString();
        if (el.TryGetProperty("RunTimeTicks", out var rt) && rt.ValueKind == JsonValueKind.Number) meta["runtimeSeconds"] = (rt.GetInt64() / 10_000_000).ToString();
        // Jellyfin's IndexNumber means different things by type: an episode's number within its season, a
        // season's number within its show. Emitting both under one key is what made "season 3 episode 5"
        // unaskable, so they're separated into MediaFacts here.
        if (Int("IndexNumber") is { } ix)
        {
            if (type == "Season") meta[MediaFacts.SeasonNumber] = ix.ToString();
            else meta[MediaFacts.EpisodeNumber] = ix.ToString();
        }
        if (Int("ParentIndexNumber") is { } pix && type != "Season") meta[MediaFacts.SeasonNumber] = pix.ToString();

        // Carried on the episode so a search hit is self-describing — "Episode 5" alone tells nobody
        // anything, and the parent series may never have been fetched.
        if (Str("SeriesName") is { } series) meta[MediaFacts.SeriesTitle] = series;
        if (type == "Episode" && Str("Name") is { } epTitle) meta[MediaFacts.EpisodeTitle] = epTitle;
        if (Str("PremiereDate") is { Length: >= 10 } premiere)
            meta[type == "Episode" ? MediaFacts.AirDate : MediaFacts.ReleaseDate] = premiere[..10];

        if (Str("Tagline") is { } tagline) meta[MediaFacts.Tagline] = tagline;
        if (Str("OriginalTitle") is { } original) meta[MediaFacts.OriginalTitle] = original;
        if (el.TryGetProperty("Studios", out var studios) && studios.ValueKind == JsonValueKind.Array
            && studios.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } studio
            && studio.TryGetProperty("Name", out var studioName))
            meta[MediaFacts.Studio] = studioName.GetString() ?? "";

        AddPeople(el, meta);
        if (Int("ChildCount") is { } cc) meta["childCount"] = cc.ToString();
        if (Str("OfficialRating") is { } ofr) meta["officialRating"] = ofr;
        if (Str("Status") is { } st) meta[MediaFacts.SeriesStatus] = st;
        if (el.TryGetProperty("CommunityRating", out var cr) && cr.ValueKind == JsonValueKind.Number) meta["communityRating"] = cr.GetDouble().ToString("0.0");
        if (el.TryGetProperty("Genres", out var g) && g.ValueKind == JsonValueKind.Array) meta["genres"] = string.Join(", ", g.EnumerateArray().Select(x => x.GetString()));
        if (el.TryGetProperty("UserData", out var ud) && ud.ValueKind == JsonValueKind.Object)
        {
            if (ud.TryGetProperty("Played", out var pl)) meta["played"] = pl.ValueKind == JsonValueKind.True ? "true" : "false";
            if (ud.TryGetProperty("PlaybackPositionTicks", out var pp) && pp.ValueKind == JsonValueKind.Number && pp.GetInt64() > 0)
                meta["positionSeconds"] = (pp.GetInt64() / 10_000_000).ToString();
            if (ud.TryGetProperty("PlayedPercentage", out var pc) && pc.ValueKind == JsonValueKind.Number) meta["playedPercent"] = pc.GetDouble().ToString("0");
        }

        if (el.TryGetProperty("Artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
            meta[MediaFacts.Artists] = string.Join(", ", artists.EnumerateArray().Select(x => x.GetString()));
        if (Str("AlbumArtist") is { } albumArtist) meta[MediaFacts.AlbumArtist] = albumArtist;
        if (Str("Album") is { } album) meta[MediaFacts.Album] = album;
        if (type == "Audio" && Int("IndexNumber") is { } track) meta[MediaFacts.TrackNumber] = track.ToString();
        if (Int("ParentIndexNumber") is { } disc && type == "Audio") meta[MediaFacts.DiscNumber] = disc.ToString();

        // What this particular copy is, as opposed to what the film is. Without it there's no way to tell
        // a 4K remux from a phone rip of the same title, which is the whole of "play the best one here".
        AddStreamInfo(el, meta);

        // A library's own art is a wide banner and a collection's is usually a still, so showing either at
        // 2:3 letterboxed a landscape image into a portrait frame. Each node says what it wants to be, and
        // its images are ordered so the one matching that shape comes first.
        var shape = JellyfinDriver.ShapeFor(kind, type);
        var hasTag = (string name) => el.TryGetProperty("ImageTags", out var t)
            && t.ValueKind == JsonValueKind.Object && t.TryGetProperty(name, out _);
        var backdrops = el.TryGetProperty("BackdropImageTags", out var bd)
            && bd.ValueKind == JsonValueKind.Array && bd.GetArrayLength() > 0;

        var images = new List<ImageRef>();
        void Add(string imageKind, string jellyfinImage, string aspect)
            => images.Add(new ImageRef(imageKind, $"{_server}/Items/{id}/Images/{jellyfinImage}?{JellyfinDriver.Fit(aspect)}&quality=90&api_key={_apiKey}", Aspect: aspect));

        if (shape is NodeShape.Wide)
        {
            // Thumb is purpose-made 16:9; a backdrop is the next best real landscape art. Primary is the
            // fallback, and for a library or an episode it already is landscape.
            if (hasTag("Thumb")) Add("thumb", "Thumb", NodeShape.Wide);
            if (backdrops) images.Add(new ImageRef("backdrop", $"{_server}/Items/{id}/Images/Backdrop/0?maxWidth=1280&api_key={_apiKey}", Aspect: NodeShape.Wide));
            if (hasTag("Primary")) Add(images.Count == 0 ? "thumb" : "poster", "Primary", images.Count == 0 ? NodeShape.Wide : NodeShape.Poster);
        }
        else
        {
            if (hasTag("Primary")) Add(shape is NodeShape.Square ? "thumb" : "poster", "Primary", shape);
            if (backdrops) images.Add(new ImageRef("backdrop", $"{_server}/Items/{id}/Images/Backdrop/0?maxWidth=1280&api_key={_apiKey}", Aspect: NodeShape.Wide));
        }
        if (hasTag("Logo")) images.Add(new ImageRef("logo", $"{_server}/Items/{id}/Images/Logo?api_key={_apiKey}"));

        var commands = new List<ItemCommand>();
        if (playable)
        {
            commands.Add(new ItemCommand("play", "Play", "play"));
            if (meta.ContainsKey("positionSeconds")) commands.Add(new ItemCommand("resume", "Resume", "resume"));
            commands.Add(new ItemCommand("queue", "Queue", "queue"));
        }

        return new LibraryNode
        {
            Id = id,
            ParentId = Str("ParentId"),
            Kind = kind,
            Title = Str("Name") ?? "",
            Subtitle = Subtitle(kind, meta),
            Overview = Str("Overview"),
            IsContainer = isFolder || kind is "series" or "season" or "album" or "artist",
            IsPlayable = playable,
            ChildCount = Int("ChildCount"),
            Metadata = meta,
            Images = images,
            Commands = commands,
            Shape = shape,
            Group = Group(kind, el)
        };
    }

    /// <summary>
    /// Pull the stream details out of a Jellyfin item's first media source.
    /// <para>
    /// "First" is deliberate and worth naming: an item with several versions lists them all, and picking
    /// between them is a decision for whatever is choosing where to play — not something to bury here. The
    /// count travels so that decision knows there was one to make.
    /// </para>
    /// </summary>
    static void AddStreamInfo(JsonElement el, Dictionary<string, string> meta)
    {
        if (!el.TryGetProperty("MediaSources", out var sources)
            || sources.ValueKind != JsonValueKind.Array
            || sources.GetArrayLength() == 0) return;

        var count = sources.GetArrayLength();
        if (count > 1) meta[MediaMeta.VersionCount] = count.ToString();

        var source = sources[0];

        if (Text(source, "Container") is { } container) meta[MediaMeta.Container] = container;
        if (Number(source, "Size") is { } size) meta[MediaMeta.FileSizeBytes] = ((long)size).ToString();
        if (Number(source, "Bitrate") is { } bitrate) meta[MediaMeta.BitrateKbps] = ((long)(bitrate / 1000)).ToString();

        // Jellyfin puts the edition in the source name when there's more than one version.
        if (count > 1 && Text(source, "Name") is { } name) meta[MediaMeta.Edition] = name;

        if (!source.TryGetProperty("MediaStreams", out var streams) || streams.ValueKind != JsonValueKind.Array) return;

        var subtitles = new List<string>();

        foreach (var stream in streams.EnumerateArray())
        {
            switch (Text(stream, "Type"))
            {
                case "Video" when !meta.ContainsKey(MediaMeta.VideoCodec):
                    if (Text(stream, "Codec") is { } vcodec) meta[MediaMeta.VideoCodec] = vcodec.ToUpperInvariant();
                    if (Number(stream, "Height") is { } h)
                    {
                        meta[MediaMeta.VideoHeight] = ((int)h).ToString();
                        if (Number(stream, "Width") is { } w) meta[MediaMeta.VideoResolution] = $"{(int)w}x{(int)h}";
                    }
                    if (Number(stream, "AverageFrameRate" ) is { } fps) meta[MediaMeta.VideoFrameRate] = fps.ToString("0.###");
                    // VideoRange is "SDR" or "HDR"; VideoRangeType is the specific flavour and is the
                    // useful one — a display that does HDR10 may still not do Dolby Vision.
                    if (Text(stream, "VideoRangeType") is { } exact && !exact.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                        meta[MediaMeta.VideoRange] = exact;
                    else if (Text(stream, "VideoRange") is { } range)
                        meta[MediaMeta.VideoRange] = range;
                    break;

                case "Audio" when !meta.ContainsKey(MediaMeta.AudioCodec):
                    if (Text(stream, "Codec") is { } acodec) meta[MediaMeta.AudioCodec] = acodec.ToUpperInvariant();
                    if (Number(stream, "Channels") is { } ch) meta[MediaMeta.AudioChannels] = Layout((int)ch);
                    if (Text(stream, "Language") is { } lang) meta[MediaMeta.AudioLanguage] = lang;
                    // The object layer rides on the codec and isn't named separately, so it comes out of
                    // the profile or the display title: "TrueHD Atmos 7.1".
                    var title = (Text(stream, "Profile") ?? "") + " " + (Text(stream, "DisplayTitle") ?? "");
                    if (title.Contains("Atmos", StringComparison.OrdinalIgnoreCase)) meta[MediaMeta.AudioProfile] = "Atmos";
                    else if (title.Contains("DTS:X", StringComparison.OrdinalIgnoreCase)
                          || title.Contains("DTS-X", StringComparison.OrdinalIgnoreCase)) meta[MediaMeta.AudioProfile] = "DTS:X";
                    break;

                case "Subtitle":
                    if (Text(stream, "Language") is { Length: > 0 } sub && !subtitles.Contains(sub)) subtitles.Add(sub);
                    break;
            }
        }

        if (subtitles.Count > 0) meta[MediaMeta.Subtitles] = string.Join(", ", subtitles);
    }

    /// <summary>Channel count as the layout people recognise — 8 channels is "7.1", not "8".</summary>
    static string Layout(int channels) => channels switch
    {
        1 => "1.0", 2 => "2.0", 3 => "2.1", 6 => "5.1", 7 => "6.1", 8 => "7.1", 10 => "9.1",
        _ => channels.ToString(),
    };

    static string? Text(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    static double? Number(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    /// <summary>
    /// The heading this item sits under. Named for every item that has an obvious answer, not only where a
    /// listing happens to be mixed: the hub drops a heading that would stand alone, so labelling costs
    /// nothing and a library that turns out to hold two kinds is grouped without the driver noticing.
    /// </summary>
    static string Group(string kind, JsonElement el) => kind switch
    {
        "collection" => "Collections",
        "movie" => "Movies",
        "series" => "Shows",
        "album" => "Albums",
        "artist" => "Artists",
        "episode" => Season(el),
        _ => ""
    };

    /// <summary>
    /// Which season an episode belongs to, in the words Jellyfin uses — which is where "Specials" comes
    /// from, and why this isn't built out of the number. Falling back to the number is for the odd item
    /// that arrives without the name.
    /// </summary>
    static string Season(JsonElement el)
    {
        if (el.TryGetProperty("SeasonName", out var name) && name.ValueKind == JsonValueKind.String
            && name.GetString() is { Length: > 0 } given)
            return given;

        if (el.TryGetProperty("ParentIndexNumber", out var ix) && ix.ValueKind == JsonValueKind.Number)
            return ix.GetInt32() == 0 ? "Specials" : $"Season {ix.GetInt32()}";

        return "";
    }

    static string Kind(string type, string? collectionType) => type switch
    {
        "CollectionFolder" or "UserView" => "library",
        "BoxSet" => "collection",
        "Folder" => "folder",
        "Series" => "series",
        "Season" => "season",
        "Episode" => "episode",
        "Movie" => "movie",
        "MusicAlbum" => "album",
        "MusicArtist" => "artist",
        "Audio" => "track",
        "Playlist" => "playlist",
        "Person" => "person",
        "Genre" or "MusicGenre" => "genre",
        "Photo" => "photo",
        _ => collectionType ?? type.ToLowerInvariant()
    };

    static string? Subtitle(string kind, IReadOnlyDictionary<string, string> m) => kind switch
    {
        "movie" => Join(m.GetValueOrDefault("year"), Runtime(m)),
        "episode" => MediaFacts.EpisodeCode(m)?.Replace("E", "·E") ?? Runtime(m),
        "season" => m.TryGetValue(MediaFacts.SeasonNumber, out var s) ? $"Season {s}" : (m.TryGetValue("childCount", out var c) ? $"{c} episodes" : null),
        // MediaFacts.SeriesStatus, which is what MapItem writes. This read "status" and so silently found
        // nothing: every series subtitle was its year alone, never "2022 · Ended".
        "series" => Join(m.GetValueOrDefault("year"), m.GetValueOrDefault(MediaFacts.SeriesStatus)),
        _ => m.TryGetValue("childCount", out var c) ? $"{c} items" : null
    };
    static string? Runtime(IReadOnlyDictionary<string, string> m) => m.TryGetValue("runtimeSeconds", out var r) && int.TryParse(r, out var s) && s > 0 ? $"{s / 60}m" : null;
    static string? Join(string? a, string? b) => string.Join(" · ", new[] { a, b }.Where(x => !string.IsNullOrEmpty(x)));

    public override async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await _poll.DisposeAsync();
        await _socket.DisposeAsync();
        _cts.Dispose();
        await base.DisposeAsync();
    }
}
