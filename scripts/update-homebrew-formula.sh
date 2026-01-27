#!/usr/bin/env bash
#
# Update the Homebrew formula with new version and SHA256 checksums
# Run after creating a GitHub release
#
# Usage: ./scripts/update-homebrew-formula.sh v0.1.1
#

set -e

VERSION="${1:-}"
if [[ -z "$VERSION" ]]; then
    echo "Usage: $0 <version>"
    echo "Example: $0 v0.1.1"
    exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
HOMEBREW_TAP="$HOME/src/homebrew-coppercli"
FORMULA="$HOMEBREW_TAP/Formula/coppercli.rb"

if [[ ! -f "$FORMULA" ]]; then
    echo "ERROR: Formula not found at $FORMULA"
    exit 1
fi

echo "Updating formula for version $VERSION..."

# Base URL for downloads
BASE_URL="https://github.com/thomergil/coppercli/releases/download/$VERSION"

# Download and compute SHA256 for each platform
echo "Downloading and computing checksums..."

ARM64_URL="$BASE_URL/coppercli-$VERSION-osx-arm64.tar.gz"
X64_URL="$BASE_URL/coppercli-$VERSION-osx-x64.tar.gz"
LINUX_URL="$BASE_URL/coppercli-$VERSION-linux-x64.tar.gz"

# Download to temp files to avoid curl pipe checksum issues
TMPDIR=$(mktemp -d)
trap "rm -rf $TMPDIR" EXIT

echo "  Fetching macOS ARM64..."
curl -sL "$ARM64_URL" -o "$TMPDIR/arm64.tar.gz"
ARM64_SHA=$(shasum -a 256 "$TMPDIR/arm64.tar.gz" | cut -d' ' -f1)
echo "    SHA256: $ARM64_SHA"

echo "  Fetching macOS x64..."
curl -sL "$X64_URL" -o "$TMPDIR/x64.tar.gz"
X64_SHA=$(shasum -a 256 "$TMPDIR/x64.tar.gz" | cut -d' ' -f1)
echo "    SHA256: $X64_SHA"

echo "  Fetching Linux x64..."
curl -sL "$LINUX_URL" -o "$TMPDIR/linux.tar.gz"
LINUX_SHA=$(shasum -a 256 "$TMPDIR/linux.tar.gz" | cut -d' ' -f1)
echo "    SHA256: $LINUX_SHA"

# Update the formula
echo "Updating formula..."

# Use sed to update version and URLs/checksums
sed -i.bak \
    -e "s|version \".*\"|version \"$VERSION\"|" \
    -e "s|/download/v[^/]*/coppercli-v[^-]*-osx-arm64|/download/$VERSION/coppercli-$VERSION-osx-arm64|" \
    -e "s|/download/v[^/]*/coppercli-v[^-]*-osx-x64|/download/$VERSION/coppercli-$VERSION-osx-x64|" \
    -e "s|/download/v[^/]*/coppercli-v[^-]*-linux-x64|/download/$VERSION/coppercli-$VERSION-linux-x64|" \
    "$FORMULA"

# Update SHA256 checksums (these are on separate lines, so we need a different approach)
# Create a temporary file with the updates
awk -v arm64="$ARM64_SHA" -v x64="$X64_SHA" -v linux="$LINUX_SHA" '
    /sha256.*ARM64/ || /sha256.*PLACEHOLDER_ARM64/ || (prev_arm64 && /sha256/) {
        if (prev_arm64) { sub(/sha256 ".*"/, "sha256 \"" arm64 "\""); prev_arm64=0 }
    }
    /sha256.*X64/ || /sha256.*PLACEHOLDER_X64/ || (prev_x64 && /sha256/) {
        if (prev_x64) { sub(/sha256 ".*"/, "sha256 \"" x64 "\""); prev_x64=0 }
    }
    /sha256.*LINUX/ || /sha256.*PLACEHOLDER_LINUX/ || (prev_linux && /sha256/) {
        if (prev_linux) { sub(/sha256 ".*"/, "sha256 \"" linux "\""); prev_linux=0 }
    }
    /osx-arm64\.tar\.gz/ { prev_arm64=1 }
    /osx-x64\.tar\.gz/ { prev_x64=1 }
    /linux-x64\.tar\.gz/ { prev_linux=1 }
    { print }
' "$FORMULA" > "$FORMULA.tmp" && mv "$FORMULA.tmp" "$FORMULA"

rm -f "$FORMULA.bak"

echo ""
echo "Formula updated. New contents:"
echo "================================"
cat "$FORMULA"
echo "================================"
echo ""
echo "Next steps:"
echo "1. cd ~/src/homebrew-coppercli"
echo "2. git add Formula/coppercli.rb"
echo "3. git commit -m 'Update to $VERSION'"
echo "4. git push"
