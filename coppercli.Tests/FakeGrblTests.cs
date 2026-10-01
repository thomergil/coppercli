using System;
using System.Threading.Tasks;
using coppercli.Core.Controllers;
using System.Threading;
using coppercli.Core.Communication;
using coppercli.Core.Settings;
using coppercli.Core.Util;
using coppercli.Tests.Fakes;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// FakeGrbl is driven here through the real Machine over a loopback socket. Where the
    /// simulator diverges from GRBL, every test built on it passes on behavior the machine
    /// does not have.
    /// </summary>
    public class FakeGrblTests
    {
        private const string Loopback = "127.0.0.1";

        /// <summary>Long enough to span several status polls, so the quiet is observable.</summary>
        private const int HomingCycleMs = 500;

        /// <summary>Bounds a homing wait in a test, well below HomingTimeoutMs.</summary>
        private const int HomingWaitMs = 5000;

        /// <summary>
        /// How long the simulated GRBL takes to restart: long enough that a line sent without
        /// waiting for its banner arrives mid-restart and is lost.
        /// </summary>
        private const int RebootQuietMs = 900;

        /// <summary>Bounds the retract in a test, well below ZHeightWaitTimeoutMs.</summary>
        private const int RetractWaitMs = 3000;

        /// <summary>A line GRBL accepts and does nothing with: a dwell of no time.</summary>
        private const string HarmlessLine = "G4 P0";

        /// <summary>Short enough to expire while GRBL is still restarting.</summary>
        private const int ShortAnswerWaitMs = 100;

        /// <summary>The alarm GRBL raises when homing cannot find a limit switch.</summary>
        private const int AlarmHomingNoSwitch = 9;

        [Fact]
        public void TheRealMachine_ConnectsAndReadsStatus()
        {
            using var grbl = new FakeGrbl();
            var settings = new MachineSettings
            {
                ConnectionType = ConnectionType.Ethernet,
                EthernetIP = Loopback,
                EthernetPort = grbl.Port
            };

            var machine = new Machine(settings);
            try
            {
                machine.Connect();
                Assert.True(machine.Connected);

                // Status arrives on the worker thread, so Connect returns before the first
                // report.
                WebServerFixture.WaitUntil(
                    () => machine.Status == GrblProtocol.StatusIdle,
                    "the simulated machine to report Idle");

                machine.SendLine(DoorTestMove.Line());
                DoorTestMove.WaitUntilItLands(machine);

                Assert.Equal(DoorTestMove.TargetZ, machine.MachinePosition.Z, DoorTestMove.PositionDecimals);
            }
            finally
            {
                machine.Disconnect();
            }
        }

        /// <summary>
        /// GRBL executes nothing while it holds at the door: lines arrive, queue, and run on
        /// the cycle start. A double that moved anyway would pass a retract queued at the
        /// door, which on the real machine runs only once the hold lifts.
        /// </summary>
        [Fact]
        public void AMoveSentWhileHoldingAtTheDoor_SitsInThePlanner()
        {
            using var grbl = new FakeGrbl();
            var machine = grbl.ConnectedMachine();
            try
            {
                grbl.SimulateDoorClosedAndHolding();
                WebServerFixture.WaitUntil(() => MachineWait.IsDoor(machine), "the door hold");

                machine.SendLine(DoorTestMove.Line());
                Thread.Sleep(DoorTestMove.SettleMs);

                Assert.Equal(0.0, machine.MachinePosition.Z, DoorTestMove.PositionDecimals);

                machine.CycleStart();
                DoorTestMove.WaitUntilItLands(machine);
            }
            finally
            {
                machine.Disconnect();
            }
        }

        /// <summary>
        /// GRBL alarms on a soft reset out of a door hold, and $X clears it. StopAndResetAsync
        /// sends that unlock; a double that never alarms would not show whether it does.
        /// </summary>
        [Fact]
        public async Task ASoftResetOutOfADoorHold_AlarmsAndIsClearedByUnlock()
        {
            using var grbl = new FakeGrbl();
            var machine = grbl.ConnectedMachine();
            try
            {
                grbl.SimulateDoorClosedAndHolding();
                WebServerFixture.WaitUntil(() => MachineWait.IsDoor(machine), "the door hold");

                Assert.True(
                    await MachineWait.StopAndResetAsync(machine), "the stop did not see the door");

                WebServerFixture.WaitUntil(
                    () => MachineWait.IsIdle(machine), "the unlock to clear the alarm the reset raised");
                Assert.Contains(GrblProtocol.CmdUnlock, grbl.Received);
            }
            finally
            {
                machine.Disconnect();
            }
        }

        /// <summary>
        /// GRBL runs the homing cycle from a loop that services no status query, so it goes
        /// quiet for the whole cycle. The status poll runs on its own schedule, so the answer
        /// to a '?' issued just before the $H lands just after it. Counting that one report
        /// as GRBL still answering reads a machine that is homing as one that refused the
        /// command - and the caller then soft-resets a live homing cycle.
        /// </summary>
        [Fact]
        public async Task AHomingCycle_IsNotReadAsARefusal_WhenOneReportWasStillInFlight()
        {
            using var grbl = new FakeGrbl { HomingMs = HomingCycleMs };
            var machine = grbl.ConnectedMachine();
            try
            {
                var outcome = await MachineWait.HomeAsync(machine, HomingWaitMs);

                Assert.True(outcome.Success,
                    "a homing cycle that ran to completion was reported as refused: "
                    + (outcome.Reason ?? "no reason given"));
                Assert.True(machine.IsHomed);
            }
            finally
            {
                machine.Disconnect();
            }
        }

        /// <summary>
        /// A stop during homing resets GRBL, and GRBL fails the cycle with an alarm when it
        /// does. It then refuses every G-code line until the lock is cleared, so a stop that
        /// does not clear it leaves the spindle command and the safety retract rejected and
        /// the tool wherever the cycle left it.
        /// </summary>
        [Fact]
        public async Task AStopDuringHoming_ClearsTheAlarmItRaised_SoTheRetractIsAccepted()
        {
            using var grbl = new FakeGrbl { HomingMs = int.MaxValue, RebootMs = RebootQuietMs };
            var machine = grbl.ConnectedMachine();
            try
            {
                machine.SendLine(GrblProtocol.CmdHome);
                WebServerFixture.WaitUntil(
                    () => grbl.Received.Contains(GrblProtocol.CmdHome),
                    "the simulated machine to take the homing command");

                await MachineWait.StopAndResetAsync(machine);

                Assert.Contains(GrblProtocol.CmdUnlock, grbl.Received);
                Assert.False(MachineWait.IsAlarm(machine),
                    "the stop left GRBL alarmed, so every line it sends next is refused");

                Assert.True(
                    await MachineWait.SafetyRetractZAsync(machine, Constants.SafeClearanceZ, RetractWaitMs),
                    "the safety retract was refused by a machine the stop left locked out");
            }
            finally
            {
                machine.Disconnect();
            }
        }

        /// <summary>
        /// GRBL 1.1 still prints ok for a $H whose cycle failed, after the alarm. Read as the
        /// $H's answer, that ok homes a machine that never found its switches, and every
        /// later G53 move goes to an origin it does not have.
        /// </summary>
        [Fact]
        public async Task AFailedHomingCycle_IsNotReadAsHomed()
        {
            using var grbl = new FakeGrbl { HomingMs = int.MaxValue };
            var machine = grbl.ConnectedMachine();
            try
            {
                var homing = MachineWait.HomeAsync(machine, HomingWaitMs);
                WebServerFixture.WaitUntil(
                    () => grbl.Received.Contains(GrblProtocol.CmdHome), "the homing command");

                grbl.SimulateHomingFailure(AlarmHomingNoSwitch);
                var outcome = await homing;

                Assert.False(outcome.Success, "a failed homing cycle was read as homed");
                Assert.False(machine.IsHomed);
            }
            finally
            {
                machine.Disconnect();
            }
        }

        /// <summary>
        /// GRBL restarting on its own drops what it held and can lose the position homing set.
        /// Its banner is the only sign, so a caller waiting on a line it dropped must hear
        /// that, and the machine must stop reading as homed.
        /// </summary>
        [Fact]
        public async Task ARestartGrblAnnounces_AbandonsWhatWasOutstanding_AndClearsHomed()
        {
            using var grbl = new FakeGrbl { HomingMs = int.MaxValue };
            var machine = grbl.ConnectedMachine();
            try
            {
                machine.IsHomed = true;
                var pending = machine.SendAsync(GrblProtocol.CmdHome, HomingWaitMs);
                WebServerFixture.WaitUntil(
                    () => grbl.Received.Contains(GrblProtocol.CmdHome), "the homing command");

                grbl.SimulateRestart();

                Assert.Equal(GrblAnswer.Abandoned, (await pending).Answer);
                Assert.False(machine.IsHomed);
            }
            finally
            {
                machine.Disconnect();
            }
        }

        /// <summary>
        /// A caller that stops waiting has been told the line got no answer, and acts on that.
        /// A line still waiting to go out must then not go out later behind their back.
        /// </summary>
        [Fact]
        public async Task ALineThatTimesOutBeforeItIsSent_IsNeverSent()
        {
            using var grbl = new FakeGrbl { RebootMs = RebootQuietMs };
            var machine = grbl.ConnectedMachine();
            try
            {
                // Lines wait for GRBL's banner after a reset, so this one is still queued
                // when its wait runs out.
                machine.SoftReset();
                var reply = await machine.SendAsync(HarmlessLine, ShortAnswerWaitMs);
                Assert.Equal(GrblAnswer.NoAnswer, reply.Answer);

                // Anything sent after the restart reaches GRBL, so once this has, the
                // withdrawn line would have too.
                Assert.True((await machine.SendAsync(GrblProtocol.CmdUnlock, HomingWaitMs)).Ran);
                Assert.DoesNotContain(HarmlessLine, grbl.Received);
            }
            finally
            {
                machine.Disconnect();
            }
        }

        private const string BannerPrefix = "Grbl";

        /// <summary>
        /// The machine reports ALARM:3 by the text of grbl_alarm_codes_en_US.csv, not the code.
        /// </summary>
        private const string AbortAlarmText = "Reset while in motion";

        private static bool IsAbortAlarm(string line) =>
            line.Contains(AbortAlarmText, StringComparison.Ordinal);

        private static System.Collections.Concurrent.ConcurrentQueue<string> RecordLines(Machine machine)
        {
            var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
            machine.LineReceived += lines.Enqueue;

            // The machine reports an alarm through its error event, not as a received line.
            machine.NonFatalException += lines.Enqueue;
            return lines;
        }

        /// <summary>
        /// GRBL keeps its alarm through a soft reset: with no motion to abort it prints no
        /// ALARM:3, only its banner and then the request to be unlocked. A double that came back
        /// Idle, or aborted a cycle that was not running, would make every stop test pass on
        /// behavior the machine does not have.
        /// </summary>
        [Fact]
        public void ASoftResetFromAlarm_ComesBackAlarmed_WithTheBannerAndTheLockMessage_AndNoAbort()
        {
            using var grbl = new FakeGrbl();
            var machine = grbl.ConnectedMachine();
            try
            {
                var lines = RecordLines(machine);
                grbl.SimulateHardLimit();
                WebServerFixture.WaitUntil(() => MachineWait.IsAlarm(machine), "the alarm");

                machine.SoftReset();

                WebServerFixture.WaitUntil(
                    () => lines.Contains(GrblProtocol.ResponseAlarmLock), "the unlock message after the banner");
                var seen = lines.ToArray();
                Assert.True(
                    Array.FindIndex(seen, l => l.StartsWith(BannerPrefix, StringComparison.Ordinal))
                    < Array.IndexOf(seen, GrblProtocol.ResponseAlarmLock),
                    "the banner did not come before the unlock message: " + string.Join(" | ", seen));
                Assert.DoesNotContain(seen, IsAbortAlarm);
                WebServerFixture.WaitUntil(
                    () => MachineWait.IsAlarm(machine), "the status to read Alarm: the reset cleared an alarm GRBL keeps");
            }
            finally
            {
                machine.Disconnect();
            }
        }

        /// <summary>
        /// A reset while the axes move aborts the cycle: GRBL prints ALARM:3 before its banner,
        /// and comes back alarmed.
        /// </summary>
        [Fact]
        public void ASoftResetWhileMoving_PrintsTheAbortAlarm_BeforeTheBanner()
        {
            using var grbl = new FakeGrbl();
            var machine = grbl.ConnectedMachine();
            try
            {
                var lines = RecordLines(machine);
                grbl.SimulateMoving();
                WebServerFixture.WaitUntil(
                    () => machine.Status.StartsWith(GrblProtocol.StatusRun, StringComparison.Ordinal), "the move");

                machine.SoftReset();

                WebServerFixture.WaitUntil(
                    () => lines.Contains(GrblProtocol.ResponseAlarmLock), "the unlock message after the banner");
                var seen = lines.ToArray();
                int abort = Array.FindIndex(seen, IsAbortAlarm);
                Assert.True(abort >= 0, "no ALARM:3 for a reset during a move: " + string.Join(" | ", seen));
                Assert.True(
                    abort < Array.FindIndex(seen, l => l.StartsWith(BannerPrefix, StringComparison.Ordinal)),
                    "ALARM:3 came after the banner: " + string.Join(" | ", seen));
                WebServerFixture.WaitUntil(() => MachineWait.IsAlarm(machine), "the status to read Alarm after the reset");
            }
            finally
            {
                machine.Disconnect();
            }
        }
    }
}
