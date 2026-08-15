using System.Text.Json;
using Agent = Remaestro.ProxyAgent;

namespace Remaestro.ProxyAgent.Tests;

/// <summary>
/// The board side of the tunnel, checked against the specification rather than against itself.
/// <para>
/// Everything the hub would contribute is a literal in <see cref="HubWire"/> — the op bytes, the header
/// layout, the two JSON documents, one whole frame in hex. So a third party writing a proxy in another
/// language can read this file as the contract and port the assertions, which is the thing the in-tree
/// version of these tests could never be.
/// </para>
/// </summary>
public class TunnelConformanceTests
{
    [Fact]
    public void Every_op_has_the_value_the_specification_gives_it()
    {
        Assert.Equal(HubWire.Hello, Agent.TunnelOp.Hello);
        Assert.Equal(HubWire.Welcome, Agent.TunnelOp.Welcome);
        Assert.Equal(HubWire.Open, Agent.TunnelOp.Open);
        Assert.Equal(HubWire.Opened, Agent.TunnelOp.Opened);
        Assert.Equal(HubWire.OpenFailed, Agent.TunnelOp.OpenFailed);
        Assert.Equal(HubWire.Close, Agent.TunnelOp.Close);
        Assert.Equal(HubWire.Data, Agent.TunnelOp.Data);
        Assert.Equal(HubWire.Event, Agent.TunnelOp.Event);
        Assert.Equal(HubWire.Ping, Agent.TunnelOp.Ping);
        Assert.Equal(HubWire.Pong, Agent.TunnelOp.Pong);
        Assert.Equal(HubWire.Update, Agent.TunnelOp.Update);
        Assert.Equal(HubWire.UpdateStatus, Agent.TunnelOp.UpdateStatus);
    }

    [Fact]
    public void Every_hid_op_has_the_value_the_specification_gives_it()
    {
        Assert.Equal(HubWire.HidScan, Agent.HidHostOp.Scan);
        Assert.Equal(HubWire.HidConnect, Agent.HidHostOp.Connect);
        Assert.Equal(HubWire.HidForget, Agent.HidHostOp.Forget);
        Assert.Equal(HubWire.HidFound, Agent.HidHostOp.Found);
        Assert.Equal(HubWire.HidAttached, Agent.HidHostOp.Attached);
        Assert.Equal(HubWire.HidDetached, Agent.HidHostOp.Detached);
        Assert.Equal(HubWire.HidReport, Agent.HidHostOp.Report);
        Assert.Equal(HubWire.HidEvdev, Agent.HidHostOp.Evdev);
    }

    [Fact]
    public void The_frame_header_is_the_shape_the_specification_gives_it()
    {
        Assert.Equal(HubWire.HeaderSize, Agent.TunnelFrame.HeaderSize);
        Assert.Equal(HubWire.MaxPayload, Agent.TunnelFrame.MaxPayload);
        Assert.Equal(HubWire.ControlChannel, Agent.TunnelWire.Control);
        Assert.Equal(HubWire.Port, Agent.TunnelWire.Port);
    }

    [Fact]
    public void One_whole_frame_is_byte_for_byte_what_the_specification_says()
    {
        // The vector that pins the layout. A reader that writes the length big-endian, or counts the header
        // into it, produces something that still parses at the other end for short frames and fails here.
        var wire = Agent.TunnelWire.Encode(
            Agent.TunnelFrame.OfText(Agent.TunnelOp.Open, 3, """{"role":"usb.input","index":0}"""));

        Assert.Equal(HubWire.OpenFrameOnChannel3Hex, Convert.ToHexString(wire));
    }

    [Fact]
    public void What_the_agent_writes_is_what_the_hub_reads()
    {
        var payload = new byte[] { 0x85, 0x73, 0x00, 0x01 };
        var wire = Agent.TunnelWire.Encode(new Agent.TunnelFrame(Agent.TunnelOp.Data, 7, payload));

        var reader = new HubWire.Reader();
        var frame = Assert.Single(reader.Push(wire));

        Assert.Equal(HubWire.Data, frame.Op);
        Assert.Equal(7, frame.Channel);
        Assert.Equal(payload, frame.Payload);
        Assert.Equal(0, reader.Pending);
    }

    [Fact]
    public void What_the_hub_writes_is_what_the_agent_reads()
    {
        var wire = HubWire.EncodeText(HubWire.Open, 3, HubWire.OpenRequest("usb.input", 0));

        var reader = new Agent.TunnelReader();
        var frame = Assert.Single(reader.Push(wire));

        Assert.Equal(Agent.TunnelOp.Open, frame.Op);
        Assert.Equal(3, frame.Channel);
        Assert.Equal("usb.input", Agent.ChannelRequest.Parse(frame.Text)!.Role);
    }

