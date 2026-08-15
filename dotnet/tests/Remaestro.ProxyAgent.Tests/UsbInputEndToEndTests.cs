using System.IO.Pipelines;
using System.Text;
using Agent = Remaestro.ProxyAgent;

namespace Remaestro.ProxyAgent.Tests;

/// <summary>
/// A button pressed on a remote plugged into a Pi, arriving at the hub as the bytes the hub decodes.
/// <para>
/// <b>Nothing here opens a socket or touches a device.</b> The transport is a pair of in-memory pipes and the
/// remote is a stream of bytes laid out the way the kernel lays them out — which is the whole reason
/// <see cref="Agent.ProxySession"/> takes a <see cref="Stream"/> and a delegate rather than opening its own.
/// </para>
/// <para>
/// The assertions stop at the <b>payload bytes</b> rather than at the line a driver eventually reads. That is
/// deliberate and it is where the contract actually ends: the hub turns <c>[0x85, 0x73, 0x00, 0x01]</c> into
/// <c>EVT KEY KEY_VOLUMEUP down</c>, and how it spells a keycode is the hub's business, not a proxy's. A
/// board author has met the contract when the right four bytes go out.
/// </para>
/// <para>
/// That is not a stylistic preference. Putting the hub's decoder inside an assertion makes the hub's
/// behaviour look like the proxy's obligation, and it has already misled once —
/// see <see cref="Holding_a_key_relays_the_repeats_and_lets_the_hub_decide"/>, which is the test that found
/// it and explains what it cost.
/// </para>
/// </summary>
public class UsbInputEndToEndTests
{
    /// <summary>The whole path: the hub opens a channel, the agent finds the remote, a key is pressed.</summary>
    [Fact]
    public async Task A_keypress_on_a_pi_becomes_the_bytes_the_hub_decodes()
    {
        await using var rig = await Rig.StartAsync();

        Assert.True(await rig.OpenAsync("usb.input", 0), rig.Refusal);

        // Which remote it turned out to be, before any button was touched: 0x82 then the name it reports.
        Assert.Equal(Attached("SEM USB Keykoard"), Assert.Single(await rig.PayloadsAsync()));

        rig.Press(115);                                       // KEY_VOLUMEUP

        Assert.Equal(
            [
                new byte[] { HubWire.HidEvdev, 115, 0, 1 },   // down
                new byte[] { HubWire.HidEvdev, 115, 0, 0 },   // up
            ],
            await rig.PayloadsAsync());
    }

    /// <summary>
    /// <b>The proxy relays; the hub decides.</b> A held key goes on the wire exactly as the kernel reported
    /// it — all four events, repeats included — and it is the hub, not the proxy, that rules that four
    /// events are not four presses.
    /// <para>
    /// <b>If you are implementing a proxy in another language, this is the rule to take away from this file.</b>
    /// A rule about how a held key should behave is a rule about what a button <i>meant</i>, and meaning is
    /// the hub's job. The proxy's job is to be a faithful pipe. <c>EvdevReader.IsButton</c> says so in as
    /// many words: <i>"autorepeat is dropped at the hub instead of here, deliberately."</i>
    /// </para>
    /// <para>
    /// <b>And this test is the reason the hub does not appear in these assertions at all.</b> Its in-tree
    /// ancestor ran the payloads through the hub's own decoder and asserted on the two lines that came out,
    /// which reads — to anyone porting it — as <i>"the agent suppresses autorepeat"</i>. It does not, and
    /// never did; the hub was doing the suppressing, inside the assertion. A proxy author following that
    /// test would have dropped the repeats locally, produced byte-identical behaviour on day one, and
    /// silently broken press-and-hold the moment the hub found a use for them — a fault with no failing
    /// test anywhere and nothing to point at.
    /// </para>
    /// <para>
    /// That is precisely the failure a conformance suite exists to prevent, and the old shape of the suite
    /// was the thing causing it. Assert on the bytes. Anything the hub renders is downstream of the
    /// contract, not part of it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Holding_a_key_relays_the_repeats_and_lets_the_hub_decide()
    {
        await using var rig = await Rig.StartAsync();
        Assert.True(await rig.OpenAsync("usb.input", 0), rig.Refusal);
        await rig.PayloadsAsync();

        rig.Send(new Agent.EvdevEvent(Agent.EvdevReader.TypeKey, 115, 1));   // down
        rig.Send(new Agent.EvdevEvent(Agent.EvdevReader.TypeKey, 115, 2));   // repeat
        rig.Send(new Agent.EvdevEvent(Agent.EvdevReader.TypeKey, 115, 2));   // repeat
        rig.Send(new Agent.EvdevEvent(Agent.EvdevReader.TypeKey, 115, 0));   // up

        Assert.Equal(
            [
                new byte[] { HubWire.HidEvdev, 115, 0, 1 },
                new byte[] { HubWire.HidEvdev, 115, 0, 2 },
                new byte[] { HubWire.HidEvdev, 115, 0, 2 },
                new byte[] { HubWire.HidEvdev, 115, 0, 0 },
            ],
            await rig.PayloadsAsync());
    }

