using System.Buffers.Binary;
using System.Text;

namespace Remaestro.ProxyAgent;

/// <summary>
/// The board side of the hub's tunnel protocol, in the second language that speaks it.
/// <para>
/// Every constant here is mirrored from <c>src/Remaestro.Hub/Proxies/</c> rather than referenced, and a
/// drift test asserts each one still matches. See the note in the csproj for why sharing the types would
/// weaken the guarantee rather than strengthen it — the ESP32 firmware is in C++ and cannot share them, so
/// a format change has to be caught by a test that reads both sides, not by a compiler that sees one.
/// </para>
/// </summary>
public static class TunnelOp
{
    public const byte Hello = 0x01;
    public const byte Welcome = 0x02;
    public const byte Open = 0x10;
    public const byte Opened = 0x11;
    public const byte OpenFailed = 0x12;
    public const byte Close = 0x13;
    public const byte Data = 0x20;
    public const byte Event = 0x30;
    public const byte Ping = 0x40;
    public const byte Pong = 0x41;
    public const byte Update = 0x50;
    public const byte UpdateStatus = 0x51;
}

/// <summary>What a <c>usb.input</c> channel carries. Mirrored from <c>HidHostOp</c>.</summary>
public static class HidHostOp
{
    public const byte Scan = 0x01;
    public const byte Connect = 0x02;
    public const byte Forget = 0x03;

    public const byte Found = 0x81;
    public const byte Attached = 0x82;
    public const byte Detached = 0x83;
    public const byte Report = 0x84;

    /// <summary>A key from evdev: <c>[codeLow, codeHigh, value]</c>. See the hub's <c>HidHostOp.Evdev</c>.</summary>
    public const byte Evdev = 0x85;
}

/// <param name="Channel">0 is control. Anything else is a peripheral the hub opened.</param>
public readonly record struct TunnelFrame(byte Op, byte Channel, ReadOnlyMemory<byte> Payload)
{
    public const int HeaderSize = 4;
    public const int MaxPayload = 4096;

    public string Text => Encoding.UTF8.GetString(Payload.Span);

    public static TunnelFrame OfText(byte op, byte channel, string text) =>
        new(op, channel, Encoding.UTF8.GetBytes(text));
}

/// <summary>Op, channel, 16-bit little-endian length, then the body.</summary>
public static class TunnelWire
{
    /// <summary>The control channel, which is never handed out to a peripheral.</summary>
    public const byte Control = 0;

    /// <summary>The port the hub listens on and every proxy is built to dial.</summary>
    public const int Port = 8130;

    public static byte[] Encode(TunnelFrame frame)
    {
        if (frame.Payload.Length > TunnelFrame.MaxPayload)
            throw new ArgumentOutOfRangeException(nameof(frame),
                $"A frame carries at most {TunnelFrame.MaxPayload} bytes; this one has {frame.Payload.Length}.");

        var buffer = new byte[TunnelFrame.HeaderSize + frame.Payload.Length];
        buffer[0] = frame.Op;
        buffer[1] = frame.Channel;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(2), (ushort)frame.Payload.Length);
        frame.Payload.Span.CopyTo(buffer.AsSpan(TunnelFrame.HeaderSize));

        return buffer;
    }
}

/// <summary>
/// Turns a stream of arbitrary TCP chunks back into frames.
/// <para>
/// The same buffering the hub does, for the same reason and with the same tests behind it: TCP has no
/// message boundaries, so a frame arrives split across two reads or three arrive in one. Code that assumes
/// one read is one message works on a desk and fails under load, looking like corruption rather than a
/// framing bug.
/// </para>
/// </summary>
public sealed class TunnelReader
{
    readonly List<byte> _buffer = [];

    /// <summary>Set when the stream is unusable and the connection should be dropped.</summary>
    public string? Fault { get; private set; }

    public IReadOnlyList<TunnelFrame> Push(ReadOnlySpan<byte> chunk)
    {
        if (Fault is not null) return [];

        _buffer.AddRange(chunk);
        var frames = new List<TunnelFrame>();

        while (_buffer.Count >= TunnelFrame.HeaderSize)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_buffer)[2..]);

            if (length > TunnelFrame.MaxPayload)
            {
                // Nothing legitimate is this big, so the stream is out of step. Resynchronising would mean
                // guessing where the next frame starts; dropping the connection is honest and we redial in
                // seconds.
                Fault = $"A frame claimed {length} bytes, over the {TunnelFrame.MaxPayload} limit.";
                _buffer.Clear();
                return frames;
            }

            var total = TunnelFrame.HeaderSize + length;
            if (_buffer.Count < total) break;

            var payload = new byte[length];
            _buffer.CopyTo(TunnelFrame.HeaderSize, payload, 0, length);
            frames.Add(new TunnelFrame(_buffer[0], _buffer[1], payload));

            _buffer.RemoveRange(0, total);
        }

        return frames;
    }

    /// <summary>How much is held waiting for the rest of a frame. For diagnostics.</summary>
    public int Pending => _buffer.Count;
}
