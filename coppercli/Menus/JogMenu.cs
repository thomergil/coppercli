// Extracted from Program.cs

using coppercli.Core.Communication;
using coppercli.Core.Util;
using coppercli.Helpers;
using Spectre.Console;
using static coppercli.CliConstants;
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
        // Minimum inner width for the cockpit layout (content between ║ borders)
        private const int CockpitMinInnerWidth = 73;

        // Minimum terminal width for cockpit layout (inner width + 2 for borders)
        private const int CockpitMinWidth = CockpitMinInnerWidth + 2;

        // Minimum terminal height for cockpit layout (30 lines + margin)
        private const int CockpitMinHeight = 27;

        // Pending multiplier for vi-style digit prefix (1-9, default 1)
        private static int _pendingMultiplier = 1;

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

                    // Clear screen on terminal resize to avoid artifacts
                    if (winWidth != lastWidth || winHeight != lastHeight)
                    {
                        Console.Clear();
                        lastWidth = winWidth;
                        lastHeight = winHeight;
                    }

                    // Reset cursor to top-left for flicker-free redraw
                    Console.SetCursorPosition(0, 0);

                    // Choose layout based on terminal size
                    if (winWidth >= CockpitMinWidth && winHeight >= CockpitMinHeight)
                    {
                        DrawCockpitLayout(machine, mode, winWidth, winHeight);
                    }
                    else
                    {
                        DrawCompactLayout(machine, mode, winWidth);
                    }

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
        /// Draws the full cockpit layout for wide terminals.
        /// </summary>
        private static void DrawCockpitLayout(Machine machine, JogMode mode, int winWidth, int winHeight)
        {
            var statusColor = StatusHelpers.IsProblematicState(machine) ? AnsiError : AnsiSuccess;
            var wp = machine.WorkPosition;
            var mp = machine.MachinePosition;
            var hasFile = AppState.CurrentFile != null;
            var cColor = hasFile ? AnsiInfo : AnsiDim;

            // Calculate box width: fill terminal but respect minimum
            int innerWidth = Math.Max(CockpitMinInnerWidth, winWidth - 2);

            // Calculate vertical padding - cockpit has 29 fixed lines
            // Subtract 1 to account for final newline causing scroll
            const int fixedLines = 29;
            int extraLines = Math.Max(0, winHeight - fixedLines - 1);

            // Distance display - calculate actual width needed for right column alignment
            var distanceStr = mode.FormatDistance(_pendingMultiplier);
            var distDisplay = _pendingMultiplier > 1
                ? $"{AnsiBoldGreen}{_pendingMultiplier}{AnsiReset}x{mode.BaseDistance}={AnsiBoldGreen}{distanceStr}{AnsiReset}"
                : $"{AnsiSuccess}{distanceStr}{AnsiReset}";

            // Column positions for command panel (fixed widths for left two columns)
            const int col1Width = 15;  // Commands column
            const int col2Width = 15;  // Set Zero column
            int col3Width = innerWidth - col1Width - col2Width - 2;  // Go To Position (remaining, minus 2 separators)

            // Header
            WriteLineTruncated(BoxBorder('╔', '╗', innerWidth), winWidth);
            WriteLineTruncated(BoxLine($" Status: {statusColor}{machine.Status}{AnsiReset}", innerWidth), winWidth);
            WriteLineTruncated(BoxBorder('╠', '╣', innerWidth), winWidth);

            // Position display - coordinates are fixed width, padding expands on right
            int posContentWidth = 62;  // "Work:    X:  -999.999   Y:  -999.999   Z:  -999.999"
            int posPadding = innerWidth - posContentWidth - 1;
            WriteLineTruncated(BoxLine($" Work:    X:{AnsiWarning}{wp.X,9:F3}{AnsiReset}   Y:{AnsiWarning}{wp.Y,9:F3}{AnsiReset}   Z:{AnsiWarning}{wp.Z,9:F3}{AnsiReset}{new string(' ', posPadding)}", innerWidth), winWidth);
            WriteLineTruncated(BoxLine($" Machine: X:{AnsiDim}{mp.X,9:F3}{AnsiReset}   Y:{AnsiDim}{mp.Y,9:F3}{AnsiReset}   Z:{AnsiDim}{mp.Z,9:F3}{AnsiReset}{new string(' ', posPadding)}", innerWidth), winWidth);
            WriteLineTruncated(BoxBorder('╠', '╣', innerWidth), winWidth);

            // Joystick area with dynamic spacing
            // XY joystick is 16 chars wide (H box + W/J center + L box)
            // Z joystick is 5 chars wide (single box with connector)
            // Info column is ~22 chars (Mode/Feed/Dist labels + values)
            const int xyJoyWidth = 16;   // " ┌───┐└───┘┌───┐" = 1+5+5+5 = 16
            const int zJoyWidth = 5;     // "┌───┐" = 5
            const int infoWidth = 22;    // Mode/Feed/Dist area
            const int minGap = 3;

            int totalFixedWidth = xyJoyWidth + zJoyWidth + infoWidth + minGap * 2;
            int extraSpace = Math.Max(0, innerWidth - totalFixedWidth - 1);  // -1 for left margin
            int gap1 = minGap + extraSpace / 2;           // Gap between XY and Z
            int gap2 = minGap + extraSpace / 2;           // Gap between Z and info

            string g1 = new string(' ', gap1);
            string g2 = new string(' ', gap2);
            string g2short = new string(' ', Math.Max(0, gap2 - 7));  // For "prefix " to poke left

            // XY joystick: all lines padded to 16 chars so Z joystick aligns
            // Col layout: [1 space][H box 5][W/J box 5][L box 5] = 16 total
            // The W/J center column is 5 chars - lines without boxes there get 5 spaces
            WriteLineTruncated(BoxLine("", innerWidth), winWidth);
            WriteLineTruncated(BoxLine($"      ┌───┐     {g1}┌───┐   {g2}{AnsiInfo}Mode:{AnsiReset} {AnsiSuccess}{mode.Name,-8}{AnsiReset}", innerWidth), winWidth);
            WriteLineTruncated(BoxLine($"      │{AnsiInfo} K {AnsiReset}│ Y+  {g1}│{AnsiInfo} W {AnsiReset}│ Z+{g2}{AnsiInfo}Feed:{AnsiReset} {mode.Feed}mm/min", innerWidth), winWidth);
            WriteLineTruncated(BoxLine($"      │ ▲ │     {g1}│ ▲ │   {g2}{AnsiInfo}Dist:{AnsiReset} {distDisplay}", innerWidth), winWidth);
            WriteLineTruncated(BoxLine($" ┌───┐└───┘┌───┐{g1}└─┬─┘", innerWidth), winWidth);
            WriteLineTruncated(BoxLine($" │{AnsiInfo} H {AnsiReset}│     │{AnsiInfo} L {AnsiReset}│{g1}  │  {g2short}prefix [{AnsiInfo}1-{mode.MaxMultiplier}{AnsiReset}] ×distance", innerWidth), winWidth);
            WriteLineTruncated(BoxLine($" │ ← │     │ → │{g1}  │  {g2}[{AnsiInfo}Tab{AnsiReset}] change mode", innerWidth), winWidth);
            WriteLineTruncated(BoxLine($" └───┘┌───┐└───┘{g1}┌─┴─┐", innerWidth), winWidth);
            WriteLineTruncated(BoxLine($"  X-  │{AnsiInfo} J {AnsiReset}│ X+  {g1}│{AnsiInfo} S {AnsiReset}│ Z-", innerWidth), winWidth);
            WriteLineTruncated(BoxLine($"      │ ▼ │     {g1}│ ▼ │", innerWidth), winWidth);
            WriteLineTruncated(BoxLine($"      └───┘     {g1}└───┘", innerWidth), winWidth);
            WriteLineTruncated(BoxLine($"        Y-      {g1}{AnsiDim}PgUp/Dn{AnsiReset}", innerWidth), winWidth);
            WriteLineTruncated(BoxLine($"     {AnsiDim}Arrows{AnsiReset}", innerWidth), winWidth);
            WriteLineTruncated(BoxLine("", innerWidth), winWidth);

            // Add vertical padding to fill available space
            for (int i = 0; i < extraLines; i++)
            {
                WriteLineTruncated(BoxLine("", innerWidth), winWidth);
            }

            // Command panels - three columns with vertical separators
            WriteLineTruncated(BoxDivider('╠', '╣', innerWidth, '═', (col1Width, '╦'), (col1Width + 1 + col2Width, '╦')), winWidth);
            WriteLineTruncated(BoxLine($" {AnsiInfo}Commands{AnsiReset}      ║ {AnsiInfo}Set Zero{AnsiReset}      ║ {AnsiInfo}Go To Position{AnsiReset}{new string(' ', col3Width - 15)}", innerWidth), winWidth);
            WriteLineTruncated(BoxDivider('║', '║', innerWidth, '─', (col1Width, '║'), (col1Width + 1 + col2Width, '║')), winWidth);
            WriteLineTruncated(BoxLine($" {AnsiInfo}M{AnsiReset}  Home       ║ {AnsiInfo}0{AnsiReset}  All XYZ    ║ {AnsiInfo}N{AnsiReset}  Origin (X0 Y0)  {AnsiInfo}T{AnsiReset}  Z+6mm{new string(' ', col3Width - 35)}", innerWidth), winWidth);
            WriteLineTruncated(BoxLine($" {AnsiInfo}U{AnsiReset}  Unlock     ║ {AnsiInfo}Z{AnsiReset}  Z only     ║ {cColor}C  Center{AnsiReset}          {AnsiInfo}B{AnsiReset}  Z+1mm{new string(' ', col3Width - 35)}", innerWidth), winWidth);
            WriteLineTruncated(BoxLine($" {AnsiInfo}R{AnsiReset}  Reset      ║ {AnsiInfo}P{AnsiReset}  Probe Z    ║                    {AnsiInfo}G{AnsiReset}  Z0{new string(' ', col3Width - 26)}", innerWidth), winWidth);
            WriteLineTruncated(BoxDivider('╠', '╣', innerWidth, '═', (col1Width, '╩'), (col1Width + 1 + col2Width, '╩')), winWidth);

            // Footer - center "Esc/Q to exit" (13 chars)
            int footerPadLeft = (innerWidth - 13) / 2;
            WriteLineTruncated(BoxLine($"{new string(' ', footerPadLeft)}{AnsiDim}Esc/Q to exit{AnsiReset}", innerWidth), winWidth);
            WriteLineTruncated(BoxBorder('╚', '╝', innerWidth), winWidth);
        }

        /// <summary>
        /// Draws the compact layout for narrow terminals.
        /// </summary>
        private static void DrawCompactLayout(Machine machine, JogMode mode, int winWidth)
        {
            var statusColor = StatusHelpers.IsProblematicState(machine) ? AnsiError : AnsiSuccess;
            var wp = machine.WorkPosition;
            var mp = machine.MachinePosition;
            var hasFile = AppState.CurrentFile != null;
            var cColor = hasFile ? AnsiInfo : AnsiDim;

            // Distance display
            var distanceStr = mode.FormatDistance(_pendingMultiplier);
            var distDisplay = _pendingMultiplier > 1
                ? $"{AnsiBoldGreen}{_pendingMultiplier}{AnsiReset} x {mode.BaseDistance}mm = {AnsiBoldGreen}{distanceStr}{AnsiReset}"
                : $"{AnsiSuccess}{distanceStr}{AnsiReset}";

            WriteLineTruncated($"{AnsiPrompt}Move{AnsiReset}  Status: {statusColor}{machine.Status}{AnsiReset}", winWidth);
            WriteLineTruncated("", winWidth);
            WriteLineTruncated($"Work:    X:{AnsiWarning}{wp.X,9:F3}{AnsiReset}  Y:{AnsiWarning}{wp.Y,9:F3}{AnsiReset}  Z:{AnsiWarning}{wp.Z,9:F3}{AnsiReset}", winWidth);
            WriteLineTruncated($"Machine: X:{AnsiDim}{mp.X,9:F3}{AnsiReset}  Y:{AnsiDim}{mp.Y,9:F3}{AnsiReset}  Z:{AnsiDim}{mp.Z,9:F3}{AnsiReset}", winWidth);
            WriteLineTruncated("", winWidth);

            WriteLineTruncated($"{AnsiInfo}Jog:{AnsiReset} {AnsiSuccess}{mode.Name}{AnsiReset} {mode.Feed}mm/min  Dist: {distDisplay}", winWidth);
            WriteLineTruncated($"  {AnsiInfo}HJKL{AnsiReset}/{AnsiInfo}Arrows{AnsiReset} X/Y   {AnsiInfo}WS{AnsiReset}/{AnsiInfo}PgUp/Dn{AnsiReset} Z   [{AnsiInfo}1-{mode.MaxMultiplier}{AnsiReset}] mult   {AnsiInfo}Tab{AnsiReset} mode", winWidth);
            WriteLineTruncated("", winWidth);

            WriteLineTruncated($"{AnsiInfo}Cmds:{AnsiReset} {AnsiInfo}M{AnsiReset}=Home {AnsiInfo}U{AnsiReset}=Unlock {AnsiInfo}R{AnsiReset}=Reset", winWidth);
            WriteLineTruncated($"{AnsiInfo}Zero:{AnsiReset} {AnsiInfo}0{AnsiReset}=All XYZ  {AnsiInfo}Z{AnsiReset}=Z only  {AnsiInfo}P{AnsiReset}=Probe", winWidth);
            WriteLineTruncated($"{AnsiInfo}Goto:{AnsiReset} {AnsiInfo}N{AnsiReset}=Origin {cColor}C=Center{AnsiReset} {AnsiInfo}T{AnsiReset}=Z+6 {AnsiInfo}B{AnsiReset}=Z+1 {AnsiInfo}G{AnsiReset}=Z0", winWidth);
            WriteLineTruncated("", winWidth);
            WriteLineTruncated($"{AnsiDim}Esc/Q to exit{AnsiReset}", winWidth);
            WriteLineTruncated("", winWidth);
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
            if (InputHelpers.IsKey(key, ConsoleKey.M))
            {
                machine.SoftReset();
                machine.SendLine(CmdHome);
                return true;
            }
            if (InputHelpers.IsKey(key, ConsoleKey.U))
            {
                machine.SendLine(CmdUnlock);
                if (StatusHelpers.IsDoor(machine) || StatusHelpers.IsHold(machine))
                {
                    machine.SendLine(CycleStart.ToString());
                }
                AnsiConsole.MarkupLine($"[{ColorSuccess}]Unlocked[/]");
                Thread.Sleep(ConfirmationDisplayMs);
                return true;
            }
            if (InputHelpers.IsKey(key, ConsoleKey.R))
            {
                machine.SoftReset();
                AnsiConsole.MarkupLine($"[{ColorWarning}]Reset[/]");
                Thread.Sleep(ConfirmationDisplayMs);
                return true;
            }
            if (InputHelpers.IsKey(key, ConsoleKey.Z))
            {
                MachineCommands.ZeroWorkOffset(machine, "Z0");
                AppState.IsWorkZeroSet = true;
                // Discard probe data - it's now invalid with new work zero
                AppState.DiscardProbeData();
                AnsiConsole.MarkupLine($"[{ColorSuccess}]Z zeroed[/]");
                Thread.Sleep(ConfirmationDisplayMs);
                MachineCommands.MoveToSafeHeight(machine, RetractZMm);
                return false; // Exit after zeroing
            }
            if (InputHelpers.IsKey(key, ConsoleKey.D0))
            {
                MachineCommands.ZeroWorkOffset(machine, "X0 Y0 Z0");
                AppState.IsWorkZeroSet = true;
                // Discard probe data - it's now invalid with new work zero
                AppState.DiscardProbeData();

                // Store work zero in session
                var session = AppState.Session;
                var pos = machine.WorkPosition;
                session.WorkZeroX = pos.X;
                session.WorkZeroY = pos.Y;
                session.WorkZeroZ = pos.Z;
                session.HasStoredWorkZero = true;
                Persistence.SaveSession();

                AnsiConsole.MarkupLine($"[{ColorSuccess}]All axes zeroed[/]");
                Thread.Sleep(ConfirmationDisplayMs);
                MachineCommands.MoveToSafeHeight(machine, RetractZMm);
                return false; // Exit after zeroing
            }
            if (InputHelpers.IsKey(key, ConsoleKey.T))
            {
                MachineCommands.MoveToSafeHeight(machine, RetractZMm);
                return true;
            }
            if (InputHelpers.IsKey(key, ConsoleKey.B))
            {
                MachineCommands.MoveToSafeHeight(machine, ReferenceZHeightMm);
                return true;
            }
            if (InputHelpers.IsKey(key, ConsoleKey.G))
            {
                MachineCommands.MoveToSafeHeight(machine, 0);
                return true;
            }
            if (InputHelpers.IsKey(key, ConsoleKey.N))
            {
                MachineCommands.SetAbsoluteMode(machine);
                MachineCommands.RapidMoveXY(machine, 0, 0);
                return true;
            }
            if (InputHelpers.IsKey(key, ConsoleKey.C))
            {
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
            double distance = mode.BaseDistance * _pendingMultiplier;
            bool jogged = JogHelpers.HandleJogKey(key, machine, mode.Feed, distance);
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
            AnsiConsole.MarkupLine($"  [{ColorInfo}]H J K L[/]  or  [{ColorInfo}]Arrow keys[/]    Move X/Y (vim-style)");
            AnsiConsole.MarkupLine($"  [{ColorInfo}]W S[/]      or  [{ColorInfo}]PgUp/PgDn[/]     Move Z up/down");
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine($"[{ColorInfo}]DISTANCE PREFIX[/] (vim-style)");
            AnsiConsole.MarkupLine($"  [{ColorInfo}]1-9[/]  Type a digit before a move key to multiply distance");
            AnsiConsole.MarkupLine($"        Example: [{ColorInfo}]3 L[/] moves 3x the base distance in X+");
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine($"[{ColorInfo}]JOG MODES[/]");
            AnsiConsole.MarkupLine($"  [{ColorInfo}]Tab[/]  Cycle through:");
            AnsiConsole.MarkupLine($"        Fast (10mm), Normal (1mm), Slow (0.1mm), Creep (0.01mm)");
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine($"[{ColorInfo}]MACHINE COMMANDS[/]");
            AnsiConsole.MarkupLine($"  [{ColorInfo}]M[/]  Home machine ($H)");
            AnsiConsole.MarkupLine($"  [{ColorInfo}]U[/]  Unlock ($X) and resume from hold/door");
            AnsiConsole.MarkupLine($"  [{ColorInfo}]R[/]  Soft reset");
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine($"[{ColorInfo}]SET WORK ZERO[/]");
            AnsiConsole.MarkupLine($"  [{ColorInfo}]0[/]  Zero all axes (X, Y, Z) - saves position to session");
            AnsiConsole.MarkupLine($"  [{ColorInfo}]Z[/]  Zero Z axis only, then retract");
            AnsiConsole.MarkupLine($"  [{ColorInfo}]P[/]  Probe Z at current XY position");
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine($"[{ColorInfo}]GO TO POSITION[/]");
            AnsiConsole.MarkupLine($"  [{ColorInfo}]N[/]  Go to work origin (X0 Y0)");
            AnsiConsole.MarkupLine($"  [{ColorInfo}]C[/]  Go to center of loaded file");
            AnsiConsole.MarkupLine($"  [{ColorInfo}]G[/]  Go to Z0 (work zero)");
            AnsiConsole.MarkupLine($"  [{ColorInfo}]T[/]  Go to Z+6mm (safe height)");
            AnsiConsole.MarkupLine($"  [{ColorInfo}]B[/]  Go to Z+1mm (reference height)");
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine($"[{ColorInfo}]OTHER[/]");
            AnsiConsole.MarkupLine($"  [{ColorInfo}]?[/]      This help screen");
            AnsiConsole.MarkupLine($"  [{ColorInfo}]Esc/Q[/]  Exit jog menu");
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine($"[{ColorDim}]Press any key to return...[/]");
            Console.ReadKey(true);
            Console.Clear();
        }

        /// <summary>
        /// Performs a single Z probe at current XY position.
        /// </summary>
        private static void ProbeZ()
        {
            var machine = AppState.Machine;
            var settings = AppState.Settings;

            bool completed = false;

            AppState.SingleProbeCallback = (pos, probeSuccess) =>
            {
                completed = true;
            };

            AppState.SingleProbing = true;
            machine.ProbeStart();
            // Use relative mode so we probe DOWN from current position
            machine.SendLine(CmdRelative);
            machine.SendLine($"{CmdProbeToward} Z-{settings.ProbeMaxDepth:F3} F{settings.ProbeFeed:F1}");
            machine.SendLine(CmdAbsolute);

            while (!completed)
            {
                if (Console.KeyAvailable && InputHelpers.IsExitKey(Console.ReadKey(true)))
                {
                    AppState.SingleProbing = false;
                    machine.ProbeStop();
                    machine.FeedHold();
                    return;
                }
                Thread.Sleep(StatusPollIntervalMs);
            }
        }
    }
}
