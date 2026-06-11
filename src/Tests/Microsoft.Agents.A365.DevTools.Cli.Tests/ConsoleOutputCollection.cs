// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests;

/// <summary>
/// Serializes test classes that redirect <c>Console.Out</c> via <c>Console.SetOut</c> to
/// capture output. Without DisableParallelization they ran concurrently with other collections,
/// so a test invoking code that writes to the console could bleed into a capturing test's
/// <c>StringWriter</c> (intermittent failures). Runs this collection in isolation.
/// </summary>
[CollectionDefinition("ConsoleOutput", DisableParallelization = true)]
public class ConsoleOutputCollection { }
