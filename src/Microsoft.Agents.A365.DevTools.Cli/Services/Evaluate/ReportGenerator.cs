// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;
using Microsoft.Extensions.Logging;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;

/// <summary>
/// Handles Step 5 of the evaluation pipeline: generates JSON and HTML reports
/// from a <see cref="SchemaEvalResult"/>, then opens the HTML report in the default browser.
/// </summary>
internal sealed partial class ReportGenerator : IReportGenerator
{
    private const string TemplatePlaceholder = "{{REPORT_DATA}}";
    private const string EmbeddedResourceName = "Microsoft.Agents.A365.DevTools.Cli.Templates.SchemaEvalReport.html";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly ILogger<ReportGenerator> _logger;

    public ReportGenerator(ILogger<ReportGenerator> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task GenerateAsync(SchemaEvalResult result, string outputDir, bool openInBrowser = true)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDir);

        Directory.CreateDirectory(outputDir);

        string safeServerName = SanitizeFileName(result.ServerName);

        // Step 1: Write JSON report
        string jsonPath = Path.Combine(outputDir, $"{safeServerName}_eval_report.json");
        string jsonContent = JsonSerializer.Serialize(result, s_jsonOptions);
        await File.WriteAllTextAsync(jsonPath, jsonContent).ConfigureAwait(false);
        _logger.LogInformation("      JSON: {JsonPath}", jsonPath);

        // Step 2: Build EvalReportData
        var reportData = new EvalReportData
        {
            Result = result,
            ImpactMap = IssueTaxonomy.GetImpactMap(),
            MaturityLadder = MaturityCalculator.GetMaturityLadder(result.Maturity.Level),
        };

        // Step 3: Read HTML template from embedded resource
        string template = await ReadEmbeddedTemplateAsync().ConfigureAwait(false);

        // Step 4: Inject report data into template
        string reportDataJson = JsonSerializer.Serialize(reportData, s_jsonOptions);
        string htmlContent = template.Replace(TemplatePlaceholder, reportDataJson, StringComparison.Ordinal);

        // Step 5: Write HTML report
        string htmlPath = Path.Combine(outputDir, $"{safeServerName}_eval_report.html");
        await File.WriteAllTextAsync(htmlPath, htmlContent).ConfigureAwait(false);
        _logger.LogInformation("      HTML: {HtmlPath}", htmlPath);

        // Step 6: Open HTML report in default browser
        if (openInBrowser)
        {
            OpenInBrowser(htmlPath);
        }
    }

    /// <summary>
    /// Reads the HTML template from the embedded resource.
    /// </summary>
    private static async Task<string> ReadEmbeddedTemplateAsync()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName);

        if (stream is null)
        {
            throw new InvalidOperationException(
                $"Embedded resource '{EmbeddedResourceName}' not found. Ensure the HTML template is included as an EmbeddedResource in the project.");
        }

        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the HTML file in the default browser, using the appropriate command
    /// for the current operating system.
    /// </summary>
    private void OpenInBrowser(string htmlPath)
    {
        try
        {
            ProcessStartInfo startInfo;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                startInfo = new ProcessStartInfo(htmlPath) { UseShellExecute = true };
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                startInfo = new ProcessStartInfo("open", htmlPath);
            }
            else
            {
                startInfo = new ProcessStartInfo("xdg-open", htmlPath);
            }

            using var process = Process.Start(startInfo);
            _logger.LogInformation("      Opened HTML report in default browser");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open HTML report in browser. Please open manually: {HtmlPath}", htmlPath);
        }
    }

    /// <summary>
    /// Sanitizes a server name for use as a filename by replacing non-alphanumeric
    /// characters (except hyphens) with underscores.
    /// </summary>
    internal static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "server";
        }

        return FileNameSanitizer().Replace(name, "_");
    }

    [GeneratedRegex(@"[^a-zA-Z0-9\-]", RegexOptions.Compiled)]
    private static partial Regex FileNameSanitizer();
}
