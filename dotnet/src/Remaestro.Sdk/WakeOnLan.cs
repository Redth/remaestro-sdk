using System.Net;
using System.Net.Sockets;

namespace Remaestro.Sdk;

/// <summary>
/// Wake-on-LAN. Televisions that speak over IP generally can't be woken over IP — the socket only exists
/// once the panel is on — so power-on is a magic packet to the set's MAC instead. LG (2016+), Samsung
/// (2016+) and Sony (2013+) all work this way.
/// </summary>
public static class WakeOnLan
{
    /// <summary>Accepts the forms people actually paste: AA:BB:CC:DD:EE:FF, AA-BB-…, or bare hex.</summary>
    public static bool TryParseMac(string? mac, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(mac)) return false;

        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12) return false;

        var parsed = new byte[6];
        for (var i = 0; i < 6; i++)
            parsed[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        bytes = parsed;
        return true;
    }

    /// <summary>Six 0xFF bytes followed by the target MAC repeated sixteen times.</summary>
    public static byte[] BuildMagicPacket(byte[] mac)
    {
        if (mac.Length != 6) throw new ArgumentException("A MAC address is six bytes.", nameof(mac));
        var packet = new byte[102];
        for (var i = 0; i < 6; i++) packet[i] = 0xFF;
        for (var rep = 0; rep < 16; rep++) mac.CopyTo(packet, 6 + rep * 6);
        return packet;
    }

    /// <summary>
    /// Broadcast a magic packet. Sent to the subnet broadcast address on the usual WoL ports — a sleeping
    /// TV has no IP bound, so it can only be reached by broadcast.
    /// </summary>
    public static async Task<bool> SendAsync(string mac, CancellationToken ct = default)
    {
        if (!TryParseMac(mac, out var bytes)) return false;
        var packet = BuildMagicPacket(bytes);

        try
        {
            using var udp = new UdpClient { EnableBroadcast = true };
            foreach (var port in new[] { 9, 7 })
                await udp.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, port));
            return true;
        }
        catch { return false; }
    }
}
