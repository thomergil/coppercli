// Extracted from Program.cs

using coppercli.Core.Communication;
using coppercli.Core.Controllers;
using coppercli.Core.Util;
using coppercli.Helpers;
using Spectre.Console;
using static coppercli.CliConstants;
using static coppercli.Core.Util.Constants;
using static coppercli.Core.Util.GrblProtocol;
using static coppercli.Helpers.DisplayHelpers;

namespace coppercli.Menus
{
    /// <summary>
    /// Jog menu for jogging and zeroing the machine.
    /// Vi-style: press digit (1-5 or 1-9 depending on mode) then direction.
    /// Features a "cockpit" layout on wide terminals.
    /// </summary>
    internal static class JogMenu
    {
        /// <summary>Display state computed from machine state, used by both layouts.</summary>
        private record JogDisplayState(
            string StatusColor,
            Vector3 WorkPos,
            Vector3 MachinePos,
            string XyColor,
            string NColor,
            string CColor)
        {
            public static JogDisplayState Create(Machine machine)
            {
                var probeContact = machine.PinStateProbe;
                var hasFile = AppState.CurrentFile != null;
                return new JogDisplayState(
                    StatusColor: MachineWait.IsProblematic(machine) ? AnsiError : AnsiSuccess,
                    WorkPos: machine.WorkPosition,
                    MachinePos: machine.MachinePosition,
                    XyColor: probeContact ? AnsiDim : AnsiInfo,
                    NColor: probeContact ? AnsiDim : AnsiInfo,
                    CColor: (hasFile && !probeContact) ? AnsiInfo : AnsiDim);
            }
        }

        // Minimum inner width for the cockpit layout (content between ║ borders)
        private const int CockpitMinInnerWidth = 61;

        // Minimum terminal width for cockpit layout (inner width + 2 for borders)
        private const int CockpitMinWidth = CockpitMinInnerWidth + 2;

        // Number of fixed lines in cockpit layout
        private const int CockpitFixedLines = 25;

        // Minimum terminal height for cockpit layout (must fit all fixed lines)
        private const int CockpitMinHeight = CockpitFixedLines;

        // Pending multiplier for vi-style digit prefix (1-9, default 1)
        private static int _pendingMultiplier = 1;

        // Current jog mode (needed for redraw during ProbeZ)
        private static JogMode _currentMode = JogModes[1]; // Normal

        public static void Show()
        {
            if (!MenuHelpers.RequireConnection())
            {
                return;
            }

            var machine = AppState.Machine;

            // Reset multiplier on entry
            _pendingMultiplier = 1;

            // Enable auto-clear while in jog menu (user can see status updates)
            machine.EnableAutoStateClear = true;

            // Clear once, then use flicker-free redraws
            Console.Clear();
            Console.CursorVisible = false;

            // Track window size to detect resizes
            var (lastWidth, lastHeight) = GetSafeWindowSize();

            try
            {
                while (true)
                {
                    var (winWidth, winHeight) = GetSafeWindowSize();
                    var mode = JogModes[AppState.JogPresetIndex];
                    _currentMode = mode;  // Track for ProbeZ redraw

                    // Clear screen on terminal resize to avoid artifacts
                    if (winWidth != lastWidth || winHeight != lastHeight)
                    {
                        Console.Clear();
                        lastWidth = winWidth;
                        lastHeight = winHeight;
                    }

                    RedrawScreen(machine, mode);

                    var keyOrNull = InputHelpers.ReadKeyPolling();
                    if (keyOrNull == null)
                    {
                        continue; // Status changed, redraw screen
                    }

                    if (!HandleKey(keyOrNull.Value, machine, mode))
                    {
                        return; // Exit requested
                    }
                }
            }
            finally
            {
                Console.CursorVisible = true;
                machine.EnableAutoStateClear = false;
            }
        }

        /// <summary>
        /// Builds a boxed line: ║ content padded to innerWidth ║
        /// </summary>
        private static string BoxLine(string content, int innerWidth)
        {
            int displayLen = CalculateDisplayLength(content);
            int padding = Math.Max(0, innerWidth - displayLen);
            return $"║{content}{new string(' ', padding)}║";
        }

