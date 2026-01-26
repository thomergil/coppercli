# Claude Code Guidelines for coppercli

## Code Style Principles

### Simplicity First
Always pursue simplicity. If code is getting complex, twisted, or gnarly, stop and rethink the approach. Simple code is easier to understand, debug, and maintain. If you find yourself adding multiple flags, nested conditions, or complex state tracking, step back and find a cleaner solution.

### Always Use Braces
Always use `{ }` for control statements, even for single-line bodies. This improves readability and prevents bugs when adding lines later.

```csharp
// Bad
if (condition)
    DoSomething();

// Good
if (condition)
{
    DoSomething();
}
```

### No Magic Numbers or Strings - EVER
**NEVER use literal numbers or strings that have semantic meaning.** Always define constants. No exceptions. This includes:
- Timeout values
- Speed/feed rates
- UI strings and menu options
- Buffer sizes
- Retry counts
- Status strings (e.g., "Idle", "Home", "Run")
- Protocol values

If a constant doesn't exist, CREATE ONE before using the value.

```csharp
// BAD - NEVER do this
Thread.Sleep(500);
if (baud == 115200) ...
if (status == "Home") ...
if (status.StartsWith("Alarm")) ...

// GOOD - always use constants
const int ConnectionDelayMs = 500;
const int DefaultBaudRate = 115200;
Thread.Sleep(ConnectionDelayMs);
if (baud == DefaultBaudRate) ...
if (status == StatusHome) ...
if (status.StartsWith(StatusAlarm)) ...
```

### DRY (Don't Repeat Yourself)
- Extract repeated code into reusable functions
- Use constants for repeated values
- Create helper methods for common patterns
- If you see similar code blocks, refactor into a shared implementation

```csharp
// Bad - repeated pattern
if (!machine.Connected)
{
    AnsiConsole.MarkupLine("[red]Not connected![/]");
    Console.ReadKey();
    return;
}

// Good - extract to helper
static bool RequireConnection()
{
    if (!machine.Connected)
    {
        AnsiConsole.MarkupLine("[red]Not connected![/]");
        Console.ReadKey();
        return false;
    }
    return true;
}

// Usage
if (!RequireConnection()) return;
```

### Menu Definitions
Menu options should be defined as structured data (arrays of structs or tuples) rather than scattered strings:

```csharp
static readonly (string Label, char Mnemonic, Action Handler)[] MainMenuItems = {
    ("Connect/Disconnect", 'c', ConnectionMenu),
    ("Home ($H)", 'h', HomeMenu),
    // ...
};
```

## Project Structure

- `coppercli/` - Main application (.NET 8)
- `coppercli.Core/` - Platform-independent core library (.NET 8)

## Git

**IMPORTANT:** NEVER run git commands (add, commit, push, etc.) unless the user EXPLICITLY asks. Do not volunteer git commands. Do not stage files automatically after editing. Do not commit. Do not push. Wait for explicit instructions.

## Building

**IMPORTANT:** Do not compile unless the user explicitly asks. This project is synced via Dropbox, and compiling while the user is also working can create conflicted copies of build artifacts.

### Running

Use the platform-agnostic run scripts which auto-detect dotnet location:

```bash
# macOS/Linux
./run.sh

# Windows (double-click or command line)
run.bat
```

### Manual build commands

```bash
dotnet build coppercli/coppercli.csproj
dotnet run --project coppercli/coppercli.csproj
dotnet build coppercli/coppercli.csproj -warnaserror  # treat warnings as errors
```

## Releases

### Creating a Release

Releases are automated via GitHub Actions. When you push a tag, GitHub builds for all platforms and creates a release:

```bash
git tag v0.1.2
git push origin v0.1.2
```

This automatically:
- Builds Windows x64 (portable exe)
- Builds Windows installer (via Inno Setup)
- Builds macOS ARM64 and x64
- Builds Linux x64
- Creates a GitHub Release with all artifacts

### Windows Installer

The Windows installer is built with [Inno Setup](https://jrsoftware.org/isinfo.php). Configuration is in `installer/coppercli.iss`.

To build locally on Windows:
```bash
installer\build-installer.bat
```

The icon (`installer/coppercli.ico`) was generated from a JPG using ImageMagick:
```bash
magick input.jpg -define icon:auto-resize=256,128,64,48,32,16 coppercli.ico
```

### Homebrew Tap

The Homebrew tap is a separate repo: `github.com/thomergil/homebrew-coppercli` (cloned at `~/src/homebrew-coppercli`).

After creating a GitHub release, update the formula:
```bash
./scripts/update-homebrew-formula.sh v0.1.2
cd ~/src/homebrew-coppercli
git add Formula/coppercli.rb
git commit -m "Update to v0.1.2"
git push
```

Users install with:
```bash
brew tap thomergil/coppercli
brew install coppercli
```

### Version Number

The version is defined in `coppercli/CliConstants.cs`:
```csharp
public const string AppVersion = "v0.1.1";
```

Update this before creating a new release tag.
