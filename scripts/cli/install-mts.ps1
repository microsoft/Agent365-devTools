# install-mts.ps1
# This script installs the MockToolingServer as a global dotnet tool from a local NuGet package.
# Usage: Run this script from the root of the extracted package (where publish/ exists)

# Get the repository root directory (two levels up from scripts/cli/)
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$mockServerProjectPath = Join-Path $repoRoot 'src\Microsoft.Agents.A365.DevTools.MockToolingServer\Microsoft.Agents.A365.DevTools.MockToolingServer.csproj'

# Verify project file exists
if (-not (Test-Path $mockServerProjectPath)) {
    Write-Error "ERROR: MockToolingServer project file not found at $mockServerProjectPath"
    exit 1
}

$outputDir = Join-Path $PSScriptRoot 'nupkg'
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir | Out-Null
}

# Clean old MockToolingServer packages to ensure fresh build
Write-Host "Cleaning old MockToolingServer packages from $outputDir..."
Get-ChildItem -Path $outputDir -Filter 'Microsoft.Agents.A365.DevTools.MockToolingServer*.nupkg' | Remove-Item -Force

# Clear NuGet package cache to avoid version conflicts
Write-Host "Clearing MockToolingServer NuGet package cache..."
Remove-Item ~/.nuget/packages/microsoft.agents.a365.devtools.mocktoolingserver -Recurse -Force -ErrorAction SilentlyContinue
# Also clear the dotnet tools cache
Remove-Item ~/.dotnet/toolResolverCache -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Package cache cleared"

# Force clean MockToolingServer by removing bin/obj folders
Write-Host "Force cleaning MockToolingServer bin and obj folders..."
$mockServerProjectDir = Split-Path $mockServerProjectPath -Parent
$mockServerBinPath = Join-Path $mockServerProjectDir "bin"
$mockServerObjPath = Join-Path $mockServerProjectDir "obj"
Write-Host "  Removing: $mockServerBinPath"
Remove-Item $mockServerBinPath -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "  Removing: $mockServerObjPath"
Remove-Item $mockServerObjPath -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "MockToolingServer folders cleaned"

# Clean the MockToolingServer project to ensure fresh build
Write-Host "Cleaning MockToolingServer project..."
dotnet clean $mockServerProjectPath -c Release

# Build MockToolingServer
Write-Host "Building MockToolingServer (Release configuration)..."
dotnet build $mockServerProjectPath -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Error "ERROR: MockToolingServer build failed. Check output above for details."
    exit 1
}

Write-Host "Packing MockToolingServer to $outputDir (Release configuration)..."
dotnet pack $mockServerProjectPath -c Release -o $outputDir -p:IncludeSymbols=false -p:TreatWarningsAsErrors=false
if ($LASTEXITCODE -ne 0) {
    Write-Error "ERROR: MockToolingServer pack failed. Check output above for details."
    exit 1
}

# Find the generated MockToolingServer .nupkg
$mockServerNupkg = Get-ChildItem -Path $outputDir -Filter 'Microsoft.Agents.A365.DevTools.MockToolingServer*.nupkg' | Select-Object -First 1
if (-not $mockServerNupkg) {
    Write-Error "ERROR: MockToolingServer NuGet package not found in $outputDir."
    exit 1
}

Write-Host "Installing MockToolingServer from local package: $($mockServerNupkg.Name)"

# Uninstall any existing MockToolingServer tool (force to handle version conflicts)
Write-Host "Uninstalling existing MockToolingServer tool..."
dotnet tool uninstall -g Microsoft.Agents.A365.DevTools.MockToolingServer 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host "Existing MockToolingServer uninstalled successfully." -ForegroundColor Green
    Start-Sleep -Seconds 1
} else {
    Write-Host "Could not uninstall existing MockToolingServer (may not be installed)." -ForegroundColor Yellow
}

# Install with specific version from local source
Write-Host "Installing MockToolingServer tool..."
$mockServerVersion = $mockServerNupkg.Name -replace 'Microsoft\.Agents\.A365\.DevTools\.MockToolingServer\.(.*)\.nupkg','$1'
Write-Host "Version: $mockServerVersion" -ForegroundColor Cyan

# Try update first (which forces reinstall), fall back to install if not already installed
Write-Host "Attempting to update MockToolingServer tool..."
dotnet tool update -g Microsoft.Agents.A365.DevTools.MockToolingServer --add-source $outputDir --version $mockServerVersion 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Update failed, attempting fresh install..."
    dotnet tool install -g Microsoft.Agents.A365.DevTools.MockToolingServer --add-source $outputDir --version $mockServerVersion
}
if ($LASTEXITCODE -ne 0) {
    Write-Error "ERROR: MockToolingServer installation failed. Check output above for details."
    exit 1
}

Write-Host "MockToolingServer installed successfully." -ForegroundColor Green
Write-Host ""
Write-Host "Verifying installation..."
$installedVersion = dotnet tool list -g | Select-String "microsoft.agents.a365.devtools.mocktoolingserver"
if ($installedVersion) {
    Write-Host "Installed: $installedVersion" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "You can now run 'a365-mock-tooling-server --help' to test the installation." -ForegroundColor Green
} else {
    Write-Warning "Could not verify installation. Try running 'a365-mock-tooling-server --help' to test."
}