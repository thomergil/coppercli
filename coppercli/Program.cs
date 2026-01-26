using coppercli.Core.Communication;
using coppercli.Core.GCode;
using coppercli.Core.Settings;
using coppercli.Core.Util;
using Spectre.Console;
using System.Text.Json;

namespace coppercli;

class Program
{
    const int ConnectionTimeoutMs = 5000;
    const int GrblResponseTimeoutMs = 3000;
    const int AutoDetectTimeoutMs = 2000;

    // Baud rates in order of likelihood for GRBL/CNC controllers
    // 115200: GRBL v0.9+ default (most common)
    // 250000: Some high-speed configurations
    // 9600: Older GRBL, some Bluetooth modules
    // 57600, 38400, 19200: Less common alternatives
    static readonly int[] CommonBaudRates = { 115200, 250000, 9600, 57600, 38400, 19200 };

    // File paths
    const string SettingsFileName = "settings.json";
    const string SessionFileName = "session.json";
    const string ProbeAutoSaveFileName = "probe_autosave.pgrid";

    // App info
    const string AppTitle = "coppercli";

    // File extensions
    static readonly string[] GCodeExtensions = { ".nc", ".gcode", ".ngc", ".gc", ".tap", ".cnc" };
    static readonly string[] ProbeGridExtensions = { ".pgrid" };

    // Probing defaults
    const double DefaultProbeMargin = 0.5;
    const double DefaultProbeGridSize = 5.0;

    // Timing constants
    const int StatusPollIntervalMs = 100;
    const int JogPollIntervalMs = 50;
    const int CommandDelayMs = 200;
    const int ConfirmationDisplayMs = 1000;
    const int ResetWaitMs = 500;          // Wait after soft reset for GRBL to reinitialize
    const int IdleWaitTimeoutMs = 3000;   // Max wait for machine to reach Idle state
    const double SafeExitHeightMm = 6.0;

    // Version
    const string Version = "v0.1.0";

    // GRBL status strings (used for status comparisons)
    const string StatusIdle = "Idle";
    const string StatusAlarm = "Alarm";
    const string StatusHold = "Hold";
    const string StatusDoor = "Door";
    const string StatusDisconnected = "Disconnected";

    // Menu item definition: (Label, Mnemonic, Number)
    static readonly (string Label, char Mnemonic, char Number)[] MainMenuItems = {
        ("Connect/Disconnect", 'c', '1'),
        ("Load G-Code File", 'l', '2'),
        ("Move", 'm', '3'),
        ("Probe", 'p', '4'),
        ("Mill", 'g', '5'),
        ("Settings", 't', '6'),
        ("About", 'a', '7'),
        ("Exit", 'q', '0'),
    };