    [Fact]
    public void A_frame_split_across_reads_is_still_one_frame()
    {
        // TCP has no message boundaries. This is the bug that works perfectly on a desk.
        var wire = HubWire.EncodeText(HubWire.Open, 1, HubWire.OpenRequest("usb.input", 2));

        var reader = new Agent.TunnelReader();
        var frames = new List<Agent.TunnelFrame>();

        foreach (var b in wire) frames.AddRange(reader.Push([b]));

        var frame = Assert.Single(frames);
        Assert.Equal(Agent.TunnelOp.Open, frame.Op);
        Assert.Equal(2, Agent.ChannelRequest.Parse(frame.Text)!.Index);
    }

    [Fact]
    public void Three_frames_in_one_read_are_still_three_frames()
    {
        var one = HubWire.Encode(HubWire.Ping, 0, default);
        var two = HubWire.EncodeText(HubWire.Open, 1, "{}");
        var three = HubWire.Encode(HubWire.Close, 1, default);

        var reader = new Agent.TunnelReader();
        var frames = reader.Push([.. one, .. two, .. three]);

        Assert.Equal(3, frames.Count);
        Assert.Equal(Agent.TunnelOp.Ping, frames[0].Op);
        Assert.Equal(Agent.TunnelOp.Open, frames[1].Op);
        Assert.Equal(Agent.TunnelOp.Close, frames[2].Op);
    }

    [Fact]
    public void A_length_nothing_could_have_sent_drops_the_connection()
    {
        // Resynchronising would mean guessing where the next frame starts. Both ends refuse, identically.
        var agent = new Agent.TunnelReader();
        agent.Push([Agent.TunnelOp.Data, 1, 0xFF, 0xFF]);
        Assert.NotNull(agent.Fault);

        var hub = new HubWire.Reader();
        hub.Push([HubWire.Data, 1, 0xFF, 0xFF]);
        Assert.NotNull(hub.Fault);
    }

