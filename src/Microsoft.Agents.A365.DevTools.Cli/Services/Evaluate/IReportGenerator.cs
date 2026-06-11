// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.A365.DevTools.Cli.Models.Evaluate;

namespace Microsoft.Agents.A365.DevTools.Cli.Services.Evaluate;

/// <summary>
/// Generates evaluation reports (JSON and HTML) from a <see cref="SchemaEvalResult"/>.
/// This is Step 5 of the evaluation pipeline: report generation and browser launch.
/// </summary>
public interface IReportGenerator
{
    /// <summary>
    /// Generates JSON and HTML reports in the specified output directory.
    /// </summary>
    /// <param name="result">The evaluation result to render.</param>
    /// <param name="outputDir">Directory where report files will be written.</param>
    /// <param name="openInBrowser">Whether to open the HTML report in the default browser.</param>
    Task GenerateAsync(SchemaEvalResult result, string outputDir, bool openInBrowser = true);
}
