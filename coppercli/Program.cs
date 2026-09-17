// Entry point. The flags choose one of three modes: --server, --macro, or, with neither,
// the interactive menu loop.

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
        // Windows needs this for the box-drawing characters the menus and overlays use.
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // G-code requires a '.' decimal separator, which `GCodeFormat.Inv()` pins at every
        // emission site. Pinning the process too covers a site added without it, at the cost
        // of invariant number and date formats on screen in comma-decimal locales.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        AppState.Settings = Persistence.LoadSettings();
        AppState.Session = Persistence.LoadSession();

        bool debugMode = ParseDebugFlag(args);

        Logger.Enabled = AppState.Settings.EnableDebugLogging || debugMode;
        if (Logger.Enabled)
        {
            Logger.Clear();
            AnsiConsole.MarkupLine($"[{ColorDim}]Log: {Logger.LogFilePath}[/]");
            Logger.Log("=== Startup ===");
        }

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

        AppState.Machine = new Machine(AppState.Settings);
        SetupEventHandlers();

        AboutMenu.ShowExperimentalWarning(Persistence.SaveSettings);

        OfferAutoReconnect();

        ExitIfDisconnected();
        ConnectionMenu.OfferToHome();

        ExitIfDisconnected();
        OfferSessionRestore();

        while (true)
        {
            MainMenu.Show();
        }
    }

    private static bool ParseDebugFlag(string[] args)
    {
        return args.Any(a => a == "--debug" || a == "-d");
    }

    /// <summary>
    /// True when --server is present, whatever --proxy-port and --web-port parsed to.
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
            // --port predates the split into two ports, and still sets the web port.
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
    /// True when --macro is present; the --name value pairs after it fill the macro's
    /// placeholders.
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

        for (int i = macroArgIndex; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg == "--debug" || arg == "-d")
            {
                continue;
            }

            if (arg.StartsWith("--") && arg.Contains('='))
            {
                var eqIndex = arg.IndexOf('=');
                var name = arg.Substring(2, eqIndex - 2);
                var value = arg.Substring(eqIndex + 1);
                macroArgs[name] = value;
            }
            else if (arg.StartsWith("--") && i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            {
                var name = arg.Substring(2);
                var value = args[i + 1];
                macroArgs[name] = value;
                i++;
            }
        }

        return true;
    }

    private static void RunMacroMode(string macroFile, Dictionary<string, string> macroArgs)
    {
        macroFile = Path.GetFullPath(PathHelpers.ExpandTilde(macroFile));

        if (!File.Exists(macroFile))
        {
            AnsiConsole.MarkupLine($"[{ColorError}]Macro file not found: {Markup.Escape(macroFile)}[/]");
            Environment.Exit(1);
        }

        foreach (var key in macroArgs.Keys.ToList())
        {
            macroArgs[key] = PathHelpers.ExpandTilde(macroArgs[key]);
        }

        // MacroMode suppresses the homing prompt on connect.
        AppState.MacroMode = true;

        AppState.Machine = new Machine(AppState.Settings);
        SetupEventHandlers();

        OfferAutoReconnect();

        MacroMenu.RunMacroFromPath(macroFile, macroArgs);

        if (AppState.Machine.Connected)
        {
            AppState.Machine.Disconnect();
        }
    }

    /// <summary>
    /// SerialProxy listens on proxyPort, CncWebServer on webPort.
    /// </summary>
    private static void RunServerMode(int? proxyPort, int? webPort)
    {
        Logger.Log("RunServerMode: starting");
        Logger.Log($"RunServerMode: IsWorkZeroSet={AppState.IsWorkZeroSet}, HasStoredWorkZero={AppState.Session.HasStoredWorkZero}");
        var settings = AppState.Settings;

        if (string.IsNullOrEmpty(settings.SerialPortName))
        {
            AnsiConsole.MarkupLine($"[{ColorError}]No saved serial port settings. Run coppercli normally first to configure.[/]");
            Environment.Exit(1);
        }

        Logger.Log("RunServerMode: creating Machine");
        AppState.Machine = new Machine(settings);
        SetupEventHandlers();

        int actualProxyPort = proxyPort ?? ProxyDefaultPort;
        int actualWebPort = webPort ?? WebDefaultPort;

        Logger.Log("RunServerMode: calling ServerMenu.RunServer");
        ServerMenu.RunServer(settings.SerialPortName, settings.SerialPortBaud, actualProxyPort, actualWebPort, exitToMenu: false);
    }

    /// <summary>
    /// Connects without asking, whenever the saved settings name a serial port or an IP.
    /// </summary>
    private static void OfferAutoReconnect()
    {
        var settings = AppState.Settings;
        var session = AppState.Session;

        var connectionType = session.LastSuccessfulConnectionType ?? settings.ConnectionType;

        // QuickConnect reads the type out of the settings, so store it before calling.
        settings.ConnectionType = connectionType;

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
    /// Only the words. `SessionRestore` holds which prompts apply, what each answer does and
    /// when the sequence ends, so the terminal and the browser cannot differ on any of it.
    /// </summary>
    private static void OfferSessionRestore()
    {
        bool carriedOn = SessionRestore.AskPendingSteps(
            step =>
            {
                ExitIfDisconnected();

                if (!string.IsNullOrEmpty(step.Detail))
                {
                    AnsiConsole.MarkupLine($"[{ColorDim}]{Markup.Escape(step.Detail)}[/]");
                }

                return MenuHelpers.ConfirmOrQuit(step.Question, step.DefaultYes);
            },
            MenuHelpers.ShowError);

        if (!carriedOn)
        {
            Environment.Exit(0);
        }
    }

    /// <summary>
    /// Called between the startup prompts, where another client can take the port away.
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

    private static void SetupEventHandlers()
    {
        var machine = AppState.Machine;

        // A background error from $X or ~ should not interrupt the operator, so probing,
        // explicit suppression and auto-state clearing each silence these.
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

        machine.PinStateChanged += () =>
        {
            Logger.Log($"Pin state: Probe={machine.PinStateProbe}, LimitX={machine.PinStateLimitX}, LimitY={machine.PinStateLimitY}, LimitZ={machine.PinStateLimitZ}");
        };
        // ProbeFinished has no handler here; `ProbeController` consumes it.

        // The web UI can take the port from under a running terminal session.
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
