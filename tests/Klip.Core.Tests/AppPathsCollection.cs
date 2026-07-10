using Xunit;

namespace Klip.Core.Tests;

/// <summary>
/// Tests that mutate AppPaths.Root (a global static) can't run in parallel with
/// each other. This collection serializes them.
/// </summary>
[CollectionDefinition("AppPaths", DisableParallelization = true)]
public sealed class AppPathsCollection;
