// coppercli - A CLI tool for PCB milling with GRBL
// Program.cs - Application entry point and coordinator

using coppercli.Core.Communication;
using coppercli.Core.GCode;
using coppercli.Core.Settings;
using coppercli.Helpers;
using coppercli.Menus;
using Spectre.Console;

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
            AnsiConsole.MarkupLine($"[dim]Log: {Logger.LogFilePath}[/]");
            Logger.Log("=== Startup ===");
        }

        if (TryParseProxyArgs(args, out int? proxyPort, out bool headless))
        {
            RunProxyMode(proxyPort, headless);
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
    /// Runs proxy mode with saved serial settings and specified (or default) TCP port.
    /// </summary>
    private static void RunProxyMode(int? tcpPort, bool headless)
    {
        var settings = AppState.Settings;

        // Validate we have saved serial settings
        if (string.IsNullOrEmpty(settings.SerialPortName))
        {
            AnsiConsole.MarkupLine("[red]No saved serial port settings. Run coppercli normally first to configure.[/]");
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
                        AnsiConsole.MarkupLine($"[green]Probe data loaded and applied: {pp.TotalPoints} points[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[green]Probe data loaded: {pp.TotalPoints} points[/]");
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
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
                        AnsiConsole.MarkupLine($"[green]Loaded probe progress: {hm.Progress}/{hm.TotalPoints} points[/]");
                        // Start probing directly
                        ProbeMenu.ContinueProbing();
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
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
                AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(msg)}[/]");
            }
        };

        machine.Info += msg =>
        {
            if (!AppState.Probing && !AppState.SuppressErrors && !machine.EnableAutoStateClear)
            {
                AnsiConsole.MarkupLine($"[blue]Info: {Markup.Escape(msg)}[/]");
            }
        };

        // Probe completion handler - delegates to ProbeMenu
        machine.ProbeFinished += ProbeMenu.OnProbeFinished;
    }
}
