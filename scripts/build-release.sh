#!/usr/bin/env bash
#
# Build release artifacts for all platforms
# Run from repo root: ./scripts/build-release.sh
#

set -e

# Get version from CliConstants.cs
VERSION=$(grep 'AppVersion = ' coppercli/CliConstants.cs | sed 's/.*"\(.*\)".*/\1/')
if [[ -z "$VERSION" ]]; then
    echo "ERROR: Could not extract version from CliConstants.cs"
    exit 1
fi

echo "=== Building coppercli $VERSION ==="
echo ""

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
RELEASE_DIR="$REPO_ROOT/release"
PROJECT="$REPO_ROOT/coppercli/coppercli.csproj"

# Clean previous builds
rm -rf "$RELEASE_DIR"
mkdir -p "$RELEASE_DIR"

# Find dotnet
find_dotnet() {
    if command -v dotnet &> /dev/null; then
        echo "dotnet"
        return 0
    fi
    for dir in /opt/homebrew/Cellar/dotnet@8/*/bin /usr/local/Cellar/dotnet@8/*/bin; do
        if [[ -x "$dir/dotnet" ]]; then
            echo "$dir/dotnet"
            return 0
        fi
    done
    if [[ -x "/opt/homebrew/bin/dotnet" ]]; then
        echo "/opt/homebrew/bin/dotnet"
        return 0
    fi
    if [[ -x "$HOME/.dotnet/dotnet" ]]; then
        echo "$HOME/.dotnet/dotnet"
        return 0
    fi
    return 1
}

DOTNET=$(find_dotnet) || {
    echo "ERROR: dotnet not found"
    exit 1
}
echo "Using: $DOTNET"
echo ""

# Build function
build_platform() {
    local rid=$1
    local archive_name=$2
    local publish_dir="$RELEASE_DIR/$rid"

    echo "Building for $rid..."
    "$DOTNET" publish "$PROJECT" \
        -c Release \
        -r "$rid" \
        --self-contained \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:EnableCompressionInSingleFile=true \
        -o "$publish_dir" \
        --verbosity quiet

    # Create archive
    cd "$publish_dir"
    if [[ "$rid" == win-* ]]; then
        # For Windows, just copy the exe (Inno Setup handles the installer)
        cp coppercli.exe "$RELEASE_DIR/$archive_name"
        echo "  Created: $archive_name"
    else
        # For Unix, create tarball
        tar -czf "$RELEASE_DIR/$archive_name" coppercli
        echo "  Created: $archive_name"
    fi
    cd "$REPO_ROOT"
}

# Build all platforms
build_platform "win-x64"       "coppercli-$VERSION-windows-x64.exe"
build_platform "osx-arm64"     "coppercli-$VERSION-macos-arm64.tar.gz"
build_platform "osx-x64"       "coppercli-$VERSION-macos-x64.tar.gz"
build_platform "linux-x64"     "coppercli-$VERSION-linux-x64.tar.gz"

# Build Windows installer if on Windows or if Inno Setup available
if [[ -f "$REPO_ROOT/installer/build-installer.ps1" ]]; then
    if command -v pwsh &> /dev/null || command -v powershell &> /dev/null; then
        echo ""
        echo "Building Windows installer..."
        cd "$REPO_ROOT/installer"
        if command -v pwsh &> /dev/null; then
            pwsh -ExecutionPolicy Bypass -File build-installer.ps1 2>/dev/null || echo "  (Skipped - Inno Setup not available on this platform)"
        fi
        if [[ -f "output/coppercli-$VERSION-setup.exe" ]]; then
            cp "output/coppercli-$VERSION-setup.exe" "$RELEASE_DIR/"
            echo "  Created: coppercli-$VERSION-setup.exe"
        fi
        cd "$REPO_ROOT"
    fi
fi

# Clean up intermediate directories
rm -rf "$RELEASE_DIR/win-x64" "$RELEASE_DIR/osx-arm64" "$RELEASE_DIR/osx-x64" "$RELEASE_DIR/linux-x64"

echo ""
echo "=== Build Complete ==="
echo "Release artifacts in: $RELEASE_DIR"
ls -lh "$RELEASE_DIR"
