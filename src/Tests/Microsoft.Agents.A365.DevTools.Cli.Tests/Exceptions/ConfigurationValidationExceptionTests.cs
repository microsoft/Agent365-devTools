// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Exceptions;

public class ConfigurationValidationExceptionTests
{
    [Fact]
    public void GuidFormatHint_IsFieldAgnosticPlainText()
    {
        var exception = new ConfigurationValidationException(
            "a365.config.json",
            new List<ValidationError>
            {
                new("serviceManagementReference", "must be a non-zero GUID, but was 'abc'. Remove it if your tenant does not require one.")
            });

        exception.MitigationSteps.Should().Contain(s => s.Contains("GUID values must use the format xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"),
            because: "a GUID validation error should show the expected GUID format");
        exception.MitigationSteps.Should().NotContain(s => s.Contains("TenantId"),
            because: "the hint covers every GUID field, so labeling it TenantId misdirects a user fixing serviceManagementReference or clientAppId");
        exception.MitigationSteps.Should().OnlyContain(s => s.All(c => c < 128),
            because: "CLI output must be plain ASCII that renders in every terminal");
    }
}
