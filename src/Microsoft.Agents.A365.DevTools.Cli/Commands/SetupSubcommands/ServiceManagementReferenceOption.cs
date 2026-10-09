// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Agents.A365.DevTools.Cli.Helpers;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Commands.SetupSubcommands;

/// <summary>
/// The --service-management-reference option shared by 'setup blueprint' and 'setup all'.
/// </summary>
internal static class ServiceManagementReferenceOption
{
    internal static Option<string?> Create() => new(
        ServiceManagementReferenceConstants.OptionName,
        description: "Sets serviceManagementReference (a GUID) on a new agent blueprint.\n" +
                     "Required by tenants that enforce it on every new application.\n" +
                     "Overrides serviceManagementReference in a365.config.json.")
    {
        // Keeps the long option name from widening the help column for every option.
        ArgumentHelpName = "guid",
    };

    /// <summary>
    /// Reads the option; returns false after logging an error when it was passed blank or is not a non-zero GUID.
    /// </summary>
    internal static bool TryGetValue(ParseResult parseResult, Option<string?> option, ILogger logger, out string? serviceManagementReference)
    {
        serviceManagementReference = null;
        if (parseResult.CommandResult.FindResultFor(option) is null)
            return true;

        var raw = parseResult.GetValueForOption(option);
        if (string.IsNullOrWhiteSpace(raw))
        {
            logger.LogError("--service-management-reference requires a value: the GUID your tenant expects in serviceManagementReference.");
            return false;
        }

        if (!ServiceManagementReferenceHelper.TryNormalize(raw, out serviceManagementReference))
        {
            logger.LogError(
                "Invalid --service-management-reference value '{Value}'. Provide a non-zero GUID (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx).",
                raw.Trim());
            return false;
        }

        return true;
    }
}
