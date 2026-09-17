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
    /// Abstract base class for workflow controllers implementing FSM logic.
    /// Enforces valid state transitions and provides common event infrastructure.
    ///
    /// One instance serves the whole session, so every field describing the current run is
    /// cleared in <see cref="ResetRunState"/> before each run starts. Paused, active and
    /// finished are derived from <see cref="State"/>.
    /// </summary>
    public abstract class ControllerBase : IController
    {
        // =========================================================================
        // State transition table - defines all valid transitions
        // =========================================================================

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

        // =========================================================================
        // State
        // =========================================================================

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

        /// <summary>
        /// True while a run is under way.
        /// </summary>
        public bool IsActive => IsActiveState(State);

        /// <summary>
        /// <see cref="IsActive"/> for a state already in hand.
        /// </summary>
        public static bool IsActiveState(ControllerState state)
        {
            return state == ControllerState.Initializing
                || state == ControllerState.Running
                || state == ControllerState.Paused;
        }

        /// <summary>
        /// True while a run is paused.
        /// </summary>
        public bool IsPaused => IsPausedState(State);

        /// <summary><see cref="IsPaused"/> for a state already in hand.</summary>
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

        /// <summary>True once this run has ended, however it ended.</summary>
        public bool HasFinished => IsFinishedState(State);

        /// <summary><see cref="IsRunInProgress"/> for a state already in hand.</summary>
        public static bool IsRunInProgressState(ControllerState state) =>
            state != ControllerState.Idle && !IsFinishedState(state);

        /// <summary>
        /// True while a run is under way in any sense: initializing, moving, paused, waiting
        /// on the operator, or finishing. <see cref="IsActive"/> is narrower - whether the
        /// machine is being driven - and a run parked at a prompt still owns the machine.
        /// </summary>
        public bool IsRunInProgress => IsRunInProgressState(State);

        /// <summary>
        /// True while a run is waiting on the operator: a tool change, or a program pause.
        /// Not active, because nothing is moving, but the job is not over.
        /// </summary>
        public static bool IsWaitingForOperatorState(ControllerState state) =>
            state == ControllerState.WaitingForUserInput;

        // =========================================================================
        // Events
        // =========================================================================

        /// <summary>The machine this controller drives, for the helpers below.</summary>
        protected abstract IMachine Machine { get; }

        public event Action<ControllerState>? StateChanged;
        public event Action<ProgressInfo>? ProgressChanged;
        public event Action<UserInputRequest>? UserInputRequired;
        public event Action<ControllerError>? ErrorOccurred;

        // =========================================================================
        // State transitions
        // =========================================================================

        /// <summary>
        /// Transition to a new state. Throws if transition is invalid.
        /// Events are fired synchronously - handler runs immediately, controller waits.
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
        /// Transition if the table allows it, and say whether it did.
        ///
        /// For a transition another thread may already have made. The test and the
        /// assignment happen under one hold of the lock, so nothing can land between them.
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

            // Log and fire event outside lock to prevent deadlocks
            ControllerLog.Log(LogStateTransition, GetType().Name, from, newState);
            StateChanged?.Invoke(newState);
            return true;
        }

        /// <summary>
        /// Check if a transition is valid according to the FSM.
        /// </summary>
        protected static bool IsValidTransition(ControllerState from, ControllerState to)
        {
            return ValidTransitions.TryGetValue(from, out var validTargets) &&
                   Array.IndexOf(validTargets, to) >= 0;
        }

        // =========================================================================
        // Event helpers
        // =========================================================================

        /// <summary>Emit a progress update.</summary>
        protected void EmitProgress(ProgressInfo progress)
        {
            ProgressChanged?.Invoke(progress);
        }

        /// <summary>Emit an error.</summary>
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
        /// Request user input and wait for response.
        /// Transitions to WaitingForUserInput, emits the request, waits, then returns to
        /// whatever state it interrupted.
        /// Returns the user's selection.
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

            // Wait for response or cancellation
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
        /// True when the caller has this moment taken a Continue from the operator. A door
        /// already closed and holding is then released without asking: they changed the
        /// tool, closed the door and pressed Continue, and a second question about the
        /// same door is the same question twice. It covers that one release only, so a
        /// door opened again afterwards is put to them as usual.
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
                    // the cycle start restarts the spindle. Sent as a prompt only: as
                    // progress as well, the screens would draw the same sentence twice, once
                    // with the choices and once without. No title - the message already names
                    // the enclosure.
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
                        EmitProgress(new ProgressInfo(PhaseWaitingForOperator, 0, message));
                        announced = true;
                    },
                    ct: ct).ConfigureAwait(false);

                // Tested against the one outcome that may carry on. A case added to the
                // enum then stops the run instead of continuing at the door.
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
                // path the next one is not sent until the safety retract finishes. Withdraw
                // it on every exit including the abort, so nothing tells the operator to
                // close a door while the tool is moving.
                if (announced)
                {
                    EmitProgress(new ProgressInfo(PhaseDoorCleared, 0, string.Empty));
                }
            }
        }

        /// <summary>
        /// Retract the tool and report whether it reached the target. Every run ends
        /// through here, so the rules below apply wherever it ends.
        ///
        /// Nothing is sent while the machine holds at the door: GRBL keeps the move in its
        /// planner and runs it when the hold is released. Stop first if the run was moving;
        /// <see cref="MachineWait.StopAndResetAsync"/> returns whether the machine was at the
        /// door, because its soft reset moves the machine from Door to Alarm.
        ///
        /// The retract runs on its own token: the run's is already cancelled by the time a
        /// stop reaches here.
        /// </summary>
        /// <param name="lift">Sends the move and returns true once the tool is there.</param>
        /// <param name="budgetMs">How long to wait for it.</param>
        protected async Task<bool> RetractToSafeZAsync(Func<CancellationToken, Task<bool>> lift, int budgetMs)
        {
            if (MachineWait.IsDoor(Machine))
            {
                ControllerLog.Log("{0}: holding at the door, not queueing a retract", GetType().Name);
                return false;
            }

            using var budget = new CancellationTokenSource(budgetMs);

            try
            {
                return await lift(budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The budget ran out mid-wait, so the position is not confirmed.
                return false;
            }
        }

        /// <summary>Retract to a machine Z, used where there is no work height to use.</summary>
        /// <inheritdoc cref="RetractToSafeZAsync(Func{CancellationToken, Task{bool}}, int)"/>
        protected Task<bool> RetractToSafeZAsync(double clearanceMachineZ, int budgetMs) =>
            RetractToSafeZAsync(
                ct => MachineWait.SafetyRetractZAsync(
                    Machine, clearanceMachineZ, Util.Constants.ZHeightWaitTimeoutMs, ct),
                budgetMs);

        /// <summary>
        /// Lift the tool after a stop, and report when the lift was not confirmed. Every
        /// controller decides this here, so the three cannot answer it differently.
        ///
        /// A stop at the door never counts as confirmed: the soft reset clears the hold, so
        /// the tool's position is unknown and no move can be queued to check it.
        /// </summary>
        /// <param name="wasHoldingAtDoor">What <see cref="MachineWait.StopAndResetAsync"/> returned.</param>
        /// <inheritdoc cref="RetractToSafeZAsync(Func{CancellationToken, Task{bool}}, int)"/>
        protected async Task LiftAfterStopAsync(
            bool wasHoldingAtDoor, Func<CancellationToken, Task<bool>> lift, int budgetMs)
        {
            if (wasHoldingAtDoor || !await RetractToSafeZAsync(lift, budgetMs).ConfigureAwait(false))
            {
                EmitError(new ControllerError(
                    ControllerConstants.ErrorStopRetractFailed, null, IsFatal: false));
            }
        }

        /// <inheritdoc cref="LiftAfterStopAsync(bool, Func{CancellationToken, Task{bool}}, int)"/>
        protected Task LiftAfterStopAsync(bool wasHoldingAtDoor, double clearanceMachineZ, int budgetMs) =>
            LiftAfterStopAsync(
                wasHoldingAtDoor,
                ct => MachineWait.SafetyRetractZAsync(
                    Machine, clearanceMachineZ, Util.Constants.ZHeightWaitTimeoutMs, ct),
                budgetMs);

        /// <summary>
        /// Stop the machine, then lift the tool, and report when the lift was not confirmed.
        /// Every run ends through here.
        /// </summary>
        /// <param name="betweenStopAndLift">
        /// Run after the machine has stopped and before the lift, for a controller with
        /// something to undo while nothing is moving.
        /// </param>
        /// <inheritdoc cref="LiftAfterStopAsync(bool, Func{CancellationToken, Task{bool}}, int)"/>
        protected async Task StopAndLiftAsync(
            Func<CancellationToken, Task<bool>> lift, int budgetMs, Func<Task>? betweenStopAndLift = null)
        {
            bool wasHoldingAtDoor = await MachineWait.StopAndResetAsync(Machine).ConfigureAwait(false);

            if (betweenStopAndLift != null)
            {
                await betweenStopAndLift().ConfigureAwait(false);
            }

            await LiftAfterStopAsync(wasHoldingAtDoor, lift, budgetMs).ConfigureAwait(false);
        }

        /// <summary>
        /// The same, lifting to a machine Z. Use this where the run does not own the work
        /// frame: a work-coordinate lift there could be a descent.
        /// </summary>
        /// <inheritdoc cref="StopAndLiftAsync(Func{CancellationToken, Task{bool}}, int, Func{Task})"/>
        protected Task StopAndLiftAsync(
            double clearanceMachineZ, int budgetMs, Func<Task>? betweenStopAndLift = null) =>
            StopAndLiftAsync(
                ct => MachineWait.SafetyRetractZAsync(
                    Machine, clearanceMachineZ, Util.Constants.ZHeightWaitTimeoutMs, ct),
                budgetMs,
                betweenStopAndLift);

        // =========================================================================
        // Abstract methods - subclasses implement these
        // =========================================================================

        /// <summary>Start the workflow. Called by StartAsync after state validation.</summary>
        protected abstract Task RunAsync(CancellationToken ct);

        /// <summary>Cleanup when stopping. Called by StopAsync.</summary>
        protected abstract Task CleanupAsync();

        /// <summary>
        /// Clear every field that describes the run rather than the machine, so the next
        /// run starts from a known state. Called from <see cref="StartAsync"/> and
        /// <see cref="Reset"/>.
        ///
        /// Abstract, not virtual: controllers are session-lifetime singletons, so a field
        /// left behind by one run is read by the next.
        ///
        /// State owned by the machine or the operator does not belong here. A work offset
        /// still shifted in GRBL, or a probe grid the operator expects to keep, outlives the
        /// run that set it.
        ///
        /// Implementations assign backing fields directly rather than the event-raising
        /// Phase properties, so a reset does not put a raw enum name on the screen.
        /// </summary>
        protected abstract void ResetRunState();

        // =========================================================================
        // IController implementation
        // =========================================================================

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

            try
            {
                TransitionTo(ControllerState.Initializing);
                await RunAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // Clean up on cancellation (stop spindle, retract Z, etc.)
                // CleanupAsync is idempotent - safe to call even if StopAsync also called
                try
                {
                    await CleanupAsync();
                }
                catch
                {
                    // Ignore cleanup errors during cancellation
                }

                if (!HasFinished)
                {
                    TransitionTo(ControllerState.Cancelled);
                }
            }
            catch (Exception ex)
            {
                // Clean up on error (stop spindle, retract Z, etc.)
                try
                {
                    await CleanupAsync();
                }
                catch
                {
                    // Ignore cleanup errors during error handling
                }

                EmitError(ex);
                if (!HasFinished)
                {
                    TransitionTo(ControllerState.Failed);
                }
            }

            // A run that returns without reaching a terminal state leaves the controller
            // claiming the machine, and every front end reads that as still running. Finish
            // it here so HasFinished is true once this task completes.
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
        /// Whether this run must stay paused. Throws when it is not paused at all, because
        /// that is a caller mistake rather than a machine state.
        ///
        /// A refusal is reported and the run stays paused rather than throwing: the terminal
        /// calls Resume straight from a key press with nothing to catch an exception.
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
