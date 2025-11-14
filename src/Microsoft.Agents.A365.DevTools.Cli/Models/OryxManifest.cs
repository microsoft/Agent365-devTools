// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Agents.A365.DevTools.Cli.Models;

/// <summary>
/// Represents an Oryx manifest for Azure App Service deployment
/// </summary>
public class OryxManifest
{
    public string Platform { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public bool BuildRequired { get; set; } = true;

    /// <summary>
    /// Write the manifest to a file in TOML format
    /// </summary>
    public async Task WriteToFileAsync(string filePath)
    {
        var buildSection = BuildRequired ? $@"[build]
platform = ""{Platform}""
version = ""{Version}""
build-command = ""pip install -r requirements.txt""

" : "";

        var content = $@"{buildSection}[run]
command = ""{Command}""
";
        await File.WriteAllTextAsync(filePath, content);
    }
}
