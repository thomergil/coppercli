#nullable enable
using System.Diagnostics;
using coppercli.Core.Communication;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Times how long a machine has read Idle without a break: the one definition of steady Idle.
    /// The settle before a job and the check that a job has finished both read it, so a
    /// machine Idle between two moves is not taken for one that has finished.
    /// </summary>
    public sealed class SteadyIdle
    {
        private readonly int _steadyMs;
        private long? _idleSince;

        public SteadyIdle(int steadyMs) => _steadyMs = steadyMs;

        /// <summary>
        /// True once the machine has read Idle for the steady time without a break; any other
        /// reading starts the count again.
        /// </summary>
        public bool IsSteady(IMachine machine)
        {
            if (!MachineWait.IsIdle(machine))
            {
                _idleSince = null;
                return false;
            }

            _idleSince ??= Stopwatch.GetTimestamp();
            return Stopwatch.GetElapsedTime(_idleSince.Value).TotalMilliseconds >= _steadyMs;
        }

        /// <summary>Starts the count again, for a caller with its own reason the machine is not done.</summary>
        public void Interrupt() => _idleSince = null;
    }
}
