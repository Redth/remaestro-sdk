using Remaestro.Drivers.Http;
using Remaestro.Sdk;

// The whole driver process: hand the driver to the SDK host, which serves the gRPC contract.
await DriverHost.RunAsync(new HttpDriver(), args);
