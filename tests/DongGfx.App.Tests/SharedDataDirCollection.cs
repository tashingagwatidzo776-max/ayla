using Xunit;

namespace DongGfx.App.Tests;

// xunit runs test classes in parallel collections (see AssemblyInfo.cs), and
// three suites touch the REAL %APPDATA%\tf\data directory that the app's DI
// graph resolves eagerly: AppStartupWiringTests builds the graph (creating
// journal/logs/ticks/analytics under DataDir), while LoginPrefillSettingsTests
// and CoverageSprintTests delete/clean it in their teardown. Run the whole
// suite and those two interleave: a teardown deletes DataDir between another
// class's existence check and Directory.CreateDirectory, and the ctor throws
// DirectoryNotFoundException out of service resolution (the FxScorecardService
// red run on main, 2026-09-25). One collection serializes them; everything
// else keeps running in parallel.
[CollectionDefinition("Shared-DataDir-Directory", DisableParallelization = false)]
public sealed class SharedDataDirDirectoryCollection
{
}
