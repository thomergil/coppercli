#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using coppercli.Core.Communication;
using static coppercli.Core.Controllers.ControllerConstants;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// The state machine every workflow runs on. `ValidTransitions` below is the whole table,
    /// and paused, active and finished are derived from <see cref="State"/> rather than stored
    /// beside it.
    ///
    /// One instance serves the whole session, so every field describing the current run is
    /// cleared in <see cref="ResetRunState"/> before each run starts. That is why
    /// <see cref="ResetRunState"/> is abstract rather than virtual: a field a subclass forgets
    /// is read by the next run.
    /// </summary>
    public abstract class ControllerBase : IController
    {
        private static readonly Dictionary<ControllerState, ControllerState[]> ValidTransitions = new()
        {
            [ControllerState.Idle] = new[] { ControllerState.Initializing },
            // Initializing can wait on the operator: the enclosure must be closed before
            // the machine will home.
            [ControllerState.Initializing] = new[] { ControllerState.Running, ControllerState.WaitingForUserInput, ControllerState.Failed, ControllerState.Cancelled },
            [ControllerState.Running] = new[] { ControllerState.Paused, ControllerState.WaitingForUserInput, ControllerState.Completing, ControllerState.Failed, ControllerState.Cancelled },
            // Paused and WaitingForUserInput can fail, because cleanup runs from there
            // too, and can reach each other: RequestUserInputAsync returns to whatever
            // state it interrupted.
            [ControllerState.Paused] = new[] { ControllerState.Running, ControllerState.WaitingForUserInput, ControllerState.Failed, ControllerState.Cancelled },
            [ControllerState.WaitingForUserInput] = new[] { ControllerState.Initializing, ControllerState.Running, ControllerState.Paused, ControllerState.Failed, ControllerState.Cancelled },
            // Completing can still be cancelled: Stop during the final retract is normal.
            [ControllerState.Completing] = new[] { ControllerState.Completed, ControllerState.Failed, ControllerState.Cancelled },
            [ControllerState.Completed] = new[] { ControllerState.Idle },
            [ControllerState.Failed] = new[] { ControllerState.Idle },
            [ControllerState.Cancelled] = new[] { ControllerState.Idle },
        };

        private ControllerState _state = ControllerState.Idle;
        private readonly object _stateLock = new();

        public ControllerState State
        {
            get
            {
                lock (_stateLock)
                {
                    return _state;
                }
            }
        }

        public bool IsActive => IsActiveState(State);

        /// <summary><see cref="IsActive"/> for a state already read.</summary>
        public static bool IsActiveState(ControllerState state)
        {
            return state == ControllerState.Initializing
                || state == ControllerState.Running
                || state == ControllerState.Paused;
        }

        public bool IsPaused => IsPausedState(State);

        /// <summary><see cref="IsPaused"/> for a state already read.</summary>
        public static bool IsPausedState(ControllerState state) => state == ControllerState.Paused;

        /// <summary>
        /// The states a run can end in. These are exactly the states <see cref="Reset"/>
        /// accepts besides Idle.
        /// </summary>
        public static bool IsFinishedState(ControllerState state)
        {
            return state == ControllerState.Completed
                || state == ControllerState.Failed
                || state == ControllerState.Cancelled;
        }

        public bool HasFinished => IsFinishedState(State);

        /// <summary><see cref="IsRunInProgress"/> for a state already read.</summary>
        public static bool IsRunInProgressState(ControllerState state) =>
            state != ControllerState.Idle && !IsFinishedState(state);

        /// <summary>
        /// True while a run is under way in any sense: initializing, moving, paused, waiting
        /// on the operator, or finishing. <see cref="IsActive"/> is narrower - whether the
        /// machine is being driven - and a run parked at a prompt still holds the machine.
        /// </summary>
        public bool IsRunInProgress => IsRunInProgressState(State);

        /// <summary>
        /// True while a run is waiting on the operator: a tool change, or a program pause.
        /// Not active, because nothing is moving, but the job is not over.
        /// </summary>
        public static bool IsWaitingForOperatorState(ControllerState state) =>
            state == ControllerState.WaitingForUserInput;

        protected abstract IMachine Machine { get; }

        public event Action<ControllerState>? StateChanged;
        public event Action<ProgressInfo>? ProgressChanged;
        public event Action<UserInputRequest>? UserInputRequired;
        public event Action<ControllerError>? ErrorOccurred;

        /// <summary>
        /// Throws <see cref="InvalidControllerStateException"/> when the table does not allow
        /// the move. StateChanged fires synchronously, so the handler runs before this returns.
        /// </summary>
        protected void TransitionTo(ControllerState newState)
        {
            if (!TryTransitionTo(newState, out var from))
            {
                throw new InvalidControllerStateException(
                    string.Format(ErrorInvalidTransition, from, newState));
            }
        }

        /// <summary>
        /// For a transition another thread may already have made. The test and the assignment
        /// happen under one hold of the lock, so nothing can land between them.
        /// </summary>
        protected bool TryTransitionTo(ControllerState newState) => TryTransitionTo(newState, out _);

        /// <param name="from">
        /// The state before the call, whether or not the move was allowed. Read under the
        /// lock, so a refusal names the state that refused it.
        /// </param>
        /// <inheritdoc cref="TryTransitionTo(ControllerState)"/>
        private bool TryTransitionTo(ControllerState newState, out ControllerState from)
        {
            lock (_stateLock)
            {
                from = _state;

                if (!IsValidTransition(_state, newState))
                {
                    return false;
                }

                _state = newState;
            }

            // Logged and raised outside the lock: a handler that calls back in would deadlock.
            ControllerLog.Log(LogStateTransition, GetType().Name, from, newState);
            StateChanged?.Invoke(newState);
            return true;
        }

        protected static bool IsValidTransition(ControllerState from, ControllerState to)
        {
            return ValidTransitions.TryGetValue(from, out var validTargets) &&
                   Array.IndexOf(validTargets, to) >= 0;
        }

        protected void EmitProgress(ProgressInfo progress)
        {
            ProgressChanged?.Invoke(progress);
        }

        protected void EmitError(ControllerError error)
        {
            ErrorOccurred?.Invoke(error);
        }

        /// <summary>
        /// Emit an error from an exception. A workflow's own refusal (InvalidOperationException
        /// or TimeoutException) reaches the operator unchanged; anything else is logged and
        /// the operator is told the run stopped.
        /// </summary>
        protected void EmitError(Exception ex, bool isFatal = true)
        {
            ControllerLog.Log("{0} run failed: {1}", GetType().Name, ex);

            EmitError(new ControllerError(
                ControllerConstants.ShowableMessage(ex), ex, isFatal));
        }

        /// <summary>
        /// Block while the run is paused, returning once it resumes or the token is
        /// cancelled. Every workflow that can pause mid-step waits here, so the poll
        /// interval is defined once.
        /// </summary>
        protected async Task WaitWhilePausedAsync(CancellationToken ct)
        {
            while (IsPaused && !ct.IsCancellationRequested)
            {
                await Task.Delay(Util.Constants.StatusPollIntervalMs, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Raise a prompt, wait for the answer and return it. The controller sits in
        /// WaitingForUserInput meanwhile and goes back to whatever state the prompt interrupted.
        /// </summary>
        protected async Task<string> RequestUserInputAsync(
            string title,
            string message,
            string[] options,
            CancellationToken ct,
            bool isDoorPrompt = false)
        {
            // Return to whatever this interrupted, not always Running: a prompt can be
            // raised while the run is still Initializing.
            var resumeTo = State;

            // Inline on purpose: answering resumes the run on the answering thread, so a
            // workflow publishes its next prompt from inside the call that answers this
            // one. ControllerBaseTests checks this.
            var tcs = new TaskCompletionSource<string>();

            var request = new UserInputRequest
            {
                Title = title,
                Message = message,
                Options = options,
                IsDoorPrompt = isDoorPrompt,
                OnResponse = response => tcs.TrySetResult(response)
            };

            // One read: every front end unsubscribes from a finally on another thread, so a
            // second read of the event field can be null after the first was not.
            var handler = UserInputRequired;
            if (handler == null)
            {
                throw new InvalidOperationException(ErrorNoPromptHandler);
            }

            TransitionTo(ControllerState.WaitingForUserInput);
            handler.Invoke(request);

            using var registration = ct.Register(() => tcs.TrySetCanceled());
            var response = await tcs.Task;

            // Another thread may have stopped the run while the prompt was up, and a
            // cancelled run must not be dragged back to Running.
            TryTransitionTo(resumeTo);
            return response;
        }

        /// <summary>
        /// Block until the enclosure is closed and the door hold released, showing which
        /// door state the machine is in.
        /// </summary>
        /// <param name="operatorJustAgreed">
        /// True when the caller has this moment taken a Continue from the operator, which
        /// releases a door already closed and holding without asking again. It covers that one
        /// release only, so a door opened again afterwards is put to them as usual.
        /// </param>
        /// <exception cref="OperationCanceledException">The operator chose to abort.</exception>
        protected async Task EnsureDoorClosedAsync(
            CancellationToken ct, bool operatorJustAgreed = false)
        {
            bool announced = false;

            bool agreementCovers = operatorJustAgreed
                && MachineWait.GetDoorState(Machine) == DoorState.WaitingForResume;

            try
            {
                var outcome = await MachineWait.ClearDoorHoldAsync(
                    Machine,
                    // A closed door is the only door state the operator can answer, because
                    // the cycle start restarts the spindle. Sent as a prompt and not also as
                    // progress, or the screens draw the same sentence twice.
                    ask: async message =>
                    {
                        if (agreementCovers)
                        {
                            agreementCovers = false;
                            ControllerLog.Log(
                                "{0}: releasing the door on the Continue just given",
                                GetType().Name);
                            return true;
                        }

                        string response = await RequestUserInputAsync(
                            string.Empty,
                            message,
                            new[] { OptionContinue, OptionAbort },
                            ct,
                            isDoorPrompt: true).ConfigureAwait(false);

                        // An abort ends the run here, while the machine is still at the
                        // door. Unwind it later and the retract is queued against a hold
                        // that has since gone.
                        if (response != OptionContinue)
                        {
                            throw new OperationCanceledException();
                        }

                        return true;
                    },
                    announce: message =>
                    {
                        // Announced means the enclosure has moved off the state the operator
                        // answered for - it is open, or still restoring. Their agreement
                        // described the machine as it was then, so it does not carry across
                        // to the hold that follows: ask before releasing that one.
                        agreementCovers = false;

                        EmitProgress(new ProgressInfo(PhaseWaitingForOperator, 0, message));
                        announced = true;
                    },
                    ct: ct).ConfigureAwait(false);

                // Continue only for Cleared. New enum values stop the run by default.
                if (outcome != DoorClearOutcome.Cleared)
                {
                    throw outcome == DoorClearOutcome.WillNotRelease
                        ? new InvalidOperationException(ErrorDoorWillNotRelease)
                        : new OperationCanceledException();
                }

            }
            finally
            {
                // A screen holds the last message until another arrives, and on the probe's
                // path the next one waits for the safety retract. Withdrawn on every exit,
                // including the abort, so nothing asks for a door while the tool is moving.
                if (announced)
                {
                    EmitProgress(new ProgressInfo(PhaseDoorCleared, 0, string.Empty));
                }
            }
        }

        /// <summary>
        /// Do not send a retract while the machine holds at the door: GRBL would execute it
        /// when the hold is released. Use a separate timeout because the run's token has
        /// already been cancelled.
        /// </summary>
        /// <param name="lift">Sends the move and returns true once the tool is there.</param>
        /// <param name="timeoutMs">How long to wait for it.</param>
        protected async Task<bool> RetractToSafeZAsync(Func<CancellationToken, Task<bool>> lift, int timeoutMs)
        {
            if (MachineWait.IsDoor(Machine))
            {
                ControllerLog.Log("{0}: holding at the door, not queueing a retract", GetType().Name);
                return false;
            }

            using var timeoutCts = new CancellationTokenSource(timeoutMs);

            try
            {
                return await lift(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A timeout leaves the retract position unconfirmed.
                return false;
            }
        }

        /// <summary>Retract to a machine Z, used where there is no work height to use.</summary>
        /// <inheritdoc cref="RetractToSafeZAsync(Func{CancellationToken, Task{bool}}, int)"/>
        protected Task<bool> RetractToSafeZAsync(double clearanceMachineZ, int timeoutMs) =>
            RetractToSafeZAsync(
                ct => MachineWait.SafetyRetractZAsync(
                    Machine, clearanceMachineZ, Util.Constants.ZHeightWaitTimeoutMs, ct),
                timeoutMs);

        /// <summary>
        /// Lift the tool after a stop and report an unconfirmed lift. A stop at the door
        /// leaves the tool's position unknown after the soft reset clears the hold.
        /// </summary>
        /// <param name="wasHoldingAtDoor">What <see cref="MachineWait.StopAndResetAsync"/> returned.</param>
        /// <inheritdoc cref="RetractToSafeZAsync(Func{CancellationToken, Task{bool}}, int)"/>
        protected async Task LiftAfterStopAsync(
            bool wasHoldingAtDoor, Func<CancellationToken, Task<bool>> lift, int timeoutMs)
        {
            if (wasHoldingAtDoor || !await RetractToSafeZAsync(lift, timeoutMs).ConfigureAwait(false))
            {
                EmitError(new ControllerError(
                    ControllerConstants.ErrorStopRetractFailed, null, IsFatal: false));
            }
        }

        /// <inheritdoc cref="LiftAfterStopAsync(bool, Func{CancellationToken, Task{bool}}, int)"/>
        protected Task LiftAfterStopAsync(bool wasHoldingAtDoor, double clearanceMachineZ, int timeoutMs) =>
            LiftAfterStopAsync(
                wasHoldingAtDoor,
                ct => MachineWait.SafetyRetractZAsync(
                    Machine, clearanceMachineZ, Util.Constants.ZHeightWaitTimeoutMs, ct),
                timeoutMs);

        /// <summary>Every run ends through here.</summary>
        /// <param name="betweenStopAndLift">
        /// Run after the machine has stopped and before the lift, for a controller with
        /// something to undo while nothing is moving.
        /// </param>
        /// <inheritdoc cref="LiftAfterStopAsync(bool, Func{CancellationToken, Task{bool}}, int)"/>
        protected async Task StopAndLiftAsync(
            Func<CancellationToken, Task<bool>> lift, int timeoutMs, Func<Task>? betweenStopAndLift = null)
        {
            bool wasHoldingAtDoor = await MachineWait.StopAndResetAsync(Machine).ConfigureAwait(false);

            if (betweenStopAndLift != null)
            {
                await betweenStopAndLift().ConfigureAwait(false);
            }

            await LiftAfterStopAsync(wasHoldingAtDoor, lift, timeoutMs).ConfigureAwait(false);
        }

        /// <summary>
        /// The same, lifting to a machine Z. Use this where the work frame may not be set,
        /// since a work-coordinate lift could then be a descent.
        /// </summary>
        /// <inheritdoc cref="StopAndLiftAsync(Func{CancellationToken, Task{bool}}, int, Func{Task})"/>
        protected Task StopAndLiftAsync(
            double clearanceMachineZ, int timeoutMs, Func<Task>? betweenStopAndLift = null) =>
            StopAndLiftAsync(
                ct => MachineWait.SafetyRetractZAsync(
                    Machine, clearanceMachineZ, Util.Constants.ZHeightWaitTimeoutMs, ct),
                timeoutMs,
                betweenStopAndLift);

        /// <summary>Called by StartAsync once the state allows a run.</summary>
        protected abstract Task RunAsync(CancellationToken ct);

        /// <summary>Called by StopAsync, and again on the cancel and error paths.</summary>
        protected abstract Task CleanupAsync();

        /// <summary>
        /// Clear every field that describes the run rather than the machine: a work offset
        /// still shifted in GRBL, or a probe grid the operator expects to keep, outlives the
        /// run that set it and stays. Assign backing fields directly rather than the
        /// event-raising Phase properties, so a reset does not put a raw enum name on a screen.
        /// </summary>
        protected abstract void ResetRunState();

        /// <summary>
        /// The run StartAsync is executing: how to stop it, and when it has finished. Null
        /// between runs.
        /// </summary>
        private sealed record RunInProgress(CancellationTokenSource Stop, TaskCompletionSource Ended);

        private RunInProgress? _run;

        public async Task StartAsync(CancellationToken ct = default)
        {
            if (State != ControllerState.Idle)
            {
                throw new InvalidControllerStateException(
                    string.Format(ErrorCannotStart, State));
            }

            // Here as well as in Reset(), so a run starts clean whether or not the caller
            // reset the controller after the last one.
            ResetRunState();

            var run = new RunInProgress(
                CancellationTokenSource.CreateLinkedTokenSource(ct),
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            Volatile.Write(ref _run, run);

            try
            {
                await RunToTheEndAsync(run.Stop.Token);
            }
            finally
            {
                Volatile.Write(ref _run, null);
                run.Ended.TrySetResult();
                run.Stop.Dispose();
            }
        }

        private async Task RunToTheEndAsync(CancellationToken ct)
        {
            try
            {
                TransitionTo(ControllerState.Initializing);
                await RunAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // CleanupAsync is idempotent, so this is safe even when StopAsync ran it.
                try
                {
                    await CleanupAsync();
                }
                catch
                {
                    // A cleanup failure must not replace the cancellation the operator asked for.
                }

                if (!HasFinished)
                {
                    TransitionTo(ControllerState.Cancelled);
                }
            }
            catch (Exception ex)
            {
                try
                {
                    await CleanupAsync();
                }
                catch
                {
                    // A cleanup failure must not replace the error that stopped the run.
                }

                EmitError(ex);
                if (!HasFinished)
                {
                    TransitionTo(ControllerState.Failed);
                }
            }

            // A run that returns without reaching a terminal state leaves the controller
            // claiming the machine, which every front end reads as still running. Finished
            // here so HasFinished is true once this task completes.
            if (!HasFinished && State != ControllerState.Idle)
            {
                ControllerLog.Log("{0}.RunAsync returned in {1} without finishing the run",
                    GetType().Name, State);
                TransitionTo(ct.IsCancellationRequested
                    ? ControllerState.Cancelled
                    : ControllerState.Failed);
            }
        }

        /// <inheritdoc/>
        public async Task ReleaseAsync()
        {
            if (State == ControllerState.Idle)
            {
                return;
            }

            if (!HasFinished)
            {
                try
                {
                    await StopAsync();
                }
                catch (Exception ex)
                {
                    // Releasing exists so the next run can start, so a cleanup that throws
                    // must not leave the controller claiming the machine.
                    ControllerLog.Log("{0}: cleanup threw while releasing: {1}",
                        GetType().Name, ex.Message);
                    TryTransitionTo(ControllerState.Cancelled);
                }
            }

            Reset();
        }

        public virtual void Pause()
        {
            if (State != ControllerState.Running)
            {
                throw new InvalidControllerStateException(
                    string.Format(ErrorCannotPause, State));
            }
            TransitionTo(ControllerState.Paused);
        }

        public virtual void Resume()
        {
            if (ResumeIsBlocked())
            {
                return;
            }

            TransitionTo(ControllerState.Running);
        }

        /// <summary>
        /// Throws when the run is not paused at all, because that is a caller mistake rather
        /// than a machine state. A machine that refuses the resume is reported instead and the
        /// run stays paused, since the terminal calls Resume straight from a key press with
        /// nothing to catch an exception.
        /// </summary>
        protected bool ResumeIsBlocked()
        {
            if (State != ControllerState.Paused)
            {
                throw new InvalidControllerStateException(
                    string.Format(ErrorCannotResume, State));
            }

            if (MachineWait.GetResumeBlocker(Machine) is string blocked)
            {
                EmitError(new ControllerError(blocked, IsFatal: false));
                return true;
            }

            return false;
        }

        public async Task StopAsync()
        {
            if (State == ControllerState.Idle)
            {
                return;
            }

            // A run in progress is stopped the way the front ends stop it: cancel it and let
            // it tear itself down. Cleaning up here as well would reset the machine a second
            // time under that teardown, and the run would go on reading a machine this reset.
            if (Volatile.Read(ref _run) is RunInProgress run)
            {
                try
                {
                    run.Stop.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // It finished between the read and the cancel.
                }

                await run.Ended.Task;
                return;
            }

            try
            {
                await CleanupAsync();
            }
            finally
            {
                if (!HasFinished)
                {
                    TransitionTo(ControllerState.Cancelled);
                }
            }
        }

        public virtual void Reset()
        {
            var currentState = State;
            if (IsFinishedState(currentState))
            {
                TransitionTo(ControllerState.Idle);
            }
            else if (currentState != ControllerState.Idle)
            {
                throw new InvalidControllerStateException(
                    string.Format(ErrorCannotReset, State));
            }

            ResetRunState();
        }
    }
}
