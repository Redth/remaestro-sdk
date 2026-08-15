using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

namespace Remaestro.Sdk;

/// <summary>
/// Tracing for the transports that aren't a <see cref="LineDevice"/>, a <see cref="ByteLink"/> or an
/// <see cref="HttpClient"/>.
/// <para>
/// Those three cover most drivers and get diagnostics for free. The rest do not: a WebOS or Samsung TV is
/// a WebSocket, an Xbox and a PlayStation are UDP, and each of them opens the socket itself. Which meant
/// that for a meaningful share of the devices people own, "turn the trace on" produced an empty list —
/// not because nothing happened, but because nothing was watching.
/// </para>
/// <para>
/// Deliberately helpers rather than a wrapper type. Each of those drivers has its own framing, its own
/// keepalive, its own idea of what a message is, and a wrapper would have to be general enough to be
/// wrong somewhere. A one-line call at the point the driver already knows what it sent is not.
/// </para>
/// </summary>
public static class DiagSockets
{
    public const string WebSocket = "ws";
    public const string Udp = "udp";

    /// <summary>Send text on a WebSocket and record it. The trace happens after the send, so a failed
    /// send shows as the exception it is rather than as a message that appears to have gone out.</summary>
    public static async Task SendTracedAsync(this WebSocket socket, string deviceId, string endpoint,
        string text, CancellationToken ct = default)
    {
        await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);
        Diag.Tx(deviceId, WebSocket, text, endpoint);
    }

    /// <summary>One message off a WebSocket, once the driver has reassembled it.</summary>
    public static void WsReceived(string deviceId, string endpoint, string text) =>
        Diag.Rx(deviceId, WebSocket, text, endpoint);

    public static void WsOpened(string deviceId, string endpoint) =>
        Diag.Open(deviceId, WebSocket, endpoint);

    public static void WsClosed(string deviceId, string endpoint, string why = "") =>
        Diag.Emit(deviceId, WebSocket, "close", why, endpoint: endpoint);

    public static void WsFailed(string deviceId, string endpoint, string message) =>
        Diag.Error(deviceId, WebSocket, message, endpoint);

    /// <summary>
    /// A datagram, with its bytes. UDP gear is overwhelmingly binary — an Xbox power packet is a header and
    /// a length, not a sentence — so the hex is the part worth having and the text is a label for it.
    /// </summary>
    public static async Task<int> SendTracedAsync(this UdpClient client, string deviceId, string endpoint,
        byte[] datagram, string label = "", CancellationToken ct = default)
    {
        var sent = await client.SendAsync(datagram, ct);
        Diag.TxBytes(deviceId, Udp, datagram, label, endpoint);
        return sent;
    }

    /// <summary>A datagram that went out, for a driver that sends it without the extension above.</summary>
    public static void UdpSent(string deviceId, string endpoint, ReadOnlySpan<byte> datagram,
        string label = "") => Diag.TxBytes(deviceId, Udp, datagram, label, endpoint);

    public static void UdpReceived(string deviceId, string endpoint, ReadOnlySpan<byte> datagram,
        string label = "") => Diag.RxBytes(deviceId, Udp, datagram, label, endpoint);

    public static void UdpFailed(string deviceId, string endpoint, string message) =>
        Diag.Error(deviceId, Udp, message, endpoint);
}