        /// <summary>
        /// Builds a horizontal border line with specified corners/joints.
        /// </summary>
        private static string BoxBorder(char left, char right, int innerWidth, char fill = '═')
        {
            return $"{left}{new string(fill, innerWidth)}{right}";
        }

        /// <summary>
        /// Builds a section divider with column separators at specified positions.
        /// </summary>
        private static string BoxDivider(char left, char right, int innerWidth, char fill, params (int pos, char joint)[] joints)
        {
            var line = new char[innerWidth];
            Array.Fill(line, fill);
            foreach (var (pos, joint) in joints)
            {
                if (pos >= 0 && pos < innerWidth)
                {
                    line[pos] = joint;
                }
            }
            return $"{left}{new string(line)}{right}";
        }

        /// <summary>
        /// Redraws the jog screen (cockpit or compact based on terminal size).
        /// </summary>
        private static void RedrawScreen(Machine machine, JogMode mode)
        {
            Console.SetCursorPosition(0, 0);
            var (winWidth, winHeight) = GetSafeWindowSize();
            if (winWidth >= CockpitMinWidth && winHeight >= CockpitMinHeight)
            {
                DrawCockpitLayout(machine, mode, winWidth, winHeight);
            }
            else
            {
                DrawCompactLayout(machine, mode, winWidth, winHeight);
            }
        }

