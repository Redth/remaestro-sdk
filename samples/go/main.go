// The Example Metronome — a reMaestro plugin in Go.
//
// Nothing here imports anything of reMaestro's. The only reMaestro artefact in this module is
// `gen/maestro/`, which `package.sh` produces from `../../proto/driver.proto` with stock `protoc` and
// which is not committed.
//
// Run it on your laptop:
//
//	REMAESTRO_DRIVER_URL=http://127.0.0.1:5199 go run .
package main

import (
	"log"
	"net"
	"net/url"
	"os"
	"strings"

	"google.golang.org/grpc"

	"example.com/metronome/gen/maestro"
	"example.com/metronome/metronome"
)

func main() {
	// The hub hands the address on two variables holding one value. `REMAESTRO_DRIVER_URL` is the one a
	// plugin author can guess; `ASPNETCORE_URLS` is the one every driver in the field already reads and is
	// still set. Read the neutral one first and fall back, so this works on a hub older than that name.
	raw := firstSet("REMAESTRO_DRIVER_URL", "ASPNETCORE_URLS")
	if raw == "" {
		log.Fatal("neither REMAESTRO_DRIVER_URL nor ASPNETCORE_URLS is set — the hub sets both; " +
			"pass one yourself to run this by hand")
	}

	addr, err := listenAddress(raw)
	if err != nil {
		log.Fatalf("could not read %q as an address: %v", raw, err)
	}

	lis, err := net.Listen("tcp", addr)
	if err != nil {
		// The hub picked this port by binding it, closing it, and handing us the number. Losing the race
		// is possible and the honest thing to do is die loudly: the hub's own guard notices that we exited
		// while something answered on the address and refuses to take that descriptor as ours.
		log.Fatalf("could not listen on %s: %v", addr, err)
	}

	// Cleartext h2c. `grpc.NewServer` with no credentials serves HTTP/2 with prior knowledge, which is
	// what `GrpcChannel.ForAddress("http://…")` on the hub speaks. Adding TLS here would break it.
	srv := grpc.NewServer()
	maestro.RegisterDriverServer(srv, metronome.NewDriver())

	log.Printf("example-metronome listening on %s (from %s)", addr, raw)

	// Nothing below is a graceful-shutdown path, and that is not an oversight — see README §"The hub does
	// not ask you to stop". The hub ends a driver with SIGKILL. Anything this process wants to survive has
	// to be durable at the moment it is true, not at the moment the process ends.
	if err := srv.Serve(lis); err != nil {
		log.Fatalf("serve: %v", err)
	}
}

func firstSet(names ...string) string {
	for _, n := range names {
		if v := strings.TrimSpace(os.Getenv(n)); v != "" {
			return v
		}
	}
	return ""
}

// The variables carry a **URL** — `http://127.0.0.1:53412` — and not a `host:port`, because the name they
// were modelled on is ASP.NET Core's. `net.Listen` wants the second. Semicolon-separated lists are legal in
// `ASPNETCORE_URLS` and the hub never writes one; the first entry is taken if a person does.
func listenAddress(raw string) (string, error) {
	first := strings.TrimSpace(strings.Split(raw, ";")[0])
	if !strings.Contains(first, "//") {
		return first, nil // already a host:port
	}
	u, err := url.Parse(first)
	if err != nil {
		return "", err
	}
	host := u.Hostname()
	if host == "" || host == "*" || host == "+" {
		host = "127.0.0.1"
	}
	port := u.Port()
	if port == "" {
		return "", errNoPort
	}
	return net.JoinHostPort(host, port), nil
}

var errNoPort = &addressError{"no port in the address the hub gave us"}

type addressError struct{ s string }

func (e *addressError) Error() string { return e.s }
