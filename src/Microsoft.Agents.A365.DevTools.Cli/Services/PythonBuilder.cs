// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Exceptions;
using Microsoft.Agents.A365.DevTools.Cli.Models;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services;

/// <summary>
/// Python platform builder
/// </summary>
public class PythonBuilder : IPlatformBuilder
{
    private readonly ILogger<PythonBuilder> _logger;
    private readonly CommandExecutor _executor;
    private string ? _pythonExe;

    public PythonBuilder(ILogger<PythonBuilder> logger, CommandExecutor executor)
    {
        _logger = logger;
        _executor = executor;
    }

    public async Task<bool> ValidateEnvironmentAsync()
    {
        _logger.LogInformation("Validating Python environment...");

        _pythonExe = await PythonLocator.FindPythonExecutableAsync(_executor);
        if (string.IsNullOrWhiteSpace(_pythonExe))
        {
            _logger.LogError("Python not found. Please install Python from https://www.python.org/");
            throw new PythonLocatorException("Python executable could not be located.");
        }

        var pythonResult = await _executor.ExecuteAsync(_pythonExe, "--version", captureOutput: true);
        if (!pythonResult.Success)
        {
            _logger.LogError("Python not found. Please install Python from https://www.python.org/");
            throw new PythonLocatorException("Python executable could not be located.");
        }

        var pipResult = await _executor.ExecuteAsync(_pythonExe, "-m pip --version", captureOutput: true);
        if (!pipResult.Success)
        {
            _logger.LogError("pip not found. Please ensure pip is installed with Python.");
            throw new PythonLocatorException("Unable to locate pip.");
        }

        _logger.LogInformation("Python version: {Version}", pythonResult.StandardOutput.Trim());
        _logger.LogInformation("pip version: {Version}", pipResult.StandardOutput.Trim());
        return true;
    }

