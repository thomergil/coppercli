#!/usr/bin/env bash
#
# Create a GitHub release and upload all artifacts
# Run from repo root: ./scripts/create-release.sh
#
# Prerequisites:
#   - gh CLI installed and authenticated (brew install gh && gh auth login)
#   - Release artifacts built (./scripts/build-release.sh)
#

set -e

# Get version from CliConstants.cs
VERSION=$(grep 'AppVersion = ' coppercli/CliConstants.cs | sed 's/.*"\(.*\)".*/\1/')
if [[ -z "$VERSION" ]]; then
    echo "ERROR: Could not extract version from CliConstants.cs"
    exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
RELEASE_DIR="$REPO_ROOT/release"

echo "=== Creating GitHub Release $VERSION ==="
echo ""

# Check prerequisites
if ! command -v gh &> /dev/null; then
    echo "ERROR: gh CLI not found"
    echo "Install with: brew install gh"
    echo "Then run: gh auth login"
    exit 1
fi

if ! gh auth status &> /dev/null; then
    echo "ERROR: gh CLI not authenticated"
    echo "Run: gh auth login"
    exit 1
fi

if [[ ! -d "$RELEASE_DIR" ]]; then
    echo "ERROR: Release directory not found: $RELEASE_DIR"
    echo "Run ./scripts/build-release.sh first"
    exit 1
fi

# Check for release artifacts
ARTIFACTS=()
for f in "$RELEASE_DIR"/*; do
    if [[ -f "$f" ]]; then
        ARTIFACTS+=("$f")
    fi
done

if [[ ${#ARTIFACTS[@]} -eq 0 ]]; then
    echo "ERROR: No release artifacts found in $RELEASE_DIR"
    exit 1
fi

echo "Found ${#ARTIFACTS[@]} artifact(s):"
for f in "${ARTIFACTS[@]}"; do
    echo "  - $(basename "$f")"
done
echo ""

# Check if tag exists
TAG="$VERSION"
if git rev-parse "$TAG" &> /dev/null; then
    echo "Tag $TAG already exists"
else
    echo "Creating tag $TAG..."
    git tag "$TAG"
    git push origin "$TAG"
fi

# Check if release exists
if gh release view "$TAG" &> /dev/null; then
    echo ""
    echo "Release $TAG already exists."
    read -p "Delete and recreate? [y/N] " -n 1 -r
    echo
    if [[ $REPLY =~ ^[Yy]$ ]]; then
        gh release delete "$TAG" --yes
    else
        echo "Uploading additional assets to existing release..."
        gh release upload "$TAG" "${ARTIFACTS[@]}" --clobber
        echo ""
        echo "=== Done ==="
        gh release view "$TAG" --web
        exit 0
    fi
fi

# Generate release notes
NOTES="## Downloads

| Platform | File |
|----------|------|"

for f in "${ARTIFACTS[@]}"; do
    name=$(basename "$f")
    case "$name" in
        *setup.exe)     NOTES="$NOTES
| Windows Installer | \`$name\` |" ;;
        *windows*.exe)  NOTES="$NOTES
| Windows (portable) | \`$name\` |" ;;
        *macos-arm64*)  NOTES="$NOTES
| macOS Apple Silicon | \`$name\` |" ;;
        *macos-x64*)    NOTES="$NOTES
| macOS Intel | \`$name\` |" ;;
        *linux*)        NOTES="$NOTES
| Linux x64 | \`$name\` |" ;;
    esac
done

NOTES="$NOTES

## Installation

**Windows:** Download and run the installer, or extract the portable exe.

**macOS/Linux:**
\`\`\`bash
tar -xzf coppercli-$VERSION-<platform>.tar.gz
./coppercli
\`\`\`

Or build from source with \`./run.sh\` (requires .NET 8 SDK).
"

# Create release
echo "Creating release $TAG..."
gh release create "$TAG" \
    "${ARTIFACTS[@]}" \
    --title "$TAG" \
    --notes "$NOTES"

echo ""
echo "=== Release Created ==="
gh release view "$TAG" --web
