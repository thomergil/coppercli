#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "$0")/.." && pwd)
test_root=$(mktemp -d)
trap 'rm -rf "$test_root"' EXIT
mkdir -p "$test_root/tap/Formula" "$test_root/bin" "$test_root/asset"

cat > "$test_root/tap/Formula/coppercli.rb" <<'FORMULA'
class Coppercli < Formula
  version "v0.5.2"
  url "https://github.com/thomergil/coppercli/releases/download/v0.5.2/coppercli-v0.5.2-osx-arm64.tar.gz"
  sha256 "old-arm64"
  url "https://github.com/thomergil/coppercli/releases/download/v0.5.2/coppercli-v0.5.2-osx-x64.tar.gz"
  sha256 "old-x64"
  url "https://github.com/thomergil/coppercli/releases/download/v0.5.2/coppercli-v0.5.2-linux-x64.tar.gz"
  sha256 "old-linux"
end
FORMULA
cp "$test_root/tap/Formula/coppercli.rb" "$test_root/original.rb"
printf 'smoke\n' > "$test_root/asset/coppercli"
tar -czf "$test_root/asset.tar.gz" -C "$test_root/asset" coppercli
expected_sha=$(shasum -a 256 "$test_root/asset.tar.gz" | cut -d' ' -f1)

cat > "$test_root/bin/curl" <<'CURL'
#!/bin/sh
[ "${FAKE_CURL_FAIL:-0}" = 1 ] && exit 22
while [ "$#" -gt 0 ]; do
    if [ "$1" = --output ]; then
        shift
        output=$1
    fi
    shift
done
cp "$FAKE_ASSET" "$output"
printf '\n'
CURL
chmod +x "$test_root/bin/curl"

HOMEBREW_TAP="$test_root/tap" FAKE_ASSET="$test_root/asset.tar.gz" \
    PATH="$test_root/bin:$PATH" bash "$repo_root/scripts/update-homebrew-formula.sh" v0.6.0 > "$test_root/success.log"
formula="$test_root/tap/Formula/coppercli.rb"
grep -Fq 'version "v0.6.0"' "$formula"
grep -Fq 'download/v0.6.0/coppercli-v0.6.0-linux-x64.tar.gz' "$formula"
[ "$(grep -Fc "sha256 \"$expected_sha\"" "$formula")" -eq 3 ]
grep -Fq "cd $test_root/tap" "$test_root/success.log"
grep -Fq "git commit -m 'Update to v0.6.0'" "$test_root/success.log"
if grep -Eq '^[0-9]+\. ' "$test_root/success.log"; then
    echo "Next steps must be copyable shell commands" >&2
    exit 1
fi

cp "$test_root/original.rb" "$formula"
if HOMEBREW_TAP="$test_root/tap" FAKE_CURL_FAIL=1 PATH="$test_root/bin:$PATH" \
    bash "$repo_root/scripts/update-homebrew-formula.sh" v0.6.0 > "$test_root/failure.log" 2>&1; then
    echo "Expected a failed download to stop the updater" >&2
    exit 1
fi
cmp "$test_root/original.rb" "$formula"
