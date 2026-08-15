using System.Buffers.Binary;
using System.Text;

namespace Remaestro.ProxyAgent.Tests;

/// <summary>
/// The hub's side of the tunnel, written from the specification rather than referenced.
/// <para>
/// <b>This is the point of the whole test project, so it is worth a paragraph.</b> In the private product
/// repository these tests link the hub assembly and compare the two implementations directly. That is the
/// right design in a tree that contains both, and it cannot be published — the hub is not open source, and a
/// conformance suite a third party cannot run is not a conformance suite.
/// </para>
/// <para>
/// So the hub side here is <i>literal</i>: the op bytes, the header layout, the JSON documents and the
/// expected payloads are all written out, taken from a run of the real hub code and pasted in. That makes
/// this a spec with vectors rather than a mirror, which is what a board author actually needs — and it means
/// these tests are equally valid for a proxy written in C, Go or Python. Nothing here shares a type with the
/// agent, so a rename on either side fails a test rather than compiling.
/// </para>
///
/// <para><b>The frame</b> — four bytes of header, then the body:</para>
/// <code>
///   offset 0   op        u8
///   offset 1   channel   u8    (0 is control; 1..255 are peripherals the hub opened)
///   offset 2   length    u16   little-endian, at most 4096
///   offset 4   payload   length bytes
/// </code>
/// <para>
/// It is binary and length-prefixed rather than JSON lines because serial traffic <i>is</i> binary, and
/// escaping is where a transparent pipe stops being transparent. TCP has no message boundaries, so a reader
/// must handle a frame split across reads and several frames in one — both are tested below, and code that
/// assumes one read is one message works on a desk and fails under load.
/// </para>
/// </summary>
public static class HubWire
{
    // ---- Ops, control channel ----------------------------------------------------------------------

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

    // ---- Ops inside a usb.input / bt.host channel's payload -----------------------------------------

    public const byte HidScan = 0x01;
    public const byte HidConnect = 0x02;
    public const byte HidForget = 0x03;
    public const byte HidFound = 0x81;
    public const byte HidAttached = 0x82;
    public const byte HidDetached = 0x83;
    public const byte HidReport = 0x84;

    /// <summary>A key the kernel already resolved: <c>[codeLow, codeHigh, value]</c>.</summary>
    public const byte HidEvdev = 0x85;

    // ---- Shape --------------------------------------------------------------------------------------

    public const int HeaderSize = 4;
    public const int MaxPayload = 4096;

    /// <summary>Never handed to a peripheral.</summary>
    public const byte ControlChannel = 0;

    /// <summary>What the hub listens on and every proxy dials.</summary>
    public const int Port = 8130;

    // ---- The role vocabulary ------------------------------------------------------------------------

    /// <summary>Every role a proxy can be wired for. Closed — a board cannot invent one.</summary>
    public static readonly string[] EveryRole =
        ["ir.tx", "ir.rx", "serial", "gpio.out", "gpio.in", "bt.hid", "bt.host", "rf.harmony", "usb.input"];

    /// <summary>
    /// What the hub will let a Linux proxy be wired for today. A role a tier cannot do is <i>absent</i> from
    /// its list rather than present and quietly dead.
    /// </summary>
    public static readonly string[] LinuxRoles = ["usb.input"];

    /// <summary>
    /// The chip ids the hub routes to its Linux validator. Anything else — anything unrecognised at all —
    /// is treated as an ESP32 and told it has no GPIO 0, so a board that names itself wrongly is silently
    /// validated as the wrong hardware.
    /// </summary>
    public static bool IsLinux(string? chip) =>
        chip is not null &&
        (chip.Equals("linux", StringComparison.OrdinalIgnoreCase) ||
         chip.StartsWith("pi-", StringComparison.OrdinalIgnoreCase));

    // ---- Documents, verbatim ------------------------------------------------------------------------

    /// <summary>
    /// What the hub sends to open a channel. Captured from <c>ChannelOpen.ToJson()</c>: note that every
    /// field is present whether or not the role uses it, so a reader must ignore the ones it does not need.
    /// </summary>
    public static string OpenRequest(string role, int index) =>
        $$"""{"role":"{{role}}","index":{{index}},"baud":9600,"framing":"8N1","carrier":38000,"address":"","radioChannel":5}""";

