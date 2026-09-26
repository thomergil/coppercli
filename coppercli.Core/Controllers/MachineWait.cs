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

        public static bool IsAsleep(IMachine machine) => machine.Status == StatusSleep;

        /// <summary>
        /// Door closed, machine parked, waiting for a cycle start.
        /// </summary>
        private static bool IsDoorWaitingForResume(IMachine machine) =>
            IsDoor(machine) && machine.StatusSubState == DoorSubStateClosed;

        /// <summary>
        /// GRBL is restoring from the park after a cycle start. A second cycle start here
        /// would interrupt that move.
        /// </summary>
        private static bool IsDoorResuming(IMachine machine) =>
            IsDoor(machine) && machine.StatusSubState == DoorSubStateResuming;

        /// <summary>GRBL is moving to the park position; see <see cref="DoorState.Retracting"/>.</summary>
        private static bool IsDoorRetracting(IMachine machine) =>
            IsDoor(machine) && machine.StatusSubState == DoorSubStateRetracting;

        /// <summary>
        /// The door may be open. True for any Door substate that is not waiting, resuming or
        /// retracting, so an unrecognized substate counts as open.
        /// </summary>
        private static bool IsDoorOpen(IMachine machine) =>
            IsDoor(machine) && !IsDoorWaitingForResume(machine) && !IsDoorResuming(machine)
                && !IsDoorRetracting(machine);

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

            if (IsDoorRetracting(machine))
            {
                return DoorState.Retracting;
            }

            return DoorState.WaitingForResume;
        }

        /// <summary>
        /// True where a cycle start would end the hold. That is the only door state with
        /// anything to ask the operator; every other door state is waited out.
        /// </summary>
        public static bool CanReleaseDoorHold(DoorState state) =>
            state == DoorState.WaitingForResume;

        /// <inheritdoc cref="CanReleaseDoorHold(DoorState)"/>
        public static bool CanReleaseDoorHold(IMachine machine) => CanReleaseDoorHold(GetDoorState(machine));

        public static string GetDoorMessage(DoorState state) => state switch
        {
            DoorState.Open => ControllerConstants.DoorOpenPrompt,
            DoorState.Resuming => ControllerConstants.DoorResumingMessage,
            DoorState.Retracting => ControllerConstants.DoorRetractingMessage,
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
                case DoorState.Retracting: return MachineActivity.DoorRetracting;
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

            if (IsAsleep(machine))
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
        /// Read the machine mode to detect an open probe cycle. A single Z probe has no
        /// controller, so controller state alone would report the machine as available.
        /// </summary>
        public static bool IsProbeCycleOpen(IMachine machine) =>
            machine.Mode == OperatingMode.Probe;

        /// <summary>
        /// The door activities, listed once so callers that treat the door differently do
        /// not repeat the list.
        /// </summary>
        public static bool IsDoorActivity(MachineActivity activity) =>
            activity is MachineActivity.DoorOpen
                or MachineActivity.DoorRetracting
                or MachineActivity.DoorHolding
                or MachineActivity.DoorResuming;

        /// <summary>
        /// The machine will not act on a command until the operator clears an alarm, closes
        /// the enclosure, waits out a park retract or restore, or resets it out of $SLP.
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
        /// Why the door stops a resume or a release, or null when the machine is not at the
        /// door. A machine holding at the door takes lines into its planner and runs them when
        /// the hold is released, so a resume waits for the enclosure to be cleared first. A
        /// retract or restore still moving returns its own message, not the door refusal.
        /// </summary>
        public static string? GetDoorRefusal(DoorState state) => state switch
        {
            DoorState.None => null,
            DoorState.Open => ControllerConstants.ErrorDoorBlocksResume,
            DoorState.WaitingForResume => ControllerConstants.ErrorDoorClosedStillHolding,
            DoorState.Retracting or DoorState.Resuming => GetDoorMessage(state),
            _ => ControllerConstants.ErrorDoorBlocksResume
        };

        /// <inheritdoc cref="GetDoorRefusal(DoorState)"/>
        public static string? GetDoorRefusal(IMachine machine) => GetDoorRefusal(GetDoorState(machine));

        /// <summary>
        /// Poll until the condition holds, the timeout expires, or the caller cancels.
        /// Set <paramref name="abortWhenUnavailable"/> to false when waiting for an
        /// alarmed or held machine to change state.
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
                // Check input first so a key press ends the wait on the same poll.
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
        /// Wait for the door hold to end after CycleStart. <see cref="WaitForIdleAsync"/>
        /// would stop immediately because GRBL still reports Door when CycleStart is sent.
        /// </summary>
        private static Task<bool> WaitForDoorReleasedAsync(
            IMachine machine, int timeoutMs, CancellationToken ct = default)
            => WaitUntilAsync(machine, m => !IsDoor(m), timeoutMs, ct, abortWhenUnavailable: false);

        /// <summary>
        /// Wait for a change from <paramref name="from"/>, including a change between door
        /// states. Closing the enclosure changes Door:1 to Door:0 without ending the hold.
        /// </summary>
        /// <param name="onPoll">
        /// Runs on every poll, for a screen that redraws or reads a key. Returning true gives
        /// up on the wait.
        /// </param>
        /// <remarks>
        /// Only ClearDoorHoldAsync decides how to handle each door state.
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
            long timeoutLimitMs = timeoutMs;

            while (elapsed.ElapsedMilliseconds < timeoutLimitMs && !ct.IsCancellationRequested)
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
        /// Wait for a GRBL reply or throw on timeout. Pass <paramref name="machine"/> to
        /// stop waiting when a door hold or alarm prevents a probe reply; caller
        /// cancellation remains an <see cref="OperationCanceledException"/>.
        /// </summary>
        public static async Task<T> AwaitReplyOrTimeoutAsync<T>(
            Task<T> reply, int timeoutMs, string timeoutMessage, CancellationToken ct,
            IMachine? machine = null)
        {
            // Cancel the timer after a reply so a long grid probe does not keep one timer
            // active per completed point.
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
        /// Ask the operator to release a door hold when GRBL permits it. This method
        /// handles door states and retry limits; callers provide prompts and cancellation.
        /// </summary>
        /// <param name="ask">
        /// Puts the closed-door question to the operator. True sends the cycle start, which
        /// restarts the spindle and moves the tool back.
        /// </param>
        /// <param name="announce">
        /// Shows a door state the operator cannot answer (see <see cref="CanReleaseDoorHold(DoorState)"/>).
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
                    // Wait for the next door state while the operator closes the door or
                    // GRBL restores the parked position. The timeout prevents an indefinite wait.
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
        /// Past the door switch and the park move: a cycle start ends the hold, or the restore
        /// it starts is already running.
        /// </summary>
        private static bool IsDoorReleasable(DoorState state) =>
            state is DoorState.WaitingForResume or DoorState.Resuming;

        /// <summary>
        /// Wait briefly for GRBL to report the closed switch, send CycleStart, then wait
        /// for the hold to end. Returns Open or Retracting without a cycle start if, after that
        /// wait, the switch reads open or the park move is still running.
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

            // Include the switch reading wait in the total timeout.
            var elapsed = Stopwatch.StartNew();

            if (!IsDoorReleasable(GetDoorState(machine)))
            {
                await WaitForDoorReadingToCatchUpAsync(machine, timeoutMs, ct).ConfigureAwait(false);

                // GRBL takes no cycle start until the park move has ended and the door reads closed.
                var state = GetDoorState(machine);
                if (!IsDoorReleasable(state))
                {
                    return state;
                }
            }

            // Door:3 is already restoring from the park, and a second cycle start would
            // interrupt that move.
            if (IsDoorWaitingForResume(machine))
            {
                ct.ThrowIfCancellationRequested();
                machine.CycleStart();
            }

            int remainingMs = Math.Max(0, timeoutMs - (int)elapsed.ElapsedMilliseconds);
            await WaitForDoorReleasedAsync(machine, remainingMs, ct).ConfigureAwait(false);

            return GetDoorState(machine);
        }

        /// <summary>
        /// Give GRBL's reading of the door switch time to catch up with the operator's
        /// answer: the substate arrives on the status poll, so the report received when they
        /// answer predates them closing the door.
        /// </summary>
        private static Task<bool> WaitForDoorReadingToCatchUpAsync(
            IMachine machine, int timeoutMs, CancellationToken ct)
        {
            long startCount = machine.StatusReportCount;

            return WaitUntilAsync(
                machine,
                m => IsDoorReleasable(GetDoorState(m))
                    || m.StatusReportCount - startCount >= DoorReadingCatchUpReports,
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
            // reset is what stops the spindle. The lines below go after them: sent while a
            // file is streaming they would be discarded.
            machine.FeedHold();
            await Task.Delay(CommandDelayMs).ConfigureAwait(false);

            machine.SoftReset();

            // A reset that interrupts a cycle leaves GRBL alarmed (one during homing always
            // does), and GRBL then refuses every G-code line until $X clears the lock. $X does
            // nothing on a machine that is not alarmed, so it is sent every time. The line is
            // held until GRBL is back from the reset, so its timeout covers that wait too.
            await machine.SendAsync(CmdUnlock, ResetAnnounceTimeoutMs + CommandAnswerTimeoutMs)
                .ConfigureAwait(false);

            // If GRBL refuses this, the retract that follows fails its height check and the
            // run reports the lift as unconfirmed.
            await machine.SendAsync(CmdSpindleOff, CommandAnswerTimeoutMs).ConfigureAwait(false);

            // Status reads Alarm until the first status report after the unlock, and the
            // retract and the next job's start check both read it, so wait for Idle. The wait
            // starts in Alarm, so it must not stop on Alarm.
            await WaitUntilAsync(machine, IsIdle, IdleWaitTimeoutMs, CancellationToken.None,
                abortWhenUnavailable: false).ConfigureAwait(false);

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
        /// confirms GRBL accepted it.
        /// </summary>
        /// <returns>Why the offset was not written, or null once GRBL accepted it.</returns>
        public static async Task<string?> ZeroWorkOffsetAsync(IMachine machine, string axes, CancellationToken ct = default)
        {
            // G10 L20 changes nothing a status report shows, so GRBL's answer is the only
            // evidence it ran.
            var reply = await machine.SendAsync(
                Inv($"{CmdZeroWorkOffset} {axes}"), CommandAnswerTimeoutMs, ct).ConfigureAwait(false);

            switch (reply.Answer)
            {
                case GrblAnswer.Ok:
                    break;

                // A refusal other than the alarm lock-out has a reason worth showing in
                // GRBL's own words.
                case GrblAnswer.Refused when reply.Rejection is { } rejection
                    && rejection.Code != GrblRejection.LockedOut:
                    return DescribeRefusal(rejection);

                case GrblAnswer.Refused:
                case GrblAnswer.NotSent:
                    return ControllerConstants.ErrorWorkZeroNotWritten;

                // Abandoned or no answer: GRBL may have written it, or may yet.
                default:
                    return ControllerConstants.ErrorWorkZeroUnconfirmed;
            }

            // GRBL answers $# only when idle or alarmed, so at a door hold the re-read would
            // be refused. The origin it has just confirmed is the one to expect.
            if (IsDoor(machine))
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
        /// The only code that sets <see cref="IMachine.IsHomed"/> true, and it sets it on
        /// GRBL's own answer that the cycle finished. <see cref="IMachine.IsHoming"/> stays
        /// true for the duration, including while the operator is asked about the door.
        /// </summary>
        /// <param name="retryAfterDoorCloses">
        /// Asked when GRBL refused $H because the door is open. True sends $H again. Null
        /// returns that refusal to the caller.
        /// </param>
        /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
        public static async Task<HomingOutcome> HomeAsync(
            IMachine machine, int timeoutMs, CancellationToken ct = default,
            Func<Task<bool>>? retryAfterDoorCloses = null)
        {
            machine.BeginHoming();

            try
            {
                while (true)
                {
                    var outcome = await SendHomeAsync(machine, timeoutMs, ct).ConfigureAwait(false);

                    if (!outcome.DoorOpen || retryAfterDoorCloses == null
                        || !await retryAfterDoorCloses().ConfigureAwait(false))
                    {
                        return outcome;
                    }
                }
            }
            finally
            {
                machine.EndHoming();
            }
        }

        private static async Task<HomingOutcome> SendHomeAsync(IMachine machine, int timeoutMs, CancellationToken ct)
        {
            // GRBL answers $H only when the cycle is over: ok if the machine homed, an
            // error if it refused the command, nothing if it alarmed part-way.
            var reply = await machine.SendAsync(CmdHome, timeoutMs, ct).ConfigureAwait(false);

            if (reply.Ran)
            {
                machine.IsHomed = true;
                return HomingOutcome.Homed;
            }

            return reply.Answer switch
            {
                GrblAnswer.Refused or GrblAnswer.NotSent => HomingOutcome.Refused(reply.Rejection),
                GrblAnswer.Abandoned => HomingOutcome.Interrupted(ErrorHomingInterrupted),
                _ => HomingOutcome.Interrupted(ControllerConstants.ErrorMachineNotResponding)
            };
        }

        /// <summary>Sends $X and reads GRBL's answer to it.</summary>
        /// <returns>Null once the machine is unlocked, otherwise why it is not.</returns>
        public static async Task<string?> UnlockAsync(IMachine machine, CancellationToken ct = default)
        {
            var reply = await machine.SendAsync(CmdUnlock, CommandAnswerTimeoutMs, ct).ConfigureAwait(false);

            return reply.Answer switch
            {
                GrblAnswer.Ok => null,
                GrblAnswer.Refused when reply.Rejection is { } rejection => DescribeRefusal(rejection),
                GrblAnswer.NotSent => ControllerConstants.ErrorCommandNotSent,
                _ => ControllerConstants.ErrorMachineNotResponding
            };
        }

        /// <summary>
        /// The operator's words for a refusal. GRBL's own text for an open door does not say
        /// what to do about it.
        /// </summary>
        public static string DescribeRefusal(GrblRejection rejection) =>
            rejection.Code == GrblRejection.DoorOpen
                ? ControllerConstants.ErrorDoorOpenRefused
                : rejection.Description;

        /// <summary>
        /// Stops all motion, clears GRBL's buffer, and optionally homes, so a bug elsewhere
        /// cannot leave the machine executing commands.
        /// </summary>
        public static async Task SafeCompletionAsync(IMachine machine, bool homeAfter = false, CancellationToken ct = default)
        {
            // Use the abort stop sequence for the same buffer and motion handling.
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
