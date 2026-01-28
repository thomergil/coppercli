using System.Text.RegularExpressions;
using coppercli.Core.Communication;
using Spectre.Console;
using static coppercli.CliConstants;
using static coppercli.Core.Util.GrblProtocol;
using static coppercli.Helpers.DisplayHelpers;

namespace coppercli.Helpers
{
    /// <summary>
    /// Helper methods for tool change workflow.
    /// </summary>
    internal static class ToolChangeHelpers
    {
        // Pattern to extract tool name from comments: (tool name) or ; tool name
        private static readonly Regex CommentPattern = new Regex(
            @"\(([^)]+)\)|;(.+)$", RegexOptions.Compiled);

        /// <summary>
        /// Current tool change overlay message. Set during tool change, cleared when done.
        /// MillMenu reads this to display in the overlay.
        /// </summary>
        public static string? OverlayMessage { get; private set; }

        /// <summary>
        /// Secondary overlay message (e.g., "Press P to proceed").
        /// </summary>
        public static string? OverlaySubMessage { get; private set; }

        /// <summary>
        /// Subtle status action shown in status line (not overlay).
        /// Used during automated phases when user doesn't need to act.
        /// </summary>
        public static string? StatusAction { get; private set; }

        /// <summary>
        /// Callback to refresh the display during tool change.
        /// Set by MillMenu before calling HandleToolChange.
        /// </summary>
        public static Action? RefreshDisplay { get; set; }

        /// <summary>
        /// Set the overlay message and refresh display.
        /// </summary>
        private static void SetOverlay(string message, string? subMessage = null)
        {
            OverlayMessage = message;
            OverlaySubMessage = subMessage;
            StatusAction = null;  // Clear status when showing overlay
            RefreshDisplay?.Invoke();
        }

        /// <summary>
        /// Set a subtle status action (no overlay).
        /// Clears any active overlay so status appears in the status bar.
        /// </summary>
        private static void SetStatus(string action)
        {
            OverlayMessage = null;      // Clear overlay when showing status
            OverlaySubMessage = null;
            StatusAction = action;
            RefreshDisplay?.Invoke();
        }

        /// <summary>
        /// Clear the overlay message.
        /// </summary>
        private static void ClearOverlay()
        {
            OverlayMessage = null;
            OverlaySubMessage = null;
            StatusAction = null;
        }

        /// <summary>
        /// Wait for stable idle while refreshing the display.
        /// </summary>
        private static void WaitForStableIdle()
        {
            var machine = AppState.Machine;
            int stableCount = 0;
            while (!StatusHelpers.WaitForStableIdleAsync(machine, ref stableCount))
            {
                RefreshDisplay?.Invoke();
                Thread.Sleep(StatusPollIntervalMs);
            }
        }

