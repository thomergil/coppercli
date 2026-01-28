// coppercli - A CLI tool for PCB milling with GRBL
// Program.cs - Application entry point and coordinator

using coppercli.Core.Communication;
using coppercli.Core.GCode;
using coppercli.Core.Settings;
using coppercli.Helpers;
using coppercli.Macro;
using coppercli.Menus;
using Spectre.Console;
using static coppercli.CliConstants;

namespace coppercli;

class Program
{
    static void Main(string[] args)
    {
        // Load persisted settings and session
        AppState.Settings = Persistence.LoadSettings();
        AppState.Session = Persistence.LoadSession();

        // Parse command-line arguments
        bool debugMode = ParseDebugFlag(args);

        // Enable logging if configured or --debug flag
        Logger.Enabled = AppState.Settings.EnableDebugLogging || debugMode;
        if (Logger.Enabled)
        {
            AnsiConsole.MarkupLine($"[{ColorDim}]Log: {Logger.LogFilePath}[/]");
            Logger.Log("=== Startup ===");
        }

        if (TryParseProxyArgs(args, out int? proxyPort, out bool headless))
        {
            RunProxyMode(proxyPort, headless);
            return;
        }

        if (TryParseMacroArg(args, out string? macroFile, out var macroArgs) && macroFile != null)
        {
            RunMacroMode(macroFile, macroArgs);
            return;
        }

        // Create machine instance with loaded settings
        AppState.Machine = new Machine(AppState.Settings);

        // Wire up event handlers
        SetupEventHandlers();

        // Show experimental warning on first run
        AboutMenu.ShowExperimentalWarning(Persistence.SaveSettings);

        // Offer to auto-reconnect if we have saved connection settings
        OfferAutoReconnect();

        // Offer to home if connected
        ConnectionMenu.OfferToHome();

        // Offer to reload files and restore state from previous session
        OfferSessionRestore();

        // Main application loop
        while (true)
        {
            MainMenu.Show();
        }
    }

    /// <summary>
    /// Checks if --debug flag is present.
    /// </summary>
    private static bool ParseDebugFlag(string[] args)
    {
        return args.Any(a => a == "--debug" || a == "-d");
    }

    /// <summary>
    /// Parses command-line arguments for proxy mode.
    /// Returns true if --proxy flag is present.
    /// </summary>
    private static bool TryParseProxyArgs(string[] args, out int? port, out bool headless)
    {
        port = null;
        headless = false;
        bool proxyMode = false;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--proxy" || args[i] == "-p")
            {
                proxyMode = true;
            }
            else if (args[i] == "--headless" || args[i] == "-H")
            {
                headless = true;
            }
            else if (args[i] == "--port" && i + 1 < args.Length)
            {
                if (int.TryParse(args[i + 1], out int parsedPort))
                {
                    port = parsedPort;
                }
                i++; // Skip the port value
            }
            else if (args[i].StartsWith("--port="))
            {
                if (int.TryParse(args[i].Substring(7), out int parsedPort))
                {
                    port = parsedPort;
                }
            }
        }

