// verify is a tiny hub-shaped client: it connects to a running Hello World, walks the calls a hub makes,
// and prints what came back.
//
// It exists because of the thing that is easy to get wrong about a proof — **a plugin that has only been
// run on the machine that built it has not been shown to run on a hub.** A hub is arm64 Linux on an
// appliance and amd64 Linux in the cloud, and neither is anybody's laptop. `verify.sh` runs the
// cross-compiled binary inside a Linux container of the right architecture and points this at it.
//
//	go run ./cmd/verify http://127.0.0.1:19998
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

	pb "example.com/helloworld/gen/maestro"
)

// What the greeting is set to on the way in, so that finding it again on the way out means the value
// crossed CreateDevice, was kept, and came back through GetState. A config round trip is the one thing a
// hello world can prove that a bare "it started" cannot.
const greeting = "Hello from the verifier."

func main() {
	// `--expect-type <id>` is the assertion that a `verify.sh` without it could not make, and the bug it
	// exists for is written up in that script: a build-context cache served the *other* publisher
	// identity's binary, every call answered, and the run printed VERIFIED. A verifier that never says
	// which artefact it verified cannot tell you it verified the wrong one.
	target := "http://127.0.0.1:19998"
	expectType := ""
	args := os.Args[1:]
	for len(args) > 0 {
		if args[0] == "--expect-type" && len(args) > 1 {
			expectType, args = args[1], args[2:]
			continue
		}
		target, args = args[0], args[1:]
	}

	// A hub speaks cleartext h2c on loopback. `insecure.NewCredentials()` is the client half of that and
	// is not a shortcut taken for a sample: `GrpcChannel.ForAddress("http://…")` is exactly this.
	conn, err := grpc.NewClient(trimScheme(target), grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		die("dial: %v", err)
	}
	defer conn.Close()
	c := pb.NewDriverClient(conn)

	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	defer cancel()

	// A hub retries Describe while a driver starts. Do the same, so this does not race a container that is
	// still coming up — and poll rather than sleeping a guessed amount, so it stretches under load
	// instead of going red.
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
	fmt.Printf("Describe        type_id=%s protocol_version=%d min_hub=%v caps=%v traits=%v\n",
		d.GetTypeId(), d.GetProtocolVersion(), d.MinHubProtocol, d.GetCapabilities(), d.GetTraits())
	if expectType != "" && d.GetTypeId() != expectType {
		die("this is not the binary that was meant to be verified: it serves device type %q and the "+
			"archive was built for %q", d.GetTypeId(), expectType)
	}

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
		DeviceId: "verify1", Name: "Verify", Config: map[string]string{"greeting": greeting},
	})
	if err != nil {
		die("CreateDevice: %v", err)
	}
	fmt.Printf("CreateDevice    ok=%v error=%q\n", created.GetOk(), created.GetError())
	if !created.GetOk() {
		die("the device refused to be created")
	}

	ex, err := c.ExecuteCommand(ctx, &pb.ExecuteCommandRequest{DeviceId: "verify1", CommandId: "power.on"})
	if err != nil {
		die("ExecuteCommand: %v", err)
	}
	fmt.Printf("ExecuteCommand  power.on ok=%v result=%v\n", ex.GetOk(), ex.GetResult())

	// A refusal the *device* made, which is a different answer from a driver that failed and has to look
	// different on the wire: `ok:false` with a sentence, not a gRPC status.
	no, err := c.ExecuteCommand(ctx, &pb.ExecuteCommandRequest{DeviceId: "verify1", CommandId: "power.quack"})
	if err != nil {
		die("a refusal came back as a gRPC error, which a hub reads as the driver having failed: %v", err)
	}
	fmt.Printf("ExecuteCommand  power.quack ok=%v error=%q\n", no.GetOk(), no.GetError())

	st, err := c.GetState(ctx, &pb.DeviceRef{DeviceId: "verify1"})
	if err != nil {
		die("GetState: %v", err)
	}
	fmt.Printf("GetState        online=%v state=%v\n", st.GetOnline(), st.GetState())
	if st.GetState()["greeting"] != greeting {
		die("the greeting did not survive the round trip: sent %q, got %q", greeting, st.GetState()["greeting"])
	}

	// Rule 3: an rpc this plugin never implemented answers UNIMPLEMENTED, and a hub reads that as "older
	// than this feature" rather than as a fault.
	if _, err := c.ListInputs(ctx, &pb.DeviceRef{DeviceId: "verify1"}); err != nil {
		fmt.Printf("ListInputs      %v\n", err)
	}

	// Wait **by condition** rather than for a guessed duration: stop as soon as a beat and a hello have
	// both arrived, and only give up on the ceiling. A fixed sleep here would be a race under load and a
	// waste of wall clock when there is none.
	var beats, hellos int
	var interval uint32
	var independent bool
	var lastSaid string
	ceiling := time.After(45 * time.Second)
collect:
	for beats == 0 || hellos < 2 {
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
					die("a heartbeat with timestamp_unix_ms == 0 reads as 1970 — a hub would call this plugin dead while it beats")
				}
			case m.GetType() == "hello.said":
				hellos++
				lastSaid = m.GetData()["greeting"]
			}
		case <-ceiling:
			break collect
		}
	}
	fmt.Printf("StreamEvents    heartbeats=%d interval_ms=%d independent=%v hellos=%d said=%q\n",
		beats, interval, independent, hellos, lastSaid)

	if _, err := c.DisposeDevice(ctx, &pb.DeviceRef{DeviceId: "verify1"}); err != nil {
		die("DisposeDevice: %v", err)
	}
	fmt.Println("DisposeDevice   ok")

	if beats == 0 {
		die("no heartbeat arrived — a hub would report this plugin stopped")
	}
	// Two, not one: the first is the command's echo and the second can only have come from the timer, so
	// requiring both is what separates "the call worked" from "the process is alive and being read".
	if hellos < 2 {
		die("only %d hello(s) arrived; the second one is the timer's and is the whole observable", hellos)
	}
	if lastSaid != greeting {
		die("the duck said %q, which is not what it was configured to say", lastSaid)
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
