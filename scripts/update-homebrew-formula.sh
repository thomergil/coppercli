#!/usr/bin/env bash
#
# Update the Homebrew formula with new version and SHA256 checksums
# Run after creating a GitHub release
#
# Usage: ./scripts/update-homebrew-formula.sh v0.1.1
#

set -euo pipefail

VERSION="${1:-}"
if [[ -z "$VERSION" ]]; then
    echo "Usage: $0 <version>"
    echo "Example: $0 v0.1.1"
    exit 1
fi

# A version with a suffix, such as v0.6.0-alpha, is a pre-release, which Homebrew users do
# not get: the formula stays on the last full release.
if [[ "$VERSION" == *-* ]]; then
    echo "ERROR: $VERSION is a pre-release; the Homebrew formula takes full releases only"
    exit 1
fi
if [[ ! "$VERSION" =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "ERROR: Expected a version such as v0.6.0"
    exit 1
fi

HOMEBREW_TAP="${HOMEBREW_TAP:-$HOME/src/homebrew-coppercli}"
FORMULA="$HOMEBREW_TAP/Formula/coppercli.rb"

if [[ ! -f "$FORMULA" ]]; then
    echo "ERROR: Formula not found at $FORMULA"
    exit 1
fi

echo "Updating formula for version $VERSION..."
BASE_URL="https://github.com/thomergil/coppercli/releases/download/$VERSION"
echo "Downloading and computing checksums..."

downloads=$(mktemp -d)
trap 'rm -rf "$downloads"; if [ -n "${candidate:-}" ]; then rm -f "$candidate"; fi' EXIT
candidate=$(mktemp "$HOMEBREW_TAP/Formula/.coppercli.rb.XXXXXX")

download_and_hash() {
    local url="$1"
    local file="$2"
    curl -q --fail --location --silent --show-error --retry 3 --output "$downloads/$file" "$url" > /dev/null
    test -s "$downloads/$file"
    tar -tzf "$downloads/$file" | grep -x 'coppercli' > /dev/null
    local sha
    sha=$(shasum -a 256 "$downloads/$file" | cut -d' ' -f1)
    if [[ ! "$sha" =~ ^[[:xdigit:]]{64}$ ]]; then
        echo "ERROR: Invalid SHA256 for $file" >&2
        return 1
    fi
    printf '%s\n' "$sha"
}

ARM64_URL="$BASE_URL/coppercli-$VERSION-osx-arm64.tar.gz"
X64_URL="$BASE_URL/coppercli-$VERSION-osx-x64.tar.gz"
LINUX_URL="$BASE_URL/coppercli-$VERSION-linux-x64.tar.gz"

echo "  Fetching macOS ARM64..."
ARM64_SHA=$(download_and_hash "$ARM64_URL" "osx-arm64.tar.gz")
echo "    SHA256: $ARM64_SHA"

echo "  Fetching macOS x64..."
X64_SHA=$(download_and_hash "$X64_URL" "osx-x64.tar.gz")
echo "    SHA256: $X64_SHA"

echo "  Fetching Linux x64..."
LINUX_SHA=$(download_and_hash "$LINUX_URL" "linux-x64.tar.gz")
echo "    SHA256: $LINUX_SHA"
echo "Updating formula..."
# The checksums sit on their own lines, so sed cannot update them with the version.
sed \
    -e "s|version \".*\"|version \"$VERSION\"|" \
    -e "s|/download/v[^/]*/coppercli-v[^-]*-osx-arm64|/download/$VERSION/coppercli-$VERSION-osx-arm64|" \
    -e "s|/download/v[^/]*/coppercli-v[^-]*-osx-x64|/download/$VERSION/coppercli-$VERSION-osx-x64|" \
    -e "s|/download/v[^/]*/coppercli-v[^-]*-linux-x64|/download/$VERSION/coppercli-$VERSION-linux-x64|" \
    "$FORMULA" | awk -v arm64="$ARM64_SHA" -v x64="$X64_SHA" -v linux="$LINUX_SHA" '

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
' > "$candidate"

for expected in "version \"$VERSION\"" "$ARM64_URL" "$X64_URL" "$LINUX_URL" \
    "sha256 \"$ARM64_SHA\"" "sha256 \"$X64_SHA\"" "sha256 \"$LINUX_SHA\""; do
    if ! grep -Fq "$expected" "$candidate"; then
        echo "ERROR: Formula update did not include $expected"
        exit 1
    fi
done
mv "$candidate" "$FORMULA"

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