    public async Task CleanAsync(string projectDir)
    {
        _logger.LogDebug("Cleaning Python project...");

        // Remove common Python cache and build directories
        var dirsToRemove = new[] {
            "__pycache__", ".pytest_cache", "*.egg-info", "build", ".venv*", "venv",
            ".venv_test", ".venv_local", ".virtual", "env", "ENV", ".mypy_cache",
            ".coverage", "htmlcov", ".tox", "dist_temp"
        };

        foreach (var pattern in dirsToRemove)
        {
            if (pattern.Contains("*"))
            {
                var dirs = Directory.GetDirectories(projectDir, pattern, SearchOption.TopDirectoryOnly);
                foreach (var dir in dirs)
                {
                    try
                    {
                        _logger.LogDebug("Removing {Dir}...", Path.GetFileName(dir));
                        Directory.Delete(dir, recursive: true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug("Could not remove {Dir}: {Error}", Path.GetFileName(dir), ex.Message);
                    }
                }
            }
            else
            {
                var dirPath = Path.Combine(projectDir, pattern);
                if (Directory.Exists(dirPath))
                {
                    try
                    {
                        _logger.LogDebug("Removing {Dir}...", pattern);
                        Directory.Delete(dirPath, recursive: true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug("Could not remove {Dir}: {Error}", pattern, ex.Message);
                    }
                }
            }
        }

        // Remove .pyc files
        foreach (var pycFile in Directory.GetFiles(projectDir, "*.pyc", SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(pycFile);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Could not remove {File}: {Error}", Path.GetFileName(pycFile), ex.Message);
            }
        }

        // Remove additional files that shouldn't be deployed
        var filesToRemove = new[] { "uv.lock", ".coverage", "pytest.ini", "tox.ini", ".env_backup" };
        foreach (var fileName in filesToRemove)
        {
            var filePath = Path.Combine(projectDir, fileName);
            if (File.Exists(filePath))
            {
                try
                {
                    _logger.LogDebug("Removing {File}...", fileName);
                    File.Delete(filePath);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Could not remove {File}: {Error}", fileName, ex.Message);
                }
            }
        }

        await Task.CompletedTask;
    }

    public async Task<string> BuildAsync(string projectDir, string outputPath, bool verbose)
    {
        // Clean up old publish directory for fresh start
        var publishPath = Path.Combine(projectDir, outputPath);

        if (Directory.Exists(publishPath))
        {
            _logger.LogInformation("Removing old publish directory...");
            Directory.Delete(publishPath, recursive: true);
        }

        _logger.LogInformation("Building Python project...");
        // Run python -m py_compile on all .py files at the project root to catch syntax errors before packaging
        var pyFiles = Directory.GetFiles(projectDir, "*.py", SearchOption.TopDirectoryOnly);
        foreach (var pyFile in pyFiles)
        {
            var result = await _executor.ExecuteAsync(_pythonExe!, $"-m py_compile \"{pyFile}\"", projectDir, captureOutput: true);
            if (!result.Success)
            {
                _logger.LogError("Python syntax error in {File}:\n{Error}", pyFile, result.StandardError);
                throw new DeployAppPythonCompileException($"Python syntax error in {pyFile}:\n{result.StandardError}");
            }
        }

        Directory.CreateDirectory(publishPath);

        // Step 1: Copy entire project structure (excluding unwanted files)
        _logger.LogInformation("Copying project files...");
        await CopyProjectFiles(projectDir, publishPath, outputPath);

        // Step 2: Copy existing dist folder to publish directory (if it exists)
        // This ensures we never modify the source dist folder
        var sourceDist = GetDistDirectory(projectDir);
        var publishDist = Path.Combine(publishPath, "dist");
        
        if (Directory.Exists(sourceDist))
        {
            _logger.LogInformation("Copying existing dist folder from source to publish directory...");
            CopyDirectory(sourceDist, publishDist, new string[0]);
            
            var wheelCount = Directory.GetFiles(publishDist, "*.whl").Length;
            _logger.LogInformation("Copied {Count} wheel files from source dist/", wheelCount);
        }

        // Step 3: Ensure local packages exist in PUBLISH directory (not source!)
        // If no wheels exist in publish/dist, run uv build in the publish directory
        await EnsureLocalPackagesExistInPublish(publishPath, publishDist, verbose);

        // Step 4: Create requirements.txt for Azure deployment
        await CreateAzureRequirementsTxt(publishPath, verbose);

        // Step 4.5: Create .deployment file to force Oryx build
        await CreateDeploymentFile(publishPath);

        // Step 5: Copy .env.template but exclude .env (security)
        CopyEnvironmentFiles(projectDir, publishPath);

        _logger.LogInformation("Python project prepared for Azure deployment");
        _logger.LogInformation("Azure will handle dependency installation during deployment");
        
        return publishPath;
    }

    public async Task<OryxManifest> CreateManifestAsync(string projectDir, string publishPath)
    {
        _logger.LogInformation("Creating Oryx manifest for Python...");
        
        // Create runtime.txt to help Oryx detect this as a Python project
        var runtimeTxtPath = Path.Combine(publishPath, "runtime.txt");
        await File.WriteAllTextAsync(runtimeTxtPath, "python-3.11");
        _logger.LogInformation("Created runtime.txt for Python version detection");
        
        // Detect Python version
        var pythonVersion = "3.11"; // Default
        var runtimePath = Path.Combine(projectDir, "runtime.txt");
        
        if (File.Exists(runtimePath))
        {
            var runtimeContent = await File.ReadAllTextAsync(runtimePath);
            var match = System.Text.RegularExpressions.Regex.Match(runtimeContent, @"python-(\d+\.\d+)");
            if (match.Success)
            {
                pythonVersion = match.Groups[1].Value;
                _logger.LogInformation("Detected Python version from runtime.txt: {Version}", pythonVersion);
            }
        }
        else
        {
            // Try to get from current python
            _pythonExe = _pythonExe ?? await PythonLocator.FindPythonExecutableAsync(_executor);
            if (string.IsNullOrWhiteSpace(_pythonExe))
            {
                _logger.LogError("Python not found. Please install Python from https://www.python.org/");
                throw new PythonLocatorException("Python executable could not be located.");
            }
            var versionResult = await _executor.ExecuteAsync(_pythonExe, "--version", captureOutput: true);
            if (versionResult.Success)
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    versionResult.StandardOutput, 
                    @"Python (\d+\.\d+)");
                if (match.Success)
                {
                    pythonVersion = match.Groups[1].Value;
                    _logger.LogInformation("Detected Python version: {Version}", pythonVersion);
                }
            }
        }

        // Detect entry point and determine start command
        var startCommand = DetectStartCommand(projectDir, publishPath);

        return new OryxManifest
        {
            Platform = "python",
            Version = pythonVersion,
            Command = startCommand,
            BuildRequired = true
        };
    }

