#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using coppercli.Core.Util;

namespace coppercli.Tests.Fakes
{
    /// <summary>
    /// GRBL's door behaviour, in one place so the three doubles cannot answer it differently.
    /// Each double keeps its own state; this owns the rules that state follows.
    ///
    /// Compared against the wire values rather than through MachineWait, because the door
    /// tests are checking those predicates and a double that called them would always agree
    /// with them.
    /// </summary>
    public sealed class DoorModel : IDisposable
    {
        private readonly Action<string, string> _setState;
        private readonly Func<string> _stateAfterRestore;
        private readonly CancellationTokenSource _disposing = new();

        /// <param name="setState">Writes the double's state and substate together.</param>
        /// <param name="stateAfterRestore">
        /// The state GRBL returns to once the restore finishes: the one the door interrupted,
        /// which is Run for a job that was streaming.
        /// </param>
        public DoorModel(Action<string, string> setState, Func<string> stateAfterRestore)
        {
            _setState = setState;
            _stateAfterRestore = stateAfterRestore;
        }

        /// <summary>
        /// How long GRBL spends restoring from the park before it leaves Door. Zero finishes
        /// it before the cycle start returns, for a test that does not care about the restore.
        /// </summary>
        public int RestoreMs { get; set; }

        /// <summary>A switch that reads closed but never lets GRBL resume.</summary>
        public bool IgnoreCycleStart { get; set; }

        /// <summary>
        /// True while GRBL executes nothing. A line sent now sits in its planner and runs
        /// when the hold is released.
        /// </summary>
        public static bool Holding(string state) =>
            state.StartsWith(GrblProtocol.StatusDoor, StringComparison.Ordinal)
            || state.StartsWith(GrblProtocol.StatusHold, StringComparison.Ordinal);

        /// <summary>
        /// Whether a feed hold changes anything. GRBL is already stopped at a door hold, and
        /// a feed hold there leaves the door state alone rather than replacing it.
        /// </summary>
        public static bool FeedHoldApplies(string state) =>
            !state.StartsWith(GrblProtocol.StatusDoor, StringComparison.Ordinal);

        /// <summary>
        /// Whether a soft reset from this state lands in Alarm. GRBL alarms on a reset out of
        /// a hold, a door hold or a move; only a reset of an already-idle machine lands in
        /// Idle. StopAndResetAsync sends $X for exactly this.
        /// </summary>
        public static bool ResetAlarms(string state) =>
            Holding(state) || state.StartsWith(GrblProtocol.StatusRun, StringComparison.Ordinal);

        /// <summary>Whether this line is the $X that clears the alarm a reset raised.</summary>
        public static bool ClearsAlarm(string line, string state) =>
            line == GrblProtocol.CmdUnlock
            && state.StartsWith(GrblProtocol.StatusAlarm, StringComparison.Ordinal);

        /// <summary>
        /// Take a cycle start at the door. GRBL resumes only once the enclosure reads closed;
        /// with it ajar or the park retract still running the cycle start is ignored and the
        /// machine keeps holding. The restore is a real move, reported as Door:3 until the
        /// retracted axes are back.
        /// </summary>
        /// <returns>True if the cycle start was taken.</returns>
        public bool CycleStart(string state, string subState)
        {
            bool holdingAtAClosedDoor =
                state.StartsWith(GrblProtocol.StatusDoor, StringComparison.Ordinal)
                && subState == GrblProtocol.DoorSubStateClosed;

            if (!holdingAtAClosedDoor || IgnoreCycleStart)
            {
                return false;
            }

            _setState(GrblProtocol.StatusDoor, GrblProtocol.DoorSubStateResuming);

            if (RestoreMs <= 0)
            {
                FinishRestore();
                return true;
            }

            int restoreMs = RestoreMs;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(restoreMs, _disposing.Token);
                    FinishRestore();
                }
                catch (OperationCanceledException)
                {
                    // The test finished before the restore did.
                }
            });

            return true;
        }

        private void FinishRestore() => _setState(_stateAfterRestore(), string.Empty);

        /// <summary>Stops a restore still counting down when the test ends.</summary>
        public void Dispose()
        {
            _disposing.Cancel();
            _disposing.Dispose();
        }
    }
}