        /// <summary>
        /// Draws the full cockpit layout for wide terminals.
        /// </summary>
        private static void DrawCockpitLayout(Machine machine, JogMode mode, int winWidth, int winHeight)
        {
            var ds = JogDisplayState.Create(machine);
            var (statusColor, wp, mp, xyColor, nColor, cColor) = ds;

            // Calculate box width: fill terminal but respect minimum
            int innerWidth = Math.Max(CockpitMinInnerWidth, winWidth - 2);

            // Calculate vertical padding (extra lines distributed above and below joysticks)
            int extraLines = Math.Max(0, winHeight - CockpitFixedLines);
            int topPadding = extraLines / 2;
            int bottomPadding = extraLines - topPadding;

            // Distance display - calculate actual width needed for right column alignment
            var distanceStr = mode.FormatDistance(_pendingMultiplier);
            var distDisplay = _pendingMultiplier > 1
                ? $"{AnsiSuccessBold}{_pendingMultiplier}{AnsiReset}x{mode.BaseDistance}={AnsiSuccessBold}{distanceStr}{AnsiReset}"
                : $"{AnsiSuccess}{distanceStr}{AnsiReset}";

            // Column positions for command panel (fixed widths for left two columns)
            const int col1Width = 15;  // Commands column
            const int col2Width = 15;  // Set Zero column
            int col3Width = innerWidth - col1Width - col2Width - 2;  // Go To Position (remaining, minus 2 separators)

            // Visible content widths for col3 padding calculations
            const int col3HeaderWidth = 15;      // "Go To Position"
            const int col3Row12Width = 35;       // Rows 1-2 content width
            const int col3Row3Width = 26;        // Row 3 content width
            const int probeContentWidth = 17;    // " Probe:   Contact" display width
            const int prefixPokeLeft = 7;        // How far "prefix" pokes left into gap2

            // Header - Status line with right-aligned hints
            var hints = $"{AnsiDim}?=Help  Esc/Q to exit{AnsiReset}";
            var statusContent = $" Status: {statusColor}{machine.Status}{AnsiReset}";
            int statusDisplayLen = CalculateDisplayLength(statusContent);
            int hintsDisplayLen = 21; // "?=Help  Esc/Q to exit"
            int statusPadding = Math.Max(1, innerWidth - statusDisplayLen - hintsDisplayLen - 1);
            WriteLineTruncated(BoxBorder('╔', '╗', innerWidth), winWidth);
            WriteLineTruncated(BoxLine($"{statusContent}{new string(' ', statusPadding)}{hints}", innerWidth), winWidth);
            WriteLineTruncated(BoxBorder('╠', '╣', innerWidth), winWidth);

            // Position display - coordinates are fixed width, padding expands on right
            int posContentWidth = 62;  // "Work:    X:  -999.999   Y:  -999.999   Z:  -999.999"
            int posPadding = Math.Max(0, innerWidth - posContentWidth - 1);
            WriteLineTruncated(BoxLine($" Work:    X:{AnsiWarning}{wp.X,9:F3}{AnsiReset}   Y:{AnsiWarning}{wp.Y,9:F3}{AnsiReset}   Z:{AnsiWarning}{wp.Z,9:F3}{AnsiReset}{new string(' ', posPadding)}", innerWidth), winWidth);
            WriteLineTruncated(BoxLine($" Machine: X:{AnsiDim}{mp.X,9:F3}{AnsiReset}   Y:{AnsiDim}{mp.Y,9:F3}{AnsiReset}   Z:{AnsiDim}{mp.Z,9:F3}{AnsiReset}{new string(' ', posPadding)}", innerWidth), winWidth);
            // Probe pin status (BitZero)
            var probeColor = machine.PinStateProbe ? AnsiError : AnsiSuccess;
            var probeText = machine.PinStateProbe ? "Contact" : "Open";
            int probePadding = Math.Max(0, innerWidth - probeContentWidth - 1);
            WriteLineTruncated(BoxLine($" Probe:   {probeColor}{probeText}{AnsiReset}{new string(' ', probePadding)}", innerWidth), winWidth);
            WriteLineTruncated(BoxBorder('╠', '╣', innerWidth), winWidth);

            // Add top padding (half of extra lines)
            for (int i = 0; i < topPadding; i++)
            {
                WriteLineTruncated(BoxLine("", innerWidth), winWidth);
            }

            // Joystick area with dynamic spacing
            // XY joystick is 16 chars wide (H box + W/J center + L box)
            // Z joystick is 5 chars wide (single box with connector)
            // Info column is ~22 chars (Mode/Feed/Dist labels + values)
            const int xyJoyWidth = 20;   // "X- │ A │     │ D │ X+" with breathing room
            const int zJoyWidth = 5;     // "┌───┐" = 5
            const int infoWidth = 16;    // Mode/Feed/Dist area (smaller = more gap space)
            const int minGap = 1;

            int totalFixedWidth = xyJoyWidth + zJoyWidth + infoWidth + minGap * 2;
            int extraSpace = Math.Max(0, innerWidth - totalFixedWidth - 1);  // -1 for left margin
            int gap1 = minGap + extraSpace / 2;           // Gap between XY and Z
            int gap2 = minGap + extraSpace / 2;           // Gap between Z and info

            string g1 = new string(' ', gap1);
            string g1short = new string(' ', Math.Max(0, gap1 - 1));  // X+ line eats 1 char left
            string g2 = new string(' ', gap2);
            string g2short = new string(' ', Math.Max(0, gap2 - prefixPokeLeft));
            string g2feed = new string(' ', Math.Max(0, gap2 - 3));   // Feed line eats 3 chars left
            string g2dist = new string(' ', Math.Max(0, gap2 - 5));   // Dist line eats 5 chars left

            // XY joystick: shifted right by 3 to make room for X- label with breathing room
            WriteLineTruncated(BoxLine($"         ┌───┐     {g1}┌───┐{g2}{AnsiInfo}Mode:{AnsiReset} {AnsiSuccess}{mode.Name,-8}{AnsiReset}", innerWidth), winWidth);
            WriteLineTruncated(BoxLine($"         │{xyColor} W {AnsiReset}│ Y+  {g1}│{AnsiInfo} Q {AnsiReset}│ Z+{g2feed}{AnsiInfo}Feed:{AnsiReset} {mode.Feed}mm/min", innerWidth), winWidth);
            WriteLineTruncated(BoxLine($"         │ ▲ │     {g1}│ ▲ │ {AnsiDim}PgUp{AnsiReset}{g2dist}{AnsiInfo}Dist:{AnsiReset} {distDisplay}", innerWidth), winWidth);
            WriteLineTruncated(BoxLine($"    ┌───┐└───┘┌───┐{g1}└─┬─┘", innerWidth), winWidth);
            WriteLineTruncated(BoxLine($" X- │{xyColor} A {AnsiReset}│     │{xyColor} D {AnsiReset}│ X+{g1short}│{g2short}  prefix [{AnsiInfo}1-{mode.MaxMultiplier}{AnsiReset}] ×distance", innerWidth), winWidth);
            WriteLineTruncated(BoxLine($"    │ ← │     │ → │  {g1}│{g2}  [{AnsiInfo}Tab{AnsiReset}] change mode", innerWidth), winWidth);
            WriteLineTruncated(BoxLine($"    └───┘┌───┐└───┘{g1}┌─┴─┐", innerWidth), winWidth);
            WriteLineTruncated(BoxLine($"         │{xyColor} X {AnsiReset}│     {g1}│{AnsiInfo} Z {AnsiReset}│ Z-", innerWidth), winWidth);
            WriteLineTruncated(BoxLine($"         │ ▼ │ Y-  {g1}│ ▼ │ {AnsiDim}PgDn{AnsiReset}", innerWidth), winWidth);
            WriteLineTruncated(BoxLine($"         └───┘     {g1}└───┘", innerWidth), winWidth);

            // Add bottom padding (remaining extra lines)
            for (int i = 0; i < bottomPadding; i++)
            {
                WriteLineTruncated(BoxLine("", innerWidth), winWidth);
            }

            // Command panels - three columns with vertical separators
            WriteLineTruncated(BoxDivider('╠', '╣', innerWidth, '═', (col1Width, '╦'), (col1Width + 1 + col2Width, '╦')), winWidth);
            WriteLineTruncated(BoxLine($" {AnsiInfo}Commands{AnsiReset}      ║ {AnsiInfo}Set Zero{AnsiReset}      ║ {AnsiInfo}Go To Position{AnsiReset}{new string(' ', Math.Max(0, col3Width - col3HeaderWidth))}", innerWidth), winWidth);
            WriteLineTruncated(BoxDivider('║', '║', innerWidth, '─', (col1Width, '║'), (col1Width + 1 + col2Width, '║')), winWidth);
            WriteLineTruncated(BoxLine($" {AnsiInfo}H{AnsiReset}  Home       ║ {AnsiInfo}0{AnsiReset}  All XYZ    ║ {nColor}.  Origin XY{AnsiReset}       {AnsiInfo}T{AnsiReset}  Z+6mm{new string(' ', Math.Max(0, col3Width - col3Row12Width))}", innerWidth), winWidth);
            WriteLineTruncated(BoxLine($" {AnsiInfo}U{AnsiReset}  Unlock     ║ {AnsiInfo}L{AnsiReset}  Level (Z)  ║ {cColor}C  Center{AnsiReset}          {AnsiInfo}G{AnsiReset}  Z+1mm{new string(' ', Math.Max(0, col3Width - col3Row12Width))}", innerWidth), winWidth);
            WriteLineTruncated(BoxLine($" {AnsiInfo}R{AnsiReset}  Reset      ║ {AnsiInfo}P{AnsiReset}  Probe Z    ║                    {AnsiInfo}B{AnsiReset}  Z0{new string(' ', Math.Max(0, col3Width - col3Row3Width))}", innerWidth), winWidth);
            WriteLineTruncated(BoxLine($" {AnsiInfo}␣{AnsiReset}  Pause      ║               ║{new string(' ', Math.Max(0, col3Width))}", innerWidth), winWidth);
            // Use addNewline: false for the last line to prevent scroll when terminal height == content height
            WriteLineTruncated(BoxDivider('╚', '╝', innerWidth, '═', (col1Width, '╩'), (col1Width + 1 + col2Width, '╩')), winWidth, addNewline: false);
        }

