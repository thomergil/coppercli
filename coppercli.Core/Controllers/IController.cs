#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Every workflow controller implements this. A controller runs a state machine and raises
    /// events; it never draws anything.
    /// </summary>
    public interface IController
    {
        ControllerState State { get; }

        /// <summary>True while a run is under way: initializing, running or paused.</summary>
        bool IsActive { get; }

        event Action<ControllerState>? StateChanged;

        event Action<ProgressInfo>? ProgressChanged;

        /// <summary>The handler must call request.OnResponse(), or the run waits forever.</summary>
        event Action<UserInputRequest>? UserInputRequired;

        event Action<ControllerError>? ErrorOccurred;

        /// <summary>Throws <see cref="InvalidControllerStateException"/> if already running.</summary>
        Task StartAsync(CancellationToken ct = default);

        void Pause();

        void Resume();

        Task StopAsync();

        void Reset();

        /// <summary>
        /// Return the controller to Idle so the next run can start, whatever state this one
        /// left it in. It stops an unfinished run first, because <see cref="Reset"/> refuses a
        /// controller that still claims to be running, and it is the only route back to Idle.
        /// </summary>
        Task ReleaseAsync();
    }
}
