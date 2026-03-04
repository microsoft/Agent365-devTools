# install-cli.ps1
# This script installs the Agent 365 CLI from a local NuGet package.
# Delegates to install-cli.sh (requires bash - Git Bash on Windows, system bash on macOS/Linux)

$shScript = Join-Path $PSScriptRoot "install-cli.sh"
if (-not (Test-Path $shScript)) {
    Write-Error "ERROR: install-cli.sh not found at $shScript"
    exit 1
}

# Find bash: Git Bash on Windows (ships with Git for Windows), system bash elsewhere
$bash = $null
if ($env:OS -eq 'Windows_NT') {
    # Locate bash.exe from git's install directory (Git for Windows always ships bash alongside git)
    $gitExe = Get-Command git -ErrorAction SilentlyContinue
    if ($gitExe) {
        $gitDir = Split-Path $gitExe.Source -Parent
        $candidate = Join-Path $gitDir "bash.exe"
        if (Test-Path $candidate) {
            $bash = $candidate
        }
    }
    if (-not $bash) {
        # Fallback: check well-known Git for Windows install paths
        foreach ($path in @(
            "$env:ProgramFiles\Git\bin\bash.exe",
            "${env:ProgramFiles(x86)}\Git\bin\bash.exe",
            "$env:LocalAppData\Programs\Git\bin\bash.exe"
        )) {
            if (Test-Path $path) { $bash = $path; break }
        }
    }
    if (-not $bash) {
        Write-Error "ERROR: bash.exe not found. Install Git for Windows from https://git-scm.com/download/win"
        exit 1
    }
} else {
    $bash = "bash"
}

& $bash $shScript
exit $LASTEXITCODE