        /// <summary>
        /// Shows a message when terminal is too small for cockpit layout.
        /// </summary>
        private static void DrawCompactLayout(Machine machine, JogMode mode, int winWidth, int winHeight)
        {
            bool needsWidth = winWidth < CockpitMinWidth;
            bool needsHeight = winHeight < CockpitMinHeight;

            string sizeHint = (needsWidth, needsHeight) switch
            {
                (true, true) => $"Enlarge window to {CockpitMinWidth}x{CockpitMinHeight}",
                (true, false) => $"Widen window to {CockpitMinWidth} columns",
                (false, true) => $"Increase window to {CockpitMinHeight} rows",
                _ => ""
            };

            WriteLineTruncated($"{AnsiWarning}{sizeHint}{AnsiReset}", winWidth);
            WriteLineTruncated($"{AnsiDim}Esc/Q to exit{AnsiReset}", winWidth, addNewline: false);
        }

        /// <summary>
        /// Handles a keypress. Returns false if exit requested.
        /// </summary>
        private static bool HandleKey(ConsoleKeyInfo key, Machine machine, JogMode mode)
        {
            if (InputHelpers.IsExitKey(key))
            {
                return false;
            }

            // Tab cycles through jog modes
            if (key.Key == ConsoleKey.Tab)
            {
                AppState.JogPresetIndex = (AppState.JogPresetIndex + 1) % JogModes.Length;
                _pendingMultiplier = 1; // Reset multiplier on mode change
                return true;
            }

            // Help screen
            if (key.KeyChar == '?')
            {
                ShowHelp();
                return true;
            }

            // Handle digit keys for vi-style multiplier (1-9, but respect MaxMultiplier)
            int? digit = key.Key switch
            {
                ConsoleKey.D1 => 1,
                ConsoleKey.D2 => 2,
                ConsoleKey.D3 => 3,
                ConsoleKey.D4 => 4,
                ConsoleKey.D5 => 5,
                ConsoleKey.D6 => 6,
                ConsoleKey.D7 => 7,
                ConsoleKey.D8 => 8,
                ConsoleKey.D9 => 9,
                _ => null
            };

            if (digit.HasValue && digit.Value <= mode.MaxMultiplier)
            {
                _pendingMultiplier = digit.Value;
                return true;
            }

            // Handle command keys
            if (InputHelpers.IsKey(key, ConsoleKey.H))
            {
                machine.SoftReset();
                MachineCommands.HomeAndWait(machine);
                return true;
            }
            if (InputHelpers.IsKey(key, ConsoleKey.U))
            {
                MachineCommands.Unlock(machine);
                if (MachineWait.IsDoor(machine) || MachineWait.IsHold(machine))
                {
                    machine.CycleStart();
                }
                ShowOverlayTimed("Unlocked", ConfirmationDisplayMs);
                return true;
            }
            if (InputHelpers.IsKey(key, ConsoleKey.R))
            {
                machine.SoftReset();
                ShowOverlayTimed("Reset", ConfirmationDisplayMs, messageColor: AnsiWarning);
                return true;
            }
            if (key.Key == ConsoleKey.Spacebar)
            {
                if (MachineWait.IsHold(machine))
                {
                    machine.CycleStart();
                }
                else if (machine.Status == StatusRun)
                {
                    machine.FeedHold();
                }
                return true;
            }
            if (InputHelpers.IsKey(key, ConsoleKey.L))
            {
                // Z-only zeroing (Level): SetWorkZeroAndWait re-applies probe grid if it was applied
                bool hadProbeApplied = AppState.AreProbePointsApplied;
                MachineCommands.SetWorkZeroAndWait(machine, "Z0");
                var zMsg = hadProbeApplied
                    ? "Z zeroed (probe grid re-applied)"
                    : "Z zeroed";
                ShowOverlayTimed(zMsg, ConfirmationDisplayMs);
                MachineCommands.MoveToSafeHeight(machine, Constants.RetractZMm);
                return false; // Exit after zeroing
            }
            if (InputHelpers.IsKey(key, ConsoleKey.D0))
            {
                // Warn if probe data exists
                if (!ConfirmZeroWithProbeData("all axes"))
                {
                    return true;
                }

                Logger.Log("JogMenu: D0 pressed, zeroing all axes");
                // SetWorkZeroAndWait discards probe data when XY is zeroed
                MachineCommands.SetWorkZeroAndWait(machine, "X0 Y0 Z0");

                // Store work zero in session
                var session = AppState.Session;
                var pos = machine.WorkPosition;
                session.WorkZeroX = pos.X;
                session.WorkZeroY = pos.Y;
                session.WorkZeroZ = pos.Z;
                session.HasStoredWorkZero = true;
                Persistence.SaveSession();
                Logger.Log($"JogMenu: Work zero saved at ({pos.X}, {pos.Y}, {pos.Z})");

                ShowOverlayTimed("All axes zeroed", ConfirmationDisplayMs);
                MachineCommands.MoveToSafeHeight(machine, Constants.RetractZMm);
                return false; // Exit after zeroing
            }
            if (InputHelpers.IsKey(key, ConsoleKey.T))
            {
                MachineCommands.MoveToSafeHeight(machine, Constants.RetractZMm);
                return true;
            }
            if (InputHelpers.IsKey(key, ConsoleKey.G))
            {
                MachineCommands.MoveToSafeHeight(machine, ReferenceZHeightMm);
                return true;
            }
            if (InputHelpers.IsKey(key, ConsoleKey.B))
            {
                MachineCommands.MoveToSafeHeight(machine, 0);
                return true;
            }
            if (key.KeyChar == '.')
            {
                // Block X/Y movement when probe is in contact
                if (machine.PinStateProbe)
                {
                    return true;
                }
                MachineCommands.SetAbsoluteMode(machine);
                MachineCommands.RapidMoveXY(machine, 0, 0);
                return true;
            }
            if (InputHelpers.IsKey(key, ConsoleKey.C))
            {
                // Block X/Y movement when probe is in contact
                if (machine.PinStateProbe)
                {
                    return true;
                }
                var currentFile = AppState.CurrentFile;
                if (currentFile != null)
                {
                    double centerX = (currentFile.Min.X + currentFile.Max.X) / 2;
                    double centerY = (currentFile.Min.Y + currentFile.Max.Y) / 2;
                    MachineCommands.SetAbsoluteMode(machine);
                    MachineCommands.RapidMoveXY(machine, centerX, centerY);
                }
                return true;
            }
            if (InputHelpers.IsKey(key, ConsoleKey.P))
            {
                ProbeZ();
                return true;
            }

            // Handle jog keys with vi-style multiplier
            // Block X/Y jog when probe is in contact (prevents dragging probe across workpiece)
            double distance = mode.BaseDistance * _pendingMultiplier;
            bool jogged = JogHelpers.HandleJogKey(key, machine, mode.Feed, distance, machine.PinStateProbe);
            if (jogged)
            {
                _pendingMultiplier = 1; // Reset after jog
            }

            return true;
        }

