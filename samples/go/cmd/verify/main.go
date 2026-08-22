// verify is a tiny hub-shaped client: it connects to a running metronome, walks the calls the hub makes,
// and prints what came back.
//
// It exists because of the thing that is easy to get wrong about a proof — **a plugin that has only been
// run on the machine that built it has not been shown to run on a hub.** The hub is arm64 Linux on an
// appliance and amd64 Linux in the cloud, and neither is anybody's laptop. `verify.sh` runs the
// cross-compiled binary inside a Linux container of the right architecture and points this at it.
//
//	go run ./cmd/verify http://127.0.0.1:19999
//
// It is not in the package: `package.sh` builds the module root, and this is `./cmd/verify`.
package main

import (
	"context"
	"fmt"
	"os"
	"time"

	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"

	pb "example.com/metronome/gen/maestro"
)

func main() {
	target := "http://127.0.0.1:19999"
	if len(os.Args) > 1 {
		target = os.Args[1]
	}

	// The hub speaks cleartext h2c on loopback. `insecure.NewCredentials()` is the client half of that and
	// is not a shortcut taken for a sample: `GrpcChannel.ForAddress("http://…")` is exactly this.
	conn, err := grpc.NewClient(trimScheme(target), grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		die("dial: %v", err)
	}
	defer conn.Close()
	c := pb.NewDriverClient(conn)

	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()

	// The hub retries Describe for up to thirty seconds while a driver starts. Do the same, so this does
	// not race a container that is still coming up.
	var d *pb.DriverDescriptor
	deadline := time.Now().Add(20 * time.Second)
	for {
		d, err = c.Describe(ctx, &pb.DescribeRequest{HubProtocol: 1})
		if err == nil || time.Now().After(deadline) {
			break
		}
		time.Sleep(200 * time.Millisecond)
	}
	if err != nil {
		die("Describe: %v", err)
	}
	fmt.Printf("Describe        type_id=%s protocol_version=%d min_hub=%v caps=%v\n",
		d.GetTypeId(), d.GetProtocolVersion(), d.MinHubProtocol, d.GetCapabilities())

	// The stream first, so the heartbeat is being read while everything below runs.
	stream, err := c.StreamEvents(ctx, &pb.StreamEventsRequest{})
	if err != nil {
		die("StreamEvents: %v", err)
	}
	frames := make(chan *pb.DeviceEventMessage, 64)
	go func() {
		for {
			m, err := stream.Recv()
			if err != nil {
				close(frames)
				return
			}
			frames <- m
		}
	}()

	created, err := c.CreateDevice(ctx, &pb.CreateDeviceRequest{
		DeviceId: "verify1", Name: "Verify",
		Config: map[string]string{"bpm": "180", "studio_token": "verify-secret"},
	})
	if err != nil {
		die("CreateDevice: %v", err)
	}
	fmt.Printf("CreateDevice    ok=%v error=%q\n", created.GetOk(), created.GetError())

	ex, err := c.ExecuteCommand(ctx, &pb.ExecuteCommandRequest{DeviceId: "verify1", CommandId: "power.on"})
	if err != nil {
		die("ExecuteCommand: %v", err)
	}
	fmt.Printf("ExecuteCommand  ok=%v error=%q\n", ex.GetOk(), ex.GetError())

	st, err := c.GetState(ctx, &pb.DeviceRef{DeviceId: "verify1"})
	if err != nil {
		die("GetState: %v", err)
	}
	fmt.Printf("GetState        online=%v state=%v\n", st.GetOnline(), st.GetState())

	// Rule 3: an rpc this plugin never implemented answers UNIMPLEMENTED and the hub reads that as "older
	// than this feature" rather than as a fault.
	if _, err := c.ListInputs(ctx, &pb.DeviceRef{DeviceId: "verify1"}); err != nil {
		fmt.Printf("ListInputs      %v\n", err)
	}

	var beats, ticks int
	var interval uint32
	var independent bool
	timeout := time.After(7 * time.Second)
collect:
	for {
		select {
		case m, ok := <-frames:
			if !ok {
				break collect
			}
			switch {
			case m.GetRuntime() != nil:
				beats++
				interval = m.GetRuntime().GetHeartbeatIntervalMs()
				independent = m.GetRuntime().GetHeartbeatIndependent()
				if m.GetTimestampUnixMs() == 0 {
					die("a heartbeat with timestamp_unix_ms == 0 reads as 1970 — the hub would call this plugin dead")
				}
			case m.GetType() == "metronome.tick":
				ticks++
			}
		case <-timeout:
			break collect
		}
	}
	fmt.Printf("StreamEvents    heartbeats=%d interval_ms=%d independent=%v ticks=%d\n",
		beats, interval, independent, ticks)

	if _, err := c.DisposeDevice(ctx, &pb.DeviceRef{DeviceId: "verify1"}); err != nil {
		die("DisposeDevice: %v", err)
	}
	fmt.Println("DisposeDevice   ok")

	if beats == 0 || ticks == 0 {
		die("no heartbeat or no device event arrived — the stream is the half a plugin loses silently")
	}
	fmt.Println("VERIFIED")
}

func trimScheme(s string) string {
	for _, p := range []string{"http://", "https://"} {
		if len(s) > len(p) && s[:len(p)] == p {
			return s[len(p):]
		}
	}
	return s
}

func die(f string, a ...any) {
	fmt.Fprintf(os.Stderr, "verify: "+f+"\n", a...)
	os.Exit(1)
}
