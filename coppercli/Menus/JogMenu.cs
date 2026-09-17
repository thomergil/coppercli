using coppercli.Core.Communication;
using coppercli.Core.Controllers;
using coppercli.Core.GCode;
using coppercli.Core.Util;
using coppercli.Helpers;
using Spectre.Console;
using static coppercli.CliConstants;
using static coppercli.Core.Util.Constants;
using static coppercli.Helpers.DisplayHelpers;

namespace coppercli.Menus
{
    /// <summary>
    /// The jog screen: a digit prefix, 1 up to the mode's `MaxMultiplier`, multiplies the
    /// distance of the direction key that follows it. It draws the cockpit box, or a size
    /// hint when the terminal is too small for it.
    /// </summary>
    internal static class JogMenu
    {
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
                    StatusColor: MachineWait.IsUnavailable(machine) ? AnsiError : AnsiSuccess,
                    WorkPos: machine.WorkPosition,
                    MachinePos: machine.MachinePosition,
                    XyColor: probeContact ? AnsiDim : AnsiInfo,
                    NColor: probeContact ? AnsiDim : AnsiInfo,
                    CColor: (hasFile && !probeContact) ? AnsiInfo : AnsiDim);
            }
        }

        // The content between the ║ borders.
        private const int CockpitMinInnerWidth = 61;

        private const int CockpitMinWidth = CockpitMinInnerWidth + 2;

        private const int CockpitFixedLines = 25;

        private const int CockpitMinHeight = CockpitFixedLines;

        private static int _pendingMultiplier = 1;

        // Static so the redraw inside ProbeZ can read the mode without HandleKey's local.
        private static JogMode _currentMode = JogModes[DefaultJogModeIndex];

        public static void Show()
        {
            if (!MenuHelpers.RequireConnection())
            {
                return;
            }

            var machine = AppState.Machine;

            _pendingMultiplier = 1;

            machine.EnableAutoStateClear = true;

            // Cleared once here; the loop below redraws in place, which does not flicker.
            Console.Clear();
            Console.CursorVisible = false;

            var (lastWidth, lastHeight) = GetSafeWindowSize();

            try
            {
                while (true)
                {
                    var (winWidth, winHeight) = GetSafeWindowSize();
                    var mode = AppState.CurrentJogMode;
                    _currentMode = mode;

                    // A resize leaves artifacts behind unless the screen is cleared.
                    if (winWidth != lastWidth || winHeight != lastHeight)
                    {
                        Console.Clear();
                        lastWidth = winWidth;
                        lastHeight = winHeight;
                    }

                    RedrawScreen(machine, mode);

                    // Nothing this screen sends moves the machine while the enclosure is
                    // open, so the message goes up here and again each time it reopens.
                    if (MachineWait.IsDoor(machine))
                    {
                        Logger.Log("JogMenu: holding at the enclosure, status={0}:{1}",
                            machine.Status, machine.StatusSubState);

                        if (!MenuHelpers.WaitForDoorClear(machine))
                        {
                            return;
                        }
                        Console.Clear();
                        continue;
                    }

                    var keyOrNull = InputHelpers.ReadKeyPolling();
                    if (keyOrNull == null)
                    {
                        continue; // Status changed, redraw screen
                    }

                    if (!HandleKey(keyOrNull.Value, machine, mode))
                    {
                        return;
                    }
                }
            }
            finally
            {
                Console.CursorVisible = true;
                machine.EnableAutoStateClear = false;
            }
        }

        /// <summary>The labels the two position rows are drawn with, padded to one width.</summary>
        internal const string WorkLabel = " Work:    ";

        /// <inheritdoc cref="WorkLabel"/>
        internal const string MachineLabel = " Machine: ";

        private const int PositionValueWidth = 9;

        private const string AxisGap = "   ";

        /// <summary>
        /// The width a position row draws in, derived from the parts `PositionRow` builds it
        /// from rather than measured again here.
        /// </summary>
        internal static int PositionContentWidth =>
            WorkLabel.Length
            + 3 * ("X:".Length + PositionValueWidth)
            + 2 * AxisGap.Length;

        /// <summary>
        /// One position row: a label, then X, Y and Z in a fixed-width field each. Both rows
        /// are drawn from here, so they cannot end up different widths.
        /// </summary>
        internal static string PositionRow(string label, Core.Util.Vector3 p, string color) =>
            $"{label}X:{color}{p.X,PositionValueWidth:F3}{AnsiReset}{AxisGap}"
            + $"Y:{color}{p.Y,PositionValueWidth:F3}{AnsiReset}{AxisGap}"
            + $"Z:{color}{p.Z,PositionValueWidth:F3}{AnsiReset}";

        private static string BoxLine(string content, int innerWidth)
        {
            int displayLen = CalculateDisplayLength(content);
            int padding = Math.Max(0, innerWidth - displayLen);
            return $"║{content}{new string(' ', padding)}║";
        }

        private static string BoxBorder(char left, char right, int innerWidth, char fill = '═')
        {
            return $"{left}{new string(fill, innerWidth)}{right}";
        }

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
                DrawSizeHint(winWidth, winHeight);
            }
        }

        private static void DrawCockpitLayout(Machine machine, JogMode mode, int winWidth, int winHeight)
        {
            var ds = JogDisplayState.Create(machine);
            var (statusColor, wp, mp, xyColor, nColor, cColor) = ds;

            int innerWidth = Math.Max(CockpitMinInnerWidth, winWidth - 2);

            int extraLines = Math.Max(0, winHeight - CockpitFixedLines);
            int topPadding = extraLines / 2;
            int bottomPadding = extraLines - topPadding;

            var distanceStr = mode.FormatDistance(_pendingMultiplier);
            var distDisplay = _pendingMultiplier > 1
                ? $"{AnsiSuccessBold}{_pendingMultiplier}{AnsiReset}x{mode.BaseDistance}={AnsiSuccessBold}{distanceStr}{AnsiReset}"
                : $"{AnsiSuccess}{distanceStr}{AnsiReset}";

            const int col1Width = 15;  // Commands column
            const int col2Width = 15;  // Set Zero column
            int col3Width = innerWidth - col1Width - col2Width - 2;  // Go To Position, less 2 separators

            // Widths as drawn, with the ANSI codes excluded, for the padding below.
            const int col3HeaderWidth = 15;      // "Go To Position"
            const int col3Row12Width = 35;
            const int col3Row3Width = 26;
            const int probeContentWidth = 17;    // " Probe:   Contact"
            const int prefixPokeLeft = 7;        // Columns the prefix label takes out of gap2

            var hints = $"{AnsiDim}?=Help  Esc to exit{AnsiReset}";
            var statusContent =
                $" Status: {statusColor}{GetActivityText(MachineWait.GetActivity(machine), machine.Status)}{AnsiReset}";
            int statusDisplayLen = CalculateDisplayLength(statusContent);
            int hintsDisplayLen = CalculateDisplayLength(hints);
            int statusPadding = Math.Max(1, innerWidth - statusDisplayLen - hintsDisplayLen - 1);
            WriteLineTruncated(BoxBorder('╔', '╗', innerWidth), winWidth);
            WriteLineTruncated(BoxLine($"{statusContent}{new string(' ', statusPadding)}{hints}", innerWidth), winWidth);
            WriteLineTruncated(BoxBorder('╠', '╣', innerWidth), winWidth);

            // Coordinates are fixed width, so the padding expands on the right.
            int posPadding = Math.Max(0, innerWidth - PositionContentWidth - 1);
            WriteLineTruncated(
                BoxLine(PositionRow(WorkLabel, wp, AnsiWarning) + new string(' ', posPadding), innerWidth),
                winWidth);
            WriteLineTruncated(
                BoxLine(PositionRow(MachineLabel, mp, AnsiDim) + new string(' ', posPadding), innerWidth),
                winWidth);
            var probeColor = machine.PinStateProbe ? AnsiError : AnsiSuccess;
            var probeText = machine.PinStateProbe ? "Contact" : "Open";
            int probePadding = Math.Max(0, innerWidth - probeContentWidth - 1);
            WriteLineTruncated(BoxLine($" Probe:   {probeColor}{probeText}{AnsiReset}{new string(' ', probePadding)}", innerWidth), winWidth);
            WriteLineTruncated(BoxBorder('╠', '╣', innerWidth), winWidth);

            for (int i = 0; i < topPadding; i++)
            {
                WriteLineTruncated(BoxLine("", innerWidth), winWidth);
            }

            const int xyJoyWidth = 20;   // "X- │ A │     │ D │ X+"
            const int zJoyWidth = 5;     // "┌───┐"
            const int infoWidth = 16;    // The Mode/Feed/Dist area
            const int minGap = 1;

            int totalFixedWidth = xyJoyWidth + zJoyWidth + infoWidth + minGap * 2;
            int extraSpace = Math.Max(0, innerWidth - totalFixedWidth - 1);  // -1 for left margin
            int gap1 = minGap + extraSpace / 2;           // Gap between XY and Z
            int gap2 = minGap + extraSpace / 2;           // Gap between Z and info

            string g1 = new string(' ', gap1);
            string g1short = new string(' ', Math.Max(0, gap1 - 1));  // X+ line runs 1 char longer
            string g2 = new string(' ', gap2);
            string g2short = new string(' ', Math.Max(0, gap2 - prefixPokeLeft));
            string g2feed = new string(' ', Math.Max(0, gap2 - 3));   // Feed line runs 3 chars longer
            string g2dist = new string(' ', Math.Max(0, gap2 - 5));   // Dist line runs 5 chars longer

            // The XY joystick is indented to leave room for the X- label.
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

            for (int i = 0; i < bottomPadding; i++)
            {
                WriteLineTruncated(BoxLine("", innerWidth), winWidth);
            }

            WriteLineTruncated(BoxDivider('╠', '╣', innerWidth, '═', (col1Width, '╦'), (col1Width + 1 + col2Width, '╦')), winWidth);
            WriteLineTruncated(BoxLine($" {AnsiInfo}Commands{AnsiReset}      ║ {AnsiInfo}Set Zero{AnsiReset}      ║ {AnsiInfo}Go To Position{AnsiReset}{new string(' ', Math.Max(0, col3Width - col3HeaderWidth))}", innerWidth), winWidth);
            WriteLineTruncated(BoxDivider('║', '║', innerWidth, '─', (col1Width, '║'), (col1Width + 1 + col2Width, '║')), winWidth);
            WriteLineTruncated(BoxLine($" {AnsiInfo}H{AnsiReset}  Home       ║ {AnsiInfo}0{AnsiReset}  All XYZ    ║ {nColor}.  Origin XY{AnsiReset}       {AnsiInfo}T{AnsiReset}  Z+6mm{new string(' ', Math.Max(0, col3Width - col3Row12Width))}", innerWidth), winWidth);
            WriteLineTruncated(BoxLine($" {AnsiInfo}U{AnsiReset}  Unlock     ║ {AnsiInfo}L{AnsiReset}  Level (Z)  ║ {cColor}C  Center{AnsiReset}          {AnsiInfo}G{AnsiReset}  Z+1mm{new string(' ', Math.Max(0, col3Width - col3Row12Width))}", innerWidth), winWidth);
            WriteLineTruncated(BoxLine($" {AnsiInfo}R{AnsiReset}  Reset      ║ {AnsiInfo}P{AnsiReset}  Probe Z    ║                    {AnsiInfo}B{AnsiReset}  Z0{new string(' ', Math.Max(0, col3Width - col3Row3Width))}", innerWidth), winWidth);
            WriteLineTruncated(BoxLine($" {AnsiInfo}␣{AnsiReset}  Pause      ║               ║{new string(' ', Math.Max(0, col3Width))}", innerWidth), winWidth);
            // Without addNewline: false the last line scrolls the screen when the content
            // exactly fills the terminal height.
            WriteLineTruncated(BoxDivider('╚', '╝', innerWidth, '═', (col1Width, '╩'), (col1Width + 1 + col2Width, '╩')), winWidth, addNewline: false);
        }

        private static void DrawSizeHint(int winWidth, int winHeight)
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
            WriteLineTruncated($"{AnsiDim}Esc to exit{AnsiReset}", winWidth, addNewline: false);
        }

        /// <summary>Returns false when the operator asked to leave the screen.</summary>
        private static bool HandleKey(ConsoleKeyInfo key, Machine machine, JogMode mode)
        {
            if (InputHelpers.IsEscapeKey(key))
            {
                return false;
            }

            if (InputHelpers.IsKey(key, ConsoleKey.Tab))
            {
                AppState.CycleJogPreset();
                _pendingMultiplier = 1;
                return true;
            }

            if (key.KeyChar == '?')
            {
                ShowHelp();
                return true;
            }

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

            if (InputHelpers.IsKey(key, ConsoleKey.H))
            {
                machine.SoftReset();
                MachineCommands.HomeAndWait(machine);
                return true;
            }
            if (InputHelpers.IsKey(key, ConsoleKey.U))
            {
                // A door hold goes through the same helper the draw loop uses, so U always
                // leads out of this screen.
                if (MachineWait.IsDoor(machine))
                {
                    MenuHelpers.WaitForDoorClear(machine);
                    Console.Clear();
                    return true;
                }

                MachineCommands.Unlock(machine);
                if (MachineWait.CanResume(machine))
                {
                    machine.CycleStart();
                }
                ShowOverlayTimed(UnlockedMessage, ConfirmationDisplayMs);
                return true;
            }
            if (InputHelpers.IsKey(key, ConsoleKey.R))
            {
                machine.SoftReset();
                ShowOverlayTimed(ResetMessage, ConfirmationDisplayMs, messageColor: AnsiWarning);
                return true;
            }
            if (InputHelpers.IsKey(key, ConsoleKey.Spacebar))
            {
                var activity = MachineWait.GetActivity(machine);
                if (MachineWait.CanResume(activity))
                {
                    machine.CycleStart();
                }
                else if (MachineWait.CanPause(activity))
                {
                    machine.FeedHold();
                }
                return true;
            }
            if (InputHelpers.IsKey(key, ConsoleKey.L))
            {
                // What this does to the height map depends on whether a run is streaming
                // the file, which is why `ShowZeroed` reports the outcome.
                var zeroed = MachineCommands.SetWorkZeroAndWait(machine, "Z0");
                if (zeroed.Refused != null)
                {
                    ShowOverlayTimed(zeroed.Refused, ConfirmationDisplayMs, messageColor: AnsiWarning);
                    return true;
                }

                ShowZeroed(ZeroedZ, zeroed.Outcome);
                MachineCommands.MoveToSafeHeight(machine, Constants.RetractZMm);
                return false;
            }
            if (InputHelpers.IsKey(key, ConsoleKey.D0))
            {
                if (!ConfirmZeroWithProbeData("all axes"))
                {
                    return true;
                }

                Logger.Log("JogMenu: D0 pressed, zeroing all axes");
                var allZeroed = MachineCommands.SetWorkZeroAndWait(machine, "X0 Y0 Z0");
                if (allZeroed.Refused != null)
                {
                    ShowOverlayTimed(allZeroed.Refused, ConfirmationDisplayMs, messageColor: AnsiWarning);
                    return true;
                }

                ShowZeroed(ZeroedAllAxes, allZeroed.Outcome);
                MachineCommands.MoveToSafeHeight(machine, Constants.RetractZMm);
                return false;
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
                MachineCommands.GotoWorkOriginXY(machine);
                return true;
            }
            if (InputHelpers.IsKey(key, ConsoleKey.C))
            {
                MachineCommands.GotoFileCenterXY(machine, AppState.CurrentFile);
                return true;
            }
            if (InputHelpers.IsKey(key, ConsoleKey.P))
            {
                ProbeZ();
                return true;
            }

            // The last argument blocks X/Y jog while the probe is touching, so it is not
            // dragged across the workpiece.
            double distance = mode.BaseDistance * _pendingMultiplier;
            bool jogged = JogHelpers.HandleJogKey(key, machine, mode.Feed, distance, machine.PinStateProbe);
            if (jogged)
            {
                _pendingMultiplier = 1;
            }

            return true;
        }

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

            AnsiConsole.MarkupLine($"[{ColorDim}]?=Help  Esc=Exit                      Press any key to return...[/]");
            Console.ReadKey(true);
            Console.Clear();
        }

        /// <summary>
        /// An outcome that left the G-code wrong is drawn in the error color, because the
        /// operator has to reload the file before cutting.
        /// </summary>
        private static void ShowZeroed(string zeroed, WorkZeroOutcome outcome) =>
            ShowOverlayTimed(
                ZeroedMessage(zeroed, outcome),
                ConfirmationDisplayMs,
                messageColor: outcome.LeftTheGCodeWrong() ? AnsiError : null);

        /// <summary>The terminal's wording for each `WorkZeroOutcome`; a new outcome needs
        /// an arm here.</summary>
        internal static string ZeroedMessage(string zeroed, WorkZeroOutcome outcome) => outcome switch
        {
            WorkZeroOutcome.MapReapplied => string.Format(ZeroedMapReapplied, zeroed),
            WorkZeroOutcome.MapNotReapplied => string.Format(ZeroedMapNotReapplied, zeroed),
            WorkZeroOutcome.MapNotDiscarded => string.Format(ZeroedMapNotDiscarded, zeroed),
            WorkZeroOutcome.MapDiscarded => string.Format(ZeroedMapDiscarded, zeroed),
            WorkZeroOutcome.FileLeftAlone => string.Format(ZeroedFileLeftAlone, zeroed),
            _ => zeroed
        };

        /// <summary>
        /// The map a zero would discard, or null when there is nothing to warn about. It reads
        /// `CurrentProbeGrid`, which covers the autosave that `ProbePoints` alone would miss,
        /// and a map measured on another board is discarded either way.
        /// </summary>
        internal static ProbeGrid? MapAZeroWouldDiscard()
        {
            var grid = AppState.CurrentProbeGrid;
            return AppState.DescribeApplicability(grid).IsUsable() ? grid : null;
        }

        private static bool ConfirmZeroWithProbeData(string axisDescription)
        {
            if (MapAZeroWouldDiscard() is not ProbeGrid grid)
            {
                return true;
            }

            // One arm per `ProbeDataState`: an arm covering two of them described a grid with
            // nothing measured as partly measured.
            string what = grid.State switch
            {
                ProbeDataState.Complete => CompleteMap,
                ProbeDataState.Partial => PartlyMeasuredMap,
                _ => UnmeasuredMap
            };

            return MenuHelpers.Confirm(string.Format(ZeroDiscardsMap, what, axisDescription), false);
        }

        private static void ProbeZ()
        {
            var machine = AppState.Machine;
            var settings = AppState.Settings;
            var controller = AppState.Probe;

            Logger.Log("JogMenu: ProbeZ starting");

            // `FromSettings` is the factory every other caller uses, so this probe gets the
            // same safe height and minimum retract as the rest.
            controller.Options = Core.Controllers.ProbeOptions.FromSettings(settings);

            using var cts = new CancellationTokenSource();

            // On a background thread so the loop below can poll for a keypress.
            var probeTask = Task.Run(async () => await controller.ProbeZSingleAsync(cts.Token));

            while (!probeTask.IsCompleted)
            {
                RedrawScreen(machine, _currentMode);

                if (Console.KeyAvailable && InputHelpers.IsExitKey(Console.ReadKey(true)))
                {
                    Logger.Log("JogMenu: ProbeZ cancelled by user");

                    // The `probeTask.Result` below then waits for the probe's own teardown,
                    // which stops the machine and lifts the tool.
                    cts.Cancel();
                    ShowOverlay(ProbeStatusStopping);
                    break;
                }
                Thread.Sleep(StatusPollIntervalMs);
            }

            try
            {
                var (success, zPosition) = probeTask.Result;
                Logger.Log($"JogMenu: ProbeZ completed - success={success}, Z={zPosition:F3}");

                if (success)
                {
                    var afterProbe = MachineCommands.SetWorkZeroAndWait(machine, "Z0");
                    if (afterProbe.Refused != null)
                    {
                        ShowOverlayTimed(
                            afterProbe.Refused, ConfirmationDisplayMs, messageColor: AnsiWarning);
                        return;
                    }

                    ShowZeroed(ZeroedZ, afterProbe.Outcome);
                }
                else
                {
                    ShowOverlayAndWait(ControllerConstants.ErrorProbeNoContact);
                }
            }
            catch (AggregateException ex)
            {
                // Every probe failure arrives here wrapped, including the TimeoutException
                // thrown when the probe never triggers or the enclosure opens. Nothing above
                // this catches, so a filter that missed one would end the process with the
                // tool at the workpiece.
                var cause = ex.InnerException;

                if (cause is OperationCanceledException)
                {
                    Logger.Log("JogMenu: ProbeZ was cancelled");
                    return;
                }

                Logger.Log("JogMenu: ProbeZ failed - {0}", ex);
                ShowOverlayAndWait(ControllerConstants.ErrorProbeNoContact);
            }
        }
    }
}