    private string DetectStartCommand(string projectDir, string publishPath)
    {
        // First, check for Microsoft Agent 365-specific entry points with smart content analysis
        var agentEntryPoints = new[] { "start_with_generic_host.py", "host_agent_server.py" };
        var detectedAgentEntry = DetectBestAgentEntry(publishPath, agentEntryPoints);
        if (!string.IsNullOrEmpty(detectedAgentEntry))
        {
            _logger.LogInformation("Detected Microsoft Agent 365 entry point: {File}, using command: python {File}", detectedAgentEntry, detectedAgentEntry);
            return $"python {detectedAgentEntry}";
        }

        // Check for common entry points
        var entryPoints = new[]
        {
            ("app.py", "gunicorn --bind=0.0.0.0:8000 app:app"),
            ("main.py", "python main.py"),
            ("start.py", "python start.py"),
            ("server.py", "python server.py"),
            ("run.py", "python run.py"),
            ("wsgi.py", "gunicorn --bind=0.0.0.0:8000 wsgi:application"),
            ("asgi.py", "uvicorn asgi:application --host 0.0.0.0 --port 8000")
        };

        foreach (var (file, command) in entryPoints)
        {
            if (File.Exists(Path.Combine(publishPath, file)))
            {
                _logger.LogInformation("Detected entry point: {File}, using command: {Command}", file, command);
                return command;
            }
        }

        // Check for Flask/Django/FastAPI patterns in Python files
        var pyFiles = Directory.GetFiles(publishPath, "*.py", SearchOption.TopDirectoryOnly);
        foreach (var pyFile in pyFiles)
        {
            var content = File.ReadAllText(pyFile);
            var fileName = Path.GetFileName(pyFile);
            var moduleName = Path.GetFileNameWithoutExtension(pyFile);

            if (content.Contains("Flask(") || content.Contains("from flask import"))
            {
                _logger.LogInformation("Detected Flask application in {File}", fileName);
                return $"gunicorn --bind=0.0.0.0:8000 {moduleName}:app";
            }
            
            if (content.Contains("FastAPI(") || content.Contains("from fastapi import"))
            {
                _logger.LogInformation("Detected FastAPI application in {File}", fileName);
                return $"uvicorn {moduleName}:app --host 0.0.0.0 --port 8000";
            }
            
            if (content.Contains("django"))
            {
                _logger.LogInformation("Detected Django application");
                return "gunicorn --bind=0.0.0.0:8000 wsgi:application";
            }

            // Check for common main function patterns
            if (content.Contains("if __name__ == \"__main__\":") || content.Contains("def main("))
            {
                _logger.LogInformation("Detected main function in {File}", fileName);
                return $"python {fileName}";
            }
        }

        // Default fallback - try common entry point files first
        var fallbackFiles = new[] { "app.py", "start.py", "run.py", "server.py", "main.py" };
        foreach (var file in fallbackFiles)
        {
            if (File.Exists(Path.Combine(publishPath, file)))
            {
                _logger.LogInformation("Using fallback entry point: {File}", file);
                return $"python {file}";
            }
        }

        // Last resort - use the first Python file found
        if (pyFiles.Length > 0)
        {
            var firstPyFile = Path.GetFileName(pyFiles[0]);
            _logger.LogWarning("Could not detect specific entry point. Using first Python file found: {File}", firstPyFile);
            return $"python {firstPyFile}";
        }

        // Final fallback
        _logger.LogWarning("Could not detect specific Python framework. Using generic python command.");
        return "python main.py";
    }