        /// <summary>
        /// Shows the help screen with a concise manual.
        /// </summary>
        private static void ShowHelp()
        {
            Console.Clear();
            AnsiConsole.MarkupLine($"[{ColorPrompt}]Jog Menu Help[/]");
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine($"[{ColorInfo}]MOVEMENT[/]");
            AnsiConsole.MarkupLine($"  [{ColorInfo}]A W D X[/]  or  [{ColorInfo}]Arrow keys[/]    Move X/Y");
            AnsiConsole.MarkupLine($"  [{ColorInfo}]Q Z[/]      or  [{ColorInfo}]PgUp/PgDn[/]     Move Z up/down");
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine($"[{ColorInfo}]DISTANCE PREFIX[/]");
            AnsiConsole.MarkupLine($"  [{ColorInfo}]1-9[/]  Type a digit before a move key to multiply distance");
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine($"[{ColorInfo}]JOG MODES[/]");
            AnsiConsole.MarkupLine($"  [{ColorInfo}]Tab[/]  Cycle: Fast (10mm), Normal (1mm), Slow (0.1mm), Creep (0.01mm)");
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine($"[{ColorInfo}]MACHINE COMMANDS[/]");
            AnsiConsole.MarkupLine($"  [{ColorInfo}]H[/]  Home    [{ColorInfo}]U[/]  Unlock    [{ColorInfo}]R[/]  Reset    [{ColorInfo}]Space[/]  Pause/Resume");
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine($"[{ColorInfo}]SET WORK ZERO[/]");
            AnsiConsole.MarkupLine($"  [{ColorInfo}]0[/]  Zero all axes (X, Y, Z), then retract");
            AnsiConsole.MarkupLine($"  [{ColorInfo}]L[/]  Level (Z axis only), then retract");
            AnsiConsole.MarkupLine($"  [{ColorInfo}]P[/]  Probe Z at current XY position");
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine($"[{ColorInfo}]GO TO POSITION[/]");
            AnsiConsole.MarkupLine($"  [{ColorInfo}].[/]  Origin XY    [{ColorInfo}]C[/]  Center of file");
            AnsiConsole.MarkupLine($"  [{ColorInfo}]B[/]  Z0 (work zero)    [{ColorInfo}]T[/]  Z+6mm    [{ColorInfo}]G[/]  Z+1mm");
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine($"[{ColorDim}]?=Help  Esc/Q=Exit                      Press any key to return...[/]");
            Console.ReadKey(true);
            Console.Clear();
        }

