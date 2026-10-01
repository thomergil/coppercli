#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using coppercli.Core.Communication;
using coppercli.Core.Util;

namespace coppercli.Tests.Fakes
{
    /// <summary>
    /// GRBL's door behavior in one place, so the three doubles cannot diverge. Each double
    /// keeps its own state and calls in here for the rules that state follows.
    ///
    /// The predicates below compare wire values directly instead of calling MachineWait: the
    /// door tests check MachineWait's predicates, and a double built on them would agree with
    /// them whatever they said.
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

        /// <summary>Set true for a door switch that reads closed while the cycle start is
        /// ignored.</summary>
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
        /// Whether a soft reset from this state stops the axes, which GRBL reports as ALARM:3:
        /// a run, a jog, a homing cycle, a feed hold still slowing (anything but Hold:0), or a
        /// door hold still parking or restoring (Door:2, Door:3). From Idle, a finished feed
        /// hold or a stopped door hold GRBL keeps its position (gnea/grbl's mc_reset, and every
        /// reset in the operator's Nomad 3 logs).
        /// </summary>
        public static bool ResetStopsMotion(string state, string subState) =>
            state.StartsWith(GrblProtocol.StatusRun, StringComparison.Ordinal)
            || state.StartsWith(GrblProtocol.StatusJog, StringComparison.Ordinal)
            || state.StartsWith(GrblProtocol.StatusHome, StringComparison.Ordinal)
            || (state.StartsWith(GrblProtocol.StatusHold, StringComparison.Ordinal)
                && subState != GrblProtocol.HoldSubStateComplete)
            || (state.StartsWith(GrblProtocol.StatusDoor, StringComparison.Ordinal)
                && (subState == GrblProtocol.DoorSubStateRetracting
                    || subState == GrblProtocol.DoorSubStateResuming));

        /// <summary>
        /// Whether GRBL comes back from a soft reset locked: after stopping motion, and from
        /// Alarm or Sleep, which a reset carries over (gnea/grbl main.c keeps the prior state).
        /// StopAndResetAsync sends $X to clear the lock.
        /// </summary>
        public static bool ResetAlarms(string state, string subState) =>
            ResetStopsMotion(state, subState)
            || state.StartsWith(GrblProtocol.StatusAlarm, StringComparison.Ordinal)
            || state.StartsWith(GrblProtocol.StatusSleep, StringComparison.Ordinal);

        /// <summary>
        /// Whether GRBL refuses this line because it is alarmed: it locks G-code out and takes
        /// only its own $ commands until the alarm is cleared.
        /// </summary>
        public static bool LockedOut(string line, string state) =>
            state.StartsWith(GrblProtocol.StatusAlarm, StringComparison.Ordinal)
            && !line.StartsWith(GrblProtocol.SystemCommandPrefix, StringComparison.Ordinal);

        /// <summary>
        /// GRBL's answer to a line in this state: G-code refused while alarmed, nothing at all
        /// while asleep, ok otherwise.
        /// </summary>
        public static GrblReply Answer(string line, string state)
        {
            if (LockedOut(line, state))
            {
                return GrblReply.Refused(new GrblRejection(GrblRejection.LockedOut, line, string.Empty));
            }

            return state.StartsWith(GrblProtocol.StatusSleep, StringComparison.Ordinal)
                ? GrblReply.NoAnswer
                : GrblReply.Ok;
        }

        /// <summary>
        /// Whether GRBL refuses this line with error:13 because the door is open. GRBL enters
        /// Door from any state but Alarm; alarmed, it stays in Alarm and refuses $X and $H
        /// until the switch reads closed.
        /// </summary>
        public static bool RefusedAtOpenDoor(string line, string state, bool switchOpen) =>
            switchOpen
            && state.StartsWith(GrblProtocol.StatusAlarm, StringComparison.Ordinal)
            && (line == GrblProtocol.CmdUnlock || line.StartsWith(GrblProtocol.CmdHome, StringComparison.Ordinal));

        /// <summary>Whether this line is the $X that clears the alarm a reset raised.</summary>
        public static bool ClearsAlarm(string line, string state) =>
            line == GrblProtocol.CmdUnlock
            && state.StartsWith(GrblProtocol.StatusAlarm, StringComparison.Ordinal);

        /// <summary>
        /// GRBL resumes only once the enclosure reads closed, so while it is ajar or the park
        /// retract is still running the cycle start is ignored and the hold stays. The restore
        /// is a real move, reported as Door:3 until the parked axes are back.
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
