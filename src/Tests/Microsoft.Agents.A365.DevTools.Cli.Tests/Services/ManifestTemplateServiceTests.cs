// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.A365.DevTools.Cli.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Microsoft.Agents.A365.DevTools.Cli.Tests.Services;

/// <summary>
/// Unit tests for ManifestTemplateService - embedded resource extraction and manifest customization.
/// </summary>
public class ManifestTemplateServiceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly ManifestTemplateService _service;
    private readonly ILogger<ManifestTemplateService> _logger;

    public ManifestTemplateServiceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"manifest-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
        _logger = Substitute.For<ILogger<ManifestTemplateService>>();
        _service = new ManifestTemplateService(_logger);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenLoggerIsNull()
    {
        // Act & Assert
        var act = () => new ManifestTemplateService(null!);
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("logger");
    }

    #endregion

    #region ValidateEmbeddedResources Tests

    [Fact]
    public void ValidateEmbeddedResources_ReturnsTrue_WhenAllResourcesPresent()
    {
        // Act
        var result = _service.ValidateEmbeddedResources();

        // Assert
        result.Should().BeTrue();
    }

    #endregion

    #region ExtractTemplates Tests

    [Fact]
    public void ExtractTemplates_CreatesDirectory_WhenDirectoryDoesNotExist()
    {
        // Arrange
        var newDirectory = Path.Combine(_testDirectory, "new-dir");
        newDirectory.Should().NotBeNull();
        Directory.Exists(newDirectory).Should().BeFalse();

        // Act
        var result = _service.ExtractTemplates(newDirectory);

        // Assert
        result.Should().BeTrue();
        Directory.Exists(newDirectory).Should().BeTrue();
    }

    [Fact]
    public void ExtractTemplates_ExtractsAllFiles_Successfully()
    {
        // Act
        var result = _service.ExtractTemplates(_testDirectory);

        // Assert
        result.Should().BeTrue();
        File.Exists(Path.Combine(_testDirectory, "manifest.json")).Should().BeTrue();
        File.Exists(Path.Combine(_testDirectory, "agenticUserTemplateManifest.json")).Should().BeTrue();
        File.Exists(Path.Combine(_testDirectory, "color.png")).Should().BeTrue();
        File.Exists(Path.Combine(_testDirectory, "outline.png")).Should().BeTrue();
    }

    [Fact]
    public void ExtractTemplates_ExtractsValidJson_InManifestFile()
    {
        // Act
        _service.ExtractTemplates(_testDirectory);

        // Assert
        var manifestPath = Path.Combine(_testDirectory, "manifest.json");
        var content = File.ReadAllText(manifestPath);
        var act = () => JsonDocument.Parse(content);
        act.Should().NotThrow();
    }

    [Fact]
    public void ExtractTemplates_ExtractsValidJson_InAgenticUserTemplateManifest()
    {
        // Act
        _service.ExtractTemplates(_testDirectory);

        // Assert
        var templatePath = Path.Combine(_testDirectory, "agenticUserTemplateManifest.json");
        var content = File.ReadAllText(templatePath);
        var act = () => JsonDocument.Parse(content);
        act.Should().NotThrow();
    }

    [Fact]
    public void ExtractTemplates_ExtractsPngFiles_WithNonZeroSize()
    {
        // Act
        _service.ExtractTemplates(_testDirectory);

        // Assert
        var colorPath = Path.Combine(_testDirectory, "color.png");
        var outlinePath = Path.Combine(_testDirectory, "outline.png");
        new FileInfo(colorPath).Length.Should().BeGreaterThan(0);
        new FileInfo(outlinePath).Length.Should().BeGreaterThan(0);
    }

    #endregion

    #region UpdateManifestIdentifiersAsync Tests

    [Fact]
    public async Task UpdateManifestIdentifiersAsync_ReturnsTrue_WhenFilesUpdatedSuccessfully()
    {
        // Arrange
        _service.ExtractTemplates(_testDirectory);
        var blueprintId = "test-blueprint-id-123";

        // Act
        var result = await _service.UpdateManifestIdentifiersAsync(_testDirectory, blueprintId);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateManifestIdentifiersAsync_UpdatesTopLevelId_InManifest()
    {
        // Arrange
        _service.ExtractTemplates(_testDirectory);
        var blueprintId = "new-blueprint-id";

        // Act
        await _service.UpdateManifestIdentifiersAsync(_testDirectory, blueprintId);

        // Assert
        var manifestPath = Path.Combine(_testDirectory, "manifest.json");
        var content = await File.ReadAllTextAsync(manifestPath);
        var doc = JsonDocument.Parse(content);
        doc.RootElement.GetProperty("id").GetString().Should().Be(blueprintId);
    }

    [Fact]
    public async Task UpdateManifestIdentifiersAsync_UpdatesAgentIdentityBlueprintId_InTemplateManifest()
    {
        // Arrange
        _service.ExtractTemplates(_testDirectory);
        var blueprintId = "agent-identity-id";

        // Act
        await _service.UpdateManifestIdentifiersAsync(_testDirectory, blueprintId);

        // Assert
        var templatePath = Path.Combine(_testDirectory, "agenticUserTemplateManifest.json");
        var content = await File.ReadAllTextAsync(templatePath);
        var doc = JsonDocument.Parse(content);
        doc.RootElement.GetProperty("agentIdentityBlueprintId").GetString().Should().Be(blueprintId);
    }

    [Fact]
    public async Task UpdateManifestIdentifiersAsync_UpdatesDisplayName_WhenProvided()
    {
        // Arrange
        _service.ExtractTemplates(_testDirectory);
        var blueprintId = "test-id";
        var displayName = "Test Agent Display Name";

        // Act
        await _service.UpdateManifestIdentifiersAsync(_testDirectory, blueprintId, displayName);

        // Assert
        var manifestPath = Path.Combine(_testDirectory, "manifest.json");
        var content = await File.ReadAllTextAsync(manifestPath);
        var doc = JsonDocument.Parse(content);
        doc.RootElement.GetProperty("name").GetProperty("short").GetString().Should().Be(displayName);
        doc.RootElement.GetProperty("name").GetProperty("full").GetString().Should().Be(displayName);
    }

    [Fact]
    public async Task UpdateManifestIdentifiersAsync_DoesNotUpdateDisplayName_WhenNull()
    {
        // Arrange
        _service.ExtractTemplates(_testDirectory);
        var blueprintId = "test-id";
        var manifestPath = Path.Combine(_testDirectory, "manifest.json");
        var originalContent = await File.ReadAllTextAsync(manifestPath);
        var originalDoc = JsonDocument.Parse(originalContent);
        var originalShortName = originalDoc.RootElement.GetProperty("name").GetProperty("short").GetString();

        // Act
        await _service.UpdateManifestIdentifiersAsync(_testDirectory, blueprintId, null);

        // Assert
        var updatedContent = await File.ReadAllTextAsync(manifestPath);
        var updatedDoc = JsonDocument.Parse(updatedContent);
        updatedDoc.RootElement.GetProperty("name").GetProperty("short").GetString().Should().Be(originalShortName);
    }

    [Fact]
    public async Task UpdateManifestIdentifiersAsync_ReturnsFalse_WhenManifestNotFound()
    {
        // Arrange
        var blueprintId = "test-id";

        // Act
        var result = await _service.UpdateManifestIdentifiersAsync(_testDirectory, blueprintId);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateManifestIdentifiersAsync_ReturnsFalse_WhenTemplateManifestNotFound()
    {
        // Arrange
        _service.ExtractTemplates(_testDirectory);
        var blueprintId = "test-id";
        
        // Delete template manifest
        File.Delete(Path.Combine(_testDirectory, "agenticUserTemplateManifest.json"));

        // Act
        var result = await _service.UpdateManifestIdentifiersAsync(_testDirectory, blueprintId);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateManifestIdentifiersAsync_UpdatesBotId_WhenBotsArrayExists()
    {
        // Arrange
        _service.ExtractTemplates(_testDirectory);
        var blueprintId = "bot-blueprint-id";

        // Act
        await _service.UpdateManifestIdentifiersAsync(_testDirectory, blueprintId);

        // Assert
        var manifestPath = Path.Combine(_testDirectory, "manifest.json");
        var content = await File.ReadAllTextAsync(manifestPath);
        var doc = JsonDocument.Parse(content);
        
        if (doc.RootElement.TryGetProperty("bots", out var bots) && bots.GetArrayLength() > 0)
        {
            bots[0].GetProperty("botId").GetString().Should().Be(blueprintId);
        }
    }

    #endregion

    #region CreateManifestZipAsync Tests

    [Fact]
    public async Task CreateManifestZipAsync_CreatesZipFile_Successfully()
    {
        // Arrange
        _service.ExtractTemplates(_testDirectory);
        var zipPath = Path.Combine(_testDirectory, "test-manifest.zip");

        // Act
        var result = await _service.CreateManifestZipAsync(_testDirectory, zipPath);

        // Assert
        result.Should().BeTrue();
        File.Exists(zipPath).Should().BeTrue();
    }

    [Fact]
    public async Task CreateManifestZipAsync_ContainsAllRequiredFiles()
    {
        // Arrange
        _service.ExtractTemplates(_testDirectory);
        var zipPath = Path.Combine(_testDirectory, "test-manifest.zip");

        // Act
        await _service.CreateManifestZipAsync(_testDirectory, zipPath);

        // Assert
        using var archive = ZipFile.OpenRead(zipPath);
        var entryNames = archive.Entries.Select(e => e.Name).ToList();
        entryNames.Should().Contain("manifest.json");
        entryNames.Should().Contain("agenticUserTemplateManifest.json");
        entryNames.Should().Contain("color.png");
        entryNames.Should().Contain("outline.png");
    }

    [Fact]
    public async Task CreateManifestZipAsync_OverwritesExisting_WhenZipAlreadyExists()
    {
        // Arrange
        _service.ExtractTemplates(_testDirectory);
        var zipPath = Path.Combine(_testDirectory, "test-manifest.zip");
        
        // Create existing file
        await File.WriteAllTextAsync(zipPath, "dummy content");
        var originalSize = new FileInfo(zipPath).Length;

        // Act
        var result = await _service.CreateManifestZipAsync(_testDirectory, zipPath);

        // Assert
        result.Should().BeTrue();
        var newSize = new FileInfo(zipPath).Length;
        newSize.Should().NotBe(originalSize);
    }

    [Fact]
    public async Task CreateManifestZipAsync_SkipsMissingFiles_WithWarning()
    {
        // Arrange
        _service.ExtractTemplates(_testDirectory);
        File.Delete(Path.Combine(_testDirectory, "color.png"));
        var zipPath = Path.Combine(_testDirectory, "test-manifest.zip");

        // Act
        var result = await _service.CreateManifestZipAsync(_testDirectory, zipPath);

        // Assert
        result.Should().BeTrue();
        using var archive = ZipFile.OpenRead(zipPath);
        var entryNames = archive.Entries.Select(e => e.Name).ToList();
        entryNames.Should().NotContain("color.png");
    }

    [Fact]
    public async Task CreateManifestZipAsync_ZipContainsValidJsonFiles()
    {
        // Arrange
        _service.ExtractTemplates(_testDirectory);
        var zipPath = Path.Combine(_testDirectory, "test-manifest.zip");

        // Act
        await _service.CreateManifestZipAsync(_testDirectory, zipPath);

        // Assert
        using var archive = ZipFile.OpenRead(zipPath);
        var manifestEntry = archive.GetEntry("manifest.json");
        manifestEntry.Should().NotBeNull();

        using var stream = manifestEntry!.Open();
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync();
        var act = () => JsonDocument.Parse(content);
        act.Should().NotThrow();
    }

    [Fact]
    public async Task CreateManifestZipAsync_ReturnsFalse_WhenExceptionOccurs()
    {
        // Arrange - Use invalid path to trigger exception
        var invalidPath = Path.Combine(_testDirectory, "invalid\0path", "test.zip");

        // Act
        var result = await _service.CreateManifestZipAsync(_testDirectory, invalidPath);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region Integration Tests

    [Fact]
    public async Task EndToEnd_ExtractUpdateAndZip_WorksTogether()
    {
        // Arrange
        var blueprintId = "e2e-blueprint-id";
        var displayName = "E2E Test Agent";
        var zipPath = Path.Combine(_testDirectory, "final-manifest.zip");

        // Act - Extract
        var extractResult = _service.ExtractTemplates(_testDirectory);
        extractResult.Should().BeTrue();

        // Act - Update
        var updateResult = await _service.UpdateManifestIdentifiersAsync(_testDirectory, blueprintId, displayName);
        updateResult.Should().BeTrue();

        // Act - Zip
        var zipResult = await _service.CreateManifestZipAsync(_testDirectory, zipPath);
        zipResult.Should().BeTrue();

        // Assert - Verify zip contents
        using var archive = ZipFile.OpenRead(zipPath);
        var manifestEntry = archive.GetEntry("manifest.json");
        manifestEntry.Should().NotBeNull();

        using var stream = manifestEntry!.Open();
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync();
        var doc = JsonDocument.Parse(content);
        
        doc.RootElement.GetProperty("id").GetString().Should().Be(blueprintId);
        doc.RootElement.GetProperty("name").GetProperty("short").GetString().Should().Be(displayName);
    }

    #endregion

    #region TryGetExistingManifestDirectory Tests

    [Fact]
    public void TryGetExistingManifestDirectory_ReturnsTrue_WhenManifestDirectoryExists()
    {
        // Arrange
        var projectPath = _testDirectory;
        var manifestDir = Path.Combine(projectPath, "manifest");
        Directory.CreateDirectory(manifestDir);

        // Act
        var result = _service.TryGetExistingManifestDirectory(projectPath, out var outDir);

        // Assert
        result.Should().BeTrue();
        outDir.Should().Be(manifestDir);
    }

    [Fact]
    public void TryGetExistingManifestDirectory_ReturnsFalse_WhenManifestDirectoryDoesNotExist()
    {
        // Arrange
        var projectPath = _testDirectory;

        // Act
        var result = _service.TryGetExistingManifestDirectory(projectPath, out var outDir);

        // Assert
        result.Should().BeFalse();
        outDir.Should().BeNull();
    }

    [Fact]
    public void TryGetExistingManifestDirectory_ReturnsFalse_WhenProjectPathDoesNotExist()
    {
        // Arrange
        var projectPath = Path.Combine(_testDirectory, "nonexistent");

        // Act
        var result = _service.TryGetExistingManifestDirectory(projectPath, out var outDir);

        // Assert
        result.Should().BeFalse();
        outDir.Should().BeNull();
    }

    [Fact]
    public void TryGetExistingManifestDirectory_ReturnsFalse_WhenProjectPathIsNull()
    {
        // Act
        var result = _service.TryGetExistingManifestDirectory(null!, out var outDir);

        // Assert
        result.Should().BeFalse();
        outDir.Should().BeNull();
    }

    #endregion

    #region ValidateManifestFormatAsync Tests

    [Fact]
    public async Task ValidateManifestFormatAsync_ReturnsTrue_WhenBothFilesAreValid()
    {
        // Arrange
        var manifestDir = Path.Combine(_testDirectory, "manifest");
        Directory.CreateDirectory(manifestDir);
        
        await File.WriteAllTextAsync(
            Path.Combine(manifestDir, "manifest.json"),
            @"{""id"": ""test-id"", ""name"": {""short"": ""Test""}}");
        
        await File.WriteAllTextAsync(
            Path.Combine(manifestDir, "agenticUserTemplateManifest.json"),
            @"{""agentIdentityBlueprintId"": ""blueprint-id""}");

        // Act
        var result = await _service.ValidateManifestFormatAsync(manifestDir);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateManifestFormatAsync_ReturnsTrue_WhenOnlyManifestJsonExists()
    {
        // Arrange
        var manifestDir = Path.Combine(_testDirectory, "manifest");
        Directory.CreateDirectory(manifestDir);
        
        await File.WriteAllTextAsync(
            Path.Combine(manifestDir, "manifest.json"),
            @"{""id"": ""test-id"", ""name"": {""short"": ""Test""}}");

        // Act
        var result = await _service.ValidateManifestFormatAsync(manifestDir);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateManifestFormatAsync_ReturnsFalse_WhenManifestJsonMissing()
    {
        // Arrange
        var manifestDir = Path.Combine(_testDirectory, "manifest");
        Directory.CreateDirectory(manifestDir);

        // Act
        var result = await _service.ValidateManifestFormatAsync(manifestDir);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateManifestFormatAsync_ReturnsFalse_WhenManifestJsonMissingIdProperty()
    {
        // Arrange
        var manifestDir = Path.Combine(_testDirectory, "manifest");
        Directory.CreateDirectory(manifestDir);
        
        await File.WriteAllTextAsync(
            Path.Combine(manifestDir, "manifest.json"),
            @"{""name"": {""short"": ""Test""}}");

        // Act
        var result = await _service.ValidateManifestFormatAsync(manifestDir);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateManifestFormatAsync_ReturnsFalse_WhenTemplateManifestMissingRequiredProperty()
    {
        // Arrange
        var manifestDir = Path.Combine(_testDirectory, "manifest");
        Directory.CreateDirectory(manifestDir);
        
        await File.WriteAllTextAsync(
            Path.Combine(manifestDir, "manifest.json"),
            @"{""id"": ""test-id""}");
        
        await File.WriteAllTextAsync(
            Path.Combine(manifestDir, "agenticUserTemplateManifest.json"),
            @"{""someOtherProperty"": ""value""}");

        // Act
        var result = await _service.ValidateManifestFormatAsync(manifestDir);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateManifestFormatAsync_ReturnsFalse_WhenManifestJsonIsInvalidJson()
    {
        // Arrange
        var manifestDir = Path.Combine(_testDirectory, "manifest");
        Directory.CreateDirectory(manifestDir);
        
        await File.WriteAllTextAsync(
            Path.Combine(manifestDir, "manifest.json"),
            @"{ invalid json }");

        // Act
        var result = await _service.ValidateManifestFormatAsync(manifestDir);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region CopyAndSupplementManifestAsync Tests

    [Fact]
    public async Task CopyAndSupplementManifestAsync_CopiesExistingFiles()
    {
        // Arrange
        var sourceDir = Path.Combine(_testDirectory, "source");
        var destDir = Path.Combine(_testDirectory, "dest");
        Directory.CreateDirectory(sourceDir);
        
        await File.WriteAllTextAsync(
            Path.Combine(sourceDir, "manifest.json"),
            @"{""id"": ""existing-id""}");

        // Act
        var result = await _service.CopyAndSupplementManifestAsync(sourceDir, destDir);

        // Assert
        result.Should().BeTrue();
        File.Exists(Path.Combine(destDir, "manifest.json")).Should().BeTrue();
        var content = await File.ReadAllTextAsync(Path.Combine(destDir, "manifest.json"));
        content.Should().Contain("existing-id");
    }

    [Fact]
    public async Task CopyAndSupplementManifestAsync_SupplementsMissingFilesFromTemplates()
    {
        // Arrange
        var sourceDir = Path.Combine(_testDirectory, "source");
        var destDir = Path.Combine(_testDirectory, "dest");
        Directory.CreateDirectory(sourceDir);
        
        // Only create manifest.json, let templates provide the rest
        await File.WriteAllTextAsync(
            Path.Combine(sourceDir, "manifest.json"),
            @"{""id"": ""test-id""}");

        // Act
        var result = await _service.CopyAndSupplementManifestAsync(sourceDir, destDir);

        // Assert
        result.Should().BeTrue();
        File.Exists(Path.Combine(destDir, "manifest.json")).Should().BeTrue();
        File.Exists(Path.Combine(destDir, "agenticUserTemplateManifest.json")).Should().BeTrue();
        File.Exists(Path.Combine(destDir, "color.png")).Should().BeTrue();
        File.Exists(Path.Combine(destDir, "outline.png")).Should().BeTrue();
    }

    [Fact]
    public async Task CopyAndSupplementManifestAsync_CreatesDestinationDirectory()
    {
        // Arrange
        var sourceDir = Path.Combine(_testDirectory, "source");
        var destDir = Path.Combine(_testDirectory, "dest", "nested");
        Directory.CreateDirectory(sourceDir);
        
        await File.WriteAllTextAsync(
            Path.Combine(sourceDir, "manifest.json"),
            @"{""id"": ""test-id""}");

        // Act
        var result = await _service.CopyAndSupplementManifestAsync(sourceDir, destDir);

        // Assert
        result.Should().BeTrue();
        Directory.Exists(destDir).Should().BeTrue();
    }

    [Fact]
    public async Task CopyAndSupplementManifestAsync_PreservesExistingIconsOverTemplates()
    {
        // Arrange
        var sourceDir = Path.Combine(_testDirectory, "source");
        var destDir = Path.Combine(_testDirectory, "dest");
        Directory.CreateDirectory(sourceDir);
        
        var customIconContent = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0xFF, 0xFF };
        await File.WriteAllBytesAsync(Path.Combine(sourceDir, "color.png"), customIconContent);

        // Act
        var result = await _service.CopyAndSupplementManifestAsync(sourceDir, destDir);

        // Assert
        result.Should().BeTrue();
        var copiedContent = await File.ReadAllBytesAsync(Path.Combine(destDir, "color.png"));
        copiedContent.Should().BeEquivalentTo(customIconContent);
    }

    #endregion
}