    [Fact]
    public async Task The_synchronisation_events_between_presses_are_not_buttons()
    {
        // The kernel writes an EV_SYN after each group. Relayed as a button it would arrive as a press of
        // keycode 0 after every real one.
        await using var rig = await Rig.StartAsync();
        Assert.True(await rig.OpenAsync("usb.input", 0), rig.Refusal);
        await rig.PayloadsAsync();

        rig.Send(new Agent.EvdevEvent(Agent.EvdevReader.TypeKey, 28, 1));    // KEY_ENTER down
        rig.Send(new Agent.EvdevEvent(Agent.EvdevReader.TypeSync, 0, 0));

        Assert.Equal(
            [new byte[] { HubWire.HidEvdev, 28, 0, 1 }],
            await rig.PayloadsAsync());
    }

    [Fact]
    public async Task A_remote_that_is_not_plugged_in_is_refused_by_name_and_says_what_is()
    {
        // The failure somebody will actually hit. "No such device" alone sends them to look at the config,
        // which is where the answer isn't.
        await using var rig = await Rig.StartAsync(configuredDevice: "Some Other Remote");

        Assert.False(await rig.OpenAsync("usb.input", 0));
        Assert.Contains("Some Other Remote", rig.Refusal);
        Assert.Contains("SEM USB Keykoard", rig.Refusal);     // what it did find
    }

    [Fact]
    public async Task A_role_this_proxy_cannot_do_is_refused_rather_than_silently_ignored()
    {
        await using var rig = await Rig.StartAsync();

        Assert.False(await rig.OpenAsync("serial", 0));
        Assert.Contains("serial", rig.Refusal);
    }

    [Fact]
    public async Task Nothing_configured_at_that_index_is_refused_in_words()
    {
        await using var rig = await Rig.StartAsync();

        Assert.False(await rig.OpenAsync("usb.input", 3));
        Assert.Contains("number 4", rig.Refusal);             // counted the way a person counts
    }

    [Fact]
    public async Task A_ping_is_answered_so_the_hub_does_not_call_it_silent()
    {
        // The hub drops a proxy that hasn't spoken for 90 seconds. One that never answered a ping would be
        // dropped and reconnect every 90 seconds for ever, which looks exactly like a flaky network.
        await using var rig = await Rig.StartAsync();

        await rig.TellAgentAsync(HubWire.Ping, HubWire.ControlChannel, default);

        var pong = await rig.NextAsync(HubWire.Pong);
        Assert.Equal(HubWire.ControlChannel, pong.Channel);
    }

    [Fact]
    public async Task An_unplugged_remote_closes_the_channel_rather_than_going_quiet()
    {
        // A device node that returns nothing has been unplugged. Left open, the driver would hold a socket
        // that leads nowhere and a room would silently stop working.
        await using var rig = await Rig.StartAsync();
        Assert.True(await rig.OpenAsync("usb.input", 0), rig.Refusal);

        rig.Unplug();

        var closed = await rig.NextAsync(HubWire.Close);
        Assert.Equal(rig.Channel, closed.Channel);
    }

    [Fact]
    public async Task The_agent_says_who_it_is_before_anything_else()
    {
        // The hub entertains nothing but Hello before it knows who is calling — it refuses a connection that
        // speaks first.
        await using var rig = await Rig.StartAsync(welcome: false);

        Assert.Equal(HubWire.HelloDocument, (await rig.NextAsync(HubWire.Hello)).Text);
    }

    [Fact]
    public async Task Two_remotes_on_one_pi_are_two_channels_that_do_not_cross()
    {
        // The reason a Pi is worth having over a board: as many receivers as it has ports. The failure this
        // guards is index bookkeeping — a second channel opening onto the first remote — which presents as
        // one remote working twice and the other never.
        await using var rig = await Rig.StartAsync(second: "Cheap Air Mouse");

        Assert.True(await rig.OpenAsync("usb.input", 0), rig.Refusal);
        Assert.Equal(Attached("SEM USB Keykoard"), Assert.Single(await rig.PayloadsAsync()));

        Assert.True(await rig.OpenAsync("usb.input", 1), rig.Refusal);
        Assert.Equal(Attached("Cheap Air Mouse"), Assert.Single(await rig.PayloadsAsync()));
    }

