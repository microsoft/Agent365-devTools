// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Helpers;

/// <summary>
/// Helper methods for file operations including cross-platform file opening
/// </summary>
public static class FileHelper
{
    /// <summary>
    /// Opens a file in the user's default editor in a cross-platform way
    /// </summary>
    /// <param name="filePath">Absolute path to the file to open</param>
    /// <param name="logger">Logger for diagnostic messages</param>
    /// <returns>True if file was opened successfully or attempt was made, false if file doesn't exist</returns>
    public static bool TryOpenFileInDefaultEditor(string filePath, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            logger.LogError("Invalid file path: path is null or empty");
            return false;
        }

        if (!File.Exists(filePath))
        {
            logger.LogError("File not found: {FilePath}", filePath);
            return false;
        }

        try
        {
            // On Unix-like systems, respect EDITOR or VISUAL environment variables
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var editor = Environment.GetEnvironmentVariable("EDITOR")
                            ?? Environment.GetEnvironmentVariable("VISUAL");

                if (!string.IsNullOrEmpty(editor))
                {
                    logger.LogDebug("Using editor from environment: {Editor}", editor);
                    try
                    {
                        var startInfo = new ProcessStartInfo
                        {
                            FileName = editor,
                            Arguments = $"\"{filePath}\"",
                            UseShellExecute = false
                        };
                        var process = Process.Start(startInfo);
                        if (process == null)
                        {
                            logger.LogWarning("Editor '{Editor}' failed to start (process returned null). Falling back to platform default.", editor);
                            // Fall through to platform-specific default open
                        }
                        else
                        {
                            logger.LogDebug("Opened file in {Editor}", editor);
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning("Could not start editor '{Editor}': {Error}. Falling back to platform default.", editor, ex.Message);
                        // Fall through to platform-specific default open
                    }
                }
            }

            // Platform-specific default open
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // On Windows with UseShellExecute, Process.Start may return null even on success
                // when the file is opened in an existing process (e.g., Notepad, VS Code)
                Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
                logger.LogDebug("Opened file in default Windows editor");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = $"\"{filePath}\"",
                    UseShellExecute = false
                };
                var process = Process.Start(startInfo);
                if (process == null)
                {
                    logger.LogWarning("Failed to open file using macOS 'open' command (process returned null)");
                    return false;
                }
                logger.LogDebug("Opened file using macOS 'open' command");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Try xdg-open which is standard on most Linux distros
                var startInfo = new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    Arguments = $"\"{filePath}\"",
                    UseShellExecute = false
                };
                var process = Process.Start(startInfo);
                if (process == null)
                {
                    logger.LogWarning("Failed to open file using Linux 'xdg-open' command (process returned null)");
                    return false;
                }
                logger.LogInformation("Opened file using Linux 'xdg-open' command");
            }
            else
            {
                logger.LogWarning("Unsupported platform for automatic file opening");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not automatically open file: {Error}", ex.Message);
            logger.LogInformation("Please manually open the file to edit it");
            return false;
        }
    }

    /// <summary>
    /// Gets a secure cross-platform directory path for storing application data in the user's home directory.
    /// Creates the directory if it doesn't exist.
    /// </summary>
    /// <param name="subdirectory">Optional subdirectory name within the .a365 folder (e.g., "cache", "logs")</param>
    /// <returns>Absolute path to the secure directory</returns>
    /// <remarks>
    /// Directory locations by OS:
    /// - Windows: C:\Users\{username}\.a365\{subdirectory}
    /// - Linux: /home/{username}/.a365/{subdirectory}
    /// - macOS: /Users/{username}/.a365/{subdirectory}
    /// </remarks>
    public static string GetSecureCrossOsDirectory(string? subdirectory = null)
    {
        var userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var baseDir = Path.Combine(userProfilePath, ".a365");
        
        var targetDir = string.IsNullOrWhiteSpace(subdirectory) 
            ? baseDir 
            : Path.Combine(baseDir, subdirectory);
        
        Directory.CreateDirectory(targetDir);
        return targetDir;
    }
}