        /// <summary>
        /// Check if a G-code line contains an M6 tool change command.
        /// </summary>
        public static bool IsM6Line(string line)
        {
            return Regex.IsMatch(line, M6Pattern, RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Check if a G-code line contains an M0 program pause command.
        /// Matches M0 or M00 but not M01 (optional stop) or other M-codes.
        /// </summary>
        public static bool IsM0Line(string line)
        {
            return Regex.IsMatch(line, M0Pattern, RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Extract tool number from a G-code line (e.g., T1, T02).
        /// Returns null if no T code found.
        /// </summary>
        public static int? ExtractToolNumber(string line)
        {
            var match = Regex.Match(line, TCodePattern, RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int toolNum))
            {
                return toolNum;
            }
            return null;
        }

        /// <summary>
        /// Extract tool name from comments in a G-code line.
        /// Looks for (comment) or ; comment format.
        /// Returns null if no meaningful comment found.
        /// </summary>
        public static string? ExtractToolName(string line)
        {
            var match = CommentPattern.Match(line);
            if (match.Success)
            {
                // Try parentheses comment first, then semicolon comment
                string comment = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                comment = comment.Trim();

                // Skip generic comments that don't describe the tool
                if (string.IsNullOrEmpty(comment) ||
                    comment.StartsWith("Tool Change", StringComparison.OrdinalIgnoreCase) ||
                    comment.Equals("M6", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return comment;
            }
            return null;
        }

        /// <summary>
        /// Find tool info by scanning the G-code file around the M6 line.
        /// Returns (toolNumber, toolName) where either may be null.
        /// </summary>
        public static (int? Number, string? Name) FindToolInfo(IReadOnlyList<string>? file, int m6LineIndex)
        {
            if (file == null || m6LineIndex < 0 || m6LineIndex >= file.Count)
            {
                return (null, null);
            }

            int? toolNumber = null;
            string? toolName = null;

            // Scan the M6 line and up to 5 lines before it for T code and comments
            int startLine = Math.Max(0, m6LineIndex - 5);
            for (int i = m6LineIndex; i >= startLine; i--)
            {
                string line = file[i];

                // Look for tool number if not found yet
                if (toolNumber == null)
                {
                    toolNumber = ExtractToolNumber(line);
                }

                // Look for tool name if not found yet
                if (toolName == null)
                {
                    toolName = ExtractToolName(line);
                }

                // Stop if we found both
                if (toolNumber != null && toolName != null)
                {
                    break;
                }
            }

            Logger.Log("FindToolInfo: line={0}, number={1}, name={2}",
                m6LineIndex, toolNumber?.ToString() ?? "null", toolName ?? "null");

            return (toolNumber, toolName);
        }

        /// <summary>
        /// Wait for user to press Y (continue) or X (abort).
        /// Returns true if Y pressed, false if X pressed or abort.
        /// Periodically refreshes display while waiting.
        /// </summary>
        private static bool WaitForContinueOrAbort()
        {
            Logger.Log("WaitForContinueOrAbort: Waiting for Y or X key...");
            while (true)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    Logger.Log("WaitForContinueOrAbort: Key pressed: {0} (char: {1})", key.Key, key.KeyChar);
                    if (InputHelpers.IsKey(key, ConsoleKey.Y, 'y'))
                    {
                        Logger.Log("WaitForContinueOrAbort: Y pressed, proceeding");
                        return true;
                    }
                    if (InputHelpers.IsKey(key, ConsoleKey.X, 'x'))
                    {
                        Logger.Log("WaitForContinueOrAbort: X pressed, aborting");
                        SetOverlay("Tool change aborted");
                        Thread.Sleep(ConfirmationDisplayMs);
                        return false;
                    }
                }
                RefreshDisplay?.Invoke();
                Thread.Sleep(StatusPollIntervalMs);
            }
        }

        /// <summary>
        /// Handle a tool change during milling.
        /// Returns true if tool change completed successfully, false if aborted.
        /// </summary>
        public static bool HandleToolChange()
        {
            var machine = AppState.Machine;

            Logger.Log("HandleToolChange: ENTRY - Status={0}, Mode={1}", machine.Status, machine.Mode);
            Logger.Log("HandleToolChange: WorkPos=({0:F3}, {1:F3}, {2:F3})",
                machine.WorkPosition.X, machine.WorkPosition.Y, machine.WorkPosition.Z);
            Logger.Log("HandleToolChange: MachinePos=({0:F3}, {1:F3}, {2:F3})",
                machine.MachinePosition.X, machine.MachinePosition.Y, machine.MachinePosition.Z);
            Logger.Log("HandleToolChange: FilePosition={0}/{1}", machine.FilePosition, machine.File?.Count ?? 0);

            bool hasToolSetter = MachineProfiles.HasToolSetter();
            var toolSetterPos = MachineProfiles.GetToolSetterPosition();

            // Wait for any buffered commands to complete before proceeding
            // Refresh display while waiting so screen isn't blank, but don't show TOOL CHANGE yet
            Logger.Log("HandleToolChange: Waiting for idle before starting tool change...");
            while (!StatusHelpers.IsIdle(machine))
            {
                RefreshDisplay?.Invoke();
                Thread.Sleep(StatusPollIntervalMs);
            }
            Logger.Log("HandleToolChange: Now idle. WorkPos=({0:F3}, {1:F3}, {2:F3})",
                machine.WorkPosition.X, machine.WorkPosition.Y, machine.WorkPosition.Z);

            // Store current position to return to after tool change (after idle, so position is accurate)
            var returnX = machine.WorkPosition.X;
            var returnY = machine.WorkPosition.Y;
            Logger.Log("HandleToolChange: Stored return position ({0:F3}, {1:F3})", returnX, returnY);

            // Find tool info from G-code (T code and comments)
            int m6Line = machine.FilePosition > 0 ? machine.FilePosition - 1 : 0;
            var (toolNumber, toolName) = FindToolInfo(machine.File, m6Line);

            // Build tool info string for display
            string toolInfoStr = "TOOL CHANGE";
            if (toolNumber != null || toolName != null)
            {
                string toolDetail = toolNumber != null ? $"T{toolNumber}" : "";
                if (toolName != null)
                {
                    toolDetail += string.IsNullOrEmpty(toolDetail) ? toolName : $" - {toolName}";
                }
                toolInfoStr = $"TOOL CHANGE: {toolDetail}";
            }

            // Raise Z to top of travel for tool change clearance
            SetStatus(ToolChangeStatusRaisingZ);
            machine.SendLine(CmdAbsolute);
            machine.SendLine($"{CmdMachineCoords} {CmdRapidMove} Z{ToolChangeClearanceZ:F1}");
            WaitForStableIdle();

            if (hasToolSetter && toolSetterPos.HasValue)
            {
                return HandleToolChangeWithSetter(toolSetterPos.Value, returnX, returnY, toolInfoStr);
            }
            else
            {
                return HandleToolChangeWithoutSetter(toolInfoStr);
            }
        }

        /// <summary>
        /// Build a rapid move command to tool setter position.
        /// Only includes Y if specified (some machines like Nomad 3 don't need Y).
        /// </summary>
        private static string BuildToolSetterMoveCommand((double X, double? Y) setterPos)
        {
            var cmd = $"{CmdMachineCoords} {CmdRapidMove} X{setterPos.X:F1}";
            if (setterPos.Y.HasValue)
            {
                cmd += $" Y{setterPos.Y.Value:F1}";
            }
            return cmd;
        }

        /// <summary>
        /// Tool change with tool setter (Mode A).
        /// Probes tool setter for offset calculation.
        /// </summary>
        private static bool HandleToolChangeWithSetter((double X, double? Y) setterPos, double returnX, double returnY, string toolInfoStr)
        {
            var machine = AppState.Machine;
            var session = AppState.Session;

            Logger.Log("HandleToolChangeWithSetter: ENTRY - setterPos=(X={0:F1}, Y={1})",
                setterPos.X, setterPos.Y.HasValue ? $"{setterPos.Y.Value:F1}" : "n/a");

            // Always reset reference tool length - user may have changed tool manually
            Logger.Log("HandleToolChangeWithSetter: Resetting reference tool length");
            session.HasReferenceToolLength = false;
            session.ReferenceToolLength = 0;
            Persistence.SaveSession();

            // Probe the CURRENT tool first before swapping (subtle status - user doesn't need to act yet)
            Logger.Log("HandleToolChangeWithSetter: HasReferenceToolLength={0}", session.HasReferenceToolLength);
            if (!session.HasReferenceToolLength)
            {
                // Move to tool setter position (machine coordinates)
                SetStatus(ToolChangeStatusMovingToSetter);
                machine.SendLine(BuildToolSetterMoveCommand(setterPos));
                WaitForStableIdle();

                SetStatus(ToolChangeStatusMeasuringRef);
                double? referenceLength = ProbeToolSetter();

                if (referenceLength == null)
                {
                    SetOverlay("Probe failed!", "X=abort");
                    Thread.Sleep(ConfirmationDisplayMs);
                    ClearOverlay();
                    return false;
                }

                session.ReferenceToolLength = referenceLength.Value;
                session.HasReferenceToolLength = true;
                Persistence.SaveSession();
                Logger.Log("HandleToolChangeWithSetter: Reference tool length = {0:F3}mm", referenceLength.Value);

                // Raise Z after probing
                SetStatus(ToolChangeStatusRaisingZ);
                machine.SendLine($"{CmdMachineCoords} {CmdRapidMove} Z{ToolChangeClearanceZ:F1}");
                WaitForStableIdle();
            }

            // Move to center of work area for tool swap (accessible position)
            var currentFile = AppState.CurrentFile;
            if (currentFile != null && currentFile.ContainsMotion)
            {
                double centerX = (currentFile.Min.X + currentFile.Max.X) / 2;
                double centerY = (currentFile.Min.Y + currentFile.Max.Y) / 2;
                SetStatus(ToolChangeStatusMovingToWork);
                machine.SendLine(CmdAbsolute);
                machine.SendLine($"{CmdRapidMove} X{centerX:F3} Y{centerY:F3}");
                WaitForStableIdle();
            }

            // NOW prompt user to change tool - this is when overlay should appear
            SetOverlay(toolInfoStr, "Change tool. Y=Continue  X=Cancel");

            if (!WaitForContinueOrAbort())
            {
                ClearOverlay();
                return false;
            }

            // Clear Door state if present (user may have just closed door)
            MachineCommands.ClearDoorState(machine);

            // Move to tool setter and probe the new tool
            SetStatus(ToolChangeStatusMovingToSetter);
            machine.SendLine(BuildToolSetterMoveCommand(setterPos));
            WaitForStableIdle();

            SetStatus(ToolChangeStatusMeasuringNew);
            double? newToolLength = ProbeToolSetter();

            if (newToolLength == null)
            {
                SetOverlay(toolInfoStr, "Probe failed!");
                Thread.Sleep(ConfirmationDisplayMs);
                ClearOverlay();
                return false;
            }

            // Calculate and apply offset by modifying work coordinate origin
            // This persists in GRBL EEPROM, unlike G43.1 which is volatile
            // Longer tool probes at less negative Z (tip reaches setter sooner)
            // Shorter tool probes at more negative Z (machine travels further)
            // offset = new - ref: positive for longer tool, negative for shorter
            double offset = newToolLength.Value - session.ReferenceToolLength;
            double currentWcoZ = machine.MachinePosition.Z - machine.WorkPosition.Z;
            double newWcoZ = currentWcoZ + offset;
            SetStatus(ToolChangeStatusAdjustingZ);
            Logger.Log("Tool offset: ref={0:F3}, new={1:F3}, offset={2:F3}, oldWcoZ={3:F3}, newWcoZ={4:F3}",
                session.ReferenceToolLength, newToolLength.Value, offset, currentWcoZ, newWcoZ);
            machine.SendLine($"{CmdSetWorkOffset} Z{newWcoZ:F3}");
            Thread.Sleep(CommandDelayMs);

            // Raise Z and return to original XY position
            SetStatus(ToolChangeStatusReturning);
            machine.SendLine(CmdAbsolute);
            machine.SendLine($"{CmdMachineCoords} {CmdRapidMove} Z{ToolChangeClearanceZ:F1}");
            WaitForStableIdle();
            machine.SendLine($"{CmdRapidMove} X{returnX:F3} Y{returnY:F3}");
            WaitForStableIdle();

            SetStatus(ToolChangeStatusComplete);
            Thread.Sleep(ConfirmationDisplayMs);
            ClearOverlay();
            return true;
        }

        /// <summary>
        /// Tool change without tool setter (Mode B).
        /// User must probe PCB surface with new tool.
        /// </summary>
        private static bool HandleToolChangeWithoutSetter(string toolInfoStr)
        {
            var machine = AppState.Machine;

            SetOverlay(toolInfoStr, "Change tool + attach clip. Y=Continue  X=Cancel");

            if (!WaitForContinueOrAbort())
            {
                ClearOverlay();
                return false;
            }

            // Probe PCB surface
            SetStatus(ToolChangeStatusProbingZ);
            bool probeSuccess = ProbePCBSurface();

            if (!probeSuccess)
            {
                SetOverlay(toolInfoStr, "Probe failed!");
                Thread.Sleep(ConfirmationDisplayMs);
                ClearOverlay();
                return false;
            }

            // Zero Z at probe position
            machine.SendLine($"{CmdZeroWorkOffset} Z0");
            SetStatus(ToolChangeStatusZeroing);

            // Raise to safe height
            machine.SendLine($"{CmdRapidMove} Z{RetractZMm:F1}");
            WaitForStableIdle();

            SetStatus(ToolChangeStatusComplete);
            Thread.Sleep(ConfirmationDisplayMs);
            ClearOverlay();
            return true;
        }

        /// <summary>
        /// Execute a probe operation to a target machine Z and wait for completion.
        /// Returns (success, machineZ) where machineZ is valid only if success is true.
        /// </summary>
        private static (bool Success, double MachineZ) ExecuteProbeToMachineZ(double targetMachineZ, double feed)
        {
            var machine = AppState.Machine;
            // Convert machine Z to work Z: WorkZ = MachineZ - WCO.Z
            // WCO.Z = MachineZ - WorkZ, so WorkZ = MachineZ - (MachinePos.Z - WorkPos.Z)
            double wcoZ = machine.MachinePosition.Z - machine.WorkPosition.Z;
            double targetWorkZ = targetMachineZ - wcoZ;
            Logger.Log("ExecuteProbeToMachineZ: targetMachineZ={0:F3}, WCO.Z={1:F3}, targetWorkZ={2:F3}",
                targetMachineZ, wcoZ, targetWorkZ);
            return ExecuteProbeToWorkZ(targetWorkZ, feed);
        }

        /// <summary>
        /// Execute a probe operation to a target work Z and wait for completion.
        /// Returns (success, machineZ) where machineZ is valid only if success is true.
        /// </summary>
        private static (bool Success, double MachineZ) ExecuteProbeToWorkZ(double targetWorkZ, double feed)
        {
            var machine = AppState.Machine;

            bool completed = false;
            bool success = false;
            double probeZ = 0;

            Action<Core.Util.Vector3, bool> probeCallback = (pos, probeSuccess) =>
            {
                success = probeSuccess;
                // Use the actual probe contact position from PRB message, not current machine position
                probeZ = machine.LastProbePosMachine.Z;
                Logger.Log("ExecuteProbeToWorkZ: Callback - success={0}, PRB machineZ={1:F3}, workZ={2:F3}",
                    probeSuccess, machine.LastProbePosMachine.Z, pos.Z);
                completed = true;
            };

            machine.ProbeFinished += probeCallback;

            try
            {
                machine.ProbeStart();
                machine.SendLine(CmdAbsolute);
                string probeCmd = $"{CmdProbeToward} Z{targetWorkZ:F3} F{feed:F1}";
                Logger.Log("ExecuteProbeToWorkZ: Sending probe command: {0}", probeCmd);
                Logger.Log("ExecuteProbeToWorkZ: Current machineZ={0:F3}, workZ={1:F3}, targetWorkZ={2:F3}",
                    machine.MachinePosition.Z, machine.WorkPosition.Z, targetWorkZ);
                machine.SendLine(probeCmd);

                while (!completed)
                {
                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(true);
                        if (InputHelpers.IsKey(key, ConsoleKey.X, 'x'))
                        {
                            // SoftReset clears buffer to prevent queued commands from executing
                            Logger.Log("ExecuteProbeToWorkZ: Aborting - sending SoftReset");
                            machine.SoftReset();
                            machine.ProbeStop();
                            Thread.Sleep(ResetWaitMs);
                            // Unlock if reset caused alarm
                            if (StatusHelpers.IsAlarm(machine))
                            {
                                machine.SendLine(CmdUnlock);
                                Thread.Sleep(CommandDelayMs);
                            }
                            return (false, 0);
                        }
                    }
                    Thread.Sleep(StatusPollIntervalMs);
                }

                machine.ProbeStop();
                return (success, probeZ);
            }
            finally
            {
                machine.ProbeFinished -= probeCallback;
            }
        }

        /// <summary>
        /// Probe the tool setter and return the Z position (machine coordinates).
        /// Uses two-pass probing: fast seek to find approximate position, then slow precise probe.
        /// Returns null if probe fails. Tool is retracted after probing.
        /// </summary>
        private static double? ProbeToolSetter()
        {
            var machine = AppState.Machine;
            var session = AppState.Session;

            // Get probe parameters from machine profile
            var config = MachineProfiles.GetToolSetterConfig();
            double probeDepth = config?.ProbeDepth ?? ToolSetterProbeDepth;
            double fastFeed = config?.FastFeed ?? ToolSetterSeekFeed;
            double slowFeed = config?.SlowFeed ?? ToolSetterProbeFeed;
            double retract = config?.Retract ?? ToolSetterRetract;

            Logger.Log("ProbeToolSetter: Using probeDepth={0:F1}, fastFeed={1:F1}, slowFeed={2:F1}, retract={3:F1}",
                probeDepth, fastFeed, slowFeed, retract);

            // If we know the approximate tool setter position from a previous probe,
            // rapid down to near it before starting the seek (saves time)
            if (session.LastToolSetterZ != 0)
            {
                double approachZ = session.LastToolSetterZ + ToolSetterApproachClearance;
                Logger.Log("ProbeToolSetter: Rapid approach to Z={0:F3}", approachZ);
                machine.SendLine(CmdAbsolute);
                machine.SendLine($"{CmdMachineCoords} {CmdRapidMove} Z{approachZ:F3}");
                StatusHelpers.WaitForIdle(machine, ZHeightWaitTimeoutMs);
            }

            // First pass: fast seek to find the tool setter
            Logger.Log("ProbeToolSetter: Fast seek at {0} mm/min", fastFeed);
            var (seekSuccess, seekZ) = ExecuteProbeToWorkZ(-probeDepth, fastFeed);
            if (!seekSuccess)
            {
                Logger.Log("ProbeToolSetter: Fast seek failed");
                return null;
            }
            Logger.Log("ProbeToolSetter: Fast seek found at Z={0:F3}", seekZ);

            // Remember this position for faster approach next time
            session.LastToolSetterZ = seekZ;
            Persistence.SaveSession();

            // Retract for precise probe
            Logger.Log("ProbeToolSetter: Retracting {0}mm", retract);
            machine.SendLine(CmdAbsolute);
            machine.SendLine($"{CmdMachineCoords} {CmdRapidMove} Z{seekZ + retract:F3}");
            StatusHelpers.WaitForIdle(machine, ZHeightWaitTimeoutMs);

            // Second pass: slow precise probe (target 1mm below fast probe contact)
            double slowProbeTarget = seekZ - 1.0;
            Logger.Log("ProbeToolSetter: Slow probe at {0} mm/min, target machineZ={1:F3}",
                slowFeed, slowProbeTarget);
            var (probeSuccess, probeZ) = ExecuteProbeToMachineZ(slowProbeTarget, slowFeed);
            if (!probeSuccess)
            {
                Logger.Log("ProbeToolSetter: Slow probe failed");
                return null;
            }
            Logger.Log("ProbeToolSetter: Precise measurement at Z={0:F3}", probeZ);

            // Retract after probing so tool is clear of the button
            Logger.Log("ProbeToolSetter: Final retract");
            machine.SendLine($"{CmdMachineCoords} {CmdRapidMove} Z{probeZ + retract:F3}");
            StatusHelpers.WaitForIdle(machine, ZHeightWaitTimeoutMs);

            return probeZ;
        }

        /// <summary>
        /// Probe the PCB surface at current XY position.
        /// Returns true if probe succeeded.
        /// </summary>
        private static bool ProbePCBSurface()
        {
            var settings = AppState.Settings;
            var (success, _) = ExecuteProbeToWorkZ(-settings.ProbeMaxDepth, settings.ProbeFeed);
            return success;
        }

        /// <summary>
        /// Probe the tool setter to establish reference tool length.
        /// Called before milling when tool setter is configured.
        /// Returns true if successful.
        /// </summary>
        public static bool ProbeReferenceToolLength()
        {
            var machine = AppState.Machine;
            var session = AppState.Session;
            var toolSetterPos = MachineProfiles.GetToolSetterPosition();

            if (toolSetterPos == null)
            {
                return false;
            }

            AnsiConsole.MarkupLine($"[{ColorDim}]Moving to tool setter...[/]");
            machine.SendLine(CmdAbsolute);
            machine.SendLine($"{CmdRapidMove} Z{RetractZMm:F1}");
            StatusHelpers.WaitForIdle(machine, ZHeightWaitTimeoutMs);
            machine.SendLine($"{CmdMachineCoords} {CmdRapidMove} X{toolSetterPos.Value.X:F1} Y{toolSetterPos.Value.Y:F1}");
            StatusHelpers.WaitForIdle(machine, MoveCompleteTimeoutMs);

            AnsiConsole.MarkupLine($"[{ColorDim}]Probing tool setter for reference...[/]");
            double? toolLength = ProbeToolSetter();

            if (toolLength == null)
            {
                AnsiConsole.MarkupLine($"[{ColorError}]Probe failed![/]");
                return false;
            }

            session.ReferenceToolLength = toolLength.Value;
            session.HasReferenceToolLength = true;
            Persistence.SaveSession();

            AnsiConsole.MarkupLine($"[{ColorSuccess}]Reference tool length: {toolLength:F3}mm[/]");

            // Raise Z after probing
            machine.SendLine($"{CmdRapidMove} Z{RetractZMm:F1}");
            StatusHelpers.WaitForIdle(machine, ZHeightWaitTimeoutMs);

            return true;
        }
    }
}