    /// <summary>What a channel says first: 0x82, then the name the device reports for itself.</summary>
    static byte[] Attached(string name) => [HubWire.HidAttached, .. Encoding.UTF8.GetBytes(name)];

    // ---- The rig ---------------------------------------------------------------------------------------

    /// <summary>
    /// A hub and a proxy on a pair of pipes, with a remote made of bytes.
    /// <para>
    /// The hub's side is driven by hand, out of <see cref="HubWire"/>, because what is under test is the
    /// agent. Nothing in here shares a type with it.
    /// </para>
    /// </summary>
    sealed class Rig : IAsyncDisposable
    {
        readonly Stream _hub;
        readonly Dictionary<string, FakeRemote> _remotes;
        readonly string _root;
        readonly CancellationTokenSource _stop = new();
        readonly HubWire.Reader _reader = new();
        readonly List<HubWire.Frame> _seen = [];
        readonly Lock _gate = new();

        Task _session = Task.CompletedTask;
        Task _drain = Task.CompletedTask;
        byte _next = 1;

        Rig(Stream hub, Dictionary<string, FakeRemote> remotes, string root)
        {
            _hub = hub;
            _remotes = remotes;
            _root = root;
        }

        /// <summary>The channel most recently opened.</summary>
        public byte Channel { get; private set; }

        /// <summary>Why the last open was refused, in the agent's own words.</summary>
        public string Refusal { get; private set; } = "";

        public static async Task<Rig> StartAsync(
            string configuredDevice = "SEM USB Keykoard",
            string? second = null,
            bool welcome = true)
        {
            var (hubSide, agentSide) = DuplexPipe.Create();

            // A sysfs laid out the way a Pi's is, so device discovery is a real directory walk rather than
            // a stubbed one — the point of reading names from files instead of an ioctl.
            var root = Path.Combine(Path.GetTempPath(), $"pi-{Guid.NewGuid():N}");
            var remotes = new Dictionary<string, FakeRemote>(StringComparer.Ordinal);

            Plug(root, remotes, "event0", "SEM USB Keykoard");
            Plug(root, remotes, "event1", "vc4-hdmi");         // a real Pi always has this, and it is not a remote
            if (second is not null) Plug(root, remotes, "event2", second);

            var config = new Agent.AgentConfig
            {
                Name = "Living room",
                Hub = "http://192.0.2.12:5006",
                Token = "s3cret",
                Pins = { Wiring(configuredDevice) },
            };

            if (second is not null) config.Pins.Add(Wiring(second));

            var session = new Agent.ProxySession(
                config,
                new Agent.BoardIdentity("remaestro-aabbccddeeff", "pi-zero-2w", "1.0.0"),
                new Agent.InputDevices(root),
                open: (device, _) => Task.FromResult(remotes[device.Path].Stream));

            var rig = new Rig(hubSide, remotes, root);

            rig._session = session.RunAsync(agentSide, rig._stop.Token);
            rig._drain = rig.DrainAsync();

            // The hello lands first either way; welcoming is what a real hub does next.
            if (welcome) await rig.TellAgentAsync(HubWire.Welcome, HubWire.ControlChannel, default);

            return rig;
        }

        static Agent.AgentPin Wiring(string device) =>
            new() { Role = Agent.AgentRole.UsbInput, Name = device, Device = device };

        static void Plug(string root, Dictionary<string, FakeRemote> remotes, string node, string name)
        {
            var dir = Path.Combine(root, "sys", "class", "input", node, "device");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "name"), name + "\n");

            var devPath = Path.Combine(root, "dev", "input", node);
            Directory.CreateDirectory(Path.Combine(root, "dev", "input"));