        /// <summary>
        /// Checks if probe data exists and prompts user to confirm zeroing.
        /// Returns true if user confirms or no probe data exists.
        /// </summary>
        private static bool ConfirmZeroWithProbeData(string axisDescription)
        {
            var probeState = Persistence.GetProbeState();
            if (probeState == Persistence.ProbeState.None)
            {
                return true; // No probe data, proceed
            }

            var stateDesc = probeState == Persistence.ProbeState.Partial ? "partial" : "complete";
            return MenuHelpers.Confirm(
                $"You have {stateDesc} probe data. Zeroing {axisDescription} will invalidate it. Continue?",
                false);
        }

        /// <summary>
        /// Performs a single Z probe at current XY position.
        /// Uses ProbeController.ProbeZSingleAsync for the actual probe operation.
        /// </summary>
        private static void ProbeZ()
        {
            var machine = AppState.Machine;
            var settings = AppState.Settings;
            var controller = AppState.Probe;

            Logger.Log("JogMenu: ProbeZ starting");

            // Configure probe options
            controller.Options = new Core.Controllers.ProbeOptions
            {
                MaxDepth = settings.ProbeMaxDepth,
                ProbeFeed = settings.ProbeFeed
            };

            // Use CancellationTokenSource for user cancellation
            using var cts = new CancellationTokenSource();

            // Start probe on background thread so we can monitor for key press
            var probeTask = Task.Run(async () => await controller.ProbeZSingleAsync(cts.Token));

            // Wait for completion or user cancel, redrawing to show Z descent
            while (!probeTask.IsCompleted)
            {
                RedrawScreen(machine, _currentMode);

                if (Console.KeyAvailable && InputHelpers.IsExitKey(Console.ReadKey(true)))
                {
                    Logger.Log("JogMenu: ProbeZ cancelled by user");
                    cts.Cancel();
                    machine.SoftReset();  // Reset to clear state
                    return;
                }
                Thread.Sleep(StatusPollIntervalMs);
            }

            // Check result
            try
            {
                var (success, zPosition) = probeTask.Result;
                Logger.Log($"JogMenu: ProbeZ completed - success={success}, Z={zPosition:F3}");

                if (success)
                {
                    // Zero Z at probe position
                    MachineCommands.SetWorkZeroAndWait(machine, "Z0");
                    Logger.Log("JogMenu: Z zeroed after probe");
                }
                else
                {
                    // Probe failed - show error overlay
                    ShowOverlayAndWait(ControllerConstants.ErrorProbeNoContact);
                }
            }
            catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
            {
                Logger.Log("JogMenu: ProbeZ was cancelled");
            }
        }
    }
}
