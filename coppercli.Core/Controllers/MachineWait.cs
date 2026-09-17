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
using static coppercli.Core.Controllers.ControllerConstants;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Every derived answer about the machine, and every wait on one. It splits GRBL's status
    /// word two ways - <see cref="DoorState"/> and <see cref="MachineActivity"/> - and holds
    /// the answers the screens read from them, so no screen works one out for itself. It takes
    /// IMachine rather than Machine so tests can drive it with a double.
    /// </summary>
    public static class MachineWait
    {
        public static bool IsIdle(IMachine machine) => machine.Status == StatusIdle;

        public static bool IsAlarm(IMachine machine) => machine.Status.StartsWith(StatusAlarm);

        public static bool IsHold(IMachine machine) => machine.Status.StartsWith(StatusHold);

        public static bool IsDoor(IMachine machine) => machine.Status.StartsWith(StatusDoor);

        /// <summary>
        /// Door closed, machine parked, waiting for a cycle start.
        /// </summary>
        private static bool IsDoorWaitingForResume(IMachine machine) =>
            IsDoor(machine) && machine.StatusSubState == DoorSubStateClosed;

        /// <summary>
        /// GRBL is restoring from the park after a cycle start. A second cycle start here
        /// would land inside that move.
        /// </summary>
        private static bool IsDoorResuming(IMachine machine) =>
            IsDoor(machine) && machine.StatusSubState == DoorSubStateResuming;

        /// <summary>
        /// The enclosure may be open. Defined as the remainder of the other two, so an
        /// unrecognized substate counts as open.
        /// </summary>
        private static bool IsDoorOpen(IMachine machine) =>
            IsDoor(machine) && !IsDoorWaitingForResume(machine) && !IsDoorResuming(machine);

        /// <summary>
        /// Which door state the machine is in, or None when it is not at the door. Every
        /// screen reads this, so a new door state is handled in one place.
        /// </summary>
        public static DoorState GetDoorState(IMachine machine)
        {
            if (!IsDoor(machine))
            {
                return DoorState.None;
            }

            if (IsDoorOpen(machine))
            {
                return DoorState.Open;
            }

            if (IsDoorResuming(machine))
            {
                return DoorState.Resuming;
            }

            return DoorState.WaitingForResume;
        }

        /// <summary>
        /// True where a cycle start would end the hold. That is the only door state with
        /// anything to ask the operator; the other two are waited out.
        /// </summary>
        public static bool CanReleaseDoorHold(DoorState state) =>
            state == DoorState.WaitingForResume;

        /// <inheritdoc cref="CanReleaseDoorHold(DoorState)"/>
        public static bool CanReleaseDoorHold(IMachine machine) => CanReleaseDoorHold(GetDoorState(machine));

        public static string GetDoorMessage(DoorState state) => state switch
        {
            DoorState.Open => ControllerConstants.DoorOpenPrompt,
            DoorState.Resuming => ControllerConstants.DoorResumingMessage,
            _ => ControllerConstants.DoorHoldingPrompt
        };

        /// <summary>
        /// Screens and controls read this instead of GRBL's status word. The connected check
        /// comes first because Connected drops before the status word is rewritten.
        /// </summary>
        public static MachineActivity GetActivity(IMachine machine)
        {
            if (!machine.Connected || machine.Status == StatusDisconnected)
            {
                return MachineActivity.Disconnected;
            }

            if (IsAlarm(machine))
            {
                return MachineActivity.Alarm;
            }

            switch (GetDoorState(machine))
            {
                case DoorState.Open: return MachineActivity.DoorOpen;
                case DoorState.WaitingForResume: return MachineActivity.DoorHolding;
                case DoorState.Resuming: return MachineActivity.DoorResuming;
            }

            if (IsHold(machine))
            {
                return MachineActivity.Hold;
            }

            if (machine.Status == StatusRun)
            {
                return MachineActivity.Running;
            }

            if (IsIdle(machine))
            {
                return MachineActivity.Idle;
            }

            if (machine.Status == StatusSleep)
            {
                return MachineActivity.Sleep;
            }

            return MachineActivity.Other;
        }

        /// <summary>
        /// The states a wait gives up on: the machine will not act, or is not there.
        /// Derived from <see cref="NeedsAttention"/>, which is what a screen calls.
        /// </summary>
        public static bool IsUnavailable(MachineActivity activity) =>
            NeedsAttention(activity) || activity == MachineActivity.Disconnected;

        /// <inheritdoc cref="IsUnavailable(MachineActivity)"/>
        public static bool IsUnavailable(IMachine machine) => IsUnavailable(GetActivity(machine));

        /// <summary>
        /// Whether a probe cycle is open, read from the machine's own mode. A single Z probe
        /// runs without a controller, so a predicate derived from one cannot see it and the
        /// tool descends while every gate reports the machine free.
        /// </summary>
        public static bool IsProbeCycleOpen(IMachine machine) =>
            machine.Mode == OperatingMode.Probe;

        /// <summary>
        /// The door activities, listed once so callers that treat the door differently do
        /// not repeat the list.
        /// </summary>
        public static bool IsDoorActivity(MachineActivity activity) =>
            activity is MachineActivity.DoorOpen
                or MachineActivity.DoorHolding
                or MachineActivity.DoorResuming;

        /// <summary>
        /// The machine will not act on a command until the operator clears an alarm, closes
        /// the enclosure, waits out a park restore, or resets it out of $SLP.
        /// <see cref="IsUnavailable"/> is this set plus Disconnected.
        /// </summary>
        public static bool NeedsAttention(MachineActivity activity) =>
            IsDoorActivity(activity)
                || activity is MachineActivity.Alarm or MachineActivity.Sleep;

        /// <inheritdoc cref="NeedsAttention(MachineActivity)"/>
        public static bool NeedsAttention(IMachine machine) => NeedsAttention(GetActivity(machine));

        /// <summary>
        /// The machine's own state stops a job starting: an alarm to clear, or asleep. The
        /// door is excluded because ControllerBase.EnsureDoorClosedAsync handles it.
        /// </summary>
        public static bool BlocksJobStart(MachineActivity activity) =>
            NeedsAttention(activity) && !IsDoorActivity(activity);

        /// <summary>
        /// GRBL has reported at least once. An open port is not enough:
        /// <see cref="IMachine.Status"/> starts at Disconnected until the first report.
        /// </summary>
        public static bool IsResponding(MachineActivity activity) => activity != MachineActivity.Disconnected;

        /// <inheritdoc cref="IsResponding(MachineActivity)"/>
        public static bool IsResponding(IMachine machine) => IsResponding(GetActivity(machine));

        /// <summary>A feed hold applies: the machine is executing a program.</summary>
        public static bool CanPause(MachineActivity activity) => activity == MachineActivity.Running;

        /// <summary>
        /// A cycle start applies. A door hold also ends on a cycle start, but goes through
        /// <see cref="ReleaseDoorHoldAsync"/>, which checks the enclosure first.
        /// </summary>
        public static bool CanResume(MachineActivity activity) => activity == MachineActivity.Hold;

        /// <inheritdoc cref="CanResume(MachineActivity)"/>
        public static bool CanResume(IMachine machine) => CanResume(GetActivity(machine));

        /// <summary>
        /// Why restarting a paused run would not work, or null if it would. A machine holding
        /// at the door takes the lines into its planner and runs them when the hold is
        /// released, so the run waits for the operator to clear the enclosure first.
        /// </summary>
        public static string? GetResumeBlocker(IMachine machine) =>
            IsDoor(machine) ? ControllerConstants.ErrorDoorBlocksResume : null;

        /// <summary>
        /// Poll until a condition holds, the deadline passes, or the caller cancels; every
        /// wait here is this loop with a different predicate. Set
        /// <paramref name="abortWhenUnavailable"/> to false for waits whose subject is an
        /// alarmed or held machine, since the default returns false as soon as it sees one.
        /// </summary>
        private static async Task<bool> WaitUntilAsync(
            IMachine machine,
            Func<IMachine, bool> until,
            int timeoutMs,
            CancellationToken ct,
            bool abortWhenUnavailable = true,
            Func<bool>? onPoll = null)
        {
            var elapsed = Stopwatch.StartNew();

            while (elapsed.ElapsedMilliseconds < timeoutMs && !ct.IsCancellationRequested)
            {
                // Checked before the condition, so a screen that reads a key stops waiting on
                // the same poll the operator pressed it, not at the end of the budget.
                if (onPoll?.Invoke() == true)
                {
                    return false;
                }

                if (until(machine))
                {
                    return true;
                }

                // The condition cannot arrive until the operator clears the machine, so
                // fail now rather than after the full timeout.
                if (abortWhenUnavailable && IsUnavailable(machine))
                {
                    return false;
                }

                await Task.Delay(StatusPollIntervalMs, ct).ConfigureAwait(false);
            }

            // The condition can arrive inside the final poll interval, so check once more
            // before reporting a timeout.
            return !ct.IsCancellationRequested && until(machine);
        }

        public static Task<bool> WaitForIdleAsync(IMachine machine, int timeoutMs, CancellationToken ct = default)
            => WaitUntilAsync(machine, m => m.Status == StatusIdle, timeoutMs, ct);

        /// <summary>
        /// A single Idle report is not enough: GRBL reports Idle while buffered motion is
        /// still about to start.
        /// </summary>
        private static Task<bool> WaitForStableIdleAsync(IMachine machine, int timeoutMs, CancellationToken ct = default)
        {
            int requiredCount = IdleSettleMs / StatusPollIntervalMs;
            int stableCount = 0;

            return WaitUntilAsync(machine, m =>
            {
                stableCount = m.Status == StatusIdle ? stableCount + 1 : 0;
                return stableCount >= requiredCount;
            }, timeoutMs, ct);
        }

        /// <summary>Machine Z, for a G53 move.</summary>
        private static Task<bool> WaitForMachineZHeightAsync(IMachine machine, double targetZ, int timeoutMs, CancellationToken ct = default)
            => WaitForZHeightCoreAsync(machine, targetZ, timeoutMs, m => m.MachinePosition.Z, ct);

        /// <summary>
        /// Wait for motion to start, which confirms the command is executing rather than
        /// still in the planner buffer.
        /// </summary>
        private static Task<bool> WaitForMoveStartAsync(IMachine machine, double startZ, int timeoutMs, CancellationToken ct = default)
            => WaitUntilAsync(
                machine,
                m => Math.Abs(m.MachinePosition.Z - startZ) > PositionToleranceMm
                     || m.Status.StartsWith(StatusRun),
                timeoutMs,
                ct);

        /// <summary>Work Z; <see cref="WaitForMachineZHeightAsync"/> is the G53 one.</summary>
        public static Task<bool> WaitForZHeightAsync(IMachine machine, double targetZ, int timeoutMs, CancellationToken ct = default)
            => WaitForZHeightCoreAsync(machine, targetZ, timeoutMs, m => m.WorkPosition.Z, ct);

        private static Task<bool> WaitForZHeightCoreAsync(IMachine machine, double targetZ, int timeoutMs, Func<IMachine, double> getZ, CancellationToken ct)
        {
            if (timeoutMs <= 0)
            {
                timeoutMs = ZHeightWaitTimeoutMs;
            }

            return WaitUntilAsync(
                machine, m => Math.Abs(getZ(m) - targetZ) < PositionToleranceMm, timeoutMs, ct);
        }

        /// <summary>
        /// Wait for the machine to leave the door hold after CycleStart is sent. Not
        /// <see cref="WaitForIdleAsync"/>: that gives up as soon as the status reads Door,
        /// which it still does when the cycle start goes out.
        /// </summary>
        private static Task<bool> WaitForDoorReleasedAsync(
            IMachine machine, int timeoutMs, CancellationToken ct = default)
            => WaitUntilAsync(machine, m => !IsDoor(m), timeoutMs, ct, abortWhenUnavailable: false);

        /// <summary>
        /// Wait for the machine to leave <paramref name="from"/>: either out of Door, or into
        /// another door state. Not <see cref="WaitForDoorReleasedAsync"/>, because closing the
        /// enclosure moves GRBL from Door:1 to Door:0, which is still Door, so a caller
        /// watching for Door to clear waits out its whole timeout.
        /// </summary>
        /// <param name="onPoll">
        /// Runs on every poll, for a screen that redraws or reads a key. Returning true gives
        /// up on the wait.
        /// </param>
        /// <remarks>
        /// Private, because ClearDoorHoldAsync contains the one loop that waits a door state
        /// out; a screen waiting on its own would write those rules a second time.
        /// </remarks>
        private static Task<bool> WaitForDoorStateChangeAsync(
            IMachine machine, DoorState from, int timeoutMs, CancellationToken ct = default,
            Func<bool>? onPoll = null)
            => WaitUntilAsync(machine, m => GetDoorState(m) != from, timeoutMs, ct,
                abortWhenUnavailable: false, onPoll: onPoll);

        /// <summary>Returns the new status, or null on timeout.</summary>
        public static async Task<string?> WaitForStatusChangeAsync(IMachine machine, string currentStatus, int timeoutMs, CancellationToken ct = default)
        {
            var elapsed = Stopwatch.StartNew();
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

        /// <summary>
        /// Await a GRBL reply that may never arrive, because the command was rejected, and
        /// give up at the timeout; cancellation surfaces as cancellation rather than as a
        /// timeout. Pass <paramref name="machine"/> to give up as soon as it stops responding,
        /// since a machine that parks at the door mid-probe never sends the reply and the
        /// caller would otherwise wait out the whole timeout with the tool still down.
        /// </summary>
        public static async Task<T> AwaitReplyOrTimeoutAsync<T>(
            Task<T> reply, int timeoutMs, string timeoutMessage, CancellationToken ct,
            IMachine? machine = null)
        {
            // Linked source so the timer is cancelled when the reply lands. Otherwise a
            // long grid probe accumulates one live timer per point.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            Task watchdog = machine == null
                ? Task.Delay(timeoutMs, timeoutCts.Token)
                : WaitUntilAsync(machine, IsUnavailable, timeoutMs, timeoutCts.Token,
                    abortWhenUnavailable: false);

            var finished = await Task.WhenAny(reply, watchdog).ConfigureAwait(false);

            timeoutCts.Cancel();

            ct.ThrowIfCancellationRequested();

            if (finished != reply)
            {
                throw new TimeoutException(
                    machine != null && IsUnavailable(machine)
                        ? ControllerConstants.ErrorMachineNotResponding
                        : timeoutMessage);
            }

            return await reply.ConfigureAwait(false);
        }

        /// <summary>
        /// Get the machine out of Door, or report why it is still there. This one method
        /// decides which door states the operator can answer, how many refused releases are
        /// enough and which states are waited out; callers supply only how to ask, how to
        /// announce, and a way out - a run its token, a screen an onPoll.
        /// </summary>
        /// <param name="ask">
        /// Puts the closed-door question to the operator. True sends the cycle start, which
        /// restarts the spindle and moves the tool back.
        /// </param>
        /// <param name="announce">
        /// Shows a door state the operator cannot answer: an open door, or a park restore
        /// still running.
        /// </param>
        /// <param name="onPoll">
        /// Called on every poll while a state is waited out; true stops waiting and reports
        /// <see cref="DoorClearOutcome.Declined"/>.
        /// </param>
        public static async Task<DoorClearOutcome> ClearDoorHoldAsync(
            IMachine machine,
            Func<string, Task<bool>> ask,
            Action<string> announce,
            Func<bool>? onPoll = null,
            CancellationToken ct = default)
        {
            int releases = 0;

            while (true)
            {
                // The waits below return at once on a cancelled token without awaiting, so
                // without this the loop spins with nothing to yield to.
                ct.ThrowIfCancellationRequested();

                var state = GetDoorState(machine);
                if (state == DoorState.None)
                {
                    return DoorClearOutcome.Cleared;
                }

                string message = GetDoorMessage(state);

                if (!CanReleaseDoorHold(state))
                {
                    // Nothing to answer: the operator closes the door, and the restore takes
                    // as long as the machine's parking settings specify. The wait ends on the
                    // next status report that shows a different state; the budget below is
                    // only there so a door nobody closes does not hold the loop forever.
                    announce(message);

                    bool gaveUp = false;
                    await WaitForDoorStateChangeAsync(
                        machine, state, ControllerConstants.DoorResumeTimeoutMs, ct,
                        onPoll: onPoll == null ? null : () => gaveUp |= onPoll()).ConfigureAwait(false);

                    if (gaveUp)
                    {
                        return DoorClearOutcome.Declined;
                    }

                    continue;
                }

                if (releases++ >= ControllerConstants.MachineClearAttempts)
                {
                    // Answered this many times and the hold is still there, so the switch or
                    // its wiring is the problem.
                    return DoorClearOutcome.WillNotRelease;
                }

                if (!await ask(message).ConfigureAwait(false))
                {
                    return DoorClearOutcome.Declined;
                }

                // Replaces the question before the release is waited out, so the operator
                // does not sit looking at a prompt they have already answered.
                announce(ControllerConstants.DoorResumingMessage);

                var left = await ReleaseDoorHoldAsync(
                    machine, ControllerConstants.DoorResumeTimeoutMs, ct).ConfigureAwait(false);

                ControllerLog.Log("ClearDoorHoldAsync: released, left={0}, status={1}", left, machine.Status);
            }
        }

        /// <summary>
        /// Release a door hold the operator has confirmed: wait for GRBL's reading of the
        /// switch to catch up, send the cycle start, then wait for the hold to lift. The
        /// catch-up allowance is short deliberately - waiting longer for the switch means
        /// sending the cycle start to a door that has been open all along.
        /// </summary>
        /// <returns>
        /// The door state the machine was left in. None means out of Door; Resuming means
        /// the restore is still running, which is not a failure.
        /// </returns>
        public static async Task<DoorState> ReleaseDoorHoldAsync(
            IMachine machine, int timeoutMs, CancellationToken ct = default)
        {
            if (!IsDoor(machine))
            {
                return DoorState.None;
            }

            // One budget covers both waits, so the total matches the timeout asked for.
            var budget = Stopwatch.StartNew();

            if (IsDoorOpen(machine))
            {
                await WaitForDoorReadingToCatchUpAsync(machine, timeoutMs, ct).ConfigureAwait(false);

                if (IsDoorOpen(machine))
                {
                    return DoorState.Open;
                }
            }

            // Door:3 is already restoring from the park, and a second cycle start would
            // land inside that move.
            if (IsDoorWaitingForResume(machine))
            {
                ct.ThrowIfCancellationRequested();
                machine.CycleStart();
            }

            int remainingMs = Math.Max(0, timeoutMs - (int)budget.ElapsedMilliseconds);
            await WaitForDoorReleasedAsync(machine, remainingMs, ct).ConfigureAwait(false);

            return GetDoorState(machine);
        }

        /// <summary>
        /// Give GRBL's reading of the door switch a few status reports to catch up. Counted
        /// in reports, not milliseconds, because the poll interval is a setting.
        /// </summary>
        private static Task<bool> WaitForDoorReadingToCatchUpAsync(
            IMachine machine, int timeoutMs, CancellationToken ct)
        {
            long startCount = machine.StatusReportCount;

            return WaitUntilAsync(
                machine,
                m => !IsDoorOpen(m) || m.StatusReportCount - startCount >= DoorReadingCatchUpReports,
                timeoutMs,
                ct,
                abortWhenUnavailable: false);
        }

        /// <returns>
        /// True only if the machine reached Idle and is not alarmed. A machine still running
        /// or held is not ready: motion sent to it queues behind what it is already doing.
        /// </returns>
        public static async Task<bool> EnsureMachineReadyAsync(IMachine machine, int timeoutMs, CancellationToken ct = default)
        {
            if (timeoutMs <= 0)
            {
                timeoutMs = IdleWaitTimeoutMs;
            }

            // A door hold makes this return false, and nothing here clears it: the cycle
            // start that releases the hold also resumes motion, so only the operator may
            // ask for it. ControllerBase.EnsureDoorClosedAsync does that.
            bool idle = await WaitForIdleAsync(machine, timeoutMs, ct);
            return idle && !IsUnavailable(machine);
        }

        /// <summary>
        /// Sends FeedHold then SoftReset, clears any alarm that follows, and commands the
        /// spindle off. Use it when cancelling, so buffered commands cannot resume.
        /// </summary>
        /// <returns>
        /// True if the machine was holding at the door when the stop was sent, which
        /// ControllerBase.RetractToSafeZAsync needs. The soft reset below moves it from Door
        /// to Alarm, so this is the last chance to read it.
        /// </returns>
        public static async Task<bool> StopAndResetAsync(IMachine machine)
        {
            bool wasHoldingAtDoor = IsDoor(machine);

            // FeedHold and SoftReset are real-time bytes, delivered in any mode, and the
            // reset is what stops the spindle. The explicit M5 goes after them: sent while a
            // file is streaming it would be discarded.
            machine.FeedHold();
            await Task.Delay(CommandDelayMs).ConfigureAwait(false);

            machine.SoftReset();
            await Task.Delay(ResetWaitMs).ConfigureAwait(false);

            if (IsAlarm(machine))
            {
                machine.SendLine(CmdUnlock);
                await Task.Delay(CommandDelayMs).ConfigureAwait(false);
            }

            // Sent again now that the reset is back in Manual mode and any alarm is cleared,
            // so this one is not discarded.
            machine.SendLine(CmdSpindleOff);

            await WaitForIdleAsync(machine, IdleWaitTimeoutMs, CancellationToken.None);

            return wasHoldingAtDoor;
        }

        /// <summary>
        /// Opens GRBL's probe cycle, or throws. A probe move sent with the cycle closed runs
        /// with nothing watching for the trigger.
        /// </summary>
        public static void OpenProbeCycle(IMachine machine)
        {
            if (!machine.ProbeStart())
            {
                throw new InvalidOperationException(ControllerConstants.ErrorProbeCycleNotOpen);
            }
        }

        /// <summary>
        /// Writes the work offset for the named axes, such as "X0 Y0 Z0" or "Z0", and
        /// confirms GRBL took it. G10 L20 changes no state, so a refusal is the only
        /// evidence it did not land.
        /// </summary>
        /// <returns>Why the offset was not written, or null once GRBL took it.</returns>
        public static async Task<string?> ZeroWorkOffsetAsync(IMachine machine, string axes, CancellationToken ct = default)
        {
            // GRBL locks G-code out in Alarm and returns nothing at all when asleep or
            // disconnected, so the line would be dropped. A door hold is different: GRBL
            // keeps the line in its planner and runs it on the cycle start.
            var activity = GetActivity(machine);
            if (IsUnavailable(activity) && !IsDoorActivity(activity))
            {
                return ControllerConstants.ErrorWorkZeroNotWritten;
            }

            GrblRejection? refusal = null;

            void OnRejected(GrblRejection rejection)
            {
                if (rejection.Command.Contains(CmdZeroWorkOffset, StringComparison.OrdinalIgnoreCase))
                {
                    refusal = rejection;
                }
            }

            machine.CommandRejected += OnRejected;

            try
            {
                machine.SendLine(Inv($"{CmdZeroWorkOffset} {axes}"));
                await Task.Delay(CommandDelayMs, ct).ConfigureAwait(false);
                await WaitForIdleAsync(machine, IdleSettleMs, ct).ConfigureAwait(false);
            }
            finally
            {
                machine.CommandRejected -= OnRejected;
            }

            if (refusal != null)
            {
                return refusal.Value.Code == GrblRejection.LockedOut
                    ? ControllerConstants.ErrorWorkZeroNotWritten
                    : refusal.Value.Description;
            }

            // At the door the write is still in GRBL's planner, so there is nothing to
            // re-read and the $# would queue behind it.
            if (IsDoorActivity(activity))
            {
                return null;
            }

            // G10 L20 moved G54, and machine.G54Offset is only as fresh as the last $#.
            // Left stale, every map measured afterwards records the pre-zero origin.
            return await machine.RefreshWorkOffsetsAsync(WorkOffsetQueryTimeoutMs, ct)
                .ConfigureAwait(false)
                ? null
                : ControllerConstants.ErrorWorkOffsetUnknown;
        }

        /// <summary>
        /// The only code that sets `machine.IsHomed`, and it sets it only once the machine has
        /// homed. `machine.IsHoming` stays true for the duration.
        /// </summary>
        public static async Task<HomingOutcome> HomeAsync(IMachine machine, int timeoutMs, CancellationToken ct = default)
        {
            // Record a refusal while waiting. Without it the only evidence of a rejected
            // $H is that the machine stayed Idle, which looks the same as not started yet.
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

                // Do not set IsHomed for a machine that never moved: a rejected or dropped
                // $H leaves the status at Idle, so the idle wait below would pass at once.
                // Every later G53 move depends on this flag.
                string? started = await WaitForStatusChangeAsync(machine, StatusIdle, MotionStartTimeoutMs, ct);

                if (started == null)
                {
                    // Still reporting and still Idle means the $H was not accepted; gone
                    // quiet means homing is under way, since some builds stop reporting during
                    // the cycle. Counted in reports, so a slow clock cannot decide.
                    bool stillReporting = machine.StatusReportCount > reportsBefore;

                    if (stillReporting)
                    {
                        return HomingOutcome.Refused(refusal);
                    }
                }

                // Idle only counts once GRBL is reporting again. While it is quiet
                // mid-cycle the last status still reads Idle, which would pass for a
                // machine part-way through homing.
                long quietAt = machine.StatusReportCount;
                var reporting = Stopwatch.StartNew();

                while (machine.StatusReportCount == quietAt
                       && reporting.ElapsedMilliseconds < timeoutMs
                       && !ct.IsCancellationRequested)
                {
                    await Task.Delay(StatusPollIntervalMs, ct).ConfigureAwait(false);
                }

                // Sustained idle, not a single sample: homing ends with a pull-off move.
                bool success = await WaitForStableIdleAsync(machine, timeoutMs, ct);

                if (!success || !IsIdle(machine))
                {
                    // The door is the likeliest interruption and the only one the operator
                    // can act on, so report it rather than a generic refusal.
                    if (IsDoor(machine))
                    {
                        return HomingOutcome.Interrupted(IsDoorOpen(machine)
                            ? ErrorMachineDoorOpen
                            : ErrorDoorClosedDuringHoming);
                    }

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
        /// Stops all motion, clears GRBL's buffer, and optionally homes, so a bug elsewhere
        /// cannot leave the machine executing commands.
        /// </summary>
        public static async Task SafeCompletionAsync(IMachine machine, bool homeAfter = false, CancellationToken ct = default)
        {
            // Same stop sequence as an abort, so the two cannot drift apart.
            await StopAndResetAsync(machine).ConfigureAwait(false);

            if (homeAfter && !(await HomeAsync(machine, HomingTimeoutMs, ct).ConfigureAwait(false)).Success)
            {
                ControllerLog.Log("SafeCompletion: post-job homing did not complete");
            }
        }

        /// <summary>Retracts Z with G53, so the work offset does not move the target.</summary>
        /// <returns>
        /// True only if Z is confirmed at the target. False means the retract did not
        /// happen (rejected, or timed out), so the caller must not start any XY motion:
        /// the tool is still down.
        /// </returns>
        public static async Task<bool> SafetyRetractZAsync(IMachine machine, double targetMachineZ, int timeoutMs, CancellationToken ct = default)
        {
            if (timeoutMs <= 0)
            {
                timeoutMs = ZHeightWaitTimeoutMs;
            }

            double startZ = machine.MachinePosition.Z;

            machine.SendLine(CmdAbsolute);
            machine.SendLine(Inv($"{CmdMachineCoords} {CmdRapidMove} Z{targetMachineZ:F3}"));

            if (Math.Abs(startZ - targetMachineZ) < PositionToleranceMm)
            {
                await Task.Delay(CommandDelayMs, ct).ConfigureAwait(false);
                await WaitForIdleAsync(machine, IdleWaitTimeoutMs, ct);
                return Math.Abs(machine.MachinePosition.Z - targetMachineZ) < PositionToleranceMm;
            }

            // Arrival is what is reported, not the start: a move that never started fails
            // the height check anyway.
            await WaitForMoveStartAsync(machine, startZ, timeoutMs, ct);

            return await WaitForMachineZHeightAsync(machine, targetMachineZ, timeoutMs, ct);
        }
    }
}
