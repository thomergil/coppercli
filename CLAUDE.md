# Claude Code Guidelines for coppercli

## Code Style Principles

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

### No Magic Numbers
All numeric and string literals that have semantic meaning should be defined as constants. This includes:
- Timeout values
- Speed/feed rates
- UI strings and menu options
- Buffer sizes
- Retry counts

```csharp
// Bad
Thread.Sleep(500);
if (baud == 115200) ...

// Good
const int ConnectionDelayMs = 500;
const int DefaultBaudRate = 115200;
Thread.Sleep(ConnectionDelayMs);
if (baud == DefaultBaudRate) ...
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
