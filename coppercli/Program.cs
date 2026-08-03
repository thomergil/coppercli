// coppercli - A CLI tool for PCB milling with GRBL
// Program.cs - Application entry point and coordinator

using System.Globalization;
using coppercli.Core.Communication;
using coppercli.Core.Controllers;
using coppercli.Core.GCode;
using coppercli.Core.Settings;
using coppercli.Helpers;
using coppercli.Macro;
using coppercli.Menus;
using Spectre.Console;
using static coppercli.CliConstants;
using static coppercli.Core.Util.Constants;

namespace coppercli;

class Program
{
    static void Main(string[] args)
    {
        // Enable UTF-8 output for Unicode box-drawing characters (required on Windows)
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // G-code requires a '.' decimal separator. GCodeFormat.Inv() enforces that at
        // every emission site; pinning the process as well means a site that is ever
        // added without it still cannot emit "Z-1,000" for GRBL to reject.
        //
        // The cost is deliberate and worth naming: operators in comma-decimal locales
        // see numbers and dates in the TUI and web UI formatted the invariant way. A
        // wrong separator in a coordinate moves the cutter; a dot in a displayed date
        // does not.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        // Load persisted settings and session
        AppState.Settings = Persistence.LoadSettings();
        AppState.Session = Persistence.LoadSession();

        // Parse command-line arguments
        bool debugMode = ParseDebugFlag(args);

        // Enable logging if configured or --debug flag
        Logger.Enabled = AppState.Settings.EnableDebugLogging || debugMode;
        if (Logger.Enabled)
        {
            Logger.Clear();  // Start fresh each run
            AnsiConsole.MarkupLine($"[{ColorDim}]Log: {Logger.LogFilePath}[/]");
            Logger.Log("=== Startup ===");
        }

        // Wire up controller logging to use Logger
        ControllerLog.LogAction = Logger.Log;

        if (TryParseServerArgs(args, out int? proxyPort, out int? webPort))
        {
            RunServerMode(proxyPort, webPort);
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
        ExitIfDisconnected();
        ConnectionMenu.OfferToHome();

        // Offer to reload files and restore state from previous session
        ExitIfDisconnected();
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
    /// Parses command-line arguments for server mode.
    /// Returns true if --server flag is present.
    /// Supports --proxy-port and --web-port for configuring individual ports.
    /// </summary>
    private static bool TryParseServerArgs(string[] args, out int? proxyPort, out int? webPort)
    {
        proxyPort = null;
        webPort = null;
        bool serverMode = false;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--server" || args[i] == "-s")
            {
                serverMode = true;
            }
            else if (args[i] == "--proxy-port" && i + 1 < args.Length)
            {
                if (int.TryParse(args[i + 1], out int parsedPort))
                {
                    proxyPort = parsedPort;
                }
                i++;
            }
            else if (args[i].StartsWith("--proxy-port="))
            {
                if (int.TryParse(args[i].Substring(13), out int parsedPort))
                {
                    proxyPort = parsedPort;
                }
            }
            else if (args[i] == "--web-port" && i + 1 < args.Length)
            {
                if (int.TryParse(args[i + 1], out int parsedPort))
                {
                    webPort = parsedPort;
                }
                i++;
            }
            else if (args[i].StartsWith("--web-port="))
            {
                if (int.TryParse(args[i].Substring(11), out int parsedPort))
                {
                    webPort = parsedPort;
                }
            }
            // Legacy --port support (applies to web port for backwards compatibility)
            else if (args[i] == "--port" && i + 1 < args.Length)
            {
                if (int.TryParse(args[i + 1], out int parsedPort))
                {
                    webPort = parsedPort;
                }
                i++;
            }
            else if (args[i].StartsWith("--port="))
            {
                if (int.TryParse(args[i].Substring(7), out int parsedPort))
                {
                    webPort = parsedPort;
                }
            }
        }

        return serverMode;
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
        // Expand ~ and convert to absolute path
        macroFile = Path.GetFullPath(PathHelpers.ExpandTilde(macroFile));

        if (!File.Exists(macroFile))
        {
            AnsiConsole.MarkupLine($"[{ColorError}]Macro file not found: {Markup.Escape(macroFile)}[/]");
            Environment.Exit(1);
        }

        // Expand ~ in macro args that look like file paths
        foreach (var key in macroArgs.Keys.ToList())
        {
            macroArgs[key] = PathHelpers.ExpandTilde(macroArgs[key]);
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
    /// Runs server mode: SerialProxy on proxyPort, CncWebServer on webPort.
    /// </summary>
    private static void RunServerMode(int? proxyPort, int? webPort)
    {
        Logger.Log("RunServerMode: starting");
        Logger.Log($"RunServerMode: IsWorkZeroSet={AppState.IsWorkZeroSet}, HasStoredWorkZero={AppState.Session.HasStoredWorkZero}");
        var settings = AppState.Settings;

        // Validate we have saved serial settings
        if (string.IsNullOrEmpty(settings.SerialPortName))
        {
            AnsiConsole.MarkupLine($"[{ColorError}]No saved serial port settings. Run coppercli normally first to configure.[/]");
            Environment.Exit(1);
        }

        // Create machine instance (needed by web server)
        Logger.Log("RunServerMode: creating Machine");
        AppState.Machine = new Machine(settings);
        SetupEventHandlers();

        int actualProxyPort = proxyPort ?? ProxyDefaultPort;
        int actualWebPort = webPort ?? WebDefaultPort;

        // Use unified server implementation
        Logger.Log("RunServerMode: calling ServerMenu.RunServer");
        ServerMenu.RunServer(settings.SerialPortName, settings.SerialPortBaud, actualProxyPort, actualWebPort, exitToMenu: false);
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
    /// <summary>
    /// Asks the questions carried over from the previous session.
    ///
    /// Which questions apply, and what each answer does, is decided in SessionRestore -
    /// shared with the browser interface. This method only asks them. The two used to
    /// each implement the sequence, and drifted: the terminal grew a condition that
    /// skipped the height-map question whenever the work zero was not trusted, leaving
    /// that data undecided on disk to resurface later as though it were current.
    /// </summary>
    private static void OfferSessionRestore()
    {
        foreach (var step in SessionRestore.GetPendingSteps())
        {
            ExitIfDisconnected();

            if (!string.IsNullOrEmpty(step.Detail))
            {
                AnsiConsole.MarkupLine($"[{ColorDim}]{Markup.Escape(step.Detail)}[/]");
            }

            var answer = MenuHelpers.ConfirmOrQuit(step.Question, step.DefaultYes);

            if (answer == null)
            {
                Environment.Exit(0);
            }

            SessionRestore.Answer(step.Topic, answer == true);
        }
    }

    /// <summary>
    /// Exits if the machine was disconnected (e.g., by another client force-disconnecting).
    /// Called between opening questions to detect disconnection.
    /// </summary>
    private static void ExitIfDisconnected()
    {
        if (!AppState.Machine.Connected)
        {
            Logger.Log("ExitIfDisconnected: machine no longer connected, exiting");
            AnsiConsole.MarkupLine($"[{ColorWarning}]Disconnected.[/]");
            Environment.Exit(0);
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
            if (!AppState.IsProbing && !AppState.SuppressErrors && !machine.EnableAutoStateClear)
            {
                AnsiConsole.MarkupLine($"[{ColorError}]Error: {Markup.Escape(msg)}[/]");
            }
        };

        machine.Info += msg =>
        {
            if (!AppState.IsProbing && !AppState.SuppressErrors && !machine.EnableAutoStateClear)
            {
                AnsiConsole.MarkupLine($"[{ColorPrompt}]Info: {Markup.Escape(msg)}[/]");
            }
        };

        // Log pin state changes for debugging
        machine.PinStateChanged += () =>
        {
            Logger.Log($"Pin state: Probe={machine.PinStateProbe}, LimitX={machine.PinStateLimitX}, LimitY={machine.PinStateLimitY}, LimitZ={machine.PinStateLimitZ}");
        };
        // Note: ProbeFinished is handled by ProbeController internally

        // Exit if force-disconnected by another client (e.g., web UI taking over)
        machine.LineReceived += line =>
        {
            if (line.StartsWith(ProxyForceDisconnectPrefix))
            {
                Logger.Log("Force-disconnected by another client, exiting");
                AnsiConsole.MarkupLine($"[{ColorWarning}]Disconnected by another client.[/]");
                Environment.Exit(0);
            }
        };
    }
}
