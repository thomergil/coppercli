// Extracted from Program.cs - Repeated G-code command patterns

using coppercli.Core.Communication;
using static coppercli.CliConstants;
using static coppercli.Core.Util.GrblProtocol;

namespace coppercli.Helpers
{
    /// <summary>
    /// Helper methods for common machine command patterns.
    /// These consolidate repeated G-code sequences into reusable methods.
    /// </summary>
    internal static class MachineCommands
    {
        /// <summary>
        /// Moves to a safe Z height in absolute mode.
        /// </summary>
        public static void MoveToSafeHeight(Machine machine, double height)
        {
            machine.SendLine(CmdAbsolute);
            machine.SendLine($"{CmdRapidMove} Z{height:F3}");
        }

        /// <summary>
        /// Performs a relative move on a single axis, then returns to absolute mode.
        /// </summary>
        public static void RelativeMove(Machine machine, string axis, double distance)
        {
            machine.SendLine(CmdRelative);
            machine.SendLine($"{CmdRapidMove} {axis}{distance:F3}");
            machine.SendLine(CmdAbsolute);
        }

        /// <summary>
        /// Sends the home command ($H).
        /// </summary>
        public static void Home(Machine machine)
        {
            machine.SendLine(CmdHome);
        }

        /// <summary>
        /// Sends the unlock command ($X).
        /// </summary>
        public static void Unlock(Machine machine)
        {
            machine.SendLine(CmdUnlock);
        }

        /// <summary>
        /// Sends the spindle off command (M5).
        /// </summary>
        public static void StopSpindle(Machine machine)
        {
            machine.SendLine(CmdSpindleOff);
        }

        /// <summary>
        /// Sends the cycle start command (~).
        /// </summary>
        public static void CycleStartCmd(Machine machine)
        {
            machine.SendLine(CycleStart.ToString());
        }

        /// <summary>
        /// Zeros the work offset for the specified axes (e.g., "X0 Y0 Z0").
        /// </summary>
        public static void ZeroWorkOffset(Machine machine, string axes)
        {
            machine.SendLine($"{CmdZeroWorkOffset} {axes}");
        }

        /// <summary>
        /// Rapid move to specified XY position.
        /// </summary>
        public static void RapidMoveXY(Machine machine, double x, double y)
        {
            machine.SendLine($"{CmdRapidMove} X{x:F3} Y{y:F3}");
        }

        /// <summary>
        /// Rapid move to specified Z position.
        /// </summary>
        public static void RapidMoveZ(Machine machine, double z)
        {
            machine.SendLine($"{CmdRapidMove} Z{z:F3}");
        }

        /// <summary>
        /// Linear move to specified XY position at given feed rate.
        /// </summary>
        public static void LinearMoveXY(Machine machine, double x, double y, double feed)
        {
            machine.SendLine($"{CmdLinearMove} X{x:F3} Y{y:F3} F{feed:F0}");
        }

        /// <summary>
        /// Probe toward workpiece on Z axis until contact (no error if no contact).
        /// </summary>
        public static void ProbeZ(Machine machine, double maxDepth, double feed)
        {
            machine.SendLine($"{CmdProbeToward} Z-{maxDepth:F3} F{feed:F1}");
        }

        /// <summary>
        /// Sets absolute distance mode (G90).
        /// </summary>
        public static void SetAbsoluteMode(Machine machine)
        {
            machine.SendLine(CmdAbsolute);
        }

        /// <summary>
        /// Sets incremental distance mode (G91).
        /// </summary>
        public static void SetRelativeMode(Machine machine)
        {
            machine.SendLine(CmdRelative);
        }

        /// <summary>
        /// Always sends soft reset and unlocks if needed. Use to stop active operations.
        /// </summary>
        public static void ForceResetAndUnlock(Machine machine)
        {
            machine.SoftReset();
            Thread.Sleep(CliConstants.ResetWaitMs);

            if (StatusHelpers.IsAlarm(machine))
            {
                machine.SendLine(CmdUnlock);
                Thread.Sleep(CliConstants.CommandDelayMs);
            }
        }

