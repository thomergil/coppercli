#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using coppercli.Core.Communication;
using static coppercli.Core.Communication.Machine;
using coppercli.Core.Util;
using static coppercli.Core.Util.Constants;
using static coppercli.Core.Util.GrblProtocol;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Utility methods for waiting on machine states and positions.
    /// Used by controllers for blocking waits with cancellation support.
    /// Uses IMachine interface to enable testing with mocks.
    /// </summary>
    public static class MachineWait
    {
        // =========================================================================
        // Status checks
        // =========================================================================

        /// <summary>Checks if the machine is in Idle state.</summary>
        public static bool IsIdle(IMachine machine) => machine.Status == StatusIdle;

        /// <summary>Checks if the machine is in Alarm state.</summary>
        public static bool IsAlarm(IMachine machine) => machine.Status.StartsWith(StatusAlarm);

        /// <summary>Checks if the machine is in Hold state.</summary>
        public static bool IsHold(IMachine machine) => machine.Status.StartsWith(StatusHold);

        /// <summary>Checks if the machine is in Door state.</summary>
        public static bool IsDoor(IMachine machine) => machine.Status.StartsWith(StatusDoor);

        /// <summary>Checks if the machine is in any problematic state (Alarm or Door).</summary>
        public static bool IsProblematic(IMachine machine) => IsAlarm(machine) || IsDoor(machine);

        // =========================================================================
        // Blocking waits (async with cancellation)
        // =========================================================================

        /// <summary>
        /// Wait for machine to reach Idle state.
        /// </summary>
        public static async Task<bool> WaitForIdleAsync(IMachine machine, int timeoutMs, CancellationToken ct = default)
        {
            var deadline = DateTime.Now.AddMilliseconds(timeoutMs);

            while (DateTime.Now < deadline && !ct.IsCancellationRequested)
            {
                if (machine.Status == StatusIdle)
                {
                    return true;
                }
                await Task.Delay(StatusPollIntervalMs, ct).ConfigureAwait(false);
            }

            return false;
        }

        /// <summary>
        /// Wait for machine to be idle for a sustained period (stable idle).
        /// Handles buffered commands that may start executing immediately after Idle is first seen.
        /// </summary>
        /// <param name="machine">The machine to monitor.</param>
        /// <param name="timeoutMs">Maximum time to wait.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <param name="onPoll">Optional callback invoked each poll iteration (for progress updates).</param>
        public static async Task<bool> WaitForStableIdleAsync(IMachine machine, int timeoutMs, CancellationToken ct = default, Action? onPoll = null)
        {
            var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            int requiredCount = IdleSettleMs / StatusPollIntervalMs;
            int stableCount = 0;

            while (DateTime.Now < deadline && !ct.IsCancellationRequested)
            {
                onPoll?.Invoke();

                if (machine.Status == StatusIdle)
                {
                    stableCount++;
                    if (stableCount >= requiredCount)
                    {
                        return true;
                    }
                }
                else
                {
                    stableCount = 0;
                }
                await Task.Delay(StatusPollIntervalMs, ct).ConfigureAwait(false);
            }

            return false;
        }

        /// <summary>
        /// Wait for work Z position to reach target height.
        /// </summary>
        public static Task<bool> WaitForZHeightAsync(IMachine machine, double targetZ, int timeoutMs, CancellationToken ct = default)
            => WaitForZHeightCoreAsync(machine, targetZ, timeoutMs, m => m.WorkPosition.Z, ct);

        /// <summary>
        /// Wait for machine Z position to reach target height (for G53 moves).
        /// </summary>
        public static Task<bool> WaitForMachineZHeightAsync(IMachine machine, double targetZ, int timeoutMs, CancellationToken ct = default)
            => WaitForZHeightCoreAsync(machine, targetZ, timeoutMs, m => m.MachinePosition.Z, ct);

        private static async Task<bool> WaitForZHeightCoreAsync(IMachine machine, double targetZ, int timeoutMs, Func<IMachine, double> getZ, CancellationToken ct)
        {
            if (timeoutMs <= 0)
            {
                timeoutMs = ZHeightWaitTimeoutMs;
            }

            var deadline = DateTime.Now.AddMilliseconds(timeoutMs);

            while (DateTime.Now < deadline && !ct.IsCancellationRequested)
            {
                if (Math.Abs(getZ(machine) - targetZ) < PositionToleranceMm)
                {
                    return true;
                }
                await Task.Delay(StatusPollIntervalMs, ct).ConfigureAwait(false);
            }

            return false;
        }

        /// <summary>
        /// Wait for machine to start moving (position changes or status becomes Run).
        /// Used to detect when a command has actually started executing.
        /// </summary>
        public static async Task<bool> WaitForMoveStartAsync(IMachine machine, double startZ, int timeoutMs, CancellationToken ct = default)
        {
            var deadline = DateTime.Now.AddMilliseconds(timeoutMs);

            while (DateTime.Now < deadline && !ct.IsCancellationRequested)
            {
                // Move started if position changed or status is Run
                if (Math.Abs(machine.MachinePosition.Z - startZ) > PositionToleranceMm)
                {
                    return true;
                }
                if (machine.Status.StartsWith(StatusRun))
                {
                    return true;
                }
                await Task.Delay(StatusPollIntervalMs, ct).ConfigureAwait(false);
            }

            return false;
        }

        /// <summary>
        /// Wait for status to change from current value.
        /// Returns the new status or null on timeout.
        /// </summary>
        public static async Task<string?> WaitForStatusChangeAsync(IMachine machine, string currentStatus, int timeoutMs, CancellationToken ct = default)
        {
            var deadline = DateTime.Now.AddMilliseconds(timeoutMs);

            while (DateTime.Now < deadline && !ct.IsCancellationRequested)
            {
                if (machine.Connected && machine.Status != StatusDisconnected && machine.Status != currentStatus)
                {
                    return machine.Status;
                }
                await Task.Delay(StatusPollIntervalMs, ct).ConfigureAwait(false);
            }

            return null;
        }

        // =========================================================================
        // Machine operations
        // =========================================================================

        /// <summary>
        /// Clear Door state if present by sending CycleStart.
        /// Does NOT handle Alarm state.
        /// </summary>
        public static async Task<bool> ClearDoorStateAsync(IMachine machine, CancellationToken ct = default)
        {
            if (IsDoor(machine))
            {
                machine.CycleStart();
                await Task.Delay(CommandDelayMs, ct).ConfigureAwait(false);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Prepare machine for operation: clear Door, wait for Idle, check for Alarm.
        /// Returns true if machine is ready, false if in Alarm state.
        /// </summary>
        public static async Task<bool> EnsureMachineReadyAsync(IMachine machine, int timeoutMs, CancellationToken ct = default)
        {
            if (timeoutMs <= 0)
            {
                timeoutMs = IdleWaitTimeoutMs;
            }

            await ClearDoorStateAsync(machine, ct);
            await WaitForIdleAsync(machine, timeoutMs, ct);
            return !IsAlarm(machine);
        }

        /// <summary>
        /// Stop machine motion and clear command buffer.
        /// Sends FeedHold, SoftReset, and clears Alarm if needed.
        /// Use when cancelling operations to prevent buffered commands from resuming.
        /// </summary>
        public static async Task StopAndResetAsync(IMachine machine)
        {
            machine.FeedHold();
            await Task.Delay(CommandDelayMs).ConfigureAwait(false);

            machine.SoftReset();
            await Task.Delay(ResetWaitMs).ConfigureAwait(false);

            if (IsAlarm(machine))
            {
                machine.SendLine(CmdUnlock);
                await Task.Delay(CommandDelayMs).ConfigureAwait(false);
            }

            await WaitForIdleAsync(machine, IdleWaitTimeoutMs, CancellationToken.None);
        }

        /// <summary>
        /// Zero work offset for specified axes and wait for command to complete.
        /// axes should be like "X0 Y0 Z0" or "Z0".
        /// G10 L20 is a settings command that GRBL processes instantly without
        /// leaving Idle state, so we add a delay to ensure it's processed.
        /// </summary>
        public static async Task ZeroWorkOffsetAsync(IMachine machine, string axes, CancellationToken ct = default)
        {
            machine.SendLine($"{CmdZeroWorkOffset} {axes}");
            // G10 L20 doesn't cause a state change, so wait for command to be processed
            await Task.Delay(CommandDelayMs, ct).ConfigureAwait(false);
            await WaitForIdleAsync(machine, IdleSettleMs, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Home the machine and wait for completion.
        /// This is the SINGLE SOURCE OF TRUTH for homing - all code paths use this.
        /// Sets machine.IsHoming during operation, machine.IsHomed = true on success.
        /// </summary>
        public static async Task<bool> HomeAsync(IMachine machine, int timeoutMs, CancellationToken ct = default)
        {
            machine.IsHoming = true;
            try
            {
                machine.SendLine(CmdHome);

                // Wait for machine to leave Idle state (enter "Home" status).
                // This prevents a race condition where we check IsIdle before the
                // machine has processed the $H command.
                await WaitForStatusChangeAsync(machine, StatusIdle, MotionStartTimeoutMs, ct);

                // Now wait for homing to complete (machine returns to Idle)
                bool success = await WaitForIdleAsync(machine, timeoutMs, ct);

                if (!success || !IsIdle(machine))
                {
                    return false;
                }

                machine.IsHomed = true;
                return true;
            }
            finally
            {
                machine.IsHoming = false;
            }
        }

        /// <summary>
        /// Safe completion: stops all motion, clears GRBL buffer, and optionally homes.
        /// Defense in depth for milling completion - ensures machine cannot continue
        /// executing commands even if there's a bug elsewhere.
        /// </summary>
        public static async Task SafeCompletionAsync(IMachine machine, bool homeAfter = false, CancellationToken ct = default)
        {
            // 1. Spindle off (safety first)
            machine.SendLine(CmdSpindleOff);

            // 2. FeedHold to stop any motion (also stops file streaming)
            machine.FeedHold();
            await Task.Delay(CommandDelayMs, ct).ConfigureAwait(false);

            // 3. SoftReset to clear GRBL's command buffer
            machine.SoftReset();
            await Task.Delay(ResetWaitMs, ct).ConfigureAwait(false);

            // 4. Clear Alarm state if needed (SoftReset causes Alarm)
            if (IsAlarm(machine))
            {
                machine.SendLine(CmdUnlock);
                await Task.Delay(CommandDelayMs, ct).ConfigureAwait(false);
            }

            // 5. Wait for idle
            await WaitForIdleAsync(machine, IdleWaitTimeoutMs, ct).ConfigureAwait(false);

            // 6. Optionally home to known safe position
            if (homeAfter)
            {
                await HomeAsync(machine, HomingTimeoutMs, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Safety retract Z to machine coordinate position and wait for completion.
        /// Uses G53 for machine coordinates. Guarantees Z is up before returning.
        /// </summary>
        public static async Task SafetyRetractZAsync(IMachine machine, double targetMachineZ, int timeoutMs, CancellationToken ct = default)
        {
            if (timeoutMs <= 0)
            {
                timeoutMs = ZHeightWaitTimeoutMs;
            }

            double startZ = machine.MachinePosition.Z;

            // Enforce absolute mode and send retract command
            machine.SendLine(CmdAbsolute);
            machine.SendLine($"{CmdMachineCoords} {CmdRapidMove} Z{targetMachineZ:F3}");

            // If already at target, just wait briefly for command to process
            if (Math.Abs(startZ - targetMachineZ) < PositionToleranceMm)
            {
                await Task.Delay(CommandDelayMs, ct).ConfigureAwait(false);
                await WaitForIdleAsync(machine, IdleWaitTimeoutMs, ct);
                return;
            }

            // Wait for move to start
            await WaitForMoveStartAsync(machine, startZ, timeoutMs, ct);

            // Wait for Z to reach target
            await WaitForMachineZHeightAsync(machine, targetMachineZ, timeoutMs, ct);
        }
    }
}
