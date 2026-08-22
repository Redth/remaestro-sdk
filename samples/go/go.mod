// A plugin is not part of any of our solutions and does not build with them. It is a module of its own,
// exactly as a stranger's would be, and it depends on nothing of ours but the generated stubs.
module example.com/metronome

go 1.24.0

require (
	google.golang.org/grpc v1.76.0
	google.golang.org/protobuf v1.36.10
)

require (
	golang.org/x/net v0.42.0 // indirect
	golang.org/x/sys v0.34.0 // indirect
	golang.org/x/text v0.27.0 // indirect
	google.golang.org/genproto/googleapis/rpc v0.0.0-20250804133106-a7a43d27e69b // indirect
)
