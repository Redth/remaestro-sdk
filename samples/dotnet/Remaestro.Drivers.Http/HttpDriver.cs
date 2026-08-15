using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Remaestro.Sdk;

namespace Remaestro.Drivers.Http;

/// <summary>
/// A generic HTTP device type. Each device is an endpoint (optionally with a base URL); its one
/// primitive command, <c>request</c>, sends a fully-specified HTTP request. Named, tokenised
/// "webhooks" are layered on top by the hub — this driver just executes concrete requests.
/// </summary>
public sealed class HttpDriver : IRemaestroDriver
{
    // One client for every device of this type — so each request says which device it's for, and the
    // diagnostics handler attributes the trace correctly when capture is on for one of them.
    private static readonly HttpClient Http =
        new(new DiagnosticsHandler(_ => null) { InnerHandler = new HttpClientHandler() }) { Timeout = TimeSpan.FromSeconds(20) };

    public string TypeId => "http";
    public string DisplayName => "HTTP / Webhook";
    public string Description => "Sends arbitrary HTTP(S) requests — for IoT gadgets, AV gear web APIs, and webhooks.";

    public IReadOnlyList<ConfigField> ConfigSchema { get; } =
    [
        new("baseUrl", "Base URL", Help: "Optional; prepended to relative request URLs, e.g. http://192.168.1.50")
    ];

    public IReadOnlyList<CommandInfo> Commands { get; } =
    [
        new("request", "HTTP Request", "Send an HTTP request.",
        [
            new("method", "Method", Default: "POST"),
            new("url", "URL", Required: true, Help: "Absolute, or relative to the device Base URL"),
            new("headers", "Headers", Type: "multiline", Help: "One per line: Name: Value"),
            new("contentType", "Content-Type", Default: "application/json"),
            new("body", "Body", Type: "multiline")
        ])
    ];

    public IReadOnlyList<EventSchema> Events { get; } =
    [
        new("request.sent", "An HTTP request completed (any status).",
        [
            new("method"), new("url"),
            new("status", "number", "HTTP status code"),
            new("ok", "bool", "True for a 2xx response")
        ]),
        new("request.failed", "An HTTP request threw before completing.",
        [
            new("method"), new("url"), new("error", "string", "Exception message")
        ])
    ];

    public IReadOnlyList<StateField> StateSchema { get; } =
    [
        // Always true, and declared rather than left out because the device does write it. A webhook holds
        // no connection and has no one endpoint to probe — every request names its own URL, and whether
        // that URL answered is `lastStatus`. See HttpWebhookDevice's constructor.
        new("online", "bool", "Always true — a webhook is the hub's own HTTP client, not a box on the network"),
        new("baseUrl", "string", "Configured base URL"),
        new("lastStatus", "number", "Status of the most recent request"),
        new("lastUrl", "string", "URL of the most recent request")
    ];

    public Task<IRemaestroDevice> CreateDeviceAsync(string deviceId, string name, IReadOnlyDictionary<string, string> config, CancellationToken ct)
    {
        config.TryGetValue("baseUrl", out var baseUrl);
        return Task.FromResult<IRemaestroDevice>(new HttpWebhookDevice(deviceId, name, baseUrl, Http, Commands));
    }
}

internal sealed class HttpWebhookDevice : DeviceBase
{
    private readonly string? _baseUrl;
    private readonly HttpClient _http;

    public HttpWebhookDevice(string deviceId, string name, string? baseUrl, HttpClient http, IReadOnlyList<CommandInfo> commands)
        : base(deviceId, name)
    {
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl!.TrimEnd('/');
        _http = http;
        Commands = commands;

        // Nothing here can fail to answer. This device holds no connection open and has no single endpoint
        // to probe — `request` names its own URL every time, and whether *that* answered is `lastStatus`,
        // per request rather than per device. So reachability is the hub's own, and it is true whenever
        // anything is here to ask. Said out loud because DeviceBase.Online is false until a device states
        // otherwise: a missing key means "hasn't answered yet", which for a webhook would never clear.
        SetState("online", "true");
        SetState("baseUrl", _baseUrl ?? "");
    }

