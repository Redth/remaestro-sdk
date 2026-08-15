using System.Buffers.Binary;

namespace Remaestro.ProxyAgent;

/// <param name="Type">The evdev event type. Only <see cref="EvdevReader.TypeKey"/> is a button.</param>
/// <param name="Code">The keycode — <c>KEY_VOLUMEUP</c> is 115.</param>
/// <param name="Value">0 released, 1 pressed, 2 the kernel repeating a held key.</param>
public readonly record struct EvdevEvent(ushort Type, ushort Code, int Value);

/// <summary>
/// Reading <c>struct input_event</c> off an event node.
/// <para>
/// The kernel writes fixed-size records to <c>/dev/input/eventN</c> and a read returns whole ones. The
/// struct is a timestamp, then <c>__u16 type</c>, <c>__u16 code</c>, <c>__s32 value</c> — and the trap is
/// entirely in the timestamp.
/// </para>
/// <para>
/// <b>The record is not the same size on every Pi, and getting it wrong fails silently.</b> The timestamp is
/// a <c>struct timeval</c>, whose two fields are the machine's word size: 24 bytes a record on 64-bit, 16 on
/// 32-bit. Reading a 32-bit stream at 24-byte strides doesn't throw, doesn't return nothing, and doesn't log
/// — it returns a plausible-looking event with the wrong keycode in it, drifting further out of step with
/// every press. That is the difference between a Pi 4 running the arm64 image and a Zero 2 W somebody put
/// 32-bit Raspberry Pi OS on, and it is exactly the kind of thing that would be found by a person pressing
/// volume-up and getting the letter Q.
/// </para>
/// <para>
/// The size is therefore derived rather than assumed, and <see cref="RecordSize"/> is settable so the other
/// case is a test rather than a second machine.
/// </para>
/// </summary>
public sealed class EvdevReader(int? recordSize = null)
{
    /// <summary>A key or button. The only type this seam carries — see the hub's <c>EvdevKeys.TypeKey</c>.</summary>
    public const ushort TypeKey = 0x01;

    /// <summary>
    /// The separator the kernel puts after each group of events. Not a button, and reported as one it would
    /// arrive as a press of keycode 0 after everything.
    /// </summary>
    public const ushort TypeSync = 0x00;

    /// <summary>On a 64-bit kernel: two 64-bit timeval fields, then 2 + 2 + 4.</summary>
    public const int RecordSize64 = 24;

    /// <summary>On a 32-bit one: two 32-bit timeval fields, then the same 8 bytes.</summary>
    public const int RecordSize32 = 16;

    /// <summary>What this machine's records are, unless a test says otherwise.</summary>
    public static int NativeRecordSize => Environment.Is64BitProcess ? RecordSize64 : RecordSize32;

    public int RecordSize { get; } = recordSize ?? NativeRecordSize;

    /// <summary>Where <c>type</c> starts: past the timestamp, which is the whole of the difference.</summary>
    int Offset => RecordSize - 8;

    /// <summary>
    /// The whole records in <paramref name="buffer"/>, and how many bytes they used.
    /// <para>
    /// A partial record at the end is left for the next read rather than guessed at. Reads on an event node
    /// return whole records in practice, but "in practice" is doing a lot of work in a loop that runs for
    /// months, and a half-record consumed once puts every subsequent event permanently out of step.
    /// </para>
    /// </summary>
    public IReadOnlyList<EvdevEvent> Decode(ReadOnlySpan<byte> buffer, out int consumed)
    {
        var events = new List<EvdevEvent>();
        consumed = 0;

        while (buffer.Length - consumed >= RecordSize)
        {
            var record = buffer.Slice(consumed, RecordSize);

            events.Add(new EvdevEvent(
                BinaryPrimitives.ReadUInt16LittleEndian(record[Offset..]),
                BinaryPrimitives.ReadUInt16LittleEndian(record[(Offset + 2)..]),
                BinaryPrimitives.ReadInt32LittleEndian(record[(Offset + 4)..])));

            consumed += RecordSize;
        }

        return events;
    }

    /// <summary>
    /// One record, laid out the way the kernel would. For tests, and for nothing else — an input device is
    /// read from and never written to.
    /// </summary>
    public byte[] Encode(EvdevEvent what)
    {
        var record = new byte[RecordSize];

        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(Offset), what.Type);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(Offset + 2), what.Code);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(Offset + 4), what.Value);

        return record;
    }

    /// <summary>
    /// Whether this event is a button press or release worth relaying.
    /// <para>
    /// Autorepeat is dropped at the hub instead of here, deliberately: the proxy relays and the hub decides,
    /// and a rule about how a held key should behave is a rule about what a button meant.
    /// </para>
    /// </summary>
    public static bool IsButton(EvdevEvent what) => what.Type == TypeKey;

    /// <summary>The three payload bytes a key event becomes on the wire.</summary>
    public static byte[] Payload(EvdevEvent what) =>
    [
        HidHostOp.Evdev,
        (byte)(what.Code & 0xFF),
        (byte)(what.Code >> 8),

        // Clamped rather than cast. A value the kernel invents later would otherwise wrap into looking like
        // a press, and inventing a keypress is the one failure this seam must never have.
        (byte)(what.Value is >= 0 and <= 2 ? what.Value : 0xFF),
    ];
}
