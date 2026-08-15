using Agent = Remaestro.ProxyAgent;

namespace Remaestro.ProxyAgent.Tests;

/// <summary>
/// Reading <c>struct input_event</c>, and the two ways it goes silently wrong.
/// </summary>
public class EvdevReaderTests
{
    [Fact]
    public void A_record_written_by_a_64_bit_kernel_reads_back_as_itself()
    {
        var reader = new Agent.EvdevReader(Agent.EvdevReader.RecordSize64);
        var wire = reader.Encode(new Agent.EvdevEvent(Agent.EvdevReader.TypeKey, 115, 1));

        Assert.Equal(24, wire.Length);

        var read = Assert.Single(reader.Decode(wire, out var consumed));
        Assert.Equal(24, consumed);
        Assert.Equal(new Agent.EvdevEvent(Agent.EvdevReader.TypeKey, 115, 1), read);
    }

    [Fact]
    public void A_record_written_by_a_32_bit_kernel_reads_back_as_itself()
    {
        // A Zero 2 W with 32-bit Raspberry Pi OS on it. Same fields, eight fewer bytes of timestamp.
        var reader = new Agent.EvdevReader(Agent.EvdevReader.RecordSize32);
        var wire = reader.Encode(new Agent.EvdevEvent(Agent.EvdevReader.TypeKey, 115, 1));

        Assert.Equal(16, wire.Length);

        var read = Assert.Single(reader.Decode(wire, out _));
        Assert.Equal(new Agent.EvdevEvent(Agent.EvdevReader.TypeKey, 115, 1), read);
    }

    /// <summary>
    /// The trap this whole class is shaped around: reading one machine's records at the other's stride does
    /// not throw, does not return nothing, and does not log — it returns a plausible event with the wrong
    /// keycode in it. Somebody presses volume-up and gets a letter.
    /// </summary>
    [Fact]
    public void Reading_at_the_wrong_stride_is_wrong_rather_than_empty()
    {
        var kernel = new Agent.EvdevReader(Agent.EvdevReader.RecordSize32);
        var wire = new List<byte>();

        for (var i = 0; i < 3; i++)
            wire.AddRange(kernel.Encode(new Agent.EvdevEvent(Agent.EvdevReader.TypeKey, 115, 1)));

        var wrong = new Agent.EvdevReader(Agent.EvdevReader.RecordSize64).Decode(wire.ToArray(), out _);

        // It reads *something*, which is exactly the problem — silence would at least be noticed.
        Assert.NotEmpty(wrong);

        // Three presses went in and two events came out, one of which is a key that was never pressed:
        // reading 16-byte records at a 24-byte stride lands the first read inside the second record's
        // timestamp, which is zeroes. Nothing throws and nothing logs.
        Assert.Equal(3, kernel.Decode(wire.ToArray(), out _).Count);
        Assert.Equal(2, wrong.Count);
        Assert.Contains(wrong, e => e is { Type: 0, Code: 0, Value: 0 });
    }

    [Fact]
    public void The_native_stride_matches_this_machines_word_size()
    {
        Assert.Equal(
            Environment.Is64BitProcess ? Agent.EvdevReader.RecordSize64 : Agent.EvdevReader.RecordSize32,
            new Agent.EvdevReader().RecordSize);
    }