    private string DetectBestAgentEntry(string publishPath, string[] agentFiles)
    {
        var foundFiles = new List<(string file, int priority, bool hasMain)>();

        foreach (var file in agentFiles)
        {
            var filePath = Path.Combine(publishPath, file);
            if (File.Exists(filePath))
            {
                var content = File.ReadAllText(filePath);
                var hasMain = content.Contains("if __name__ == \"__main__\":") || content.Contains("def main(");
                var priority = CalculateAgentEntryPriority(file, content);
                foundFiles.Add((file, priority, hasMain));
                _logger.LogDebug("Found Microsoft Agent 365 entry candidate: {File} (priority: {Priority}, hasMain: {HasMain})", file, priority, hasMain);
            }
        }

        if (foundFiles.Count == 0)
            return string.Empty;

        // Sort by: 1) has main function, 2) priority score, 3) alphabetical
        var best = foundFiles
            .OrderByDescending(f => f.hasMain ? 1 : 0)
            .ThenByDescending(f => f.priority)
            .ThenBy(f => f.file)
            .First();

        _logger.LogInformation("Selected best Microsoft Agent 365 entry point: {File} (priority: {Priority}, hasMain: {HasMain})", 
            best.file, best.priority, best.hasMain);

        return best.file;
    }

    private int CalculateAgentEntryPriority(string fileName, string content)
    {
        int priority = 0;

        // Higher priority for files that seem to be primary entry points
        if (fileName.Contains("start"))
            priority += 10;

        if (fileName.Contains("main"))
            priority += 8;

        if (fileName.Contains("server"))
            priority += 6;

        // Analyze content for entry point indicators
        if (content.Contains("if __name__ == \"__main__\":"))
            priority += 15;

        if (content.Contains("def main("))
            priority += 10;

        if (content.Contains("create_and_run_host") || content.Contains("run_host"))
            priority += 5;

        if (content.Contains("AgentFrameworkAgent"))
            priority += 3;

        if (content.Contains("uvicorn") || content.Contains("run") || content.Contains("serve"))
            priority += 2;

        return priority;
    }

    private async Task<CommandResult> ExecuteWithOutputAsync(string command, string arguments, string workingDirectory, bool verbose)
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

    private async Task CopyProjectFiles(string projectDir, string publishPath, string outputPath)
    {
        var excludePatterns = new[]
        {
            outputPath, "__pycache__", ".git", ".venv*", "venv", "node_modules",
            ".vs", ".vscode", "*.pyc", ".env", ".pytest_cache", "app.zip", "uv.lock",
            ".venv_test", ".venv_local", ".virtual", "env", "ENV"  // Additional venv patterns
        };

        foreach (var item in Directory.GetFileSystemEntries(projectDir))
        {
            var itemName = Path.GetFileName(item);
            
            // Skip excluded patterns
            if (excludePatterns.Any(pattern => 
                itemName.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
                (pattern.Contains('*') && MatchesWildcard(itemName, pattern))))
            {
                continue;
            }
            
            var destPath = Path.Combine(publishPath, itemName);
            
            if (Directory.Exists(item))
            {
                CopyDirectory(item, destPath, excludePatterns);
            }
            else
            {
                File.Copy(item, destPath, overwrite: true);
            }
        }
        
        await Task.CompletedTask;
    }

    private void CopyDirectory(string sourceDir, string destDir, string[] excludePatterns)
    {
        Directory.CreateDirectory(destDir);
        
        foreach (var item in Directory.GetFileSystemEntries(sourceDir))
        {
            var itemName = Path.GetFileName(item);
            
            if (excludePatterns.Any(pattern => 
                itemName.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
                (pattern.Contains('*') && MatchesWildcard(itemName, pattern))))
            {
                continue;
            }
            
            var destPath = Path.Combine(destDir, itemName);
            
            if (Directory.Exists(item))
            {
                CopyDirectory(item, destPath, excludePatterns);
            }
            else
            {
                File.Copy(item, destPath, overwrite: true);
            }
        }
    }

    private bool MatchesWildcard(string text, string pattern)
    {
        if (pattern == "*") return true;

        // Handle *.extension patterns
        if (pattern.StartsWith("*."))
            return text.EndsWith(pattern.Substring(1), StringComparison.OrdinalIgnoreCase);

        // Handle prefix* patterns (like .venv*)
        if (pattern.EndsWith("*"))
            return text.StartsWith(pattern.Substring(0, pattern.Length - 1), StringComparison.OrdinalIgnoreCase);

        return false;
    }