    [Fact]
    public void A_frame_over_the_limit_is_refused_at_the_writer_too()
    {
        // The other half of the same rule: an agent must not put on the wire what its peer must drop.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Agent.TunnelWire.Encode(new Agent.TunnelFrame(
                Agent.TunnelOp.Data, 1, new byte[HubWire.MaxPayload + 1])));
    }

    [Fact]
    public void The_role_this_tier_serves_is_spelled_the_way_the_specification_spells_it()
    {
        Assert.Equal("usb.input", Agent.AgentRole.UsbInput);

        // And the agent claims exactly what the hub will let a Linux proxy be wired for — no more, so a
        // config can't ask for something that fails at open; no less, so a role isn't offered and then
        // refused by the machine that was supposed to do it.
        Assert.Equal(HubWire.LinuxRoles.Order(), Agent.AgentRole.All.Order());

        // And every role it claims is in the vocabulary at all.
        Assert.All(Agent.AgentRole.All, role => Assert.Contains(role, HubWire.EveryRole));
    }

    [Fact]
    public void Every_chip_the_agent_reports_is_one_the_hub_calls_linux()
    {
        // BoardIdentity.DetectChip's whole job is to produce a string the hub routes to the Linux validator.
        // One that didn't would be silently handed to the ESP32 pin rules and told it has no GPIO 0.
        string[] models =
        [
            "Raspberry Pi Zero 2 W Rev 1.0",
            "Raspberry Pi 3 Model B Plus Rev 1.3",
            "Raspberry Pi 4 Model B Rev 1.4",
            "Raspberry Pi 5 Model B Rev 1.0",
            "Raspberry Pi Compute Module 4 Rev 1.1",
            "Raspberry Pi Compute Module 5 Rev 1.0",
            "Some Mini PC Nobody Has Heard Of",
            "",
        ];

        foreach (var model in models)
        {
            var chip = Agent.BoardIdentity.DetectChip(model);

            Assert.True(HubWire.IsLinux(chip),
                $"“{model}” became “{chip}”, which this hub would validate as an ESP32.");
        }
    }

    [Theory]
    [InlineData("Raspberry Pi Zero 2 W Rev 1.0", "pi-zero-2w")]
    [InlineData("Raspberry Pi 3 Model B Plus Rev 1.3", "pi-3")]
    [InlineData("Raspberry Pi 4 Model B Rev 1.4", "pi-4")]
    [InlineData("Raspberry Pi 5 Model B Rev 1.0", "pi-5")]
    [InlineData("Raspberry Pi Compute Module 4 Rev 1.1", "pi-cm4")]
    [InlineData("Raspberry Pi Compute Module 5 Rev 1.0", "pi-cm5")]
    [InlineData("Banana Pi BPI-M5", "linux")]
    [InlineData("", "linux")]
    public void A_board_names_itself_the_way_the_hub_spells_it(string model, string expected)
    {
        Assert.Equal(expected, Agent.BoardIdentity.DetectChip(model));
    }

    [Fact]
    public void A_zero_2w_is_not_mistaken_for_a_pi_3()
    {
        // The Zero 2 W's device tree says "Raspberry Pi Zero 2 W", and its SoC is the Pi 3's. A substring
        // test written in the wrong order would call it a pi-3 — which is the same family and therefore
        // silently harmless, right up until the console tells somebody they own a different computer.
        Assert.Equal("pi-zero-2w", Agent.BoardIdentity.DetectChip("Raspberry Pi Zero 2 W Rev 1.0"));
    }

    [Fact]
    public void The_hello_the_agent_sends_is_the_hello_the_hub_reads()
    {
        var hello = new Agent.AgentHello(
            "remaestro-aabbccddeeff", "pi-zero-2w", "1.0.0", "Living room", "s3cret");

        Assert.Equal(HubWire.HelloDocument, hello.ToJson());
    }

    [Fact]
    public void The_config_the_hub_writes_is_the_config_the_agent_reads()
    {
        // The other half of the contract, and the one that decides whether a proxy does anything at all.
        var config = Agent.AgentConfig.Parse(HubWire.ConfigDocument);

        Assert.NotNull(config);
        Assert.Equal("Living room", config.Name);
        Assert.Equal("s3cret", config.Token);
        Assert.Equal("192.0.2.12", config.HubHost());

        var wiring = Assert.Single(config.Pins);
        Assert.Equal("usb.input", wiring.Role);
        Assert.Equal("Sofa remote", wiring.Name);

        // The field this whole tier turns on. A hub that wrote it under another name would leave the proxy
        // configured, connected, and listening to nothing.
        Assert.Equal("SEM USB Keykoard", wiring.Device);
    }

    [Fact]
    public void A_key_a_newer_hub_sends_does_not_stop_an_older_proxy_reading_the_rest()
    {
        // The whole compatibility story in one test. Throwing here would take a proxy off the network over a
        // field it did not need.
        using var document = JsonDocument.Parse(HubWire.ConfigDocument);
        var extended = HubWire.ConfigDocument[..^1] + ""","somethingInvented":{"deep":[1,2,3]}}""";

        var config = Agent.AgentConfig.Parse(extended);

        Assert.NotNull(config);
        Assert.Equal("Living room", config.Name);
        Assert.Single(config.Pins);
    }

    [Fact]
    public void A_proxy_counts_its_own_roles_the_way_the_hub_numbers_them()
    {
        // The hub numbers the picker entries per role in config order, and the agent has to resolve an index
        // the same way or a channel opens onto the wrong remote.
        var config = new Agent.AgentConfig
        {
            Pins =
            {
                new Agent.AgentPin { Role = "serial", Name = "Projector" },
                new Agent.AgentPin { Role = Agent.AgentRole.UsbInput, Device = "First" },
                new Agent.AgentPin { Role = "ir.tx", Name = "Blaster" },
                new Agent.AgentPin { Role = Agent.AgentRole.UsbInput, Device = "Second" },
            },
        };

        Assert.Equal("First", config.Find(Agent.AgentRole.UsbInput, 0)!.Device);
        Assert.Equal("Second", config.Find(Agent.AgentRole.UsbInput, 1)!.Device);
        Assert.Null(config.Find(Agent.AgentRole.UsbInput, 2));
    }

    [Theory]
    [InlineData("http://192.0.2.12:5006", "192.0.2.12")]
    [InlineData("http://192.0.2.12:5006/", "192.0.2.12")]
    [InlineData("https://hub.example.com", "hub.example.com")]
    [InlineData("192.0.2.12", "192.0.2.12")]
    [InlineData("192.0.2.12:5006", "192.0.2.12")]
    [InlineData("", null)]
    public void The_hub_address_is_read_out_of_whatever_shape_it_arrived_in(string hub, string? expected)
    {
        Assert.Equal(expected, new Agent.AgentConfig { Hub = hub }.HubHost());
    }
}
