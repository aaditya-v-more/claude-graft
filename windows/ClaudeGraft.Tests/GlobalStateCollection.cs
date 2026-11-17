using Xunit;

namespace ClaudeGraft.Tests;

/// Tests that redirect the static GraftPaths overrides share process state, so
/// they must not run in parallel with one another. xUnit runs every class in one
/// collection sequentially; classes touching those overrides join this one.
[CollectionDefinition("GlobalState")]
public sealed class GlobalStateCollection { }
