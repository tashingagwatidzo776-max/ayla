using Xunit;

// The growth E2E tests each run a real fake WebSocket server plus runner
// timers; under in-assembly parallelism they contend for CPU and sockets
// and intermittently blow their (generous) wait deadlines. The suites are
// wait-based, not timing-based, so running this assembly serially trades a
// little wall time for determinism. Core tests stay parallel — the port
// race there is fixed at the source instead.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