            remotes[devPath] = new FakeRemote();
        }

        /// <summary>Ask the agent to open a channel, and say whether it agreed.</summary>
        public async Task<bool> OpenAsync(string role, int index)
        {
            Channel = _next++;

            await TellAgentAsync(HubWire.Open, Channel,
                Encoding.UTF8.GetBytes(HubWire.OpenRequest(role, index)));

            var answer = await NextAsync(HubWire.Opened, HubWire.OpenFailed);

            if (answer.Op != HubWire.OpenFailed) return true;

            Refusal = answer.Text;
            return false;
        }

        /// <summary>Press and release a key on the remote the current channel is listening to.</summary>
        public void Press(ushort code)
        {
            Send(new Agent.EvdevEvent(Agent.EvdevReader.TypeKey, code, 1));
            Send(new Agent.EvdevEvent(Agent.EvdevReader.TypeKey, code, 0));
        }

        /// <summary>Put one raw evdev record into every fake remote, exactly as a kernel would.</summary>
        public void Send(Agent.EvdevEvent what)
        {
            var record = new Agent.EvdevReader().Encode(what);

            foreach (var remote in _remotes.Values) remote.Write(record);
        }

        /// <summary>Every remote goes away, the way a dongle pulled out of a socket does.</summary>
        public void Unplug()
        {
            foreach (var remote in _remotes.Values) remote.Unplug();
        }

        public async Task TellAgentAsync(byte op, byte channel, ReadOnlyMemory<byte> payload)
        {
            await _hub.WriteAsync(HubWire.Encode(op, channel, payload.Span));
            await _hub.FlushAsync();
        }

        /// <summary>Every payload that arrived on the current channel, in order, and then forgotten.</summary>
        public async Task<IReadOnlyList<byte[]>> PayloadsAsync()
        {
            // These are two in-memory pipes and a task. Anything that hasn't landed in a moment isn't coming.
            await Task.Delay(250);

            lock (_gate)
            {
                var mine = _seen.Where(f => f.Op == HubWire.Data && f.Channel == Channel).ToList();
                _seen.RemoveAll(f => f.Op == HubWire.Data && f.Channel == Channel);

                return [.. mine.Select(f => f.Payload)];
            }
        }

        /// <summary>The next frame carrying one of these ops, waiting for it to arrive.</summary>
        public async Task<HubWire.Frame> NextAsync(params byte[] ops)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);

            while (DateTimeOffset.UtcNow < deadline)
            {
                lock (_gate)
                {
                    var at = _seen.FindIndex(f => ops.Contains(f.Op));
                    if (at >= 0)
                    {
                        var found = _seen[at];
                        _seen.RemoveAt(at);
                        return found;
                    }
                }

                await Task.Delay(25);
            }

            lock (_gate)
                throw new Xunit.Sdk.XunitException(
                    $"waited for {string.Join(" or ", ops.Select(HubWire.Describe))} and it never "
                    + $"arrived; saw {string.Join(", ", _seen.Select(f => HubWire.Describe(f.Op)))}");
        }

        /// <summary>Read the agent's side of the wire for as long as the rig lives.</summary>
        async Task DrainAsync()
        {
            var buffer = new byte[8192];

            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var read = await _hub.ReadAsync(buffer, _stop.Token);
                    if (read == 0) break;

                    var frames = _reader.Push(buffer.AsSpan(0, read));
                    lock (_gate) _seen.AddRange(frames);
                }
            }
            catch (Exception) { /* the rig is going away, which is how this ends */ }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            Unplug();

            foreach (var task in new[] { _session, _drain })
            {
                try { await task.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception) { /* cancelled, which is what we asked for */ }
            }

            _stop.Dispose();
            await _hub.DisposeAsync();

            try { Directory.Delete(_root, recursive: true); }
            catch (Exception) { /* a temp directory left behind is not a failing test */ }
        }
    }

    /// <summary>A remote made of bytes: whatever is written to it is what the agent reads off the node.</summary>
    sealed class FakeRemote
    {
        readonly Pipe _pipe = new();

        public FakeRemote() => Stream = _pipe.Reader.AsStream();

        public Stream Stream { get; }

        public void Write(byte[] record) =>
            _pipe.Writer.WriteAsync(record).AsTask().GetAwaiter().GetResult();

        /// <summary>Ends the stream, which is what an event node does when its device is pulled out.</summary>
        public void Unplug()
        {
            try { _pipe.Writer.Complete(); }
            catch (Exception) { /* already gone */ }
        }
    }

    /// <summary>Two streams, each reading what the other wrote. A socket without an address.</summary>
    static class DuplexPipe
    {
        public static (Stream Left, Stream Right) Create()
        {
            var toRight = new Pipe();
            var toLeft = new Pipe();

            return (
                new Joined(toLeft.Reader.AsStream(), toRight.Writer.AsStream()),
                new Joined(toRight.Reader.AsStream(), toLeft.Writer.AsStream()));
        }

        sealed class Joined(Stream read, Stream write) : Stream
        {
            public override bool CanRead => true;
            public override bool CanWrite => true;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
                read.ReadAsync(buffer, ct);

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
                write.WriteAsync(buffer, ct);

            public override Task FlushAsync(CancellationToken ct) => write.FlushAsync(ct);
            public override void Flush() => write.Flush();

            public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);

            public override void Write(byte[] buffer, int offset, int count) =>
                write.Write(buffer, offset, count);

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (!disposing) return;

                read.Dispose();
                write.Dispose();
            }
        }
    }
}