    [Fact]
    public void A_half_arrived_record_is_kept_rather_than_guessed_at()
    {
        // Consumed once, every subsequent event is permanently out of step — and nothing says so.
        var reader = new Agent.EvdevReader(Agent.EvdevReader.RecordSize64);
        var whole = reader.Encode(new Agent.EvdevEvent(Agent.EvdevReader.TypeKey, 115, 1));

        var events = reader.Decode(whole.AsSpan(0, 20), out var consumed);

        Assert.Empty(events);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void Several_records_in_one_read_are_several_events()
    {
        var reader = new Agent.EvdevReader(Agent.EvdevReader.RecordSize64);

        var wire = new List<byte>();
        wire.AddRange(reader.Encode(new Agent.EvdevEvent(Agent.EvdevReader.TypeKey, 103, 1)));
        wire.AddRange(reader.Encode(new Agent.EvdevEvent(Agent.EvdevReader.TypeSync, 0, 0)));
        wire.AddRange(reader.Encode(new Agent.EvdevEvent(Agent.EvdevReader.TypeKey, 103, 0)));

        var events = reader.Decode(wire.ToArray(), out var consumed);

        Assert.Equal(3, events.Count);
        Assert.Equal(72, consumed);
        Assert.Equal(2, events.Count(Agent.EvdevReader.IsButton));
    }

    [Fact]
    public void Only_key_events_are_buttons()
    {
        // An air mouse emits EV_REL for every millimetre it moves. None of those is a press.
        Assert.True(Agent.EvdevReader.IsButton(new Agent.EvdevEvent(Agent.EvdevReader.TypeKey, 115, 1)));
        Assert.False(Agent.EvdevReader.IsButton(new Agent.EvdevEvent(Agent.EvdevReader.TypeSync, 0, 0)));
        Assert.False(Agent.EvdevReader.IsButton(new Agent.EvdevEvent(0x02, 0, 5)));   // EV_REL
        Assert.False(Agent.EvdevReader.IsButton(new Agent.EvdevEvent(0x03, 0, 5)));   // EV_ABS
    }

    [Fact]
    public void The_payload_is_the_four_bytes_the_hub_decodes()
    {
        var payload = Agent.EvdevReader.Payload(new Agent.EvdevEvent(Agent.EvdevReader.TypeKey, 115, 1));

        // 0x85, then the keycode little-endian, then the edge. KEY_VOLUMEUP is 115, and the hub renders
        // this exact payload as "EVT KEY KEY_VOLUMEUP down".
        Assert.Equal([HubWire.HidEvdev, 115, 0, 1], payload);
    }

    [Fact]
    public void A_keycode_over_255_survives_the_two_bytes_it_is_given()
    {
        // KEY_VOICECOMMAND is 582, and the microphone is the button this tier exists to carry. A single-byte
        // payload would have truncated it to 70 — KEY_F12 — which is a working button of the wrong name.
        var payload = Agent.EvdevReader.Payload(new Agent.EvdevEvent(Agent.EvdevReader.TypeKey, 582, 1));

        Assert.Equal([HubWire.HidEvdev, 0x46, 0x02, 1], payload);       // 582 == 0x0246
    }

    [Fact]
    public void A_value_nobody_has_defined_never_becomes_a_press()
    {
        // Inventing a keypress is the one failure this seam must not have. Anything outside 0/1/2 is clamped
        // to 0xFF rather than cast, which the hub reads as "not a press" instead of wrapping into one.
        var payload = Agent.EvdevReader.Payload(new Agent.EvdevEvent(Agent.EvdevReader.TypeKey, 115, 9999));

        Assert.Equal([HubWire.HidEvdev, 115, 0, 0xFF], payload);
    }
}

/// <summary>Finding the right device, on a filesystem laid out like a Pi's.</summary>
public class InputDeviceTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), $"pi-{Guid.NewGuid():N}");

    public InputDeviceTests()
    {
        Plug("event0", "vc4-hdmi");
        Plug("event1", "SEM USB Keykoard");
        Plug("event2", "SEM USB Keykoard Consumer Control");
        Plug("event10", "Logitech K400 Plus");
    }

    void Plug(string node, string name)
    {
        var dir = Path.Combine(_root, "sys", "class", "input", node, "device");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "name"), name + "\n");
    }

    Agent.InputDevices Devices => new(_root);

    [Fact]
    public void Every_event_node_is_listed_with_the_name_it_reports()
    {
        var all = Devices.All();

        Assert.Equal(4, all.Count);
        Assert.Contains(all, d => d.Name == "SEM USB Keykoard");
    }

    [Fact]
    public void Nodes_are_ordered_by_number_and_not_as_text()
    {
        // event10 sorting between event1 and event2 would renumber which device an index refers to, which
        // is a remote that works until somebody plugs in a keyboard.
        Assert.Equal(
            ["event0", "event1", "event2", "event10"],
            Devices.All().Select(d => Path.GetFileName(d.Path)));
    }

    [Fact]
    public void A_device_is_found_by_a_piece_of_its_name()
    {
        // Loosely, because these names are read off a picker and typed back — including the vendor's
        // spelling mistake, which is real and is why this example is what it is.
        Assert.Equal("SEM USB Keykoard", Devices.Resolve("Keykoard")!.Name);
        Assert.Equal("SEM USB Keykoard", Devices.Resolve("sem usb")!.Name);
    }

    [Fact]
    public void A_path_is_taken_literally()
    {
        var found = Devices.Resolve(Path.Combine(_root, "dev", "input", "event1"));

        Assert.NotNull(found);
        Assert.Equal("SEM USB Keykoard", found.Name);
    }

    [Fact]
    public void A_by_id_symlink_is_accepted_even_though_it_is_in_no_list()
    {
        // The recommended way to write a path, and it never appears in /sys/class/input — it is a name for
        // an event node rather than one. Refusing it would refuse the one stable path there is.
        var found = Devices.Resolve("/dev/input/by-id/usb-SEM_USB_Keykoard-event-kbd");

        Assert.NotNull(found);
        Assert.Equal("/dev/input/by-id/usb-SEM_USB_Keykoard-event-kbd", found.Path);
    }

    [Fact]
    public void Nothing_matching_is_nothing_rather_than_the_first_thing()
    {
        Assert.Null(Devices.Resolve("Sonos Remote"));
        Assert.Null(Devices.Resolve(""));
        Assert.Null(Devices.Resolve(null));
    }

    [Fact]
    public void A_machine_with_no_input_layer_at_all_lists_nothing()
    {
        Assert.Empty(new Agent.InputDevices(Path.Combine(_root, "nowhere")).All());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp directory left behind is not a failing test */ }
    }
}
