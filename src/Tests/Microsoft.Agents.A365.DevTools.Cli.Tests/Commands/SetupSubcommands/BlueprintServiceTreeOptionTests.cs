// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Commands.SetupSubcommands;

/// <summary>
/// Regression tests for ServiceTree support on blueprint creation.
/// </summary>
public class BlueprintServiceTreeOptionTests
{
    [Fact]
    public void BlueprintCreationOptions_DefaultsServiceTreeIdToNull()
    {
        var options = new BlueprintCreationOptions();

        options.ServiceTreeId.Should().BeNull(
            because: "tenants that do not enforce ServiceTree registration must keep the existing " +
                     "manifest shape, so serviceManagementReference is only sent when requested");
    }

    [Fact]
    public void BlueprintCreationOptions_PreservesServiceTreeId()
    {
        var options = new BlueprintCreationOptions(ServiceTreeId: "00000000-0000-0000-0000-000000000000");

        options.ServiceTreeId.Should().Be("00000000-0000-0000-0000-000000000000",
            because: "the value is written to the blueprint's serviceManagementReference, which Entra " +
                     "requires in tenants that enforce ServiceTree registration");
    }

    [Fact]
    public void BlueprintCreationOptions_ServiceTreeIdIsIndependentOfDeferConsent()
    {
        var options = new BlueprintCreationOptions(DeferConsent: true, ServiceTreeId: "svc-tree-1");

        options.DeferConsent.Should().BeTrue();
        options.ServiceTreeId.Should().Be("svc-tree-1",
            because: "setup all defers consent and must still be able to supply a ServiceTree ID");
    }
}
