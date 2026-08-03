#nullable enable
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using coppercli.Core.Communication;
using static coppercli.Core.Communication.Machine;
using coppercli.Core.Util;
using static coppercli.Core.Util.Constants;
using static coppercli.Core.Util.GrblProtocol;
using static coppercli.Core.Util.GCodeFormat;

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
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            long budgetMs = timeoutMs;

            while (elapsed.ElapsedMilliseconds < budgetMs && !ct.IsCancellationRequested)
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
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            long budgetMs = timeoutMs;
            int requiredCount = IdleSettleMs / StatusPollIntervalMs;
            int stableCount = 0;

            while (elapsed.ElapsedMilliseconds < budgetMs && !ct.IsCancellationRequested)
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

            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            long budgetMs = timeoutMs;

            while (elapsed.ElapsedMilliseconds < budgetMs && !ct.IsCancellationRequested)
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
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            long budgetMs = timeoutMs;

            while (elapsed.ElapsedMilliseconds < budgetMs && !ct.IsCancellationRequested)
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
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            long budgetMs = timeoutMs;

            while (elapsed.ElapsedMilliseconds < budgetMs && !ct.IsCancellationRequested)
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
        // Reply awaiting
        // =========================================================================

        /// <summary>
        /// Awaits a reply task, but never past a timeout. On cancellation the reply's own
        /// cancellation surfaces (rather than being mislabelled a timeout); on a genuine
        /// timeout, throws with the given message. Used for GRBL replies that may never
        /// arrive because the command was rejected.
        /// </summary>
        public static async Task<T> AwaitReplyOrTimeoutAsync<T>(
            Task<T> reply, int timeoutMs, string timeoutMessage, CancellationToken ct)
        {
            // Linked source so the timer is cancelled the instant the reply lands - a long
            // grid probe must not accumulate one live timer per point.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var finished = await Task.WhenAny(
                reply, Task.Delay(timeoutMs, timeoutCts.Token)).ConfigureAwait(false);

            timeoutCts.Cancel();

            if (finished != reply && !ct.IsCancellationRequested)
            {
                throw new TimeoutException(timeoutMessage);
            }

            return await reply.ConfigureAwait(false);
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
        /// Prepare machine for operation: wait for Idle and confirm nothing is wrong.
        /// </summary>
        /// <returns>
        /// True only if the machine actually reached Idle and is not alarmed. A machine
        /// still running or held is NOT ready - motion sent to it would queue behind
        /// whatever it is already doing.
        /// </returns>
        public static async Task<bool> EnsureMachineReadyAsync(IMachine machine, int timeoutMs, CancellationToken ct = default)
        {
            if (timeoutMs <= 0)
            {
                timeoutMs = IdleWaitTimeoutMs;
            }

            // Door is NOT cleared here. This runs before a job starts, and clearing it
            // sends CycleStart - resuming motion because the software decided to, not
            // because the operator said the enclosure was clear. An open door blocks the
            // start instead; the operator closes it and starts again.
            bool idle = await WaitForIdleAsync(machine, timeoutMs, ct);
            return idle && !IsProblematic(machine);
        }

        /// <summary>
        /// Stop machine motion and clear command buffer.
        /// Sends FeedHold and SoftReset, clears any resulting Alarm, then commands
        /// spindle off.
        /// Use when cancelling operations to prevent buffered commands from resuming.
        /// </summary>
        public static async Task StopAndResetAsync(IMachine machine)
        {
            // FeedHold and SoftReset are real-time bytes, delivered whatever mode the
            // machine is in, and the reset is what actually stops the spindle. The
            // explicit M5 comes after them, once the reset has returned us to Manual
            // mode - sent before, it would be dropped, because ordinary commands are
            // discarded while a file is streaming.
            machine.FeedHold();
            await Task.Delay(CommandDelayMs).ConfigureAwait(false);

            machine.SoftReset();
            await Task.Delay(ResetWaitMs).ConfigureAwait(false);

            if (IsAlarm(machine))
            {
                machine.SendLine(CmdUnlock);
                await Task.Delay(CommandDelayMs).ConfigureAwait(false);
            }

            // Belt and braces, now that the reset has put us back in Manual mode and any
            // alarm is cleared, so this one will actually be sent.
            machine.SendLine(CmdSpindleOff);

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
            machine.SendLine(Inv($"{CmdZeroWorkOffset} {axes}"));
            // G10 L20 doesn't cause a state change, so wait for command to be processed
            await Task.Delay(CommandDelayMs, ct).ConfigureAwait(false);
            await WaitForIdleAsync(machine, IdleSettleMs, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Home the machine and wait for completion.
        /// This is the SINGLE SOURCE OF TRUTH for homing - all code paths use this.
        /// Sets machine.IsHoming during operation, machine.IsHomed = true on success.
        /// </summary>
        public static async Task<HomingOutcome> HomeAsync(IMachine machine, int timeoutMs, CancellationToken ct = default)
        {
            // Listen for a refusal while we wait. Without this the only evidence of a
            // rejected $H is that the machine stayed Idle - indistinguishable from a
            // machine that simply has not started yet, and useless to explain.
            GrblRejection? refusal = null;

            void OnRejected(GrblRejection rejection)
            {
                if (rejection.Command.Contains(CmdHome, StringComparison.OrdinalIgnoreCase))
                {
                    refusal = rejection;
                }
            }

            machine.CommandRejected += OnRejected;
            machine.IsHoming = true;

            try
            {
                long reportsBefore = machine.StatusReportCount;
                machine.SendLine(CmdHome);

                // We must not certify a machine that never moved: a rejected or dropped
                // $H leaves the status at Idle, and the idle wait below would then
                // succeed on its first poll. Every later G53 move trusts this flag.
                string? started = await WaitForStatusChangeAsync(machine, StatusIdle, MotionStartTimeoutMs, ct);

                if (started == null)
                {
                    // No state change seen. Distinguish the two reasons: if GRBL is
                    // still answering status queries and still says Idle, the $H did not
                    // take. If it has gone quiet, homing is under way (some builds stop
                    // answering during the cycle) and we wait it out below.
                    // Counted, not timed: a clock step must not decide whether a $H
                    // took. More reports since we asked means GRBL is answering and
                    // still Idle, so the command did not take.
                    bool grblStillAnswering = machine.StatusReportCount > reportsBefore;

                    if (grblStillAnswering)
                    {
                        return HomingOutcome.Refused(refusal);
                    }
                }

                // Idle is only believable once GRBL is talking to us again: while it is
                // quiet mid-cycle the last status we hold still says Idle, and taking
                // that at face value would certify a machine part-way through homing.
                long quietAt = machine.StatusReportCount;
                var talking = Stopwatch.StartNew();

                while (machine.StatusReportCount == quietAt
                       && talking.ElapsedMilliseconds < timeoutMs
                       && !ct.IsCancellationRequested)
                {
                    await Task.Delay(StatusPollIntervalMs, ct).ConfigureAwait(false);
                }

                // Sustained idle, not a single sample - homing ends with a pull-off move.
                bool success = await WaitForStableIdleAsync(machine, timeoutMs, ct);

                if (!success || !IsIdle(machine))
                {
                    return HomingOutcome.Refused(refusal);
                }

                machine.IsHomed = true;
                return HomingOutcome.Homed;
            }
            finally
            {
                machine.IsHoming = false;
                machine.CommandRejected -= OnRejected;
            }
        }

        /// <summary>
        /// Safe completion: stops all motion, clears GRBL buffer, and optionally homes.
        /// Defense in depth for milling completion - ensures machine cannot continue
        /// executing commands even if there's a bug elsewhere.
        /// </summary>
        public static async Task SafeCompletionAsync(IMachine machine, bool homeAfter = false, CancellationToken ct = default)
        {
            // Same stop sequence as an abort - kept as one routine so the two cannot
            // drift apart. They already had: only one of them stopped the spindle.
            await StopAndResetAsync(machine).ConfigureAwait(false);

            if (homeAfter && !(await HomeAsync(machine, HomingTimeoutMs, ct).ConfigureAwait(false)).Success)
            {
                ControllerLog.Log("SafeCompletion: post-job homing did not complete");
            }
        }

        /// <summary>
        /// Safety retract Z to a machine coordinate using G53.
        /// </summary>
        /// <returns>
        /// True only if Z is confirmed at the target. False means the retract did NOT
        /// happen - the command may have been rejected or the move timed out - and the
        /// caller must not proceed with any XY motion, because the tool is still down.
        /// </returns>
        public static async Task<bool> SafetyRetractZAsync(IMachine machine, double targetMachineZ, int timeoutMs, CancellationToken ct = default)
        {
            if (timeoutMs <= 0)
            {
                timeoutMs = ZHeightWaitTimeoutMs;
            }

            double startZ = machine.MachinePosition.Z;

            // Enforce absolute mode and send retract command
            machine.SendLine(CmdAbsolute);
            machine.SendLine(Inv($"{CmdMachineCoords} {CmdRapidMove} Z{targetMachineZ:F3}"));

            // If already at target, just wait briefly for command to process
            if (Math.Abs(startZ - targetMachineZ) < PositionToleranceMm)
            {
                await Task.Delay(CommandDelayMs, ct).ConfigureAwait(false);
                await WaitForIdleAsync(machine, IdleWaitTimeoutMs, ct);
                return Math.Abs(machine.MachinePosition.Z - targetMachineZ) < PositionToleranceMm;
            }

            // Wait for move to start, then for Z to arrive. Arrival is what we report -
            // a move that never started still fails the height check below.
            await WaitForMoveStartAsync(machine, startZ, timeoutMs, ct);

            return await WaitForMachineZHeightAsync(machine, targetMachineZ, timeoutMs, ct);
        }
    }
}
