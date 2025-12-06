# install-cli.ps1
# This script installs the Agent 365 CLI from a local NuGet package in the publish folder.
# Usage: Run this script from the root of the extracted package (where publish/ exists)

# Get the repository root directory (two levels up from scripts/cli/)
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projectPath = Join-Path $repoRoot 'src\Microsoft.Agents.A365.DevTools.Cli\Microsoft.Agents.A365.DevTools.Cli.csproj'

# Verify the project file exists
if (-not (Test-Path $projectPath)) {
    Write-Error "ERROR: Project file not found at $projectPath"
    exit 1
}

$outputDir = Join-Path $PSScriptRoot 'nupkg'
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir | Out-Null
}

# Clean old packages to ensure fresh build
Write-Host "Cleaning old packages from $outputDir..."
Get-ChildItem -Path $outputDir -Filter '*.nupkg' | Remove-Item -Force

# Build the project first to ensure NuGet restore and build outputs exist
Write-Host "Building CLI tool (Release configuration)..."
dotnet build $projectPath -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Error "ERROR: dotnet build failed. Check output above for details."
    exit 1
}
Write-Host "Packing CLI tool to $outputDir (Release configuration)..."
dotnet pack $projectPath -c Release -o $outputDir --no-build -p:IncludeSymbols=false -p:TreatWarningsAsErrors=false
if ($LASTEXITCODE -ne 0) {
    Write-Error "ERROR: dotnet pack failed. Check output above for details."
    exit 1
}

# Find the generated .nupkg
$nupkg = Get-ChildItem -Path $outputDir -Filter 'Microsoft.Agents.A365.DevTools.Cli*.nupkg' | Select-Object -First 1
if (-not $nupkg) {
    Write-Error "ERROR: NuGet package not found in $outputDir."
    exit 1
}

Write-Host "Installing Agent 365 CLI from local package: $($nupkg.Name)"

# Uninstall any existing global CLI tool (force to handle version conflicts)
Write-Host "Uninstalling existing CLI tool..."
try {
    dotnet tool uninstall -g Microsoft.Agents.A365.DevTools.Cli 2>$null
    Write-Host "Existing CLI uninstalled successfully." -ForegroundColor Green
} catch {
    Write-Host "No existing CLI found. Proceeding with fresh install." -ForegroundColor Yellow
}

# Install with specific version from local source
Write-Host "Installing CLI tool..."
$version = $nupkg.Name -replace 'Microsoft\.Agents\.A365\.DevTools\.Cli\.(.*)\.nupkg','$1'
dotnet tool install -g Microsoft.Agents.A365.DevTools.Cli --add-source $outputDir --version $version
if ($LASTEXITCODE -ne 0) {
    Write-Error "ERROR: CLI installation failed. Check output above for details."
    exit 1
}

Write-Host "Agent 365 CLI installed successfully. Run 'a365 --help' to verify installation."