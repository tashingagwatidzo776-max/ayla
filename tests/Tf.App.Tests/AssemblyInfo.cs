using Xunit;

// Test classes form separate xunit collections and run in parallel: every E2E
// scenario builds its own fake broker servers on race-free loopback ports
// (TestHttpListenerFactory), its own TradeStore/Journal under a fresh temp
// directory, and its own hub/runner state, so nothing is shared across
// collections. Suites are wait-based, not timing-based, and each fake-server
// client shortens the settlement poll so the suites stay fast under contention.
// The Core assembly stays parallel too — its port race is fixed at the source.
// CollectionBehavior attribute omitted: xunit v2's default is collection-level
// parallelism, which is exactly what we want. Nothing is shared across
// collections — including unlock state: ManualRealMoneyGate is instance-
// scoped (DI singleton in the app, per-test-hub in tests), so no class can
// race another's latch. (When the gate was STATIC, a parallel Reset() once
// flipped it mid-test and failed the release drill on the v0.0.1 tag.)
