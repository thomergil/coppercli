#!/usr/bin/env bash
#
# Platform-agnostic script to run coppercli
# Finds dotnet in common installation locations across macOS, Linux, and Windows
#

set -e

DOTNET_INSTALL_URL="https://dotnet.microsoft.com/download/dotnet/10.0"
PROJECT="coppercli/coppercli.csproj"

find_dotnet() {
    if command -v dotnet &> /dev/null; then
        echo "dotnet"
        return 0
    fi

    # macOS Homebrew keg-only dotnet@10 (Apple Silicon, then Intel)
    for dir in /opt/homebrew/opt/dotnet@10/bin /usr/local/opt/dotnet@10/bin; do
        if [[ -x "$dir/dotnet" ]]; then
            echo "$dir/dotnet"
            return 0
        fi
    done

    # macOS Homebrew symlink locations
    if [[ -x "/opt/homebrew/bin/dotnet" ]]; then
        echo "/opt/homebrew/bin/dotnet"
        return 0
    fi
    if [[ -x "/usr/local/bin/dotnet" ]]; then
        echo "/usr/local/bin/dotnet"
        return 0
    fi

    # macOS MacPorts
    if [[ -x "/opt/local/bin/dotnet" ]]; then
        echo "/opt/local/bin/dotnet"
        return 0
    fi

    # Linux common locations
    if [[ -x "/usr/bin/dotnet" ]]; then
        echo "/usr/bin/dotnet"
        return 0
    fi
    if [[ -x "/usr/share/dotnet/dotnet" ]]; then
        echo "/usr/share/dotnet/dotnet"
        return 0
    fi
    if [[ -x "$HOME/.dotnet/dotnet" ]]; then
        echo "$HOME/.dotnet/dotnet"
        return 0
    fi

    # Snap (Linux)
    if [[ -x "/snap/bin/dotnet" ]]; then
        echo "/snap/bin/dotnet"
        return 0
    fi

    # Windows (Git Bash / MSYS2 / WSL)
    if [[ -x "/c/Program Files/dotnet/dotnet.exe" ]]; then
        echo "/c/Program Files/dotnet/dotnet.exe"
        return 0
    fi
    if [[ -x "/mnt/c/Program Files/dotnet/dotnet.exe" ]]; then
        echo "/mnt/c/Program Files/dotnet/dotnet.exe"
        return 0
    fi

    return 1
}

# The script directory holds coppercli.csproj.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"
DOTNET=$(find_dotnet) || {
    echo "ERROR: dotnet not found!"
    echo ""
    echo "Please install .NET 10 SDK from:"
    echo "  $DOTNET_INSTALL_URL"
    echo ""
    echo "Or install via package manager:"
    echo "  macOS (Homebrew):  brew install dotnet@10"
    echo "  macOS (MacPorts):  sudo port install dotnet-sdk-10.0"
    echo "  Ubuntu/Debian:     sudo apt install dotnet-sdk-10.0"
    echo "  Fedora:            sudo dnf install dotnet-sdk-10.0"
    echo "  Arch:              sudo pacman -S dotnet-sdk-10.0"
    exit 1
}

if [ $# -eq 0 ]; then
    echo "Starting coppercli..."
else
    echo "Starting coppercli with args: $@"
fi
exec "$DOTNET" run --project "$PROJECT" -- "$@"
