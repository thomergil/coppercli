#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using static coppercli.Core.Controllers.ControllerConstants;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Abstract base class for workflow controllers implementing FSM logic.
    /// Enforces valid state transitions and provides common event infrastructure.
    /// </summary>
    public abstract class ControllerBase : IController
    {
        // =========================================================================
        // State transition table - defines all valid transitions
        // =========================================================================

        private static readonly Dictionary<ControllerState, ControllerState[]> ValidTransitions = new()
        {
            [ControllerState.Idle] = new[] { ControllerState.Initializing },
            [ControllerState.Initializing] = new[] { ControllerState.Running, ControllerState.Failed, ControllerState.Cancelled },
            [ControllerState.Running] = new[] { ControllerState.Paused, ControllerState.WaitingForUserInput, ControllerState.Completing, ControllerState.Failed, ControllerState.Cancelled },
            [ControllerState.Paused] = new[] { ControllerState.Running, ControllerState.Cancelled },
            [ControllerState.WaitingForUserInput] = new[] { ControllerState.Running, ControllerState.Cancelled },
            [ControllerState.Completing] = new[] { ControllerState.Completed, ControllerState.Failed },
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

        // =========================================================================
        // Events
        // =========================================================================

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
            ControllerState oldState;

            lock (_stateLock)
            {
                if (!IsValidTransition(_state, newState))
                {
                    throw new InvalidOperationException(
                        string.Format(ErrorInvalidTransition, _state, newState));
                }

                oldState = _state;
                _state = newState;
            }

            // Log and fire event outside lock to prevent deadlocks
            ControllerLog.Log(LogStateTransition, GetType().Name, oldState, newState);
            StateChanged?.Invoke(newState);
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

        /// <summary>Emit an error from an exception.</summary>
        protected void EmitError(Exception ex, bool isFatal = true)
        {
            EmitError(new ControllerError(ex.Message, ex, isFatal));
        }

        /// <summary>
        /// Request user input and wait for response.
        /// Transitions to WaitingForUserInput, emits request, waits, transitions back to Running.
        /// Returns the user's selection.
        /// </summary>
        protected async Task<string> RequestUserInputAsync(
            string title,
            string message,
            string[] options,
            CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<string>();

            var request = new UserInputRequest
            {
                Title = title,
                Message = message,
                Options = options,
                OnResponse = response => tcs.TrySetResult(response)
            };

            TransitionTo(ControllerState.WaitingForUserInput);
            UserInputRequired?.Invoke(request);

            // Wait for response or cancellation
            using var registration = ct.Register(() => tcs.TrySetCanceled());
            var response = await tcs.Task;

            TransitionTo(ControllerState.Running);
            return response;
        }

        // =========================================================================
        // Abstract methods - subclasses implement these
        // =========================================================================

        /// <summary>Start the workflow. Called by StartAsync after state validation.</summary>
        protected abstract Task RunAsync(CancellationToken ct);

        /// <summary>Cleanup when stopping. Called by StopAsync.</summary>
        protected abstract Task CleanupAsync();

        // =========================================================================
        // IController implementation
        // =========================================================================

        public async Task StartAsync(CancellationToken ct = default)
        {
            if (State != ControllerState.Idle)
            {
                throw new InvalidOperationException(
                    string.Format(ErrorCannotStart, State));
            }

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

                if (State != ControllerState.Completed && State != ControllerState.Cancelled)
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
                if (State != ControllerState.Failed)
                {
                    TransitionTo(ControllerState.Failed);
                }
            }
        }

        public virtual void Pause()
        {
            if (State != ControllerState.Running)
            {
                throw new InvalidOperationException(
                    string.Format(ErrorCannotPause, State));
            }
            TransitionTo(ControllerState.Paused);
        }

        public virtual void Resume()
        {
            if (State != ControllerState.Paused)
            {
                throw new InvalidOperationException(
                    string.Format(ErrorCannotResume, State));
            }
            TransitionTo(ControllerState.Running);
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
                if (State != ControllerState.Completed && State != ControllerState.Failed)
                {
                    TransitionTo(ControllerState.Cancelled);
                }
            }
        }

        public virtual void Reset()
        {
            var currentState = State;
            if (currentState == ControllerState.Completed ||
                currentState == ControllerState.Failed ||
                currentState == ControllerState.Cancelled)
            {
                TransitionTo(ControllerState.Idle);
            }
            else if (currentState != ControllerState.Idle)
            {
                throw new InvalidOperationException(
                    string.Format(ErrorCannotReset, State));
            }
        }
    }
}