        /// <summary>
        /// Clears Door state if present by sending CycleStart.
        /// Returns true if Door was cleared, false if no action needed.
        /// Does NOT handle Alarm state - caller should check for Alarm separately.
        /// </summary>
        public static bool ClearDoorState(Machine machine)
        {
            if (StatusHelpers.IsDoor(machine))
            {
                // Door state: send CycleStart to resume (safe while machine is moving)
                machine.CycleStart();
                Thread.Sleep(CliConstants.CommandDelayMs);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Performs a relative Z move, then returns to absolute mode.
        /// Use for small Z adjustments like retracting after a probe.
        /// </summary>
        public static void RaiseZRelative(Machine machine, double distance)
        {
            machine.SendLine(CmdRelative);
            machine.SendLine($"{CmdRapidMove} Z{distance:F3}");
            machine.SendLine(CmdAbsolute);
        }

        /// <summary>
        /// Rapid move to Z height and wait for arrival.
        /// Combines MoveToSafeHeight with WaitForZHeight for common "raise and wait" pattern.
        /// </summary>
        public static void RapidMoveAndWaitZ(Machine machine, double targetZ, int timeoutMs = 0)
        {
            machine.SendLine(CmdAbsolute);
            machine.SendLine($"{CmdRapidMove} Z{targetZ:F3}");
            StatusHelpers.WaitForZHeight(machine, targetZ, timeoutMs);
        }

        /// <summary>
        /// Safety retract Z to machine coordinate position and BLOCK until complete.
        /// Uses G53 for machine coordinates. Waits for move to start, then waits for
        /// machine Z position to reach target. This is the safest way to retract before
        /// starting an operation - guarantees Z is up before any other commands execute.
        /// </summary>
        public static void SafetyRetractZ(Machine machine, double targetMachineZ, int timeoutMs = 0)
        {
            if (timeoutMs <= 0)
            {
                timeoutMs = ZHeightWaitTimeoutMs;
            }

            double startZ = machine.MachinePosition.Z;

            // Always enforce absolute mode - there's no valid reason to be in G91 during milling
            machine.SendLine(CmdAbsolute);
            machine.SendLine($"{CmdMachineCoords} {CmdRapidMove} Z{targetMachineZ:F3}");

            // If already at target, just wait briefly for command to process
            if (Math.Abs(startZ - targetMachineZ) < PositionToleranceMm)
            {
                Thread.Sleep(CommandDelayMs);
                StatusHelpers.WaitForIdle(machine, IdleWaitTimeoutMs);
                return;
            }

            // Wait for move to actually start (position changes or status becomes Run)
            var startTime = DateTime.Now;
            while ((DateTime.Now - startTime).TotalMilliseconds < timeoutMs)
            {
                if (Math.Abs(machine.MachinePosition.Z - startZ) > PositionToleranceMm)
                {
                    break;  // Move started
                }
                if (machine.Status.StartsWith(StatusRun))
                {
                    break;  // Move started
                }
                Thread.Sleep(StatusPollIntervalMs);
            }

            // Now wait for Z to reach target position
            StatusHelpers.WaitForMachineZHeight(machine, targetMachineZ, timeoutMs);
        }

        /// <summary>
        /// Prepares machine for an operation by clearing Door state, waiting for Idle,
        /// and checking for Alarm. Returns true if machine is ready, false if in Alarm state.
        /// Use at the start of milling, probing, or other operations that require a clean state.
        /// </summary>
        public static bool EnsureMachineReady(Machine machine, int idleTimeoutMs = 0)
        {
            if (idleTimeoutMs <= 0)
            {
                idleTimeoutMs = CliConstants.IdleWaitTimeoutMs;
            }

            ClearDoorState(machine);
            StatusHelpers.WaitForIdle(machine, idleTimeoutMs);
            return !StatusHelpers.IsAlarm(machine);
        }

    }
}
