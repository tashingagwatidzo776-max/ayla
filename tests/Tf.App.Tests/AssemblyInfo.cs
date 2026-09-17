using Xunit;

// Test classes form separate xunit collections and run in parallel: every E2E
// scenario builds its own fake broker servers on race-free loopback ports
// (TestHttpListenerFactory), its own TradeStore/Journal under a fresh temp
// directory, and its own hub/runner state, so nothing is shared across
// collections. Suites are wait-based, not timing-based, and each fake-server
// client shortens the settlement poll so the suites stay fast under contention.
// The Core assembly stays parallel too — its port race is fixed at the source.
// CollectionBehavior attribute omitted: xunit v2's default is collection-level
// parallelism, which is exactly what we want — with one carve-out: the three
// gate/unlock classes below share the STATIC ManualRealMoneyGate latch, so
// they are pinned into the "ManualRealMoneyGate" collection and run
// sequentially. A parallel Reset() from another class once flipped the latch
// mid-test and failed the release drill on the v0.0.1 tag itself.
