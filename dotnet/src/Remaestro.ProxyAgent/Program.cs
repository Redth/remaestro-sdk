using System.Net.Sockets;
using Remaestro.ProxyAgent;

// The Linux proxy agent: the board side of the hub's tunnel, on a machine with a filesystem.
//
// It dials out and never listens, which is what makes a proxy safe to leave in a living room — no inbound
// port, nothing forwarded, no sshd needed for it to work. See docs/proxy-hardware.md.

var configPath = args.FirstOrDefault(a => !a.StartsWith('-'))
    ?? Environment.GetEnvironmentVariable("REMAESTRO_PROXY_CONFIG")
    ?? "/etc/remaestro/proxy.json";

void Say(string what) => Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} {what}");

var identity = BoardIdentity.Detect();
Say($"This is {identity.Id}, a {identity.Chip}, running {identity.Firmware}.");

using var quit = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    quit.Cancel();
};

var devices = new InputDevices();

// Every reconnect re-reads the config rather than holding the one it started with. The hub writes a new
// document and the proxy restarts today, but a config that is only read at boot is how a machine ends up
// serving a configuration nobody can see any more.
while (!quit.IsCancellationRequested)
{
    var config = await AgentConfig.ReadAsync(configPath, quit.Token);

    if (config is null)
    {
        Say($"No configuration at {configPath} yet. Adopt this proxy from the hub's Proxies page.");
        await Wait(TimeSpan.FromSeconds(30));
        continue;
    }

    if (config.HubHost() is not { } host)
    {
        Say($"{configPath} doesn't say where the hub is.");
        await Wait(TimeSpan.FromSeconds(30));
        continue;
    }

    try
    {
        using var socket = new TcpClient();

        // Nagle batches small writes, which for a remote-control keypress means adding up to 40 ms to
        // something a person is watching happen. The hub's end sets this for the same reason.
        socket.NoDelay = true;

        await socket.ConnectAsync(host, TunnelWire.Port, quit.Token);
        Say($"Connected to the hub at {host}:{TunnelWire.Port}.");

        await using var stream = socket.GetStream();

        var session = new ProxySession(config, identity, devices, log: Say);
        await session.RunAsync(stream, quit.Token);

        Say("The hub connection ended.");
    }
    catch (OperationCanceledException) when (quit.IsCancellationRequested) { break; }
    catch (Exception ex)
    {
        Say($"Couldn't reach the hub at {host}: {ex.Message}");
    }

    // Fixed rather than backing off. A proxy is on a house network beside the hub it dials, the traffic is
    // one connection every few seconds at worst, and a backoff would mean a proxy that was unplugged over a
    // weekend takes minutes to come back after somebody plugs it in — which is the moment they are standing
    // there watching it.
    await Wait(TimeSpan.FromSeconds(5));
}

Say("Stopped.");
return 0;

async Task Wait(TimeSpan how)
{
    try { await Task.Delay(how, quit.Token); }
    catch (OperationCanceledException) { /* quitting */ }
}