    // JSON serialization options (shared)
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };


    static Machine machine = null!;
    static MachineSettings settings = null!;
    static SessionState session = null!;
    static GCodeFile? currentFile;
    static ProbeGrid? heightMap;
    static bool heightMapApplied = false;
    static bool workZeroSet = false;
    static bool probing = false;
    static bool suppressErrors = false;  // Temporarily suppress error messages (e.g., during stop sequence)
    static bool singleProbing = false;
    static Action<Vector3, bool>? singleProbeCallback;

    /// <summary>
    /// Checks if machine is connected. Shows error and waits for key if not.
    /// Returns true if connected, false otherwise.
    /// </summary>
    static bool RequireConnection()
    {
        if (machine.Connected)
        {
            return true;
        }
        AnsiConsole.MarkupLine("[red]Not connected![/]");
        Console.ReadKey();
        return false;
    }

    /// <summary>
    /// Checks if a key press matches a given ConsoleKey or character.
    /// Handles cross-platform compatibility where key.Key or key.KeyChar may work differently.
    /// </summary>
    static bool IsKey(ConsoleKeyInfo key, ConsoleKey consoleKey, char c)
    {
        return key.Key == consoleKey || char.ToLower(key.KeyChar) == char.ToLower(c);
    }

    /// <summary>
    /// Checks if a key press is Escape.
    /// </summary>
    static bool IsEscapeKey(ConsoleKeyInfo key)
    {
        return key.Key == ConsoleKey.Escape;
    }

    /// <summary>
    /// Checks if a key press is Enter.
    /// </summary>
    static bool IsEnterKey(ConsoleKeyInfo key)
    {
        return key.Key == ConsoleKey.Enter;
    }

    /// <summary>
    /// Checks if a key press is Backspace.
    /// </summary>
    static bool IsBackspaceKey(ConsoleKeyInfo key)
    {
        return key.Key == ConsoleKey.Backspace;
    }

    /// <summary>
    /// Checks if a key press is an exit key (Escape, Q, or 0).
    /// </summary>
    static bool IsExitKey(ConsoleKeyInfo key)
    {
        return IsEscapeKey(key) || IsKey(key, ConsoleKey.Q, 'q') || IsKey(key, ConsoleKey.D0, '0');
    }

    /// <summary>
    /// Formats a TimeSpan as H:MM:SS or M:SS depending on duration.
    /// Uses shorter format for sub-hour durations to save screen space.
    /// </summary>
    static string FormatTimeSpan(TimeSpan ts)
    {
        // Always use HH:MM:SS format for consistent width
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    /// <summary>
    /// Safely gets console window dimensions, returning defaults if unavailable.
    /// Window queries can throw on terminal resize or when running without a TTY.
    /// </summary>
    static (int Width, int Height) GetSafeWindowSize()
    {
        const int DefaultWidth = 80;
        const int DefaultHeight = 24;
        try
        {
            return (Console.WindowWidth, Console.WindowHeight);
        }
        catch
        {
            return (DefaultWidth, DefaultHeight);
        }
    }

    /// <summary>
    /// Builds an ASCII progress bar string.
    /// </summary>
    static string BuildProgressBar(double percentage, int width)
    {
        int filled = (int)(percentage / 100.0 * width);
        return new string('█', filled) + new string('░', width - filled);
    }

    /// <summary>
    /// Calculates visible length of a string, excluding ANSI escape codes.
    /// </summary>
    static int VisibleLength(string text)
    {
        return System.Text.RegularExpressions.Regex.Replace(text, @"\x1b\[[0-9;]*m", "").Length;
    }

    /// <summary>
    /// Writes a line padded with spaces to clear old content.
    /// Handles ANSI codes correctly for length calculation.
    /// Prevents line wrapping which causes display corruption with cursor repositioning.
    /// </summary>
    static void WriteLineTruncated(string text, int maxWidth)
    {
        if (maxWidth <= 0)
        {
            return;
        }
        int visible = VisibleLength(text);
        // Pad to fill line width (clears old content)
        if (visible < maxWidth - 1)
        {
            Console.WriteLine(text + new string(' ', maxWidth - visible - 1));
        }
        else
        {
            Console.WriteLine(text);
        }
    }

    /// <summary>
    /// Pauses G-code execution.
    /// </summary>
    static void PauseExecution()
    {
        machine.FilePause();
        machine.FeedHold();
    }

    /// <summary>
    /// Resumes G-code execution and enters monitor mode.
    /// </summary>
    static void ResumeExecution()
    {
        machine.CycleStart();
        machine.FileStart();
    }

    /// <summary>
    /// Stops G-code execution gracefully (feed hold then pause).
    /// </summary>
    static void StopExecution()
    {
        machine.FeedHold();  // Graceful deceleration stop
        machine.FilePause(); // Change mode to manual
    }

    /// <summary>
    /// Flushes any buffered keyboard input to prevent keypresses from bleeding into subsequent prompts.
    /// </summary>
    static void FlushKeyboard()
    {
        while (Console.KeyAvailable)
        {
            Console.ReadKey(true);
        }
    }

    /// <summary>
    /// Standard y/n confirmation prompt.
    /// </summary>
    static bool Confirm(string prompt, bool defaultValue = true)
    {
        return ConfirmInternal(prompt, defaultValue, allowCancel: false, exitOnCancel: false);
    }

    /// <summary>
    /// Confirmation prompt that accepts q/Escape to cancel and return to menu.
    /// Use this in multi-step flows where user might want to abort entirely.
    /// </summary>
    static bool ConfirmOrCancel(string prompt, bool defaultValue = true)
    {
        return ConfirmInternal(prompt, defaultValue, allowCancel: true, exitOnCancel: false);
    }

    /// <summary>
    /// Confirmation prompt that accepts q/Escape to exit the program entirely.
    /// Use this for initial startup questions where user should be able to quit.
    /// </summary>
    static bool ConfirmOrExit(string prompt, bool defaultValue = true)
    {
        return ConfirmInternal(prompt, defaultValue, allowCancel: true, exitOnCancel: true);
    }

    /// <summary>
    /// Internal confirmation implementation.
    /// </summary>
    static bool ConfirmInternal(string prompt, bool defaultValue, bool allowCancel, bool exitOnCancel = false)
    {
        FlushKeyboard();
        string defaultHint = defaultValue
            ? (allowCancel ? "Y/n/q" : "Y/n")
            : (allowCancel ? "y/N/q" : "y/N");
        AnsiConsole.Markup($"{prompt} [dim]({defaultHint})[/] ");

        while (true)
        {
            var key = Console.ReadKey(true);

            if (IsEnterKey(key))
            {
                AnsiConsole.WriteLine(defaultValue ? "y" : "n");
                return defaultValue;
            }
            else if (IsKey(key, ConsoleKey.Y, 'y'))
            {
                AnsiConsole.WriteLine("y");
                return true;
            }
            else if (IsKey(key, ConsoleKey.N, 'n'))
            {
                AnsiConsole.WriteLine("n");
                return false;
            }
            else if (allowCancel && (IsKey(key, ConsoleKey.Q, 'q') || IsEscapeKey(key)))
            {
                AnsiConsole.WriteLine("[quit]");
                if (exitOnCancel)
                {
                    Environment.Exit(0);
                }
                return false;
            }
            // Ignore other keys, keep waiting
        }
    }

    /// <summary>
    /// Prompts for text input with a default value. Returns null if user presses Escape to cancel.
    /// This prevents users from getting "trapped" in input prompts.
    /// </summary>
    static string? AskOrCancel(string prompt, string defaultValue = "")
    {
        FlushKeyboard();
        AnsiConsole.Markup($"{prompt} [dim](Esc=cancel)[/] ");
        if (!string.IsNullOrEmpty(defaultValue))
        {
            AnsiConsole.Markup($"[dim]({defaultValue})[/] ");
        }

        var input = new System.Text.StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(true);

            if (IsEscapeKey(key))
            {
                AnsiConsole.WriteLine("[cancelled]");
                return null;
            }
            else if (IsEnterKey(key))
            {
                AnsiConsole.WriteLine();
                string result = input.Length > 0 ? input.ToString() : defaultValue;
                return result;
            }
            else if (IsBackspaceKey(key))
            {
                if (input.Length > 0)
                {
                    input.Length--;
                    Console.Write("\b \b");  // Erase character
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                input.Append(key.KeyChar);
                Console.Write(key.KeyChar);
            }
        }
    }

    /// <summary>
    /// Prompts for numeric input with a default value. Returns null if user presses Escape to cancel.
    /// </summary>
    static double? AskDoubleOrCancel(string prompt, double defaultValue)
    {
        string? result = AskOrCancel(prompt, defaultValue.ToString());
        if (result == null)
        {
            return null;
        }
        if (double.TryParse(result, out double value))
        {
            return value;
        }
        AnsiConsole.MarkupLine("[red]Invalid number, using default[/]");
        return defaultValue;
    }

    /// <summary>
    /// Prompts for integer input with a default value. Returns null if user presses Escape to cancel.
    /// </summary>
    static int? AskIntOrCancel(string prompt, int defaultValue)
    {
        string? result = AskOrCancel(prompt, defaultValue.ToString());
        if (result == null)
        {
            return null;
        }
        if (int.TryParse(result, out int value))
        {
            return value;
        }
        AnsiConsole.MarkupLine("[red]Invalid number, using default[/]");
        return defaultValue;
    }

    /// <summary>
    /// Offers to home the machine after connection if not in alarm state.
    /// </summary>
    static void OfferToHome(string grblStatus)
    {
        if (!grblStatus.StartsWith(StatusAlarm) && ConfirmOrExit("Home machine?", false))
        {
            machine.SoftReset();
            machine.SendLine("$H");
            AnsiConsole.MarkupLine("[green]Homing command sent[/]");
        }
    }

    /// <summary>
    /// Offers to trust the stored work coordinate system (G54) from a previous session.
    /// GRBL remembers the work offset even after disconnect/reconnect, so if the user
    /// set work zero on a PCB corner before, it's likely still valid.
    /// </summary>
    static void OfferToAcceptStoredWorkZero()
    {
        // Skip if already set (e.g., user homed the machine)
        if (workZeroSet)
        {
            return;
        }

        AnsiConsole.MarkupLine("[yellow]Choose N if uncertain.[/]");
        if (ConfirmOrExit("Trust work zero from previous session?", true))
        {
            workZeroSet = true;
            AnsiConsole.MarkupLine("[green]Work zero accepted[/]");
        }
    }

    /// <summary>
    /// Offers to reload the last loaded G-Code file if one exists.
    /// </summary>
    static void OfferToReloadGCodeFile()
    {
        if (string.IsNullOrEmpty(session.LastLoadedGCodeFile) || !File.Exists(session.LastLoadedGCodeFile))
        {
            return;
        }

        var fileName = Path.GetFileName(session.LastLoadedGCodeFile);
        if (ConfirmOrExit($"Reload {fileName}?", true))
        {
            LoadGCodeFromPath(session.LastLoadedGCodeFile);
        }
    }

    /// <summary>
    /// Offers to reload the last saved probe file if one exists.
    /// </summary>
    static void OfferToReloadProbeFile()
    {
        if (string.IsNullOrEmpty(session.LastSavedProbeFile) || !File.Exists(session.LastSavedProbeFile))
        {
            return;
        }

        // Don't offer if we already have probe data loaded
        if (heightMap != null)
        {
            return;
        }

        var fileName = Path.GetFileName(session.LastSavedProbeFile);
        if (ConfirmOrExit($"Load probe data {fileName}?", true))
        {
            try
            {
                heightMap = ProbeGrid.Load(session.LastSavedProbeFile);
                heightMapApplied = false;
                workZeroSet = true;  // Probe data implies trusting the work zero
                AnsiConsole.MarkupLine($"[green]Probe data loaded: {heightMap.TotalPoints} points (work zero trusted)[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error loading probe data: {Markup.Escape(ex.Message)}[/]");
            }
        }
    }

    /// <summary>

    /// <summary>
    /// Offers to continue an interrupted probe session if one exists.
    /// </summary>
    static void OfferToContinueProbing()
    {
        if (string.IsNullOrEmpty(session.ProbeAutoSavePath) || !File.Exists(session.ProbeAutoSavePath))
        {
            return;
        }

        try
        {
            var savedMap = ProbeGrid.Load(session.ProbeAutoSavePath);
            if (savedMap.NotProbed.Count == 0)
            {
                // Probe was complete, clear the autosave
                ClearProbeAutoSave();
                return;
            }

            AnsiConsole.MarkupLine($"[yellow]Found incomplete probe: {savedMap.Progress}/{savedMap.TotalPoints} points[/]");
            if (ConfirmOrCancel("Continue probing?", true))
            {
                heightMap = savedMap;
                heightMapApplied = false;

                // Start probing from where we left off
                if (machine.Connected && workZeroSet)
                {
                    probing = true;
                    machine.ProbeStart();
                    machine.SendLine("G90");
                    machine.SendLine($"G0 Z{settings.ProbeSafeHeight:F3}");
                    AnsiConsole.MarkupLine("[green]Resuming probe. Press Escape to stop.[/]");
                    ProbeNextPoint();
                    WaitForProbingComplete();
                    machine.ProbeStop();
                    Console.WriteLine();

                    if (heightMap != null && heightMap.NotProbed.Count == 0)
                    {
                        ClearProbeAutoSave();
                        ShowProbeResults();
                        if (currentFile != null && Confirm("Apply probe data to G-Code?", true))
                        {
                            ApplyProbeGrid();
                        }
                    }
                }
                else
                {
                    AnsiConsole.MarkupLine("[yellow]Probe data loaded. Connect and set work zero, then use Probe menu to continue.[/]");
                }
            }
            else
            {
                if (Confirm("Discard incomplete probe data?", false))
                {
                    ClearProbeAutoSave();
                }
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error loading probe autosave: {Markup.Escape(ex.Message)}[/]");
            ClearProbeAutoSave();
        }
    }

    /// <summary>
    /// Clears the probe autosave file and settings.
    /// </summary>
    static void ClearProbeAutoSave()
    {
        if (!string.IsNullOrEmpty(session.ProbeAutoSavePath) && File.Exists(session.ProbeAutoSavePath))
        {
            try { File.Delete(session.ProbeAutoSavePath); } catch { }
        }
        session.ProbeAutoSavePath = "";
        SaveSession();
    }

    /// <summary>
    /// Saves the current probe progress for later resume.
    /// </summary>
    static void SaveProbeProgress()
    {
        if (heightMap == null) return;

        try
        {
            string path = Path.Combine(Path.GetDirectoryName(SettingsFileName) ?? ".", ProbeAutoSaveFileName);
            heightMap.Save(path);
            session.ProbeAutoSavePath = path;
            SaveSession();
        }
        catch { /* Ignore save errors during probing */ }
    }

    static void Main(string[] args)
    {
        Console.Title = AppTitle;

        // Load settings
        bool hasSettings = LoadSettings();

        // Show experimental warning on first run
        ShowExperimentalWarning();

        // Create machine
        machine = new Machine(settings);

        // Subscribe to events
        machine.ConnectionStateChanged += () => { };
        machine.StatusChanged += () => { };
        machine.PositionUpdateReceived += () => { };
        machine.NonFatalException += msg => { if (!probing && !suppressErrors) AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(msg)}[/]"); };
        machine.Info += msg => { if (!probing && !suppressErrors) AnsiConsole.MarkupLine($"[blue]Info: {Markup.Escape(msg)}[/]"); };
        machine.LineReceived += msg => { };
        machine.ProbeFinished += OnProbeFinished;

        // Auto-connect if we have saved settings
        if (hasSettings)
        {
            AutoConnect();
        }

        // Load G-Code file from command line argument
        if (args.Length > 0)
        {
            LoadGCodeFromPath(args[0]);
        }

        // Main loop
        while (true)
        {
            try
            {
                ShowMainMenu();
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
            }
        }
    }

    /// <summary>
    /// Waits for GRBL to respond with a valid status (not "Disconnected").
    /// Returns the status string if successful, null if timeout.
    /// </summary>
    static string? WaitForGrblResponse()
    {
        var timeout = DateTime.Now.AddMilliseconds(GrblResponseTimeoutMs);
        while (DateTime.Now < timeout)
        {
            if (machine.Connected && machine.Status != StatusDisconnected)
            {
                return machine.Status;
            }
            Thread.Sleep(StatusPollIntervalMs);
        }
        return null;
    }

    /// <summary>
    /// Result of a connection attempt.
    /// </summary>
    enum ConnectionResult
    {
        Success,
        Timeout,
        PortNotOpened,
        Error
    }

    /// <summary>
    /// Attempts to connect to the machine with timeout.
    /// Returns the result and GRBL status (if successful) or error message (if failed).
    /// </summary>
    static (ConnectionResult Result, string? Message) TryConnect(int timeoutMs)
    {
        string? grblStatus = null;
        bool timedOut = false;
        Exception? error = null;

        var connectTask = Task.Run(() =>
        {
            machine.Connect();
            if (!machine.Connected)
            {
                return null;
            }
            machine.SoftReset();
            return WaitForGrblResponse();
        });

        if (connectTask.Wait(timeoutMs))
        {
            try
            {
                grblStatus = connectTask.Result;
            }
            catch (AggregateException ae)
            {
                error = ae.InnerException;
            }
        }
        else
        {
            timedOut = true;
            // Observe the task to prevent unhandled exception
            connectTask.ContinueWith(t =>
            {
                var _ = t.Exception; // Observe
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        if (error != null)
        {
            return (ConnectionResult.Error, error.Message);
        }
        if (timedOut)
        {
            return (ConnectionResult.Timeout, null);
        }
        if (grblStatus != null)
        {
            return (ConnectionResult.Success, grblStatus);
        }
        if (machine.Connected)
        {
            return (ConnectionResult.Success, null); // Connected but no GRBL response yet
        }
        return (ConnectionResult.PortNotOpened, null);
    }

    /// <summary>
    /// Waits for GRBL status to change from the current status.
    /// Returns the new status string if successful, null if timeout.
    /// </summary>
    static string? WaitForStatusChange(string currentStatus)
    {
        var timeout = DateTime.Now.AddMilliseconds(GrblResponseTimeoutMs);
        while (DateTime.Now < timeout)
        {
            if (machine.Connected && machine.Status != StatusDisconnected && machine.Status != currentStatus)
            {
                return machine.Status;
            }
            Thread.Sleep(StatusPollIntervalMs);
        }
        return null;
    }

    /// <summary>
    /// Generic JSON file loader. Returns default instance if file doesn't exist or fails to load.
    /// </summary>
    static T LoadJsonFile<T>(string fileName, string displayName) where T : new()
    {
        if (!File.Exists(fileName))
        {
            return new T();
        }

        try
        {
            string json = File.ReadAllText(fileName);
            return JsonSerializer.Deserialize<T>(json) ?? new T();
        }
        catch
        {
            return new T();
        }
    }

    /// <summary>
    /// Generic JSON file saver. Silently ignores errors if silent=true.
    /// </summary>
    static void SaveJsonFile<T>(T data, string fileName, string displayName, bool silent = false)
    {
        try
        {
            string json = JsonSerializer.Serialize(data, JsonOptions);
            File.WriteAllText(fileName, json);
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                AnsiConsole.MarkupLine($"[red]Failed to save {displayName}: {Markup.Escape(ex.Message)}[/]");
            }
        }
    }

    /// <summary>
    /// Loads settings and session from files. Returns true if settings file existed.
    /// </summary>
    static bool LoadSettings()
    {
        bool existed = File.Exists(SettingsFileName);
        settings = LoadJsonFile<MachineSettings>(SettingsFileName, "settings");
        session = LoadJsonFile<SessionState>(SessionFileName, "session");
        if (existed)
        {
            AnsiConsole.MarkupLine($"[green]Loaded {SettingsFileName}[/]");
        }
        return existed;
    }

    /// <summary>
    /// Attempts to auto-connect using saved settings.
    /// </summary>
    static void AutoConnect()
    {
        var portName = settings.ConnectionType == ConnectionType.Serial
            ? settings.SerialPortName
            : $"{settings.EthernetIP}:{settings.EthernetPort}";

        ConnectionResult result = ConnectionResult.Error;
        string? message = null;

        AnsiConsole.Status()
            .Start($"Auto-connecting to {portName}...", ctx =>
            {
                (result, message) = TryConnect(ConnectionTimeoutMs + GrblResponseTimeoutMs);
            });

        switch (result)
        {
            case ConnectionResult.Success when message != null:
                AnsiConsole.MarkupLine($"[green]Connected to {portName}! GRBL status: {message}[/]");
                // Try to unlock if in Alarm state
                if (message.StartsWith(StatusAlarm))
                {
                    AnsiConsole.MarkupLine("[yellow]Alarm state detected, sending unlock ($X)...[/]");
                    machine.SendLine("$X");
                    var newStatus = WaitForStatusChange(message);
                    if (newStatus != null && !newStatus.StartsWith(StatusAlarm))
                    {
                        AnsiConsole.MarkupLine($"[green]Unlocked! Status: {newStatus}[/]");
                        message = newStatus;
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[yellow]Still in alarm state. May need to home ($H).[/]");
                    }
                }
                OfferToHome(message);
                break;
            case ConnectionResult.Success:
                AnsiConsole.MarkupLine($"[yellow]Connected to {portName} but no GRBL response.[/]");
                machine.Disconnect();
                break;
            case ConnectionResult.Timeout:
                AnsiConsole.MarkupLine($"[yellow]Auto-connect to {portName} timed out.[/]");
                if (machine.Connected)
                {
                    machine.Disconnect();
                }
                break;
            case ConnectionResult.PortNotOpened:
            case ConnectionResult.Error:
                AnsiConsole.MarkupLine($"[yellow]Auto-connect to {portName} failed.[/]");
                break;
        }

        // Offer to auto-detect if startup connection failed and using serial
        if (!machine.Connected && settings.ConnectionType == ConnectionType.Serial)
        {
            if (Confirm("Auto-detect serial port?", false))
            {
                string[] ports = GetAvailablePorts();
                if (ports.Length > 0 && AutoDetectSerial(ports))
                {
                    SaveSettings();
                }
            }
        }

        // Offer to load files regardless of connection status
        OfferToReloadGCodeFile();
        OfferToReloadProbeFile();

        // These require connection
        if (machine.Connected)
        {
            OfferToAcceptStoredWorkZero();
            OfferToContinueProbing();
        }
    }

    /// <summary>
    /// Saves settings to file (with user feedback).
    /// </summary>
    static void SaveSettings()
    {
        SaveJsonFile(settings, SettingsFileName, "settings");
        AnsiConsole.MarkupLine($"[green]Saved {SettingsFileName}[/]");
    }

    /// <summary>
    /// Saves session state to file (silent, no user feedback).
    /// </summary>
    static void SaveSession()
    {
        SaveJsonFile(session, SessionFileName, "session", silent: true);
    }

    /// <summary>
    /// Returns the menu key character for a given index.
    /// 0-9 use digits '1'-'9' then '0', indices 10+ use 'A', 'B', 'C'...
    /// </summary>
    static char GetMenuKey(int index)
    {
        if (index < 9)
        {
            return (char)('1' + index);
        }
        else if (index == 9)
        {
            return '0';
        }
        else
        {
            return (char)('A' + index - 10);
        }
    }

    /// <summary>
    /// Displays a menu and returns the selected index. Supports arrow navigation, number keys, and mnemonic keys.
    /// Options format: "1. Label (x)" where x is the mnemonic key.
    /// </summary>
    static int ShowMenu(string title, string[] options, int initialSelection = 0)
    {
        int selected = Math.Clamp(initialSelection, 0, options.Length - 1);

        // Extract mnemonic keys from options (look for (x) at end)
        // Also extract leading number/letter keys (e.g., "0. Back" or "A. Option")
        var mnemonics = new Dictionary<char, int>();
        var leadingKeys = new Dictionary<char, int>();
        for (int i = 0; i < options.Length; i++)
        {
            // Check for mnemonic in parentheses at end
            var match = System.Text.RegularExpressions.Regex.Match(options[i], @"\((\w)\)$");
            if (match.Success)
            {
                mnemonics[char.ToLower(match.Groups[1].Value[0])] = i;
            }

            // Check for leading number/letter (e.g., "0. " or "A. ")
            var leadingMatch = System.Text.RegularExpressions.Regex.Match(options[i], @"^(\w)\.");
            if (leadingMatch.Success)
            {
                leadingKeys[char.ToUpper(leadingMatch.Groups[1].Value[0])] = i;
            }
        }

        while (true)
        {
            // Draw menu
            Console.SetCursorPosition(0, Console.CursorTop);
            AnsiConsole.MarkupLine($"[bold]{title}[/]");
            for (int i = 0; i < options.Length; i++)
            {
                if (i == selected)
                {
                    AnsiConsole.MarkupLine($"[green]> {options[i]}[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"  {options[i]}");
                }
            }
            AnsiConsole.MarkupLine("[dim]Arrows + Enter, number, letter, or Esc to go back[/]");

            var key = Console.ReadKey(true);

            char pressedKey = char.ToUpper(key.KeyChar);

            // Check leading keys first (e.g., "0. Back" responds to '0')
            if (leadingKeys.TryGetValue(pressedKey, out int leadingIdx))
            {
                return leadingIdx;
            }

            // Mnemonic keys (from parentheses at end of option)
            if (mnemonics.TryGetValue(char.ToLower(key.KeyChar), out int idx))
            {
                return idx;
            }

            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    selected = (selected - 1 + options.Length) % options.Length;
                    break;
                case ConsoleKey.DownArrow:
                    selected = (selected + 1) % options.Length;
                    break;
                case ConsoleKey.Enter:
                    return selected;
                case ConsoleKey.Escape:
                    return options.Length - 1; // Assume last option is Back/Exit
            }

            // Move cursor back up to redraw
            Console.SetCursorPosition(0, Console.CursorTop - options.Length - 2);
        }
    }

    /// <summary>
    /// Returns the smart default menu selection based on workflow state.
    /// Guides user through: Connect → Load File → Move (set zero) → Probe → Mill
    /// </summary>
    static int GetSmartMenuDefault()
    {
        if (!machine.Connected)
        {
            return 0; // Connect
        }
        if (currentFile == null)
        {
            return 1; // Load G-Code
        }
        if (!workZeroSet)
        {
            return 2; // Move (to set work zero)
        }
        if (heightMap == null)
        {
            return 3; // Probe
        }
        return 4; // Mill
    }

    static void ShowMainMenu()
    {
        Console.Clear();

        // Show status header
        var statusColor = machine.Connected ? "green" : "red";
        var statusText = machine.Connected ? machine.Status : StatusDisconnected;

        AnsiConsole.Write(new Rule($"[bold blue]{AppTitle} {Version}[/]").RuleStyle("blue"));
        AnsiConsole.MarkupLine($"Status: [{statusColor}]{statusText}[/] | " +
            $"X:[yellow]{machine.WorkPosition.X:F3}[/] " +
            $"Y:[yellow]{machine.WorkPosition.Y:F3}[/] " +
            $"Z:[yellow]{machine.WorkPosition.Z:F3}[/]");

        if (currentFile != null)
        {
            AnsiConsole.MarkupLine($"File: [cyan]{currentFile.FileName}[/] ({currentFile.Toolpath.Count} commands)");
        }

        if (heightMap != null)
        {
            AnsiConsole.MarkupLine($"Probe: [cyan]{heightMap.SizeX}x{heightMap.SizeY}[/] ({heightMap.Progress}/{heightMap.TotalPoints} points)");
        }

        AnsiConsole.WriteLine();

        // Build menu options from structured data
        var options = MainMenuItems.Select((item, index) => {
            string label = $"{item.Number}. {item.Label} ({item.Mnemonic})";
            // Disable Mill (index 4) when no file loaded
            if (index == 4 && currentFile == null)
            {
                return $"{item.Number}. {item.Label} [dim](load file first)[/]";
            }
            return label;
        }).ToArray();

        // Smart default based on workflow state
        int smartDefault = GetSmartMenuDefault();
        int choice = ShowMenu("Select an option:", options, smartDefault);

        // Dispatch to handler based on selection (order matches MainMenuItems)
        Action[] handlers = {
            ConnectionMenu, LoadGCodeFile, MoveMenu,
            ProbingMenu, RunGCode, SettingsMenu, ShowAbout, ExitProgram
        };

        if (choice >= 0 && choice < handlers.Length)
        {
            handlers[choice]();
        }
    }

    static void ConnectionMenu()
    {
        if (machine.Connected)
        {
            if (Confirm("Disconnect from machine?"))
            {
                machine.Disconnect();
                workZeroSet = false;
                AnsiConsole.MarkupLine("[yellow]Disconnected[/]");
            }
        }
        else
        {
            var connOptions = new[] {
                "1. Serial (s)",
                "2. Ethernet (e) [untested]",
                "0. Back"
            };
            int connChoice = ShowMenu("Connection type:", connOptions);

            if (connChoice == 2)
            {
                return;
            }

            if (connChoice == 0)
            {
                settings.ConnectionType = ConnectionType.Serial;

                // List available ports (can be slow on Windows)
                string[] ports = Array.Empty<string>();
                AnsiConsole.Status()
                    .Start("Enumerating serial ports...", ctx =>
                    {
                        ports = GetAvailablePorts();
                    });

                if (ports.Length == 0)
                {
                    AnsiConsole.MarkupLine("[red]No serial ports found![/]");
                    return;
                }

                var portOptions = new List<string> { "1. Auto-detect (scan all ports) (a)" };
                for (int i = 0; i < ports.Length; i++)
                {
                    portOptions.Add($"{i + 2}. {ports[i]}");
                }
                portOptions.Add($"{ports.Length + 2}. Enter manually (m)");

                int portChoice = ShowMenu("Select serial port:", portOptions.ToArray());

                if (portChoice == 0)
                {
                    if (AutoDetectSerial(ports))
                    {
                        SaveSettings();
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[red]No GRBL device found on any port.[/]");
                    }
                    return;
                }

                string selectedPort;
                if (portChoice == ports.Length + 1) // Enter manually
                {
                    selectedPort = AnsiConsole.Ask<string>("Enter port name:", settings.SerialPortName);
                }
                else
                {
                    selectedPort = ports[portChoice - 1];
                }

                settings.SerialPortName = selectedPort;

                // Build baud rate options
                var baudOptions = CommonBaudRates.Select((b, i) => $"{i + 1}. {b}").ToArray();
                int baudChoice = ShowMenu("Select baud rate:", baudOptions);
                settings.SerialPortBaud = CommonBaudRates[baudChoice];
            }
            else
            {
                settings.ConnectionType = ConnectionType.Ethernet;
                settings.EthernetIP = AnsiConsole.Ask("Enter IP address:", settings.EthernetIP);
                settings.EthernetPort = AnsiConsole.Ask("Enter port:", settings.EthernetPort);
            }

            try
            {
                ConnectionResult result = ConnectionResult.Error;
                string? message = null;

                AnsiConsole.Status()
                    .Start("Connecting...", ctx =>
                    {
                        (result, message) = TryConnect(ConnectionTimeoutMs + GrblResponseTimeoutMs);
                    });

                switch (result)
                {
                    case ConnectionResult.Success when message != null:
                        AnsiConsole.MarkupLine($"[green]Connected! GRBL status: {message}[/]");
                        SaveSettings();
                        OfferToHome(message);
                        break;
                    case ConnectionResult.Success:
                        AnsiConsole.MarkupLine("[yellow]Warning: Port opened but no GRBL response received.[/]");
                        AnsiConsole.MarkupLine("[yellow]Check that the correct port is selected and GRBL is running.[/]");
                        if (Confirm("Disconnect?", true))
                        {
                            machine.Disconnect();
                        }
                        break;
                    case ConnectionResult.Timeout:
                        AnsiConsole.MarkupLine("[red]Connection timed out.[/]");
                        if (machine.Connected)
                        {
                            machine.Disconnect();
                        }
                        break;
                    case ConnectionResult.PortNotOpened:
                        AnsiConsole.MarkupLine("[red]Could not open serial port. Is the machine powered on?[/]");
                        break;
                    case ConnectionResult.Error:
                        AnsiConsole.MarkupLine($"[red]Connection failed: {Markup.Escape(message ?? "Unknown error")}[/]");
                        break;
                }

                // Offer to load files regardless of connection status
                OfferToReloadGCodeFile();
                OfferToReloadProbeFile();

                // These require connection
                if (machine.Connected)
                {
                    OfferToAcceptStoredWorkZero();
                    OfferToContinueProbing();
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Connection failed: {Markup.Escape(ex.Message)}[/]");
            }
        }
    }

    /// <summary>
    /// Attempts to auto-detect a GRBL device by cycling through COM ports and baud rates.
    /// Tries most likely baud rates first across all ports for faster detection.
    /// Returns true if a device was found and connected.
    /// </summary>
    static bool AutoDetectSerial(string[] ports)
    {
        AnsiConsole.MarkupLine($"[blue]Scanning {ports.Length} port(s) at {CommonBaudRates.Length} baud rates...[/]");

        // Try most likely baud rates first across all ports
        foreach (var baud in CommonBaudRates)
        {
            foreach (var port in ports)
            {
                AnsiConsole.Markup($"  Trying [cyan]{port}[/] @ [cyan]{baud}[/]... ");

                settings.SerialPortName = port;
                settings.SerialPortBaud = baud;

                try
                {
                    var (result, message) = TryConnect(AutoDetectTimeoutMs);

                    if (result == ConnectionResult.Success && message != null)
                    {
                        AnsiConsole.MarkupLine($"[green]Found! Status: {message}[/]");
                        return true;
                    }

                    if (machine.Connected)
                    {
                        machine.Disconnect();
                    }

                    var status = result switch
                    {
                        ConnectionResult.Timeout => "timeout",
                        ConnectionResult.PortNotOpened => "not available",
                        ConnectionResult.Error => "failed",
                        _ => "no response"
                    };
                    AnsiConsole.MarkupLine($"[dim]{status}[/]");
                }
                catch (Exception)
                {
                    if (machine.Connected)
                    {
                        machine.Disconnect();
                    }
                    AnsiConsole.MarkupLine("[dim]failed[/]");
                }
            }
        }

        return false;
    }

    static string[] GetAvailablePorts()
    {
        var ports = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            ports.AddRange(System.IO.Ports.SerialPort.GetPortNames());
        }
        else
        {
            // Unix-like systems
            if (Directory.Exists("/dev"))
            {
                var patterns = new[] { "ttyUSB*", "ttyACM*", "tty.usbserial*", "cu.usbmodem*", "tty.usbmodem*" };
                foreach (var pattern in patterns)
                {
                    try
                    {
                        ports.AddRange(Directory.GetFiles("/dev", pattern));
                    }
                    catch { }
                }
            }
        }

        return ports.ToArray();
    }

    // Jog speed/distance presets: (feed mm/min, distance mm, label)
    static readonly (double Feed, double Distance, string Label)[] JogPresets =
    {
        (5000, 10.0,  "Fast   5000mm/min  10mm"),
        (500,  1.0,   "Normal  500mm/min   1mm"),
        (50,   0.1,   "Slow     50mm/min 0.1mm"),
        (5,    0.01,  "Creep     5mm/min 0.01mm"),
    };
    static int jogPresetIndex = 1;  // Start at Normal

    static void MoveMenu()
    {
        if (!RequireConnection())
        {
            return;
        }

        while (true)
        {
            Console.Clear();
            AnsiConsole.Write(new Rule("[bold blue]Move[/]").RuleStyle("blue"));
            var statusColor = (machine.Status.StartsWith(StatusAlarm) || machine.Status.StartsWith(StatusDoor)) ? "red" : "green";
            AnsiConsole.MarkupLine($"Status: [{statusColor}]{machine.Status}[/]");
            AnsiConsole.MarkupLine($"Position: X:[yellow]{machine.WorkPosition.X:F3}[/] Y:[yellow]{machine.WorkPosition.Y:F3}[/] Z:[yellow]{machine.WorkPosition.Z:F3}[/]");

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Jog:[/]");
            AnsiConsole.MarkupLine("  [cyan]Arrow keys[/] - X/Y    [cyan]W/S[/] or [cyan]PgUp/PgDn[/] - Z");
            AnsiConsole.MarkupLine($"  [cyan]Tab[/] - Cycle speed    [green]{JogPresets[jogPresetIndex].Label}[/]");

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Commands:[/]");
            AnsiConsole.MarkupLine("  [cyan]H[/] - Home    [cyan]U[/] - Unlock    [cyan]R[/] - Reset    [cyan]Esc/Q[/] - Exit");

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Set Work Zero:[/]");
            AnsiConsole.MarkupLine("  [cyan]0[/] - Zero All (XYZ)    [cyan]Z[/] - Zero Z only");

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Go to Position:[/]");
            AnsiConsole.MarkupLine("  [cyan]X[/] - X0 Y0    [cyan]C[/] - Center of G-code    [cyan]G[/] - Z0    [cyan]6[/] - Z+6mm    [cyan]1[/] - Z+1mm");

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Probe:[/]");
            AnsiConsole.MarkupLine("  [cyan]P[/] - Find Z (probe down until contact)");
            AnsiConsole.WriteLine();

            var key = Console.ReadKey(true);

            if (IsExitKey(key))
            {
                return;
            }

            // Tab cycles through jog speeds
            if (key.Key == ConsoleKey.Tab)
            {
                jogPresetIndex = (jogPresetIndex + 1) % JogPresets.Length;
                continue;
            }

            // Handle command keys
            if (IsKey(key, ConsoleKey.H, 'h'))
            {
                machine.SoftReset();
                machine.SendLine("$H");
                AnsiConsole.MarkupLine("[green]Home All command sent[/]");
                Thread.Sleep(ConfirmationDisplayMs);
                continue;
            }
            if (IsKey(key, ConsoleKey.U, 'u'))
            {
                machine.SendLine("$X");
                if (machine.Status.StartsWith(StatusDoor) || machine.Status.StartsWith(StatusHold))
                {
                    machine.SendLine("~");
                }
                AnsiConsole.MarkupLine("[green]Unlock command sent[/]");
                Thread.Sleep(CommandDelayMs);
                continue;
            }
            if (IsKey(key, ConsoleKey.R, 'r'))
            {
                machine.SoftReset();
                AnsiConsole.MarkupLine("[yellow]Soft reset sent[/]");
                Thread.Sleep(CommandDelayMs);
                continue;
            }
            if (IsKey(key, ConsoleKey.Z, 'z'))
            {
                machine.SendLine("G10 L20 P0 Z0");
                AnsiConsole.MarkupLine("[green]Z zeroed[/]");
                Thread.Sleep(ConfirmationDisplayMs);
                machine.SendLine("G0 Z6");
                AnsiConsole.MarkupLine("[green]Moving to Z+6mm[/]");
                Thread.Sleep(CommandDelayMs);
                continue;
            }
            if (IsKey(key, ConsoleKey.D0, '0'))
            {
                machine.SendLine("G10 L20 P0 X0 Y0 Z0");
                workZeroSet = true;
                AnsiConsole.MarkupLine("[green]All axes zeroed (work zero set)[/]");
                Thread.Sleep(ConfirmationDisplayMs);
                machine.SendLine("G0 Z6");
                AnsiConsole.MarkupLine("[green]Moving to Z+6mm[/]");
                Thread.Sleep(CommandDelayMs);
                continue;
            }
            if (IsKey(key, ConsoleKey.D6, '6'))
            {
                machine.SendLine("G0 Z6");
                AnsiConsole.MarkupLine("[green]Moving to Z+6mm[/]");
                Thread.Sleep(CommandDelayMs);
                continue;
            }
            if (IsKey(key, ConsoleKey.D1, '1'))
            {
                machine.SendLine("G0 Z1");
                AnsiConsole.MarkupLine("[green]Moving to Z+1mm[/]");
                Thread.Sleep(CommandDelayMs);
                continue;
            }
            if (IsKey(key, ConsoleKey.G, 'g'))
            {
                machine.SendLine("G0 Z0");
                AnsiConsole.MarkupLine("[green]Moving to Z0[/]");
                Thread.Sleep(CommandDelayMs);
                continue;
            }
            if (IsKey(key, ConsoleKey.X, 'x'))
            {
                machine.SendLine("G0 X0 Y0");
                AnsiConsole.MarkupLine("[green]Moving to X0 Y0[/]");
                Thread.Sleep(CommandDelayMs);
                continue;
            }
            if (IsKey(key, ConsoleKey.C, 'c'))
            {
                if (currentFile == null)
                {
                    AnsiConsole.MarkupLine("[red]No G-code file loaded[/]");
                    Thread.Sleep(ConfirmationDisplayMs);
                }
                else
                {
                    double centerX = (currentFile.Min.X + currentFile.Max.X) / 2;
                    double centerY = (currentFile.Min.Y + currentFile.Max.Y) / 2;
                    machine.SendLine($"G0 X{centerX:F3} Y{centerY:F3}");
                    AnsiConsole.MarkupLine($"[green]Moving to center X{centerX:F3} Y{centerY:F3}[/]");
                    Thread.Sleep(CommandDelayMs);
                }
                continue;
            }
            if (IsKey(key, ConsoleKey.P, 'p'))
            {
                ProbeZ();
                Thread.Sleep(ConfirmationDisplayMs);
                continue;
            }

            // Get current jog preset
            var (feed, distance, _) = JogPresets[jogPresetIndex];

            bool jogged = false;
            switch (key.Key)
            {
                case ConsoleKey.UpArrow: machine.Jog('Y', distance, feed); jogged = true; break;
                case ConsoleKey.DownArrow: machine.Jog('Y', -distance, feed); jogged = true; break;
                case ConsoleKey.LeftArrow: machine.Jog('X', -distance, feed); jogged = true; break;
                case ConsoleKey.RightArrow: machine.Jog('X', distance, feed); jogged = true; break;
                case ConsoleKey.PageUp: machine.Jog('Z', distance, feed); jogged = true; break;
                case ConsoleKey.PageDown: machine.Jog('Z', -distance, feed); jogged = true; break;
            }
            // Handle W/S for Z jog
            if (!jogged && IsKey(key, ConsoleKey.W, 'w'))
            {
                machine.Jog('Z', distance, feed);
                jogged = true;
            }
            if (!jogged && IsKey(key, ConsoleKey.S, 's'))
            {
                machine.Jog('Z', -distance, feed);
                jogged = true;
            }

            if (jogged)
            {
                Thread.Sleep(JogPollIntervalMs);
                // Flush keyboard buffer to prevent runaway jog from key repeat
                while (Console.KeyAvailable)
                {
                    Console.ReadKey(true);
                }
            }
        }
    }

    static void LoadGCodeFile()
    {
        var path = BrowseForGCodeFile();
        if (path != null)
        {
            LoadGCodeFromPath(path);
        }
    }

    /// <summary>
    /// Generic file browser. Returns selected file path or null if cancelled.
    /// </summary>
    static string? BrowseForFile(string[] extensions, string? defaultFileName = null)
    {
        // Start at last browse directory if it exists, otherwise current directory
        string currentDir = !string.IsNullOrEmpty(session.LastBrowseDirectory) && Directory.Exists(session.LastBrowseDirectory)
            ? session.LastBrowseDirectory
            : Environment.CurrentDirectory;

        while (true)
        {
            var items = new List<(string Display, string FullPath, bool IsDir)>();

            // Add parent directory option first
            var parent = Directory.GetParent(currentDir);
            if (parent != null)
            {
                items.Add(("..", parent.FullName, true));
            }

            // Add subdirectories
            try
            {
                foreach (var dir in Directory.GetDirectories(currentDir).OrderBy(d => Path.GetFileName(d)))
                {
                    var name = Path.GetFileName(dir);
                    if (!name.StartsWith("."))
                    {
                        items.Add((name + "/", dir, true));
                    }
                }
            }
            catch
            {
                // Skip inaccessible directories
            }

            // Add matching files
            try
            {
                foreach (var file in Directory.GetFiles(currentDir).OrderBy(f => Path.GetFileName(f)))
                {
                    var ext = Path.GetExtension(file).ToLower();
                    if (extensions.Contains(ext))
                    {
                        items.Add((Path.GetFileName(file), file, false));
                    }
                }
            }
            catch
            {
                // Skip inaccessible files
            }

            // Build menu options - use GetMenuKey for proper numbering (1-9, 0, A-Z)
            var menuOptions = new List<string>();
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                menuOptions.Add($"{GetMenuKey(i)}. {Markup.Escape(item.Display)}");
            }
            menuOptions.Add($"{GetMenuKey(items.Count)}. Cancel");

            Console.Clear();
            AnsiConsole.Write(new Rule($"[bold blue]{Markup.Escape(currentDir)}[/]").RuleStyle("blue"));
            AnsiConsole.WriteLine();

            int choice = ShowMenu("Select file or directory:", menuOptions.ToArray());

            if (choice == items.Count || choice < 0)
            {
                // Cancel selected or Esc pressed
                return null;
            }

            var selected = items[choice];
            if (selected.IsDir)
            {
                // Navigate into directory
                currentDir = selected.FullPath;
            }
            else
            {
                // File selected
                return selected.FullPath;
            }
        }
    }

    static string? BrowseForGCodeFile() => BrowseForFile(GCodeExtensions);
    static string? BrowseForProbeGridFile() => BrowseForFile(ProbeGridExtensions);

    static void LoadGCodeFromPath(string path)
    {
        // Expand ~ for home directory
        if (path.StartsWith("~"))
        {
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Substring(2));
        }

        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]File not found: {Markup.Escape(path)}[/]");
            Console.ReadKey();
            return;
        }

        try
        {
            currentFile = GCodeFile.Load(path);
            AnsiConsole.MarkupLine($"[green]Loaded: {currentFile.FileName}[/]");
            AnsiConsole.WriteLine(currentFile.GetInfo());

            if (currentFile.Warnings.Count > 0)
            {
                AnsiConsole.MarkupLine($"[yellow]Warnings ({currentFile.Warnings.Count}):[/]");
                foreach (var w in currentFile.Warnings.Take(5))
                {
                    AnsiConsole.MarkupLine($"  [yellow]{Markup.Escape(w)}[/]");
                }
                if (currentFile.Warnings.Count > 5)
                {
                    AnsiConsole.MarkupLine($"  [yellow]... and {currentFile.Warnings.Count - 5} more[/]");
                }
                AnsiConsole.MarkupLine("[yellow]Press any key to continue...[/]");
                Console.ReadKey(true);
            }

            // Load into machine
            machine.SetFile(currentFile.GetGCode());

            // Reset height map applied state for new file
            heightMapApplied = false;

            // Save the file path and directory for next time
            session.LastLoadedGCodeFile = path;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                session.LastBrowseDirectory = dir;
            }
            SaveSession();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error loading file: {Markup.Escape(ex.Message)}[/]");
            Console.ReadKey(true);
        }
    }

    /// <summary>
    /// Checks if there's incomplete probe data that can be continued.
    /// </summary>
    static bool HasIncompleteProbeData()
    {
        return (heightMap != null && heightMap.NotProbed.Count > 0) ||
               (!string.IsNullOrEmpty(session.ProbeAutoSavePath) && File.Exists(session.ProbeAutoSavePath));
    }

    /// <summary>
    /// Clears all probe data (in-memory and autosave).
    /// </summary>
    static void ClearProbeData()
    {
        heightMap = null;
        heightMapApplied = false;
        ClearProbeAutoSave();
        AnsiConsole.MarkupLine("[yellow]Probe data cleared[/]");
    }

    static void ProbingMenu()
    {
        while (true)
        {
            Console.Clear();
            AnsiConsole.Write(new Rule("[bold blue]Probe[/]").RuleStyle("blue"));

            // Check for incomplete probe data (in memory or autosave)
            bool hasIncomplete = HasIncompleteProbeData();
            bool hasComplete = heightMap != null && heightMap.NotProbed.Count == 0;

            if (heightMap != null)
            {
                AnsiConsole.WriteLine(heightMap.GetInfo());
                if (!heightMapApplied && currentFile != null)
                {
                    AnsiConsole.MarkupLine("[yellow]* Probe data not yet applied to G-Code[/]");
                }
                else if (heightMapApplied)
                {
                    AnsiConsole.MarkupLine("[green]Probe data applied to G-Code[/]");
                }
            }
            else if (hasIncomplete)
            {
                AnsiConsole.MarkupLine("[yellow]Incomplete probe data found (autosaved)[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[dim]No probe data[/]");
            }

            if (currentFile == null)
            {
                AnsiConsole.MarkupLine("[dim]No G-Code file loaded (required for probing)[/]");
            }

            if (!workZeroSet)
            {
                AnsiConsole.MarkupLine("[dim]Work zero not set (required for probing)[/]");
            }

            AnsiConsole.WriteLine();

            // Build dynamic menu based on state
            bool canProbe = currentFile != null && workZeroSet && machine.Connected;
            bool canSave = heightMap != null && heightMap.Progress > 0;

            var menuItems = new List<(string Label, Action Handler)>();

            if (hasIncomplete)
            {
                // Has incomplete data - offer to continue or clear
                string continueLabel = canProbe
                    ? "Continue Probing (c)"
                    : "Continue Probing [dim](connect & set work zero first)[/]";
                menuItems.Add((continueLabel, ContinueProbing));
                menuItems.Add(("Clear Probe Data (x)", ClearProbeData));

                string startLabel = canProbe
                    ? "Clear and Start Probing (p)"
                    : "Clear and Start Probing [dim](load G-Code & set work zero first)[/]";
                menuItems.Add((startLabel, () => { ClearProbeData(); StartProbing(); }));
            }
            else
            {
                // No incomplete data - just offer to start
                string startLabel = canProbe
                    ? "Start Probing (p)"
                    : "Start Probing [dim](load G-Code & set work zero first)[/]";
                menuItems.Add((startLabel, StartProbing));
            }

            menuItems.Add(("Load from File (l)", LoadProbeGrid));

            if (hasComplete && currentFile != null && !heightMapApplied)
            {
                menuItems.Add(("Apply to G-Code (a)", ApplyProbeGrid));
            }

            menuItems.Add(("Back", () => { }));

            // Build options array with numbers
            var options = menuItems.Select((item, i) =>
                $"{(i == menuItems.Count - 1 ? "0" : (i + 1).ToString())}. {item.Label}"
            ).ToArray();

            int choice = ShowMenu("Probe options:", options);

            if (choice >= 0 && choice < menuItems.Count)
            {
                if (choice == menuItems.Count - 1)
                {
                    // Back selected
                    if (probing)
                    {
                        probing = false;
                        machine.ProbeStop();
                    }
                    return;
                }
                menuItems[choice].Handler();
            }
        }
    }

    static void LoadProbeGrid()
    {
        var path = BrowseForProbeGridFile();
        if (path == null)
        {
            return;
        }

        try
        {
            heightMap = ProbeGrid.Load(path);
            heightMapApplied = false;
            AnsiConsole.MarkupLine($"[green]Probe data loaded[/]");
            AnsiConsole.WriteLine(heightMap.GetInfo());
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
            Console.ReadKey();
        }
    }

    static void SaveProbeGrid()
    {
        if (heightMap == null)
        {
            AnsiConsole.MarkupLine("[red]No probe data to save[/]");
            Console.ReadKey();
            return;
        }

        // Default path in last browse directory
        string defaultPath = !string.IsNullOrEmpty(session.LastBrowseDirectory)
            ? Path.Combine(session.LastBrowseDirectory, "probe.pgrid")
            : "probe.pgrid";

        var path = AnsiConsole.Ask("Save path:", defaultPath);

        // Expand ~ for home directory
        if (path.StartsWith("~"))
        {
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Substring(2));
        }

        try
        {
            heightMap.Save(path);
            AnsiConsole.MarkupLine($"[green]Probe data saved to {Markup.Escape(path)}[/]");

            // Save the directory for next time
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                session.LastBrowseDirectory = dir;
                SaveSession();
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
            Console.ReadKey();
        }
    }

    // =========================================================================
    // PROBING
    //
    // Algorithm (matches original OpenCNCPilot):
    // 1. Start: Move to ProbeSafeHeight (5mm default) - only once at beginning
    // 2. For each point:
    //    a. Sort remaining points by distance to current position (nearest first)
    //    b. Move XY to next point (staying at current Z)
    //    c. Probe down (G38.3) until contact or max depth
    //    d. Retract by ProbeMinimumHeight (1mm default) relative to probed surface
    // 3. End: Return to safe height
    //
    // Safety: XY moves happen at (last probed Z + ProbeMinimumHeight). This is safe
    // as long as surface variation between adjacent points is < ProbeMinimumHeight.
    // For PCB work with typical warpage, 1mm clearance is sufficient.
    // =========================================================================

    /// <summary>
    /// Continues an interrupted probing session. Loads from autosave if needed.
    /// </summary>
    static void ContinueProbing()
    {
        if (!RequireConnection())
        {
            return;
        }

        if (!workZeroSet)
        {
            AnsiConsole.MarkupLine("[red]Work zero not set. Use Move menu to zero all axes (0) first.[/]");
            Console.ReadKey();
            return;
        }

        // Load from autosave if not already in memory
        if (heightMap == null || heightMap.NotProbed.Count == 0)
        {
            if (string.IsNullOrEmpty(session.ProbeAutoSavePath) || !File.Exists(session.ProbeAutoSavePath))
            {
                AnsiConsole.MarkupLine("[red]No incomplete probe data found.[/]");
                Console.ReadKey();
                return;
            }

            try
            {
                heightMap = ProbeGrid.Load(session.ProbeAutoSavePath);
                heightMapApplied = false;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error loading probe data: {Markup.Escape(ex.Message)}[/]");
                Console.ReadKey();
                return;
            }
        }

        if (heightMap.NotProbed.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Probe data is already complete.[/]");
            Console.ReadKey();
            return;
        }

        AnsiConsole.MarkupLine($"[green]Resuming probe: {heightMap.Progress}/{heightMap.TotalPoints} points complete[/]");

        // Initialize probing
        probing = true;
        machine.ProbeStart();

        // Move to safe height
        machine.SendLine("G90");
        machine.SendLine($"G0 Z{settings.ProbeSafeHeight:F3}");

        AnsiConsole.MarkupLine("[green]Probing resumed. Press Escape to stop.[/]");

        // Continue probing
        ProbeNextPoint();

        // Wait for completion or cancel
        WaitForProbingComplete();

        // Cleanup
        machine.ProbeStop();
        Console.WriteLine();

        // Show results
        if (heightMap != null && heightMap.NotProbed.Count == 0)
        {
            ClearProbeAutoSave();
            ShowProbeResults();
            if (currentFile != null && Confirm("Apply probe data to G-Code?", true))
            {
                ApplyProbeGrid();
            }
        }
    }

    /// <summary>
    /// Starts the probing sequence. Creates a probe grid from the G-Code bounds,
    /// then probes each point to build a height map for surface compensation.
    /// </summary>
    static void StartProbing()
    {
        if (!RequireConnection()) return;

        if (currentFile == null)
        {
            AnsiConsole.MarkupLine("[red]No G-Code file loaded. Load a file first.[/]");
            Console.ReadKey();
            return;
        }

        if (!workZeroSet)
        {
            AnsiConsole.MarkupLine("[red]Work zero not set. Use Move menu to zero all axes (0) first.[/]");
            Console.ReadKey();
            return;
        }

        // Get probing parameters (user can press Escape to cancel)
        double? marginInput = AskDoubleOrCancel("Probe margin (mm):", DefaultProbeMargin);
        if (marginInput == null)
        {
            return;
        }
        double margin = marginInput.Value;

        double? gridSizeInput = AskDoubleOrCancel("Grid size (mm):", DefaultProbeGridSize);
        if (gridSizeInput == null)
        {
            return;
        }
        double gridSize = gridSizeInput.Value;

        // Create height map from G-Code bounds + margin
        if (!CreateProbeGrid(margin, gridSize))
        {
            return;
        }

        // Offer to traverse outline first for collision checking
        if (ConfirmOrCancel("Traverse outline first?", true))
        {
            if (!TraverseProbeOutline())
            {
                return;  // User cancelled or error
            }
        }

        // Initialize probing
        probing = true;
        machine.ProbeStart();

        // Move to safe height (only done once at start)
        machine.SendLine("G90");
        machine.SendLine($"G0 Z{settings.ProbeSafeHeight:F3}");

        AnsiConsole.MarkupLine("[green]Probing started. Press Escape to stop.[/]");

        // Start first probe
        ProbeNextPoint();

        // Wait for completion or cancel
        WaitForProbingComplete();

        // Cleanup
        machine.ProbeStop();
        Console.WriteLine();

        // Show results
        if (heightMap != null && heightMap.NotProbed.Count == 0)
        {
            ClearProbeAutoSave();
            ShowProbeResults();
            if (Confirm("Apply probe data to G-Code?", true))
            {
                ApplyProbeGrid();
            }
        }
    }

    /// <summary>
    /// Traverses the outline of the probe grid at safe height for collision checking.
    /// Returns true if completed, false if cancelled or error.
    /// </summary>
    static bool TraverseProbeOutline()
    {
        if (heightMap == null)
        {
            return false;
        }

        // Ask for traverse height with current setting as default (Escape to cancel)
        double? traverseHeightInput = AskDoubleOrCancel("Traverse height (mm):", settings.OutlineTraverseHeight);
        if (traverseHeightInput == null)
        {
            return false;
        }
        double traverseHeight = traverseHeightInput.Value;

        AnsiConsole.MarkupLine($"[yellow]Traversing probe outline at Z={traverseHeight:F1}mm, feed={settings.OutlineTraverseFeed:F0}mm/min[/]");
        AnsiConsole.MarkupLine("[dim]Press Escape to cancel[/]");

        // Get the four corners of the probe grid
        double minX = heightMap.Min.X;
        double minY = heightMap.Min.Y;
        double maxX = heightMap.Max.X;
        double maxY = heightMap.Max.Y;

        // Move to traverse height - use MAX of current Z and traverse height to NEVER go down
        double currentZ = machine.WorkPosition.Z;
        double safeZ = Math.Max(currentZ, traverseHeight);
        AnsiConsole.MarkupLine($"[dim]Current Z={currentZ:F2}, moving to Z={safeZ:F2}[/]");
        machine.SendLine("G90");
        machine.SendLine($"G0 Z{safeZ:F3}");

        // Wait for Z move to complete before any XY movement
        WaitForZHeight(safeZ);

        // Traverse the outline: start at min corner, go clockwise
        // Using G1 (linear move) with feed rate for controlled speed
        var corners = new[]
        {
            (minX, minY, "bottom-left"),
            (maxX, minY, "bottom-right"),
            (maxX, maxY, "top-right"),
            (minX, maxY, "top-left"),
            (minX, minY, "back to start")
        };

        foreach (var (x, y, label) in corners)
        {
            // Check for escape key
            if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)
            {
                machine.FeedHold();
                machine.SoftReset();
                AnsiConsole.MarkupLine("\n[yellow]Outline traverse cancelled - machine stopped[/]");
                return false;
            }

            AnsiConsole.MarkupLine($"  Moving to {label} ({x:F1}, {y:F1})...");
            machine.SendLine($"G1 X{x:F3} Y{y:F3} F{settings.OutlineTraverseFeed:F0}");

            // Wait for move to complete by polling position
            if (!WaitForMoveComplete(x, y))
            {
                // Escape was pressed during move
                machine.FeedHold();
                machine.SoftReset();
                AnsiConsole.MarkupLine("\n[yellow]Outline traverse cancelled - machine stopped[/]");
                return false;
            }
        }

        AnsiConsole.MarkupLine("[green]Outline traverse complete![/]");

        if (!ConfirmOrCancel("Continue with probing?", true))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Waits for Z to reach the target height (within tolerance).
    /// </summary>
    static void WaitForZHeight(double targetZ)
    {
        const double PositionTolerance = 0.1;  // mm
        const int PollIntervalMs = 100;
        const int TimeoutMs = 30000;  // 30 seconds max

        var startTime = DateTime.Now;

        while ((DateTime.Now - startTime).TotalMilliseconds < TimeoutMs)
        {
            double dz = Math.Abs(machine.WorkPosition.Z - targetZ);
            if (dz < PositionTolerance)
            {
                return;  // Reached target
            }
            Thread.Sleep(PollIntervalMs);
        }
    }

    /// <summary>
    /// Waits for the machine to reach the target position (within tolerance).
    /// Returns true if reached, false if cancelled by Escape.
    /// </summary>
    static bool WaitForMoveComplete(double targetX, double targetY)
    {
        const double PositionTolerance = 0.1;  // mm
        const int PollIntervalMs = 100;
        const int TimeoutMs = 60000;  // 1 minute max

        var startTime = DateTime.Now;

        while ((DateTime.Now - startTime).TotalMilliseconds < TimeoutMs)
        {
            // Check for escape
            if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)
            {
                return false;  // Cancelled
            }

            var pos = machine.WorkPosition;
            double dx = Math.Abs(pos.X - targetX);
            double dy = Math.Abs(pos.Y - targetY);

            if (dx < PositionTolerance && dy < PositionTolerance)
            {
                return true;  // Reached target
            }

            Thread.Sleep(PollIntervalMs);
        }

        return true;  // Timeout, but don't treat as cancel
    }

    /// <summary>
    /// Creates the probe grid from G-Code bounds plus margin.
    /// </summary>
    static bool CreateProbeGrid(double margin, double gridSize)
    {
        var minX = currentFile!.Min.X - margin;
        var minY = currentFile.Min.Y - margin;
        var maxX = currentFile.Max.X + margin;
        var maxY = currentFile.Max.Y + margin;

        try
        {
            heightMap = new ProbeGrid(gridSize, new Vector2(minX, minY), new Vector2(maxX, maxY));
            heightMapApplied = false;
            AnsiConsole.MarkupLine($"[green]Probe grid: {heightMap.SizeX}x{heightMap.SizeY} = {heightMap.TotalPoints} points[/]");
            AnsiConsole.MarkupLine($"[dim]Bounds: X({minX:F2} to {maxX:F2}) Y({minY:F2} to {maxY:F2})[/]");
            return true;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error creating probe grid: {Markup.Escape(ex.Message)}[/]");
            Console.ReadKey();
            return false;
        }
    }

    /// <summary>
    /// Waits for probing to complete, showing progress matrix and handling user cancel.
    /// </summary>
    static void WaitForProbingComplete()
    {
        int lastProgress = -1;

        while (probing && heightMap != null && heightMap.NotProbed.Count > 0)
        {
            if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)
            {
                probing = false;
                machine.ProbeStop();
                AnsiConsole.MarkupLine("\n[yellow]Probing stopped by user[/]");
                break;
            }

            // Only redraw when progress changes
            if (heightMap.Progress != lastProgress)
            {
                lastProgress = heightMap.Progress;
                DrawProbeMatrix();
            }

            Thread.Sleep(StatusPollIntervalMs);
        }
    }

    /// <summary>
    /// Draws a visual matrix of probe progress. 'x' = probed, '.' = unprobed
    /// </summary>
    static void DrawProbeMatrix()
    {
        if (heightMap == null) return;

        // Build set of unprobed points for fast lookup
        var unprobed = new HashSet<(int, int)>();
        foreach (var p in heightMap.NotProbed)
        {
            unprobed.Add((p.Item1, p.Item2));
        }

        // Calculate scaling if grid is too large for terminal
        // Divide maxWidth by 2 since we use 2 chars per cell for aspect ratio
        int maxWidth = Math.Min((Console.WindowWidth - 5) / 2, 40);
        int maxHeight = Math.Min(Console.WindowHeight - 10, 30);

        int stepX = Math.Max(1, (heightMap.SizeX + maxWidth - 1) / maxWidth);
        int stepY = Math.Max(1, (heightMap.SizeY + maxHeight - 1) / maxHeight);

        // Calculate actual matrix width for centering (2 chars per cell for aspect ratio)
        int matrixWidth = ((heightMap.SizeX + stepX - 1) / stepX) * 2;
        int leftPadding = Math.Max(0, (Console.WindowWidth - matrixWidth) / 2);
        string pad = new string(' ', leftPadding);

        // Clear and draw header (centered)
        Console.Clear();
        string zRange = heightMap.Progress > 0
            ? $"Z: {heightMap.MinHeight:F3} to {heightMap.MaxHeight:F3}"
            : "Z: --";
        string header = $"Probing: {heightMap.Progress}/{heightMap.TotalPoints} | {zRange}";
        int headerPad = Math.Max(0, (Console.WindowWidth - header.Length) / 2);
        Console.WriteLine();
        AnsiConsole.MarkupLine(new string(' ', headerPad) + $"[bold]{header}[/]");
        AnsiConsole.MarkupLine(new string(' ', headerPad) + "[dim]Press Escape to stop[/]");
        Console.WriteLine();

        // Draw matrix (Y=0 at bottom, so draw from top row down)
        // Use 2 chars per column to compensate for terminal char aspect ratio (~2:1)
        for (int y = heightMap.SizeY - 1; y >= 0; y -= stepY)
        {
            var line = new System.Text.StringBuilder();
            line.Append(pad);
            for (int x = 0; x < heightMap.SizeX; x += stepX)
            {
                // Check if any point in this cell is unprobed
                bool cellProbed = true;
                for (int dy = 0; dy < stepY && y - dy >= 0; dy++)
                {
                    for (int dx = 0; dx < stepX && x + dx < heightMap.SizeX; dx++)
                    {
                        if (unprobed.Contains((x + dx, y - dy)))
                        {
                            cellProbed = false;
                            break;
                        }
                    }
                    if (!cellProbed) break;
                }
                line.Append(cellProbed ? "██" : "··");
            }
            Console.WriteLine(line.ToString());
        }
    }

    /// <summary>
    /// Displays probe results summary and prompts to save.
    /// </summary>
    static void ShowProbeResults()
    {
        if (heightMap == null)
        {
            return;
        }

        AnsiConsole.MarkupLine("[green]Probing complete![/]");
        AnsiConsole.MarkupLine($"  Points: {heightMap.TotalPoints}");
        AnsiConsole.MarkupLine($"  Z range: {heightMap.MinHeight:F3} to {heightMap.MaxHeight:F3} mm");
        AnsiConsole.MarkupLine($"  Variance: {heightMap.MaxHeight - heightMap.MinHeight:F3} mm");
        AnsiConsole.WriteLine();

        // Default filename based on loaded G-code file, or timestamp if none loaded
        string defaultFilename;
        if (currentFile != null && !string.IsNullOrEmpty(currentFile.FileName))
        {
            defaultFilename = Path.GetFileNameWithoutExtension(currentFile.FileName) + ".pgrid";
        }
        else
        {
            defaultFilename = DateTime.Now.ToString("yyyy-MM-dd-HH-mm") + ".pgrid";
        }

        string defaultPath = !string.IsNullOrEmpty(session.LastBrowseDirectory)
            ? Path.Combine(session.LastBrowseDirectory, defaultFilename)
            : defaultFilename;

        var path = AnsiConsole.Ask("Save probe data:", defaultPath);

        // Expand ~ for home directory
        if (path.StartsWith("~"))
        {
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Substring(2));
        }

        // Allow skipping save by entering empty or just pressing enter with empty input
        if (string.IsNullOrWhiteSpace(path))
        {
            AnsiConsole.MarkupLine("[yellow]Probe data not saved[/]");
            return;
        }

        // Warn if file exists
        if (File.Exists(path))
        {
            if (!Confirm($"Overwrite {Path.GetFileName(path)}?", true))
            {
                AnsiConsole.MarkupLine("[yellow]Probe data not saved[/]");
                return;
            }
        }

        try
        {
            heightMap.Save(path);
            AnsiConsole.MarkupLine($"[green]Probe data saved to {Markup.Escape(path)}[/]");

            // Save probe file path and directory for next time
            session.LastSavedProbeFile = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                session.LastBrowseDirectory = dir;
            }
            SaveSession();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error saving: {Markup.Escape(ex.Message)}[/]");
        }
    }

    /// <summary>
    /// Probes the next point in the queue. Called initially and after each probe completes.
    ///
    /// Sequence: Sort by nearest -> Move XY -> Probe down -> Retract
    /// The retract is queued immediately after probe so it executes before OnProbeFinished.
    /// </summary>
    static void ProbeNextPoint()
    {
        if (!probing || heightMap == null || heightMap.NotProbed.Count == 0) return;

        // Sort by nearest point (minimizes travel, matches original OpenCNCPilot behavior)
        SortProbePointsByDistance();

        var coords = heightMap.GetCoordinates(heightMap.NotProbed[0]);

        // Move XY at current Z (safe: we're at SafeHeight or retracted above last surface)
        machine.SendLine($"G0 X{coords.X:F3} Y{coords.Y:F3}");

        // Probe down until contact
        machine.SendLine($"G38.3 Z-{settings.ProbeMaxDepth:F3} F{settings.ProbeFeed:F1}");

        // Retract above surface (queued to execute before OnProbeFinished callback)
        machine.SendLine("G91");  // Relative mode
        machine.SendLine($"G0 Z{settings.ProbeMinimumHeight:F3}");
        machine.SendLine("G90");  // Back to absolute
    }

    /// <summary>
    /// Sorts remaining probe points by distance to current position.
    /// Uses ProbeXAxisWeight to bias towards X or Y axis movement.
    /// </summary>
    static void SortProbePointsByDistance()
    {
        if (heightMap == null) return;
        var currentPos = machine.WorkPosition.GetXY();
        heightMap.NotProbed.Sort((a, b) =>
        {
            var va = heightMap.GetCoordinates(a) - currentPos;
            var vb = heightMap.GetCoordinates(b) - currentPos;
            va.X *= settings.ProbeXAxisWeight;
            vb.X *= settings.ProbeXAxisWeight;
            return va.Magnitude.CompareTo(vb.Magnitude);
        });
    }

    /// <summary>
    /// Callback when GRBL reports probe result. Handles both single probe and grid probing.
    /// </summary>
    static void OnProbeFinished(Vector3 pos, bool success)
    {
        // Handle single probe (Z find)
        if (singleProbing)
        {
            singleProbing = false;
            machine.ProbeStop();
            singleProbeCallback?.Invoke(pos, success);
            singleProbeCallback = null;
            return;
        }

        // Handle grid probing
        if (!probing || heightMap == null || heightMap.NotProbed.Count == 0)
        {
            return;
        }

        if (success)
        {
            RecordProbePoint(pos);
            ContinueOrFinishProbing(pos);
        }
        else
        {
            HandleProbeFail();
        }
    }

    /// <summary>
    /// Records a successful probe point in the height map and saves progress.
    /// </summary>
    static void RecordProbePoint(Vector3 pos)
    {
        if (heightMap == null || heightMap.NotProbed.Count == 0) return;
        var point = heightMap.NotProbed[0];
        heightMap.AddPoint(point.Item1, point.Item2, pos.Z);
        heightMap.NotProbed.RemoveAt(0);

        // Auto-save progress for resume capability
        SaveProbeProgress();
    }

    /// <summary>
    /// Continues to next probe point or finishes if complete.
    /// Commands are queued in GRBL's buffer and execute in order.
    /// </summary>
    static void ContinueOrFinishProbing(Vector3 lastPos)
    {
        if (heightMap == null) return;

        if (heightMap.NotProbed.Count > 0)
        {
            ProbeNextPoint();
        }
        else
        {
            // All done - return to safe height
            machine.SendLine($"G0 Z{Math.Max(settings.ProbeSafeHeight, lastPos.Z):F3}");
            probing = false;
        }
    }

    /// <summary>
    /// Handles probe failure - either aborts or skips the point based on settings.
    /// </summary>
    static void HandleProbeFail()
    {
        if (settings.AbortOnProbeFail)
        {
            probing = false;
            AnsiConsole.MarkupLine("\n[red]Probe failed! Aborting.[/]");
            return;
        }

        // Skip this point and continue
        if (heightMap != null && heightMap.NotProbed.Count > 0)
        {
            heightMap.NotProbed.RemoveAt(0);
        }

        // Go to safe height before continuing (we don't know where we are)
        machine.SendLine("G91");
        machine.SendLine($"G0 Z{settings.ProbeSafeHeight:F3}");
        machine.SendLine("G90");

        if (heightMap != null && heightMap.NotProbed.Count > 0)
        {
            ProbeNextPoint();
        }
        else
        {
            probing = false;
        }
    }

    /// <summary>
    /// Performs a single Z probe at current XY position.
    /// Probes down until contact and stops there (does not retract).
    /// </summary>
    static void ProbeZ()
    {
        AnsiConsole.MarkupLine($"[yellow]Probing Z at current XY (max depth: {settings.ProbeMaxDepth}mm, feed: {settings.ProbeFeed}mm/min)[/]");
        AnsiConsole.MarkupLine("[dim]Probing...[/]");

        bool completed = false;
        bool success = false;
        double foundZ = 0;

        singleProbeCallback = (pos, probeSuccess) =>
        {
            success = probeSuccess;
            foundZ = pos.Z;
            completed = true;
        };

        singleProbing = true;
        machine.ProbeStart();
        machine.SendLine("G90");
        machine.SendLine($"G38.3 Z-{settings.ProbeMaxDepth:F3} F{settings.ProbeFeed:F1}");

        // Wait for probe to complete
        while (!completed)
        {
            if (Console.KeyAvailable && IsExitKey(Console.ReadKey(true)))
            {
                singleProbing = false;
                machine.ProbeStop();
                machine.FeedHold();
                AnsiConsole.MarkupLine("[yellow]Probe cancelled[/]");
                return;
            }
            Thread.Sleep(StatusPollIntervalMs);
        }

        if (success)
        {
            AnsiConsole.MarkupLine($"[green]Found Z at {foundZ:F3}mm[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[red]Probe failed - no contact[/]");
        }
    }

    /// <summary>
    /// Applies the completed height map to the G-Code for surface compensation.
    /// </summary>
    static void ApplyProbeGrid()
    {
        if (currentFile == null)
        {
            AnsiConsole.MarkupLine("[red]No G-code file loaded[/]");
            Console.ReadKey();
            return;
        }

        if (heightMap == null || heightMap.NotProbed.Count > 0)
        {
            AnsiConsole.MarkupLine("[red]Probe data not complete[/]");
            Console.ReadKey();
            return;
        }

        try
        {
            currentFile = currentFile.ApplyProbeGrid(heightMap);
            machine.SetFile(currentFile.GetGCode());
            heightMapApplied = true;
            AnsiConsole.MarkupLine("[green]Probe data applied to G-Code![/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
            Console.ReadKey();
        }
    }

    static void RunGCode()
    {
        if (!RequireConnection())
        {
            return;
        }

        if (machine.File.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No file loaded[/]");
            Console.ReadKey();
            return;
        }

        if (!ConfirmOrCancel("Probe equipment removed and spindle ready?", true))
        {
            return;
        }

        // Move Z up to safe height before starting to avoid scratching
        AnsiConsole.MarkupLine($"[dim]Moving to safe height Z{SafeExitHeightMm:F1}...[/]");
        machine.SendLine("G90");
        machine.SendLine($"G0 Z{SafeExitHeightMm:F1}");
        Thread.Sleep(CommandDelayMs);

        machine.FileGoto(0);
        machine.FileStart();
        MonitorMilling();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Mill Progress Display Constants
    // ═══════════════════════════════════════════════════════════════════════════

    // Grid sizing: Terminal characters are roughly 2:1 (taller than wide), so we use
    // 2 characters per cell horizontally to approximate square cells on screen.
    const int MillGridCharsPerCell = 2;
    const int MillGridMaxWidth = 50;      // Max cells wide (100 chars)
    const int MillGridMaxHeight = 20;     // Max cells tall
    const int MillGridMinWidth = 10;      // Min available width to show grid (5 cells)
    const int MillGridMinHeight = 3;      // Min available height to show grid (3 rows)

    // Terminal padding: Reserve space for header (~9 lines) + grid borders/labels (~3 lines)
    const int MillTermWidthPadding = 4;   // Left/right margins
    const int MillTermHeightPadding = 12; // Header + borders + bounds line
    const int MillBorderPadding = 2;      // Box border characters (│ on each side)

    // Progress bar
    const int MillProgressBarWidth = 30;

    // ETA calculation: Require minimum data before showing estimate to avoid
    // wildly inaccurate early predictions based on just a few lines.
    const int MillMinLinesForEta = 10;
    const double MillMinSecondsForEta = 1.0;

    // Z threshold: Only mark cells as "visited" when spindle is below this depth.
    // This prevents rapid traverse moves from filling in the map.
    const double MillCuttingDepthThreshold = 0.1;

    // Minimum range for coordinate mapping. Prevents division by zero for
    // toolpaths that are essentially 1D (e.g., a single line).
    const double MillMinRangeThreshold = 0.001;

    // Visual markers for the 2D grid display
    const string MillCurrentPosMarker = "● ";  // Shows current spindle position
    const string MillVisitedMarker = "░░";     // Shows where spindle has cut
    const string MillEmptyMarker = "··";       // Unvisited areas

    /// <summary>
    /// Monitors G-code execution with real-time progress display.
    /// Handles pause/resume/stop controls and tracks elapsed time.
    /// </summary>
    static void MonitorMilling()
    {
        // State tracking
        bool paused = false;
        bool hasEverRun = false;  // Prevents premature exit during mode transition
        var visitedCells = new HashSet<(int, int)>();

        // Time tracking: We exclude paused time from ETA calculations
        var startTime = DateTime.Now;
        var pauseStartTime = DateTime.Now;
        var totalPausedTime = TimeSpan.Zero;
        int startLine = machine.FilePosition;

        // Track terminal size to detect resize (need to clear on resize)
        var (lastWidth, lastHeight) = GetSafeWindowSize();

        // Clear screen once at start, then use cursor repositioning for updates
        Console.Clear();
        Console.CursorVisible = false;

        try
        {
            while (true)
            {
                bool isRunning = machine.Mode == Machine.OperatingMode.SendFile;

                // Track that we've seen the machine actually start running.
                // This prevents exiting immediately if the mode hasn't transitioned yet
                // (race condition between FileStart() and mode state update).
                if (isRunning)
                {
                    hasEverRun = true;
                }

                // Exit only after: (1) we've started, (2) stopped running, (3) not paused,
                // (4) reached end of file, (5) machine is idle (not still executing buffered commands).
                bool reachedEnd = machine.FilePosition >= machine.File.Count;
                bool machineIdle = machine.Status == StatusIdle;
                if (hasEverRun && !isRunning && !paused && reachedEnd && machineIdle)
                {
                    break;
                }

                // Handle keyboard controls
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    if (IsKey(key, ConsoleKey.P, 'p'))
                    {
                        PauseExecution();
                        paused = true;
                        pauseStartTime = DateTime.Now;
                    }
                    else if (IsKey(key, ConsoleKey.R, 'r'))
                    {
                        ResumeExecution();
                        paused = false;
                        totalPausedTime += DateTime.Now - pauseStartTime;
                    }
                    else if (IsKey(key, ConsoleKey.X, 'x'))
                    {
                        StopAndRaiseZ();
                        return;
                    }
                }

                // Calculate active milling time (excluding pauses) for accurate ETA
                var currentPausedTime = paused ? (DateTime.Now - pauseStartTime) : TimeSpan.Zero;
                var elapsed = DateTime.Now - startTime - totalPausedTime - currentPausedTime;

                // Detect terminal resize and clear to prevent artifacts
                var (curWidth, curHeight) = GetSafeWindowSize();
                if (curWidth != lastWidth || curHeight != lastHeight)
                {
                    Console.Clear();
                    lastWidth = curWidth;
                    lastHeight = curHeight;
                }

                DrawMillProgress(paused, visitedCells, elapsed, startLine);
                Thread.Sleep(StatusPollIntervalMs);
            }

            // Final display update showing completed state
            var finalElapsed = DateTime.Now - startTime - totalPausedTime;
            DrawMillProgress(false, visitedCells, finalElapsed, startLine);
            Console.WriteLine();
            AnsiConsole.MarkupLine("[green]Mill complete[/]");
            Thread.Sleep(ConfirmationDisplayMs);
        }
        finally
        {
            // Always restore cursor visibility, even on early exit or exception
            Console.CursorVisible = true;
        }
    }

    /// <summary>
    /// Renders the mill progress display: header info, progress bar, and optional 2D position map.
    /// Uses cursor repositioning for flicker-free updates (fixed-width formatting prevents artifacts).
    /// </summary>
    static void DrawMillProgress(bool paused, HashSet<(int, int)> visitedCells, TimeSpan elapsed, int startLine)
    {
        if (currentFile == null || !currentFile.ContainsMotion)
        {
            return;
        }

        // Reposition cursor to top-left; fixed-width numbers prevent artifacts
        Console.SetCursorPosition(0, 0);

        var (winWidth, winHeight) = GetSafeWindowSize();

        // ANSI color codes
        const string Cyan = "\u001b[36m";
        const string BoldCyan = "\u001b[1;36m";
        const string Yellow = "\u001b[93m";
        const string BoldBlue = "\u001b[1;34m";
        const string BoldRed = "\u001b[1;31m";
        const string Reset = "\u001b[0m";

        // Header (centered, on line 0 to minimize flicker)
        string header = $"{BoldBlue}Milling{Reset}";
        int headerPad = Math.Max(0, (winWidth - 7) / 2);  // 7 = visible length of "Milling"
        WriteLineTruncated(new string(' ', headerPad) + header, winWidth);
        WriteLineTruncated("", winWidth);

        // Progress statistics with fixed-width formatting to prevent display jumping
        var pos = machine.WorkPosition;
        int fileLine = machine.FilePosition;
        int totalLines = machine.File.Count;
        double pct = totalLines > 0 ? (100.0 * fileLine / totalLines) : 0;
        string etaStr = CalculateEta(elapsed, fileLine - startLine, totalLines - fileLine);
        string pausedIndicator = paused ? $"  {Yellow}PAUSED{Reset}" : "        ";  // Same visible width

        // Pad line numbers to width of total
        int lineWidth = totalLines.ToString().Length;
        string lineStr = fileLine.ToString().PadLeft(lineWidth);

        WriteLineTruncated($"  {Cyan}{BuildProgressBar(pct, Math.Min(MillProgressBarWidth, winWidth - 15))}{Reset} {pct,5:F1}%", winWidth);
        WriteLineTruncated($"  Elapsed: {Cyan}{FormatTimeSpan(elapsed)}{Reset}   ETA: {Cyan}{etaStr}{Reset}{pausedIndicator}", winWidth);
        WriteLineTruncated($"  X:{Cyan}{pos.X,8:F2}{Reset}  Y:{Cyan}{pos.Y,8:F2}{Reset}  Z:{Cyan}{pos.Z,8:F2}{Reset}   Line {lineStr}/{totalLines}", winWidth);
        WriteLineTruncated("", winWidth);
        WriteLineTruncated($"  {BoldCyan}P{Reset}=Pause  {BoldCyan}R{Reset}=Resume  {BoldRed}X{Reset}=Stop", winWidth);

        // === 2D position grid ===
        double minX = currentFile.Min.X;
        double maxX = currentFile.Max.X;
        double minY = currentFile.Min.Y;
        double maxY = currentFile.Max.Y;
        double rangeX = Math.Max(maxX - minX, MillMinRangeThreshold);
        double rangeY = Math.Max(maxY - minY, MillMinRangeThreshold);

        // Calculate available space for grid
        // Width: subtract borders (2) and small margin (2) = 4 chars
        // Height: subtract header (~9 lines) and grid overhead (~3 lines) = 12 lines
        int availableWidth = winWidth - 4;  // Room for borders and margin
        int availableHeight = winHeight - MillTermHeightPadding;

        // Calculate grid dimensions to fit available space
        // Each cell is 2 chars wide; ensure grid fits within terminal
        int gridWidth = Math.Clamp(availableWidth / MillGridCharsPerCell, 1, MillGridMaxWidth);
        int gridHeight = Math.Clamp(availableHeight, 1, MillGridMaxHeight);

        // Check if terminal is large enough for meaningful grid display
        bool gridVisible = availableWidth >= MillGridMinWidth && availableHeight >= MillGridMinHeight;

        WriteLineTruncated("", winWidth);

        if (!gridVisible)
        {
            WriteLineTruncated("  (Window too small for map)", winWidth);
            return;
        }

        // Map machine position to grid coordinates
        int gridX = MapToGrid(pos.X, minX, rangeX, gridWidth);
        int gridY = MapToGrid(pos.Y, minY, rangeY, gridHeight);

        // Only mark cells as visited when actively cutting (Z below surface)
        if (pos.Z < MillCuttingDepthThreshold)
        {
            visitedCells.Add((gridX, gridY));
        }

        DrawPositionGrid(gridWidth, gridHeight, gridX, gridY, visitedCells, winWidth, minX, maxX, minY, maxY);
    }

    /// <summary>
    /// Maps a coordinate value to a grid cell index.
    /// </summary>
    static int MapToGrid(double value, double min, double range, int gridSize)
    {
        int index = (int)((value - min) / range * (gridSize - 1));
        return Math.Clamp(index, 0, gridSize - 1);
    }

    /// <summary>
    /// Calculates ETA string based on running average of time per line.
    /// Returns "--:--" if insufficient data for reliable estimate.
    /// </summary>
    static string CalculateEta(TimeSpan elapsed, int linesCompleted, int linesRemaining)
    {
        if (linesCompleted <= MillMinLinesForEta || elapsed.TotalSeconds <= MillMinSecondsForEta)
        {
            return "--:--:--";  // Same width as HH:MM:SS
        }
        double secondsPerLine = elapsed.TotalSeconds / linesCompleted;
        var eta = TimeSpan.FromSeconds(secondsPerLine * linesRemaining);
        return FormatTimeSpan(eta);
    }

    /// <summary>
    /// Draws the 2D position grid showing spindle location and visited areas.
    /// Grid is centered horizontally and uses box-drawing characters for borders.
    /// </summary>
    static void DrawPositionGrid(int width, int height, int posX, int posY,
        HashSet<(int, int)> visited, int winWidth, double minX, double maxX, double minY, double maxY)
    {
        const string Yellow = "\u001b[93m";
        const string Reset = "\u001b[0m";

        int matrixWidth = width * MillGridCharsPerCell;
        int leftPadding = Math.Max(0, (winWidth - matrixWidth - MillBorderPadding) / 2);
        string pad = new string(' ', leftPadding);

        // Top border
        WriteLineTruncated($"{pad}┌{new string('─', matrixWidth)}┐", winWidth);

        // Grid rows: Y=0 at bottom in CNC coordinates, so draw top-to-bottom
        for (int y = height - 1; y >= 0; y--)
        {
            var line = new System.Text.StringBuilder(pad);
            line.Append('│');

            for (int x = 0; x < width; x++)
            {
                if (x == posX && y == posY)
                {
                    line.Append(Yellow).Append(MillCurrentPosMarker).Append(Reset);
                }
                else if (visited.Contains((x, y)))
                {
                    line.Append(MillVisitedMarker);
                }
                else
                {
                    line.Append(MillEmptyMarker);
                }
            }

            line.Append('│');
            WriteLineTruncated(line.ToString(), winWidth);
        }

        // Bottom border and coordinate labels
        WriteLineTruncated($"{pad}└{new string('─', matrixWidth)}┘", winWidth);
        WriteLineTruncated($"{pad}  X: {minX:F1} to {maxX:F1}  Y: {minY:F1} to {maxY:F1}", winWidth);
    }

    /// <summary>
    /// Emergency stop: halts execution, stops spindle, and raises Z to safe height.
    /// Cursor visibility is restored by MonitorMilling's finally block.
    /// </summary>
    static void StopAndRaiseZ()
    {
        int position = machine.FilePosition;

        // Suppress expected alarm messages during reset
        suppressErrors = true;

        // Soft reset to immediately stop and clear GRBL's buffer
        machine.SoftReset();

        Console.WriteLine();
        AnsiConsole.MarkupLine($"[yellow]Stopped at line {position} - stopping spindle and raising Z...[/]");

        // Wait for machine to finish reset
        Thread.Sleep(ResetWaitMs);

        // Unlock if in alarm state (soft reset can trigger alarm)
        if (machine.Status.StartsWith(StatusAlarm))
        {
            machine.SendLine("$X");  // Unlock
            Thread.Sleep(CommandDelayMs);
        }

        suppressErrors = false;  // Re-enable error messages

        machine.SendLine("M5");  // Stop spindle
        machine.SendLine("G90");
        machine.SendLine($"G0 Z{SafeExitHeightMm:F1}");
        Thread.Sleep(CommandDelayMs);
    }

    /// <summary>
    /// Waits for the machine to reach Idle state, with timeout.
    /// </summary>
    static bool WaitForIdle(int timeoutMs)
    {
        var start = DateTime.Now;
        while ((DateTime.Now - start).TotalMilliseconds < timeoutMs)
        {
            if (machine.Status == StatusIdle)
            {
                return true;
            }
            Thread.Sleep(StatusPollIntervalMs);
        }
        return false;
    }

    static void SettingsMenu()
    {
        while (true)
        {
            Console.Clear();
            AnsiConsole.Write(new Rule("[bold blue]Settings[/]").RuleStyle("blue"));

            var table = new Table();
            table.AddColumn("Setting");
            table.AddColumn("Value");

            table.AddRow("Serial Port", settings.SerialPortName);
            table.AddRow("Baud Rate", settings.SerialPortBaud.ToString());
            table.AddRow("Jog Feed", settings.JogFeed.ToString());
            table.AddRow("Jog Distance", settings.JogDistance.ToString());
            table.AddRow("Probe Feed", settings.ProbeFeed.ToString());
            table.AddRow("Probe Max Depth", settings.ProbeMaxDepth.ToString());
            table.AddRow("Probe Safe Height", settings.ProbeSafeHeight.ToString());
            table.AddRow("Outline Traverse Height", settings.OutlineTraverseHeight.ToString());
            table.AddRow("Outline Traverse Feed", settings.OutlineTraverseFeed.ToString());

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();

            var options = new[] {
                "1. Jog Feed (f)",
                "2. Jog Distance (d)",
                "3. Jog Feed Slow (g)",
                "4. Jog Distance Slow (e)",
                "5. Probe Feed (p)",
                "6. Probe Max Depth (m)",
                "7. Probe Safe Height (h)",
                "8. Outline Traverse Height (t)",
                "9. Outline Traverse Feed (o)",
                "A. Save Settings (s)",
                "0. Back"
            };

            int choice = ShowMenu("Edit setting:", options);

            switch (choice)
            {
                case 0:
                    settings.JogFeed = AnsiConsole.Ask("Jog Feed:", settings.JogFeed);
                    break;
                case 1:
                    settings.JogDistance = AnsiConsole.Ask("Jog Distance:", settings.JogDistance);
                    break;
                case 2:
                    settings.JogFeedSlow = AnsiConsole.Ask("Jog Feed (Slow):", settings.JogFeedSlow);
                    break;
                case 3:
                    settings.JogDistanceSlow = AnsiConsole.Ask("Jog Distance (Slow):", settings.JogDistanceSlow);
                    break;
                case 4:
                    settings.ProbeFeed = AnsiConsole.Ask("Probe Feed:", settings.ProbeFeed);
                    break;
                case 5:
                    settings.ProbeMaxDepth = AnsiConsole.Ask("Probe Max Depth:", settings.ProbeMaxDepth);
                    break;
                case 6:
                    settings.ProbeSafeHeight = AnsiConsole.Ask("Probe Safe Height:", settings.ProbeSafeHeight);
                    break;
                case 7:
                    settings.OutlineTraverseHeight = AnsiConsole.Ask("Outline Traverse Height:", settings.OutlineTraverseHeight);
                    break;
                case 8:
                    settings.OutlineTraverseFeed = AnsiConsole.Ask("Outline Traverse Feed (mm/min):", settings.OutlineTraverseFeed);
                    break;
                case 9:
                    SaveSettings();
                    break;
                case 10:
                    return;
            }
        }
    }

    static void ShowAbout()
    {
        Console.Clear();
        AnsiConsole.Write(new Rule($"[bold blue]About {AppTitle}[/]").RuleStyle("blue"));
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]{AppTitle} {Version}[/] - A CLI tool for PCB milling with GRBL");
        AnsiConsole.MarkupLine("By Thomer Gil");
        AnsiConsole.MarkupLine("[link]https://github.com/thomergil/coppercli[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold red]!! EXTREMELY EXPERIMENTAL !![/]");
        AnsiConsole.MarkupLine("[red]This software may damage your CNC machine.[/]");
        AnsiConsole.MarkupLine("[red]Use at your own risk.[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Based on the excellent [cyan]OpenCNCPilot[/]");
        AnsiConsole.MarkupLine("by martin2250: [link]https://github.com/martin2250/OpenCNCPilot[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Features:");
        AnsiConsole.MarkupLine("  - Surface probing for PCB auto-leveling");
        AnsiConsole.MarkupLine("  - G-code height map compensation");
        AnsiConsole.MarkupLine("  - GRBL machine control and jogging");
        AnsiConsole.MarkupLine("  - Serial and Ethernet connections (Ethernet untested)");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Press any key to return...[/]");
        Console.ReadKey(true);
    }

    /// <summary>
    /// Shows experimental warning on first startup. Offers to silence for future runs.
    /// </summary>
    static void ShowExperimentalWarning()
    {
        if (settings.SilenceExperimentalWarning)
        {
            return;
        }

        Console.Clear();
        AnsiConsole.Write(new Rule("[bold red]WARNING[/]").RuleStyle("red"));
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold red]!! EXTREMELY EXPERIMENTAL !![/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[red]This software may damage your CNC machine.[/]");
        AnsiConsole.MarkupLine("[red]Use at your own risk.[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]By continuing, you accept full responsibility for any damage[/]");
        AnsiConsole.MarkupLine("[yellow]that may occur to your machine, workpiece, or surroundings.[/]");
        AnsiConsole.WriteLine();

        if (Confirm("Silence this warning next time?", true))
        {
            settings.SilenceExperimentalWarning = true;
            SaveSettings();
        }
    }

    static void ExitProgram()
    {
        if (machine.Connected)
        {
            machine.Disconnect();
        }
        Environment.Exit(0);
    }
}
