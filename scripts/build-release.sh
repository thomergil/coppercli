#!/usr/bin/env bash
#
# Build release artifacts for all platforms
# Run from repo root: ./scripts/build-release.sh
#

set -e
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
rm -rf "$RELEASE_DIR"
mkdir -p "$RELEASE_DIR"
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
    cd "$publish_dir"
    if [[ "$rid" == win-* ]]; then
        # Windows gets the bare exe; Inno Setup builds the installer from it.
        cp coppercli.exe "$RELEASE_DIR/$archive_name"
        echo "  Created: $archive_name"
    else
        tar -czf "$RELEASE_DIR/$archive_name" coppercli
        echo "  Created: $archive_name"
    fi
    cd "$REPO_ROOT"
}
build_platform "win-x64"       "coppercli-$VERSION-windows-x64.exe"
build_platform "osx-arm64"     "coppercli-$VERSION-macos-arm64.tar.gz"
build_platform "osx-x64"       "coppercli-$VERSION-macos-x64.tar.gz"
build_platform "linux-x64"     "coppercli-$VERSION-linux-x64.tar.gz"
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
rm -rf "$RELEASE_DIR/win-x64" "$RELEASE_DIR/osx-arm64" "$RELEASE_DIR/osx-x64" "$RELEASE_DIR/linux-x64"

echo ""
echo "=== Build Complete ==="
echo "Release artifacts in: $RELEASE_DIR"
ls -lh "$RELEASE_DIR"