    private string GetDistDirectory(string projectDir)
    {
        // First check if dist exists in project directory
        var localDist = Path.Combine(projectDir, "dist");
        if (Directory.Exists(localDist))
        {
            return localDist;
        }
        
        // Then check parent directory (common pattern)
        var parentDir = Path.GetDirectoryName(projectDir);
        if (parentDir != null)
        {
            var parentDist = Path.Combine(parentDir, "dist");
            if (Directory.Exists(parentDist))
            {
                return parentDist;
            }
        }
        
        return localDist; // Return local path even if it doesn't exist
    }

    private async Task EnsureLocalPackagesExistInPublish(string publishPath, string publishDist, bool verbose)
    {
        // Check if wheel files exist in the PUBLISH dist directory (not source!)
        if (!Directory.Exists(publishDist) || !Directory.GetFiles(publishDist, "*.whl").Any())
        {
            _logger.LogInformation("No local packages found in publish/dist, running uv build in publish directory...");
            
            // Run uv build in the PUBLISH directory (not the source directory!)
            var buildResult = await ExecuteWithOutputAsync("uv", "build", publishPath, verbose);
            if (!buildResult.Success)
            {
                _logger.LogWarning("uv build failed: {Error}. Continuing without local packages.", buildResult.StandardError);
            }
            else
            {
                var wheelCount = Directory.Exists(publishDist) ? Directory.GetFiles(publishDist, "*.whl").Length : 0;
                _logger.LogInformation("Successfully built {Count} local packages in publish directory", wheelCount);
            }
        }
        else
        {
            var wheelCount = Directory.GetFiles(publishDist, "*.whl").Length;
            _logger.LogInformation("Found {Count} existing wheel files in publish/dist", wheelCount);
        }
        
        await Task.CompletedTask;
    }

    private async Task CreateAzureRequirementsTxt(string publishPath, bool verbose)
    {
        var requirementsTxt = Path.Combine(publishPath, "requirements.txt");
        
        // Azure-native requirements.txt that mirrors local workflow
        // --pre allows installation of pre-release versions
        var content = "--find-links dist\n--pre\n-e .\n";
        
        await File.WriteAllTextAsync(requirementsTxt, content);
        _logger.LogInformation("Created requirements.txt for Azure deployment");
    }

    private async Task CreateDeploymentFile(string publishPath)
    {
        var deploymentPath = Path.Combine(publishPath, ".deployment");
        var content = "[config]\nSCM_DO_BUILD_DURING_DEPLOYMENT=true\n";
        
        await File.WriteAllTextAsync(deploymentPath, content);
        _logger.LogInformation("Created .deployment file to force Oryx build");
    }

    private void CopyEnvironmentFiles(string projectDir, string publishPath)
    {
        // Copy .env.template if it exists (for documentation)
        var envTemplatePath = Path.Combine(projectDir, ".env.template");
        if (File.Exists(envTemplatePath))
        {
            File.Copy(envTemplatePath, Path.Combine(publishPath, ".env.template"), overwrite: true);
            _logger.LogInformation("Copied .env.template file");
        }
        
        // Exclude .env file from deployment for security
        _logger.LogInformation("Excluded .env file from deployment package for security");
        _logger.LogInformation("Environment variables should be set as Azure App Settings");
    }

    /// <summary>
    /// Converts .env file to Azure App Settings using a single az webapp config appsettings set command
    /// </summary>
    public async Task<bool> ConvertEnvToAzureAppSettingsAsync(string projectDir, string resourceGroup, string webAppName, bool verbose)
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

    /// <summary>
    /// Sets the startup command for the Azure Web App to run the detected Python entry point
    /// </summary>
    public async Task<bool> SetStartupCommandAsync(string projectDir, string resourceGroup, string webAppName, bool verbose)
    {
        var publishPath = Path.Combine(projectDir, "publish");
        var startCommand = DetectStartCommand(projectDir, publishPath);
        
        _logger.LogInformation("Setting Azure Web App startup command: {Command}", startCommand);
        
        var azCommand = $"webapp config set -g {resourceGroup} -n {webAppName} --startup-file \"{startCommand}\"";
        
        var result = await ExecuteWithOutputAsync("az", azCommand, projectDir, verbose);
        if (result.Success)
        {
            _logger.LogInformation("Successfully set startup command for Azure Web App");
            return true;
        }
        else
        {
            _logger.LogError("Failed to set startup command: {Error}", result.StandardError);
            return false;
        }
    }
}