    public override IReadOnlyList<CommandInfo> Commands { get; }

    /// <summary>
    /// Nothing, and said out loud. <c>request</c>'s <c>url</c> is the endpoint being called, not content
    /// being handed over — this device type sends an HTTP request and has no idea what a film is.
    /// <para>
    /// Declared rather than left null because <c>url</c> is the sniff's first guess, and nothing is more
    /// certain to be sniffed than a parameter literally called that. A webhook was, until now, offered
    /// alongside the players as a place to send a film. See <see cref="MediaPlayback.Nothing"/>.
    /// </para>
    /// </summary>
    public override MediaPlayback? Playback { get; } = MediaPlayback.Nothing;

    public override async Task<CommandResult> ExecuteAsync(string commandId, IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        if (commandId != "request")
            return CommandResult.Fail($"Unknown command '{commandId}'.");

        var method = args.GetValueOrDefault("method", "POST").Trim().ToUpperInvariant();
        var url = args.GetValueOrDefault("url", "").Trim();
        if (string.IsNullOrEmpty(url)) return CommandResult.Fail("URL is required.");
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && _baseUrl is not null)
            url = _baseUrl + (url.StartsWith('/') ? "" : "/") + url;

        using var req = new HttpRequestMessage(new HttpMethod(method), url);
        DiagnosticsHandler.Tag(req, DeviceId);

        var body = args.GetValueOrDefault("body", "");
        if (!string.IsNullOrEmpty(body) && method is not "GET" and not "HEAD")
        {
            req.Content = new StringContent(body, Encoding.UTF8);
            var contentType = args.GetValueOrDefault("contentType", "application/json");
            if (!string.IsNullOrWhiteSpace(contentType))
                req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType.Split(';')[0].Trim());
        }

        foreach (var line in args.GetValueOrDefault("headers", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var hn = line[..idx].Trim();
            var hv = line[(idx + 1)..].Trim();
            if (!req.Headers.TryAddWithoutValidation(hn, hv))
                req.Content?.Headers.TryAddWithoutValidation(hn, hv);
        }

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            var status = (int)resp.StatusCode;
            var ok = resp.IsSuccessStatusCode;
            var contentType = resp.Content.Headers.ContentType?.ToString() ?? "";
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            var respBody = Encoding.UTF8.GetString(bytes);
            if (respBody.Length > 262_144) respBody = respBody[..262_144];

            var headers = new Dictionary<string, string>();
            foreach (var h in resp.Headers) headers[h.Key] = string.Join(", ", h.Value);
            foreach (var h in resp.Content.Headers) headers[h.Key] = string.Join(", ", h.Value);

            SetState("lastStatus", status.ToString());
            SetState("lastUrl", url);
            Emit("request.sent", new Dictionary<string, string>
            {
                ["method"] = method, ["url"] = url, ["status"] = status.ToString(), ["ok"] = ok.ToString()
            });

            // Full response so the hub can build events from it (headers/body/json/bytes).
            var result = new Dictionary<string, string>
            {
                ["status"] = status.ToString(),
                ["ok"] = ok ? "true" : "false",
                ["contentType"] = contentType,
                ["headers"] = JsonSerializer.Serialize(headers, HttpJson.Default.DictionaryStringString),
                ["body"] = respBody,
                ["bytesBase64"] = bytes.Length <= 65_536 ? Convert.ToBase64String(bytes) : ""
            };
            return new CommandResult(ok, ok ? null : $"HTTP {status}", result);
        }
        catch (Exception ex)
        {
            Emit("request.failed", new Dictionary<string, string> { ["method"] = method, ["url"] = url, ["error"] = ex.Message });
            return CommandResult.Fail(ex.Message);
        }
    }
}

// AOT-safe JSON: source-generated metadata so no reflection/dynamic codegen is needed at runtime.
// (Reference pattern for making a driver Native-AOT clean — see scripts/publish-drivers.sh.)
[System.Text.Json.Serialization.JsonSerializable(typeof(System.Collections.Generic.Dictionary<string, string>))]
internal partial class HttpJson : System.Text.Json.Serialization.JsonSerializerContext;
