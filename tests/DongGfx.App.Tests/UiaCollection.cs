using Xunit;

namespace DongGfx.App.Tests;

// The UIA suites each launch and drive a real WPF instance and write to the
// machine-wide clipboard. xunit runs test CLASSES in parallel collections, so
// without this the boot smoke and the journal-copy smoke could boot two
// DongGfx instances at once — and the clipboard is one resource, not
// per-process, so their assertions would interleave and flake. One collection
// runs its members serially; both suites still vacuous-pass when a developer's
// own instance is already running.
[CollectionDefinition("Uia")]
public sealed class UiaCollection
{
}