    /// <summary>
    /// A configuration document exactly as the hub writes it. Captured from <c>ProxyConfigJson.Write</c>.
    /// <c>pin</c> and <c>pin2</c> are always present and always meaningless on a machine with no GPIO.
    /// </summary>
    public const string ConfigDocument =
        """{"name":"Living room","hub":"http://192.0.2.12:5006","token":"s3cret","pins":[{"role":"usb.input","pin":-1,"pin2":-1,"name":"Sofa remote","device":"SEM USB Keykoard","settings":{}}]}""";

    /// <summary>A hello exactly as the hub parses it. Captured from <c>TunnelHello.ToJson</c>.</summary>
    public const string HelloDocument =
        """{"id":"remaestro-aabbccddeeff","chip":"pi-zero-2w","firmware":"1.0.0","name":"Living room","token":"s3cret"}""";

    /// <summary>
    /// One whole frame on the wire, byte for byte, as a hex string: an <c>Open</c> on channel 3 carrying a
    /// thirty-byte body. This is the single vector that pins the header layout — op, channel, little-endian
    /// length, body — and a reader that gets the endianness backwards fails here and nowhere else.
    /// </summary>
    public const string OpenFrameOnChannel3Hex =
        "10031E007B22726F6C65223A227573622E696E707574222C22696E646578223A307D";

    // ---- The codec ----------------------------------------------------------------------------------

    public static byte[] Encode(byte op, byte channel, ReadOnlySpan<byte> payload)
    {
        var buffer = new byte[HeaderSize + payload.Length];

        buffer[0] = op;
        buffer[1] = channel;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(2), (ushort)payload.Length);
        payload.CopyTo(buffer.AsSpan(HeaderSize));

        return buffer;
    }

    public static byte[] EncodeText(byte op, byte channel, string text) =>
        Encode(op, channel, Encoding.UTF8.GetBytes(text));

    /// <param name="Channel">0 is control.</param>
    public readonly record struct Frame(byte Op, byte Channel, byte[] Payload)
    {
        public string Text => Encoding.UTF8.GetString(Payload);
    }

    /// <summary>
    /// The re-framer. Deliberately naive — it is here to be obviously correct rather than fast, so that a
    /// disagreement between it and the agent is the agent's.
    /// </summary>
    public sealed class Reader
    {
        readonly List<byte> _buffer = [];

        /// <summary>Set when the stream is unusable and the connection should be dropped.</summary>
        public string? Fault { get; private set; }

        /// <summary>How much is held waiting for the rest of a frame.</summary>
        public int Pending => _buffer.Count;

        public IReadOnlyList<Frame> Push(ReadOnlySpan<byte> chunk)
        {
            if (Fault is not null) return [];

            _buffer.AddRange(chunk);
            var frames = new List<Frame>();

            while (_buffer.Count >= HeaderSize)
            {
                var length = _buffer[2] | (_buffer[3] << 8);

                if (length > MaxPayload)
                {
                    // Nothing legitimate is this big, so the stream is out of step. Resynchronising would
                    // mean guessing where the next frame starts; both ends refuse, identically.
                    Fault = $"A frame claimed {length} bytes, over the {MaxPayload} limit.";
                    _buffer.Clear();
                    return frames;
                }

                if (_buffer.Count < HeaderSize + length) break;

                frames.Add(new Frame(_buffer[0], _buffer[1], [.. _buffer.GetRange(HeaderSize, length)]));
                _buffer.RemoveRange(0, HeaderSize + length);
            }

            return frames;
        }
    }

    /// <summary>What an op is called, for a failure message worth reading.</summary>
    public static string Describe(byte op) => op switch
    {
        Hello => "Hello",
        Welcome => "Welcome",
        Open => "Open",
        Opened => "Opened",
        OpenFailed => "OpenFailed",
        Close => "Close",
        Data => "Data",
        Event => "Event",
        Ping => "Ping",
        Pong => "Pong",
        Update => "Update",
        UpdateStatus => "UpdateStatus",
        _ => $"0x{op:X2}",
    };
}
