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

# Clear NuGet package cache to avoid version conflicts
Write-Host "Clearing NuGet package cache..."
Remove-Item ~/.nuget/packages/microsoft.agents.a365.devtools.cli -Recurse -Force -ErrorAction SilentlyContinue
# Also clear the dotnet tools cache
Remove-Item ~/.dotnet/toolResolverCache -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Package cache cleared"

# Force clean by removing bin/obj folders
Write-Host "Force cleaning bin and obj folders..."
$projectDir = Split-Path $projectPath -Parent
$binPath = Join-Path $projectDir "bin"
$objPath = Join-Path $projectDir "obj"
Write-Host "  Removing: $binPath"
Remove-Item $binPath -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "  Removing: $objPath"
Remove-Item $objPath -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Folders cleaned"

# Clean the project to ensure fresh build
Write-Host "Cleaning project..."
dotnet clean $projectPath -c Release

# Build the project first to ensure NuGet restore and build outputs exist
Write-Host "Building CLI tool (Release configuration)..."
dotnet build $projectPath -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Error "ERROR: dotnet build failed. Check output above for details."
    exit 1
}
Write-Host "Packing CLI tool to $outputDir (Release configuration)..."
# Remove --no-build to ensure pack rebuilds if needed
dotnet pack $projectPath -c Release -o $outputDir -p:IncludeSymbols=false -p:TreatWarningsAsErrors=false
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

# Kill any running a365 processes to release file locks
Write-Host "Checking for running a365 processes..."
$processes = Get-Process -Name "a365" -ErrorAction SilentlyContinue
if ($processes) {
    Write-Host "Stopping running a365 processes..." -ForegroundColor Yellow
    $processes | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

# Uninstall any existing global CLI tool (force to handle version conflicts)
Write-Host "Uninstalling existing CLI tool..."
dotnet tool uninstall -g Microsoft.Agents.A365.DevTools.Cli 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host "Existing CLI uninstalled successfully." -ForegroundColor Green
    # Give the system a moment to release file locks
    Start-Sleep -Seconds 1
} else {
    Write-Host "Could not uninstall existing CLI (may not be installed or locked)." -ForegroundColor Yellow
    # Try to clear the tool directory manually if locked
    $toolPath = Join-Path $env:USERPROFILE ".dotnet\tools\.store\microsoft.agents.a365.devtools.cli"
    if (Test-Path $toolPath) {
        Write-Host "Attempting to clear locked tool directory..." -ForegroundColor Yellow
        Remove-Item $toolPath -Recurse -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
    }
}

# Install with specific version from local source
Write-Host "Installing CLI tool..."
$version = $nupkg.Name -replace 'Microsoft\.Agents\.A365\.DevTools\.Cli\.(.*)\.nupkg','$1'
Write-Host "Version: $version" -ForegroundColor Cyan

# Try update first (which forces reinstall), fall back to install if not already installed
Write-Host "Attempting to update tool..."
dotnet tool update -g Microsoft.Agents.A365.DevTools.Cli --add-source $outputDir --version $version 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Update failed, attempting fresh install..."
    dotnet tool install -g Microsoft.Agents.A365.DevTools.Cli --add-source $outputDir --version $version
}
if ($LASTEXITCODE -ne 0) {
    Write-Error "ERROR: CLI installation failed. Check output above for details."
    exit 1
}

Write-Host "Agent 365 CLI installed successfully." -ForegroundColor Green
Write-Host ""
Write-Host "Verifying installation..."
$installedVersion = dotnet tool list -g | Select-String "microsoft.agents.a365.devtools.cli"
if ($installedVersion) {
    Write-Host "Installed: $installedVersion" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "IMPORTANT: If you have the CLI running in another terminal, close it and reopen to pick up the new version." -ForegroundColor Yellow
} else {
    Write-Warning "Could not verify installation. Try running 'a365 --help' to test."
}