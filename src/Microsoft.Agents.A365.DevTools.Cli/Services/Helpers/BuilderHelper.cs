// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Constants;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Helpers
{
    public class BuilderHelper
    {
        private readonly ILogger _logger;
        private readonly CommandExecutor _executor;

        public BuilderHelper(ILogger logger, CommandExecutor executor)
        {
            _logger = logger;
            _executor = executor;
        }

        /// <summary>
        /// Converts .env file to Azure App Settings using a single az webapp config appsettings set command
        /// </summary>
        public async Task<bool> ConvertEnvToAzureAppSettingsIfExistsAsync(
            string projectDir,
            string resourceGroup,
            string webAppName,
            bool verbose)
        {
            var envFilePath = Path.Combine(projectDir, ".env");
            if (!File.Exists(envFilePath))
            {
                _logger.LogInformation("No .env file found to convert to Azure App Settings");
                return true; // Not an error, just no env file
            }

            _logger.LogInformation("Converting .env file to Azure App Settings...");

            var envSettings = new List<string>();
            var lines = await File.ReadAllLinesAsync(envFilePath);

            foreach (var line in lines)
            {
                // Skip empty lines and comments
                if (string.IsNullOrWhiteSpace(line) || line.Trim().StartsWith("#"))
                    continue;

                // Parse KEY=VALUE format
                var equalIndex = line.IndexOf('=');
                if (equalIndex > 0 && equalIndex < line.Length - 1)
                {
                    var key = line.Substring(0, equalIndex).Trim();
                    var value = line.Substring(equalIndex + 1).Trim();

                    // Skip BEARER_TOKEN - this is for local development only, not production
                    if (key.Equals(AuthenticationConstants.BearerTokenEnvironmentVariable, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogDebug("Skipping BEARER_TOKEN (local development only, not deployed to Azure)");
                        continue;
                    }

                    // Remove quotes if present
                    if ((value.StartsWith("\"") && value.EndsWith("\"")) ||
                        (value.StartsWith("'") && value.EndsWith("'")))
                    {
                        value = value.Substring(1, value.Length - 2);
                    }

                    envSettings.Add($"{key}={value}");
                    _logger.LogDebug("Found environment variable: {Key}", key);
                }
            }

            if (envSettings.Count == 0)
            {
                _logger.LogInformation("No valid environment variables found in .env file");
                return true;
            }

            // Build single az webapp config appsettings set command with all variables
            var settingsArgs = string.Join(" ", envSettings.Select(setting => $"\"{setting}\""));
            var azCommand = $"webapp config appsettings set -g {resourceGroup} -n {webAppName} --settings {settingsArgs}";

            _logger.LogInformation("Setting {Count} environment variables as Azure App Settings...", envSettings.Count);

            var result = await ExecuteWithOutputAsync("az", azCommand, projectDir, verbose);
            if (result.Success)
            {
                _logger.LogInformation("Successfully converted {Count} environment variables to Azure App Settings", envSettings.Count);
                return true;
            }
            else
            {
                _logger.LogError("Failed to set Azure App Settings: {Error}", result.StandardError);
                return false;
            }
        }

        public async Task<CommandResult> ExecuteWithOutputAsync(string command, string arguments, string workingDirectory, bool verbose)
        {
            var result = await _executor.ExecuteAsync(command, arguments, workingDirectory);

            if (verbose || !result.Success)
            {
                if (!string.IsNullOrWhiteSpace(result.StandardOutput))
                {
                    _logger.LogInformation("Output:\n{Output}", result.StandardOutput);
                }
                if (!string.IsNullOrWhiteSpace(result.StandardError))
                {
                    _logger.LogWarning("Warnings/Errors:\n{Error}", result.StandardError);
                }
            }

            return result;
        }
    }
}
