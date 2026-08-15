using System.Diagnostics;

namespace Remaestro.Sdk;

/// <summary>
/// Records a driver's HTTP conversation into <see cref="Diag"/> — the request line and how the device
/// answered — when the hub has turned diagnostics on for the device. Add it to an <see cref="HttpClient"/>'s
/// handler chain and every call it carries is traced, with the same redaction and off-by-default cost as the
/// serial transports.
/// <para>
/// The device id is resolved per request so one shared client can serve several devices: set it on the
/// request with <see cref="Tag"/>, or give the handler a fixed id for the common one-client-per-device case.
/// </para>
/// </summary>
public sealed class DiagnosticsHandler(Func<HttpRequestMessage, string?> deviceId) : DelegatingHandler
{
    static readonly HttpRequestOptionsKey<string> Key = new("remaestro.deviceId");

    /// <summary>One client per device — the usual shape. Every call is attributed to <paramref name="id"/>.</summary>
    public DiagnosticsHandler(string id) : this(_ => id) { }

    /// <summary>Attribute one request to a device, for a client shared across several of them.</summary>
    public static HttpRequestMessage Tag(HttpRequestMessage request, string deviceId)
    {
        request.Options.Set(Key, deviceId);
        return request;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var id = request.Options.TryGetValue(Key, out var tagged) ? tagged : deviceId(request);
        if (id is not { Length: > 0 } || !Diag.Enabled(id))
            return await base.SendAsync(request, ct);

        var where = request.RequestUri is { IsAbsoluteUri: true } uri ? $"{uri.Scheme}://{uri.Authority}" : "";

        Diag.Emit(id, "http", "tx", $"{request.Method} {request.RequestUri}",
            await RequestBodyAsync(request, ct), where);

        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = await base.SendAsync(request, ct);
            var ms = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Diag.Emit(id, "http", "rx", $"{(int)response.StatusCode} {response.ReasonPhrase} · {ms} ms",
                await SnippetAsync(response, ct), where);
            return response;
        }
        catch (Exception ex)
        {
            Diag.Error(id, "http", $"{request.Method} {request.RequestUri} — {ex.Message}", where);
            throw;
        }
    }

    /// <summary>
    /// What we sent it, when that's text. Half of "why did the device reject this" lives in the request body
    /// — a SOAP envelope, a JSON command — and a trace showing only the method and URL cannot answer it.
    /// </summary>
    static async Task<string> RequestBodyAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Content is not { } content) return "";

        var type = content.Headers.ContentType?.MediaType ?? "";
        if (!Textual(type)) return type.Length > 0 ? $"[{type}]" : "";

        try
        {
            // Buffered, so reading it here doesn't consume a stream the handler chain still has to send.
            await content.LoadIntoBufferAsync(64 * 1024);
            var body = await content.ReadAsStringAsync(ct);
            return body.Length > 600 ? body[..600] + "…" : body;
        }
        catch { return ""; }
    }

    static bool Textual(string type) =>
        type.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || type.Contains("json", StringComparison.OrdinalIgnoreCase)
        || type.Contains("xml", StringComparison.OrdinalIgnoreCase)
        || type.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);

    // A short, safe look at the body for the trace — enough to see a device's error text without buffering a
    // media stream. Only text-ish content, only the head of it, and never at the cost of the real response.
    static async Task<string> SnippetAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!Textual(type)) return type.Length > 0 ? $"[{type}]" : "";

        try
        {
            // Buffer first — bounded — so reading the body for the trace doesn't drain a stream the driver
            // still has to read. A body past the cap is left alone rather than risk holding a large one twice.
            await response.Content.LoadIntoBufferAsync(256 * 1024);
            var body = await response.Content.ReadAsStringAsync(ct);
            return body.Length > 600 ? body[..600] + "…" : body;
        }
        catch { return ""; }
    }
}