        return proxyMode;
    }

    /// <summary>
    /// Parses command-line arguments for macro mode.
    /// Returns true if --macro flag is present.
    /// Also extracts any --name value pairs for placeholder substitution.
    /// </summary>
    private static bool TryParseMacroArg(string[] args, out string? macroFile, out Dictionary<string, string> macroArgs)
    {
        macroFile = null;
        macroArgs = new Dictionary<string, string>();
        int macroArgIndex = -1;

        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--macro" || args[i] == "-m") && i + 1 < args.Length)
            {
                macroFile = args[i + 1];
                macroArgIndex = i + 2;
                break;
            }
            else if (args[i].StartsWith("--macro="))
            {
                macroFile = args[i].Substring(8);
                macroArgIndex = i + 1;
                break;
            }
        }

        if (macroFile == null)
        {
            return false;
        }

        // Parse remaining args as --name value or --name=value pairs
        for (int i = macroArgIndex; i < args.Length; i++)
        {
            var arg = args[i];

            // Skip known global flags
            if (arg == "--debug" || arg == "-d")
            {
                continue;
            }

            if (arg.StartsWith("--") && arg.Contains('='))
            {
                // --name=value format
                var eqIndex = arg.IndexOf('=');
                var name = arg.Substring(2, eqIndex - 2);
                var value = arg.Substring(eqIndex + 1);
                macroArgs[name] = value;
            }
            else if (arg.StartsWith("--") && i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            {
                // --name value format
                var name = arg.Substring(2);
                var value = args[i + 1];
                macroArgs[name] = value;
                i++; // Skip value
            }
        }

        return true;
    }

    /// <summary>
    /// Runs macro mode - connects to machine and runs specified macro file.
    /// </summary>
    private static void RunMacroMode(string macroFile, Dictionary<string, string> macroArgs)
    {
        // Expand ~ for home directory
        if (macroFile.StartsWith("~"))
        {
            macroFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), macroFile.Substring(2));
        }

        // Convert to absolute path
        macroFile = Path.GetFullPath(macroFile);

        if (!File.Exists(macroFile))
        {
            AnsiConsole.MarkupLine($"[{ColorError}]Macro file not found: {Markup.Escape(macroFile)}[/]");
            Environment.Exit(1);
        }

        // Expand ~ in macro args that look like file paths
        foreach (var key in macroArgs.Keys.ToList())
        {
            var value = macroArgs[key];
            if (value.StartsWith("~"))
            {
                macroArgs[key] = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    value.Substring(2));
            }
        }

        // Set macro mode flag (suppresses homing prompt on connect)
        AppState.MacroMode = true;

        // Create machine instance with loaded settings
        AppState.Machine = new Machine(AppState.Settings);

        // Wire up event handlers
        SetupEventHandlers();

        // Auto-connect if we have saved connection settings
        OfferAutoReconnect();

        // Run the macro with provided args
        MacroMenu.RunMacroFromPath(macroFile, macroArgs);

        // Clean up
        if (AppState.Machine.Connected)
        {
            AppState.Machine.Disconnect();
        }
    }

    /// <summary>
    /// Runs proxy mode with saved serial settings and specified (or default) TCP port.
    /// </summary>
    private static void RunProxyMode(int? tcpPort, bool headless)
    {
        var settings = AppState.Settings;

        // Validate we have saved serial settings
        if (string.IsNullOrEmpty(settings.SerialPortName))
        {
            AnsiConsole.MarkupLine($"[{ColorError}]No saved serial port settings. Run coppercli normally first to configure.[/]");
            Environment.Exit(1);
        }

        int port = tcpPort ?? CliConstants.ProxyDefaultPort;

        if (headless)
        {
            ProxyMenu.RunHeadless(settings.SerialPortName, settings.SerialPortBaud, port);
        }
        else
        {
            ProxyMenu.RunInteractive(settings.SerialPortName, settings.SerialPortBaud, port);
        }
    }

    /// <summary>
    /// Auto-reconnects using saved settings on startup.
    /// </summary>
    private static void OfferAutoReconnect()
    {
        var settings = AppState.Settings;
        var session = AppState.Session;

        // Use last successful connection type if available, otherwise fall back to saved type
        var connectionType = session.LastSuccessfulConnectionType ?? settings.ConnectionType;

        // Set the connection type so QuickConnect uses the right one
        settings.ConnectionType = connectionType;

        // Auto-connect if we have saved connection settings for this type
        if (connectionType == ConnectionType.Serial && !string.IsNullOrEmpty(settings.SerialPortName))
        {
            ConnectionMenu.QuickConnect();
        }
        else if (connectionType == ConnectionType.Ethernet && !string.IsNullOrEmpty(settings.EthernetIP))
        {
            ConnectionMenu.QuickConnect();
        }
    }

    /// <summary>
    /// Offers to reload files and restore state from previous session.
    /// </summary>
    private static void OfferSessionRestore()
    {
        var session = AppState.Session;
        var machine = AppState.Machine;

        // Offer to reload last G-code file
        if (!string.IsNullOrEmpty(session.LastLoadedGCodeFile) && File.Exists(session.LastLoadedGCodeFile))
        {
            var fileName = Path.GetFileName(session.LastLoadedGCodeFile);
            var result = MenuHelpers.ConfirmOrQuit($"Reload last G-code file ({fileName})?", true);
            if (result == null)
            {
                Environment.Exit(0);
            }
            if (result == true)
            {
                FileMenu.LoadGCodeFromPath(session.LastLoadedGCodeFile);
            }
            else
            {
                // User rejected - clear the file so we don't keep asking
                // (LastBrowseDirectory is preserved so file browser starts in the right place)
                session.LastLoadedGCodeFile = "";
                Persistence.SaveSession();
            }
        }

        // Offer to trust stored work zero (must be decided before probe data, which depends on it)
        if (machine.Connected && session.HasStoredWorkZero)
        {
            var result = MenuHelpers.ConfirmOrQuit("Trust work zero from previous session?", true);
            if (result == null)
            {
                Environment.Exit(0);
            }
            if (result == true)
            {
                AppState.IsWorkZeroSet = true;
            }
        }

        // Offer to load saved probe data (only if work zero is trusted - probe data depends on it)
        if (AppState.IsWorkZeroSet && !string.IsNullOrEmpty(session.LastSavedProbeFile) && File.Exists(session.LastSavedProbeFile))
        {
            var fileName = Path.GetFileName(session.LastSavedProbeFile);
            var result = MenuHelpers.ConfirmOrQuit($"Load probe data {fileName}?", true);
            if (result == null)
            {
                Environment.Exit(0);
            }
            if (result == true)
            {
                try
                {
                    AppState.ProbePoints = ProbeGrid.Load(session.LastSavedProbeFile);
                    var pp = AppState.ProbePoints;

                    // Auto-apply probe data if G-code is loaded and probe is complete
                    if (AppState.ApplyProbeData())
                    {
                        AnsiConsole.MarkupLine($"[{ColorSuccess}]Probe data loaded and applied: {pp.TotalPoints} points[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[{ColorSuccess}]Probe data loaded: {pp.TotalPoints} points[/]");
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[{ColorError}]Error: {Markup.Escape(ex.Message)}[/]");
                }
            }
        }

        // Offer to continue incomplete probing (only if work zero trusted and no complete probe loaded)
        if (machine.Connected && AppState.IsWorkZeroSet)
        {
            var hasCompleteProbe = AppState.ProbePoints != null && AppState.ProbePoints.NotProbed.Count == 0;
            if (!hasCompleteProbe && !string.IsNullOrEmpty(session.ProbeAutoSavePath) && File.Exists(session.ProbeAutoSavePath))
            {
                var result = MenuHelpers.ConfirmOrQuit("Continue incomplete probing session?", true);
                if (result == null)
                {
                    Environment.Exit(0);
                }
                if (result == true)
                {
                    try
                    {
                        AppState.ProbePoints = ProbeGrid.Load(session.ProbeAutoSavePath);
                        AppState.AreProbePointsApplied = false;
                        var hm = AppState.ProbePoints;
                        AnsiConsole.MarkupLine($"[{ColorSuccess}]Loaded probe progress: {hm.Progress}/{hm.TotalPoints} points[/]");
                        // Start probing directly
                        ProbeMenu.ContinueProbing();
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[{ColorError}]Error: {Markup.Escape(ex.Message)}[/]");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Configures event handlers for machine events.
    /// </summary>
    private static void SetupEventHandlers()
    {
        var machine = AppState.Machine;

        // Error and info message handlers - suppress during probing, explicit suppression,
        // or auto-state clearing (background errors from $X or ~ shouldn't interrupt user)
        machine.NonFatalException += msg =>
        {
            if (!AppState.Probing && !AppState.SuppressErrors && !machine.EnableAutoStateClear)
            {
                AnsiConsole.MarkupLine($"[{ColorError}]Error: {Markup.Escape(msg)}[/]");
            }
        };

        machine.Info += msg =>
        {
            if (!AppState.Probing && !AppState.SuppressErrors && !machine.EnableAutoStateClear)
            {
                AnsiConsole.MarkupLine($"[{ColorPrompt}]Info: {Markup.Escape(msg)}[/]");
            }
        };

        // Probe completion handler - delegates to ProbeMenu
        machine.ProbeFinished += ProbeMenu.OnProbeFinished;
    }
}
