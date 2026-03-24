// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests;

/// <summary>
/// Serializes test classes that share the process-level AzCliHelper token cache.
/// Without serialization, a constructor calling ResetAzCliTokenCacheForTesting() in one
/// class can clear tokens that another class just warmed, causing real az CLI subprocesses
/// to be spawned and tests to fail or run slowly.
/// </summary>
[CollectionDefinition("AzCliTokenCache", DisableParallelization = true)]
public class AzCliTokenCacheCollection { }
