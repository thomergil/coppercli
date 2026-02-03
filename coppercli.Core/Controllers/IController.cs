#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Base interface for all workflow controllers.
    /// Controllers own state machines and emit events - they never render UI.
    /// </summary>
    public interface IController
    {
        /// <summary>Current state in the FSM.</summary>
        ControllerState State { get; }

        /// <summary>Fired on every state transition.</summary>
        event Action<ControllerState>? StateChanged;

        /// <summary>Fired when progress updates (percentage, phase, etc.).</summary>
        event Action<ProgressInfo>? ProgressChanged;

        /// <summary>Fired when user input is needed. Handler must call request.OnResponse().</summary>
        event Action<UserInputRequest>? UserInputRequired;

        /// <summary>Fired on errors. Check IsFatal to see if operation can continue.</summary>
        event Action<ControllerError>? ErrorOccurred;

        /// <summary>Start the workflow. Throws if already running.</summary>
        Task StartAsync(CancellationToken ct = default);

        /// <summary>Pause the workflow (if pausable).</summary>
        void Pause();

        /// <summary>Resume after pause.</summary>
        void Resume();

        /// <summary>Stop the workflow and cleanup.</summary>
        Task StopAsync();

        /// <summary>Reset to Idle state for next operation.</summary>
        void Reset();
    }
}
