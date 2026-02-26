#!/usr/bin/env bash
# install-cli.sh
# This script installs the Agent 365 CLI from a local NuGet package.
# Usage: Run this script from the repo root, or directly from scripts/cli/

# Get the repository root directory (two levels up from scripts/cli/)
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
PROJECT_PATH="$REPO_ROOT/src/Microsoft.Agents.A365.DevTools.Cli/Microsoft.Agents.A365.DevTools.Cli.csproj"

# Verify the project file exists
if [ ! -f "$PROJECT_PATH" ]; then
    echo "ERROR: Project file not found at $PROJECT_PATH" >&2
    exit 1
fi

OUTPUT_DIR="$SCRIPT_DIR/nupkg"
mkdir -p "$OUTPUT_DIR"

# Clean old packages to ensure fresh build
echo "Cleaning old packages from $OUTPUT_DIR..."
rm -f "$OUTPUT_DIR"/*.nupkg

# Clear NuGet package cache to avoid version conflicts
echo "Clearing NuGet package cache..."
rm -rf ~/.nuget/packages/microsoft.agents.a365.devtools.cli
# Also clear the dotnet tools cache
rm -rf ~/.dotnet/toolResolverCache
echo "Package cache cleared"

# Force clean by removing bin/obj folders
echo "Force cleaning bin and obj folders..."
PROJECT_DIR="$(dirname "$PROJECT_PATH")"
echo "  Removing: $PROJECT_DIR/bin"
rm -rf "$PROJECT_DIR/bin"
echo "  Removing: $PROJECT_DIR/obj"
rm -rf "$PROJECT_DIR/obj"
echo "Folders cleaned"

# Clean the project to ensure fresh build
echo "Cleaning project..."
dotnet clean "$PROJECT_PATH" -c Release

# Build the project first to ensure NuGet restore and build outputs exist
echo "Building CLI tool (Release configuration)..."
dotnet build "$PROJECT_PATH" -c Release
if [ $? -ne 0 ]; then
    echo "ERROR: dotnet build failed. Check output above for details." >&2
    exit 1
fi

echo "Packing CLI tool to $OUTPUT_DIR (Release configuration)..."
dotnet pack "$PROJECT_PATH" -c Release -o "$OUTPUT_DIR" -p:IncludeSymbols=false -p:TreatWarningsAsErrors=false
if [ $? -ne 0 ]; then
    echo "ERROR: dotnet pack failed. Check output above for details." >&2
    exit 1
fi

# Find the generated .nupkg
NUPKG=$(find "$OUTPUT_DIR" -name 'Microsoft.Agents.A365.DevTools.Cli*.nupkg' | head -1)
if [ -z "$NUPKG" ]; then
    echo "ERROR: NuGet package not found in $OUTPUT_DIR." >&2
    exit 1
fi

echo "Installing Agent 365 CLI from local package: $(basename "$NUPKG")"

# Kill any running a365 processes to release file locks
echo "Checking for running a365 processes..."
if pgrep -x "a365" > /dev/null 2>&1; then
    echo "Stopping running a365 processes..."
    pkill -x "a365" 2>/dev/null || true
    sleep 1
fi

# Uninstall any existing global CLI tool (force to handle version conflicts)
echo "Uninstalling existing CLI tool..."
if dotnet tool uninstall -g Microsoft.Agents.A365.DevTools.Cli 2>/dev/null; then
    echo "Existing CLI uninstalled successfully."
    # Give the system a moment to release file locks
    sleep 1
else
    echo "Could not uninstall existing CLI (may not be installed or locked)."
    # Try to clear the tool directory manually if locked
    TOOL_PATH="$HOME/.dotnet/tools/.store/microsoft.agents.a365.devtools.cli"
    if [ -d "$TOOL_PATH" ]; then
        echo "Attempting to clear locked tool directory..."
        rm -rf "$TOOL_PATH" 2>/dev/null || true
        sleep 1
    fi
fi

# Install with specific version from local source
echo "Installing CLI tool..."
VERSION=$(basename "$NUPKG" | sed 's/Microsoft\.Agents\.A365\.DevTools\.Cli\.\(.*\)\.nupkg/\1/')
echo "Version: $VERSION"

# Try update first (which forces reinstall), fall back to install if not already installed
echo "Attempting to update tool..."
if ! dotnet tool update -g Microsoft.Agents.A365.DevTools.Cli --add-source "$OUTPUT_DIR" --version "$VERSION" > /dev/null 2>&1; then
    echo "Update failed, attempting fresh install..."
    dotnet tool install -g Microsoft.Agents.A365.DevTools.Cli --add-source "$OUTPUT_DIR" --version "$VERSION"
fi
if [ $? -ne 0 ]; then
    echo "ERROR: CLI installation failed. Check output above for details." >&2
    exit 1
fi

echo "Agent 365 CLI installed successfully."
echo ""
echo "Verifying installation..."
INSTALLED=$(dotnet tool list -g | grep -i "microsoft.agents.a365.devtools.cli" || true)
if [ -n "$INSTALLED" ]; then
    echo "Installed: $INSTALLED"
    echo ""
    echo "IMPORTANT: If you have the CLI running in another terminal, close it and reopen to pick up the new version."
else
    echo "WARNING: Could not verify installation. Try running 'a365 --help' to test."
fi
