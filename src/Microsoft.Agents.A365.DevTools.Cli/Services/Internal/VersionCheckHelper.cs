// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Internal;

/// <summary>
/// Shared helpers used by both <see cref="Services.VersionCheckService"/> and
/// <see cref="Services.NoticeService"/>. Internal to the assembly.
/// </summary>
internal static class VersionCheckHelper
{
    private const string PackageId = "Microsoft.Agents.A365.DevTools.Cli";

    /// <summary>
    /// Returns true if the current process is running inside a known CI/CD environment.
    /// Both version and notice checks are skipped in CI to avoid unnecessary network calls.
    /// </summary>
    internal static bool IsRunningInCiCd()
    {
        var ciEnvVars = new[]
        {
            "CI",                    // Generic CI indicator
            "TF_BUILD",              // Azure DevOps
            "GITHUB_ACTIONS",        // GitHub Actions
            "JENKINS_HOME",          // Jenkins
            "GITLAB_CI",             // GitLab CI
            "CIRCLECI",              // CircleCI
            "TRAVIS",                // Travis CI
            "TEAMCITY_VERSION",      // TeamCity
            "BUILDKITE",             // Buildkite
            "CODEBUILD_BUILD_ID"     // AWS CodeBuild
        };

        return ciEnvVars.Any(envVar => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVar)));
    }

    /// <summary>
    /// Parses a semantic version string into a comparable <see cref="Version"/>.
    /// Handles formats such as "1.1.52", "1.1.0-preview.50", and "1.1.52-preview".
    /// Throws <see cref="FormatException"/> if parsing fails.
    /// </summary>
    internal static Version ParseVersion(string versionString)
    {
        var parsed = TryParseVersion(versionString);
        if (parsed == null)
            throw new FormatException($"Invalid version format: {versionString}");
        return parsed;
    }

    /// <summary>
    /// Tries to parse a semantic version string. Returns null on failure.
    ///
    /// Supported formats:
    /// - "1.1.52-preview" (iteration in base version number)
    /// - "1.1.0-preview.50" (preview number is a separate segment)
    /// </summary>
    internal static Version? TryParseVersion(string versionString)
    {
        try
        {
            // Remove build metadata (+...)
            var cleanVersion = versionString.Split('+')[0];

            if (cleanVersion.Contains('-'))
            {
                var parts = cleanVersion.Split('-');
                var baseVersion = parts[0]; // e.g., "1.1.52" or "1.1.0"

                if (parts.Length > 1)
                {
                    var previewPart = parts[1]; // e.g., "preview" or "preview.50"

                    // Format: "1.1.0-preview.50" — append preview number as revision
                    if (previewPart.StartsWith("preview.") && previewPart.Length > 8)
                    {
                        var previewNumber = previewPart.Substring(8);
                        cleanVersion = int.TryParse(previewNumber, out var preview)
                            ? $"{baseVersion}.{preview}"
                            : baseVersion;
                    }
                    else
                    {
                        // Format: "1.1.52-preview" — iteration is already in the base number
                        cleanVersion = baseVersion;
                    }
                }
                else
                {
                    cleanVersion = baseVersion;
                }
            }

            // Ensure at least 3 components for the Version constructor
            var versionParts = cleanVersion.Split('.');
            var componentsNeeded = 3 - versionParts.Length;
            for (var i = 0; i < componentsNeeded; i++)
                cleanVersion += ".0";

            return new Version(cleanVersion);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the appropriate <c>dotnet tool update</c> command for the given version string.
    /// Appends <c>--prerelease</c> when the version is a preview build.
    /// </summary>
    internal static string GetUpdateCommand(string version)
    {
        var baseCommand = $"dotnet tool update -g {PackageId}";
        return version.Contains("preview", StringComparison.OrdinalIgnoreCase)
            ? $"{baseCommand} --prerelease"
            : baseCommand;
    }
}
