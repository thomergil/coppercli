#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using coppercli.Core.Communication;
using coppercli.Core.Controllers;
using coppercli.Core.GCode;
using coppercli.Core.Util;
using coppercli.Tests.Fakes;
using Xunit;
using static coppercli.Core.Controllers.ControllerConstants;
using static coppercli.Core.Util.Constants;

namespace coppercli.Tests
{
    // MillingController driven end to end through StartAsync: M6 detection, the M0/M1/M2/M30
    // stops, the settling phase and the enclosure door. Tests that need a running stream use
    // FakeMachine, which simulates motion and GRBL replies; the rest use MockMachine, whose
    // state each test sets directly.
    public class MillingControllerTests
    {
        private MockMachine CreateMachineWithFile(params string[] lines)
        {
            var machine = new MockMachine
            {
                Status = "Idle",
                Connected = true,
                MachinePosition = new Vector3(0, 0, -1),
                WorkPosition = new Vector3(0, 0, 0)
            };
            machine.LoadFile(lines);
            return machine;
        }

        // FakeMachine's simulated move and homing time, tuned down so a run costs only the
        // fixed Core-side settle delays that no test double can shorten.
        private const double FastMoveSpeedMmPerSec = 10000.0;
        private const int FastHomingDurationMs = 50;

        // Far above the time a run needs, so a controller that never reaches the event under
        // test fails on a timeout instead of hanging the suite.
        private const int ToolChangeWaitTimeoutMs = 20_000;
        private const int CompletionWaitTimeoutMs = 30_000;
        private const int StateTransitionWaitTimeoutMs = 5_000;

        // How far past SettleWaitMs a test waits, to be sure the settle timeout has run out.
        private const int SettleMarginMs = 100;

        private const int ToolChangeLineIndex = 2;
        private const int SingleRunToolNumber = 4;
        private const int FirstAbortedToolNumber = 5;
        private const int SecondRunToolNumber = 6;

        private static FakeMachine CreateFastFakeMachine(params string[] lines)
        {
            var machine = new FakeMachine
            {
                RapidSpeed = FastMoveSpeedMmPerSec,
                FeedSpeed = FastMoveSpeedMmPerSec,
                HomingDurationMs = FastHomingDurationMs,
            };
            machine.LoadFile(lines);
            return machine;
        }

        private static string[] FileWithToolChange(int toolNumber) => new[]
        {
            "G21",
            "G90",
            $"M6 T{toolNumber}",
            "G1 X1 Y1 F100",
        };

        /// <summary>
        /// Long enough that the stream is still running when a test pauses or opens the door.
        /// </summary>
        private static readonly string[] ALongCut =
            new[] { "G21", "G90" }
                .Concat(Enumerable.Range(1, LongCutMoves)
                    .Select(i => GCodeFormat.Inv($"G1 X{i % 10} Y{i % 7} F60")))
                .ToArray();

        private const int LongCutMoves = 200;

        private static readonly string[] FileWithoutToolChange =
        {
            "G21",
            "G90",
            "G1 X1 Y1 F100",
        };

        private const string ToolChangeTimeoutMessage = "Timed out waiting for ToolChangeDetected.";

        /// <summary>
        /// Bounded so a controller that never fires ToolChangeDetected fails the test with a
        /// TimeoutException instead of hanging it.
        /// </summary>
        private static async Task<ToolChangeInfo> WaitForToolChangeOrTimeoutAsync(
            MillingController controller, int timeoutMs)
        {
            var tcs = new TaskCompletionSource<ToolChangeInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnToolChange(ToolChangeInfo info) => tcs.TrySetResult(info);

            controller.ToolChangeDetected += OnToolChange;
            try
            {
                return await MachineWait.AwaitReplyOrTimeoutAsync(
                    tcs.Task, timeoutMs, ToolChangeTimeoutMessage, CancellationToken.None);
            }
            finally
            {
                controller.ToolChangeDetected -= OnToolChange;
            }
        }

        /// <summary>
        /// Models a refused tool change: the token is cancelled and Resume() is never called.
        /// Returns with the controller Reset() to Idle, so the caller can start a second run.
        /// </summary>
        private static async Task AbortDuringToolChangeAsync(MillingController controller, int toolNumber)
        {
            using var cts = new CancellationTokenSource();
            var run = controller.StartAsync(cts.Token);
            try
            {
                var toolChange = await WaitForToolChangeOrTimeoutAsync(controller, ToolChangeWaitTimeoutMs);
                Assert.Equal(toolNumber, toolChange.ToolNumber);

                // MillingController transitions to Paused before firing the event, so this
                // returns at once. It guards the cancel below if that order ever changes.
                await AsyncWait.WaitUntilAsync(() => controller.State == ControllerState.Paused, "the controller to reach Paused", StateTransitionWaitTimeoutMs);
            }
            finally
            {
                cts.Cancel();
                await run;
            }

            Assert.Equal(ControllerState.Cancelled, controller.State);
            controller.Reset();
            Assert.Equal(ControllerState.Idle, controller.State);
        }

        [Fact]
        public void NewController_HasIdleState()
        {
            var machine = new MockMachine();
            var controller = new MillingController(machine);

            Assert.Equal(ControllerState.Idle, controller.State);
            Assert.Equal(MillingPhase.NotStarted, controller.Phase);
        }

        [Fact]
        public void Constructor_WithNullMachine_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new MillingController(null!));
        }

        [Fact]
        public async Task Milling_DrivenThroughAnM6File_FiresToolChangeDetected()
        {
            using var machine = CreateFastFakeMachine(FileWithToolChange(SingleRunToolNumber));
            machine.IsHomed = true;
            var controller = new MillingController(machine)
            {
                Options = new MillingOptions ()
            };

            using var cts = new CancellationTokenSource();
            var run = controller.StartAsync(cts.Token);

            ToolChangeInfo detected;
            try
            {
                detected = await WaitForToolChangeOrTimeoutAsync(controller, ToolChangeWaitTimeoutMs);
            }
            finally
            {
                // Cancel so the run reaches a terminal state instead of staying Paused.
                cts.Cancel();
                await run;
            }

            Assert.Equal(SingleRunToolNumber, detected.ToolNumber);
            Assert.Equal(ToolChangeLineIndex, detected.LineNumber);
        }

        /// <summary>A cut, then a tool change, then the next tool's work.</summary>
        private static readonly string[] CutThenToolChange =
        {
            "G21", "G90", "G0 X0 Y0 Z1", "G1 Z-0.1 F100", "G1 X1 F100", "G0 Z1", "M6 T2", "G1 X2 Y2 F100"
        };

        /// <summary>The line after the M6, where the stream stops.</summary>
        private const int CutThenToolChangeResumeLine = 7;

        /// <summary>
        /// Sections or a depth adjustment the run can be told to end, recording the machine's
        /// file position each time the run asked about them and ended them.
        /// </summary>
        private sealed class FakeSectionsAndDepth : ISectionsAndDepth
        {
            public bool Apply { get; init; } = true;
            public string? Refusal { get; init; }

            /// <summary>Runs as the rest of the job is rebuilt, as a stop arriving then would.</summary>
            public Action? WhileEnding { get; init; }

            /// <summary>The G-code the rebuild puts on the machine, keeping its place; null to keep the G-code.</summary>
            public string[]? Rebuilt { get; init; }

            public IMachine? Machine { get; set; }
            public ConcurrentQueue<int> AskedAt { get; } = new();
            public ConcurrentQueue<int> EndedAt { get; } = new();

            public bool ApplyAfterTheToolChange()
            {
                AskedAt.Enqueue(Machine!.FilePosition);
                return Apply;
            }

            public string? EndAtTheToolChange()
            {
                EndedAt.Enqueue(Machine!.FilePosition);
                WhileEnding?.Invoke();
                if (Rebuilt != null && Machine is FakeMachine fake)
                {
                    int streamed = fake.FilePosition;
                    fake.LoadFile(Rebuilt);
                    fake.FileGoto(streamed);
                }
                return Refusal;
            }
        }

        /// <summary>What a run of CutThenToolChange did at its tool change.</summary>
        private sealed record ToolChangeOutcome(
            UserInputRequest? Asked, MillingPhase PhaseWhenAsked, ControllerState StateWhenAsked,
            bool ReachedToolChange, int EndedBeforeTheToolChange, bool OperatorMayResumeThere,
            JobEstimate? EstimateThere, ControllerState Ended, string? Error, bool StoppedWhileCutting);

        /// <summary>
        /// Starts a run of CutThenToolChange with <paramref name="sectionsAndDepth"/>, answers
        /// the question about them with <paramref name="answer"/> if one is asked, and stops the
        /// run once the tool change is reached or the run ends.
        /// </summary>
        private static async Task<ToolChangeOutcome> RunToTheToolChangeAsync(
            FakeSectionsAndDepth sectionsAndDepth, string answer, CancellationTokenSource? stop = null,
            int feedPercent = Constants.OverrideDefaultPercent)
        {
            using var machine = CreateMachineToEstimate(CutThenToolChange);
            machine.FeedOverride = feedPercent;
            sectionsAndDepth.Machine = machine;
            var controller = new MillingController(machine, sectionsAndDepth) { Options = new MillingOptions() };
            var prompts = new PromptRecorder(controller);
            string? error = null;
            controller.ErrorOccurred += e => error = e.Message;
            bool reached = false;
            int endedBefore = -1;
            bool mayResumeThere = true;
            JobEstimate? estimateThere = null;
            controller.ToolChangeDetected += _ =>
            {
                reached = true;
                endedBefore = sectionsAndDepth.EndedAt.Count;
                mayResumeThere = controller.OperatorMayResume;
                estimateThere = controller.Estimate;
            };

            using var cts = stop ?? new CancellationTokenSource();
            var run = controller.StartAsync(cts.Token);
            UserInputRequest? asked = null;
            var phaseWhenAsked = MillingPhase.NotStarted;
            var stateWhenAsked = ControllerState.Idle;
            ControllerState ended;
            try
            {
                await AsyncWait.WaitUntilAsync(() => reached || prompts.All.Count > 0 || controller.HasFinished,
                    ToolChangeTimeoutMessage, ToolChangeWaitTimeoutMs);
                if (prompts.All.Count > 0)
                {
                    asked = await prompts.NextAsync(StateTransitionWaitTimeoutMs);
                    phaseWhenAsked = controller.Phase;
                    stateWhenAsked = controller.State;
                    asked.OnResponse(answer);
                    await AsyncWait.WaitUntilAsync(() => reached || controller.HasFinished,
                        ToolChangeTimeoutMessage, ToolChangeWaitTimeoutMs);
                }
                ended = controller.State;
            }
            finally
            {
                cts.Cancel();
                await AwaitRunOutcomeAsync(run);
            }

            return new ToolChangeOutcome(asked, phaseWhenAsked, stateWhenAsked, reached, endedBefore, mayResumeThere,
                estimateThere, ended, error, machine.StoppedWhileCutting);
        }

        /// <summary>
        /// Catches a run carrying sections or a depth adjustment past a tool change without
        /// asking: the next tool usually drills and cuts the board out, which the operator
        /// means for the whole board. The run asks while paused at the tool change, and Clear
        /// ends them at the line after it.
        /// </summary>
        [Fact]
        public async Task AToolChangeAfterMillingWithSectionsOrDepth_AsksKeepClearOrAbort_AndClearEndsThemThere()
        {
            var sectionsAndDepth = new FakeSectionsAndDepth();

            var outcome = await RunToTheToolChangeAsync(sectionsAndDepth, OptionClear);

            Assert.NotNull(outcome.Asked);
            Assert.Equal(SectionsAndDepthTitle, outcome.Asked!.Title);
            Assert.Equal(new[] { OptionKeep, OptionClear, OptionAbort }, outcome.Asked.Options);
            Assert.Equal(MillingPhase.ToolChange, outcome.PhaseWhenAsked);
            Assert.Equal(ControllerState.WaitingForUserInput, outcome.StateWhenAsked);
            Assert.Equal(new[] { CutThenToolChangeResumeLine }, sectionsAndDepth.AskedAt);
            Assert.Equal(new[] { CutThenToolChangeResumeLine }, sectionsAndDepth.EndedAt);
            Assert.True(outcome.ReachedToolChange, "the tool change did not follow the answer");
            Assert.Equal(1, outcome.EndedBeforeTheToolChange);
            Assert.False(outcome.OperatorMayResumeThere, "the operator's resume would stream past the tool change");
            Assert.Equal(ControllerState.Paused, outcome.Ended);
            Assert.Null(outcome.Error);
        }

        /// <summary>
        /// Catches a stop that arrives while the rest of the job is rebuilt still starting a tool
        /// change, whose run the stop does not wait for and which moves the machine.
        /// </summary>
        [Fact]
        public async Task AStop_WhileClearRebuildsTheJob_StartsNoToolChange()
        {
            using var stop = new CancellationTokenSource();
            var sectionsAndDepth = new FakeSectionsAndDepth { WhileEnding = stop.Cancel };

            var outcome = await RunToTheToolChangeAsync(sectionsAndDepth, OptionClear, stop);

            Assert.False(outcome.ReachedToolChange, "a stopped run started a tool change");
            Assert.Equal(ControllerState.Cancelled, outcome.Ended);
        }

        /// <summary>
        /// Catches a resume after the operator paused while the machine still ran the moves sent
        /// before a tool change: restarting the stream there ran the next tool's work with the
        /// old tool.
        /// </summary>
        [Fact]
        public async Task AResume_AfterAPauseWhileTheMovesBeforeAToolChangeRun_StillStopsForTheToolChange()
        {
            using var machine = CreateFastFakeMachine(CutThenToolChange);
            machine.IsHomed = true;
            machine.DrainAfterPauseMs = DrainBeforeTheToolChangeMs;
            var controller = new MillingController(machine) { Options = new MillingOptions() };
            bool reached = false;
            controller.ToolChangeDetected += _ => reached = true;

            using var cts = new CancellationTokenSource();
            var run = controller.StartAsync(cts.Token);
            try
            {
                await AsyncWait.WaitUntilAsync(
                    () => machine.Mode == Machine.OperatingMode.Manual && machine.FilePosition == CutThenToolChangeResumeLine
                        && controller.Phase == MillingPhase.Milling,
                    ToolChangeTimeoutMessage, ToolChangeWaitTimeoutMs);
                controller.Pause();
                Assert.True(controller.OperatorMayResume);

                controller.Resume();

                await AsyncWait.WaitUntilAsync(() => reached || controller.HasFinished,
                    ToolChangeTimeoutMessage, ToolChangeWaitTimeoutMs);
                Assert.True(reached, "the resume streamed past the tool change");
                Assert.Equal(CutThenToolChangeResumeLine, machine.FilePosition);
            }
            finally
            {
                cts.Cancel();
                await AwaitRunOutcomeAsync(run);
            }
        }

        /// <summary>Long enough for the test to pause the run before the machine finishes the moves.</summary>
        private const int DrainBeforeTheToolChangeMs = 2_000;

        /// <summary>
        /// Catches the tool change showing the time left of the G-code the operator cleared:
        /// the estimate follows the G-code the rebuild put on the machine.
        /// </summary>
        [Fact]
        public async Task AfterClear_TheToolChangeShowsTheTimeLeftOfTheRebuiltGCode()
        {
            var sectionsAndDepth = new FakeSectionsAndDepth { Rebuilt = CutThenToolChange.Append(ExtraCut).ToArray() };

            var outcome = await RunToTheToolChangeAsync(sectionsAndDepth, OptionClear);

            Assert.Equal(SecondsAfterTheToolChange + ExtraCutSeconds, outcome.EstimateThere!.TimeLeft.TotalSeconds, EstimatePrecision);
        }

        /// <summary>A cut from (2, 2) to (3, 3) at the feed before it, 100 mm/min.</summary>
        private const string ExtraCut = "G1 X3 Y3";
        private static readonly double ExtraCutSeconds = 60 * Math.Sqrt(2) / 100;

        /// <summary>
        /// Catches the estimate ignoring GRBL's overrides: at half the feed the next tool's cut
        /// takes twice as long.
        /// </summary>
        [Fact]
        public async Task TheTimeLeft_FollowsTheFeedOverride()
        {
            var outcome = await RunToTheToolChangeAsync(new FakeSectionsAndDepth { Apply = false }, OptionKeep, feedPercent: 50);

            Assert.Equal(2 * SecondsAfterTheToolChange, outcome.EstimateThere!.TimeLeft.TotalSeconds, EstimatePrecision);
        }

        /// <summary>Catches Keep ending the sections or depth anyway.</summary>
        [Fact]
        public async Task KeepingTheSectionsAndDepth_LeavesThemAndGoesOnToTheToolChange()
        {
            var sectionsAndDepth = new FakeSectionsAndDepth();

            var outcome = await RunToTheToolChangeAsync(sectionsAndDepth, OptionKeep);

            Assert.NotNull(outcome.Asked);
            Assert.Empty(sectionsAndDepth.EndedAt);
            Assert.True(outcome.ReachedToolChange, "the tool change did not follow the answer");
        }

        /// <summary>
        /// Catches an operator who wants out at this question being made to pick Keep or Clear:
        /// Abort stops the run there, as a stop does, and not as a stop while cutting.
        /// </summary>
        [Fact]
        public async Task AbortingAtTheSectionsAndDepthQuestion_StopsTheRun()
        {
            var sectionsAndDepth = new FakeSectionsAndDepth();

            var outcome = await RunToTheToolChangeAsync(sectionsAndDepth, OptionAbort);

            Assert.False(outcome.ReachedToolChange, "the run went on to the tool change after Abort");
            Assert.Empty(sectionsAndDepth.EndedAt);
            Assert.Equal(ControllerState.Cancelled, outcome.Ended);
            Assert.False(outcome.StoppedWhileCutting, "a stop at the tool change was taken for a stop while cutting");
        }

        /// <summary>
        /// Catches a run going on to the next tool in the sections the operator declined, when
        /// the rest of the job could not be rebuilt for the whole board: it fails and says why,
        /// and the next job is not told it stopped while cutting.
        /// </summary>
        [Fact]
        public async Task ClearingTheSectionsAndDepth_WhenTheyCannotBeEnded_FailsTheRunWithTheReason()
        {
            var sectionsAndDepth = new FakeSectionsAndDepth { Refusal = ErrorSectionsAndDepthNotEnded };

            var outcome = await RunToTheToolChangeAsync(sectionsAndDepth, OptionClear);

            Assert.False(outcome.ReachedToolChange, "the run went on to the tool change in the sections the operator declined");
            Assert.Equal(ControllerState.Failed, outcome.Ended);
            Assert.Equal(ErrorSectionsAndDepthNotEnded, outcome.Error);
            Assert.False(outcome.StoppedWhileCutting, "a stop at the tool change was taken for a stop while cutting");
        }

        /// <summary>Catches the question asked when there is nothing to keep or clear.</summary>
        [Fact]
        public async Task AToolChangeWithNoSectionsOrDepthToEnd_IsNotQuestioned()
        {
            var sectionsAndDepth = new FakeSectionsAndDepth { Apply = false };

            var outcome = await RunToTheToolChangeAsync(sectionsAndDepth, OptionKeep);

            Assert.Equal(new[] { CutThenToolChangeResumeLine }, sectionsAndDepth.AskedAt);
            Assert.Null(outcome.Asked);
            Assert.True(outcome.ReachedToolChange);
        }

        /// <summary>
        /// A fake that lists a Nomad 3's top speeds, with work zero inside its travel so the
        /// tool's place on the G-code can be found.
        /// </summary>
        private static FakeMachine CreateMachineToEstimate(params string[] lines)
        {
            var machine = CreateFastFakeMachine(lines);
            machine.IsHomed = true;
            machine.SetWorkOffset(-150, -100, -50);
            machine.TopSpeeds = FakeGrbl.TopSpeeds;
            return machine;
        }

        // CutThenToolChange's moves at their feeds and top speeds, in seconds: the plunge's 1.1 mm
        // and the cut's 1 mm at 100 mm/min, the rapid's 1.1 mm of Z at Z's top speed, and after
        // the tool change the next tool's sqrt(5) mm at 100 mm/min. The first rapid's start is
        // not known, so it takes none.
        private static readonly double SecondsBeforeTheToolChange = 60 * 1.1 / 100 + 60 * 1.0 / 100 + 60 * 1.1 / FakeGrbl.TopSpeeds.Z;
        private static readonly double SecondsAfterTheToolChange = 60 * Math.Sqrt(5) / 100;

        /// <summary>
        /// Catches a run counting its progress in lines, or a tool change showing the time the
        /// job had before it: stopped at the tool change, the estimate has the work before it
        /// done and the next tool's work left.
        /// </summary>
        [Fact]
        public async Task AtAToolChange_TheEstimate_LeavesTheNextToolsWork()
        {
            using var machine = CreateMachineToEstimate(CutThenToolChange);
            var controller = new MillingController(machine) { Options = new MillingOptions() };
            JobEstimate? atTheToolChange = null;
            controller.ToolChangeDetected += _ => atTheToolChange = controller.Estimate;

            using var cts = new CancellationTokenSource();
            var run = controller.StartAsync(cts.Token);
            try
            {
                await AsyncWait.WaitUntilAsync(() => atTheToolChange != null || controller.HasFinished,
                    ToolChangeTimeoutMessage, ToolChangeWaitTimeoutMs);
            }
            finally
            {
                cts.Cancel();
                await AwaitRunOutcomeAsync(run);
            }

            Assert.NotNull(atTheToolChange);
            Assert.Equal(SecondsAfterTheToolChange, atTheToolChange!.TimeLeft.TotalSeconds, EstimatePrecision);
            Assert.Equal(SecondsBeforeTheToolChange / (SecondsBeforeTheToolChange + SecondsAfterTheToolChange),
                atTheToolChange.FractionDone, EstimatePrecision);
        }

        private const int EstimatePrecision = 3;

        /// <summary>
        /// Catches a finished run showing time left, here a last dwell no tool position can
        /// show is done, or its estimate outliving the run once released.
        /// </summary>
        [Fact]
        public async Task ACompletedRun_HasAllDoneAndNothingLeft_UntilReleased()
        {
            using var machine = CreateMachineToEstimate(EndsInADwell);
            var controller = new MillingController(machine) { Options = new MillingOptions() };

            await controller.StartAsync();

            Assert.Equal(ControllerState.Completed, controller.State);
            Assert.Equal(new JobEstimate(1, TimeSpan.Zero), controller.Estimate);

            await controller.ReleaseAsync();
            Assert.Null(controller.Estimate);
        }

        private static readonly string[] EndsInADwell = { "G21", "G90", "G0 X0 Y0 Z1", "G1 Z-0.1 F100", "G1 X1 F100", "G0 Z1", "G4 P1" };

        /// <summary>Catches top speeds read once per connection, so a change to GRBL's settings goes unseen.</summary>
        [Fact]
        public async Task EachRun_ReadsGrblsSettings()
        {
            using var machine = CreateMachineToEstimate(EndsInADwell);
            var controller = new MillingController(machine) { Options = new MillingOptions() };

            await controller.StartAsync();
            await controller.ReleaseAsync();
            await controller.StartAsync();

            Assert.Equal(2, machine.SettingsRefreshCount);
        }

        /// <summary>Catches a run stopped because GRBL would not list its settings, which only the estimate uses.</summary>
        [Fact]
        public async Task ARun_WhoseSettingsGrblWillNotList_StillCompletes()
        {
            using var machine = CreateMachineToEstimate(EndsInADwell);
            machine.SettingsRefreshSucceeds = false;
            var controller = new MillingController(machine) { Options = new MillingOptions() };

            await controller.StartAsync();

            Assert.Equal(ControllerState.Completed, controller.State);
        }

        [Fact]
        public void M6Pattern_MatchesVariousFormats()
        {
            var testCases = new[]
            {
                ("M6 T1", true, 1),
                ("M06 T2", true, 2),
                ("m6 t3", true, 3),
                ("  M6 T4  ", true, 4),
                ("M6", true, 0),
                ("G0 X0", false, 0),
            };

            foreach (var (line, shouldMatch, expectedTool) in testCases)
            {
                // Assert against GCodeParser, not a copy of the regex: a copy can keep passing
                // while the production recognizer is wrong.
                Assert.Equal(shouldMatch, GCodeParser.IsM6Line(line));

                if (shouldMatch)
                {
                    var (toolNumber, _) = GCodeParser.FindToolInfo(new[] { line }, 0);
                    Assert.Equal(expectedTool, toolNumber ?? 0);
                }
            }
        }

        // An aborted tool change must not leave the controller unusable for the rest of the
        // session: IsPaused derives from ControllerState, which Reset() returns to Idle.

        [Fact]
        public async Task MillingAfterAnAbortedToolChange_StillDetectsTheNextOne()
        {
            using var machine = CreateFastFakeMachine(FileWithToolChange(FirstAbortedToolNumber));
            machine.IsHomed = true;
            var controller = new MillingController(machine)
            {
                Options = new MillingOptions ()
            };

            await AbortDuringToolChangeAsync(controller, FirstAbortedToolNumber);

            // Reuse the controller from the abort above: M6 detection must still fire on this
            // second run.
            machine.LoadFile(FileWithToolChange(SecondRunToolNumber));
            using var secondRunCts = new CancellationTokenSource();
            var secondRun = controller.StartAsync(secondRunCts.Token);

            ToolChangeInfo secondToolChange;
            try
            {
                secondToolChange = await WaitForToolChangeOrTimeoutAsync(controller, ToolChangeWaitTimeoutMs);
            }
            finally
            {
                secondRunCts.Cancel();
                await secondRun;
            }

            Assert.Equal(SecondRunToolNumber, secondToolChange.ToolNumber);
            Assert.Equal(ToolChangeLineIndex, secondToolChange.LineNumber);
        }

        [Fact]
        public async Task MillingAfterAnAbortedToolChange_StillReachesCompletion()
        {
            using var machine = CreateFastFakeMachine(FileWithToolChange(FirstAbortedToolNumber));
            machine.IsHomed = true;
            var controller = new MillingController(machine)
            {
                Options = new MillingOptions ()
            };

            await AbortDuringToolChangeAsync(controller, FirstAbortedToolNumber);

            // Reuse the controller from the abort above, now with a file that has no M6:
            // completion must still be detected.
            machine.LoadFile(FileWithoutToolChange);
            using var secondRunCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(CompletionWaitTimeoutMs));
            await controller.StartAsync(secondRunCts.Token);

            Assert.Equal(ControllerState.Completed, controller.State);
        }

        // Machine.SetFile marks M0/M1/M2/M30 as pause lines alongside M6, so the stream stops
        // at all of them. Each stop must end in a prompt or a completed job, or the run sits
        // mid-cut with nothing on screen.

        private const string UserInputTimeoutMessage = "Timed out waiting for UserInputRequired.";

        /// <summary>
        /// Bounded so a controller that never fires UserInputRequired fails the test with a
        /// TimeoutException instead of hanging it.
        /// </summary>
        private static async Task<UserInputRequest> WaitForUserInputOrTimeoutAsync(
            MillingController controller, int timeoutMs)
        {
            var tcs = new TaskCompletionSource<UserInputRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnUserInputRequired(UserInputRequest request) => tcs.TrySetResult(request);

            controller.UserInputRequired += OnUserInputRequired;
            try
            {
                return await MachineWait.AwaitReplyOrTimeoutAsync(
                    tcs.Task, timeoutMs, UserInputTimeoutMessage, CancellationToken.None);
            }
            finally
            {
                controller.UserInputRequired -= OnUserInputRequired;
            }
        }

        /// <summary>
        /// Queues every prompt a run raises, so a test can subscribe before StartAsync. The
        /// one-shot waiter above loses a prompt raised before it subscribes, and the door
        /// prompt is the first one raised.
        /// </summary>
        private sealed class PromptRecorder
        {
            private readonly ConcurrentQueue<UserInputRequest> _requests = new();

            public PromptRecorder(MillingController controller)
            {
                controller.UserInputRequired += _requests.Enqueue;
            }

            public IReadOnlyCollection<UserInputRequest> All => _requests;

            public async Task<UserInputRequest> NextAsync(int timeoutMs)
            {
                await AsyncWait.WaitUntilAsync(() => !_requests.IsEmpty, UserInputTimeoutMessage, timeoutMs);

                // One reader, so the request the wait saw is still there.
                Assert.True(_requests.TryDequeue(out var request), UserInputTimeoutMessage);
                return request!;
            }
        }

        // Opening the enclosure makes GRBL park and hold, and closing it does not end the hold
        // (GetDoorState returns WaitingForResume for Door:0). A run waits out an open door and
        // prompts for a closed one, because the cycle start that releases it restarts the
        // spindle.

        [Fact]
        public async Task MovingMachineAtSettleTimeout_FailsRunWithReason()
        {
            // Nothing checks for a moving machine before settling, because such a check also
            // blocks a door hold the controller can release. The settling phase is what
            // reports it.
            var machine = CreateMachineWithFile("G21", "G90", "G1 X1 Y1 F100");
            machine.Status = GrblProtocol.StatusRun;

            string? reported = await RunAndCaptureErrorAsync(machine);

            Assert.Equal(ErrorMachineNotSettled, reported);
        }

        [Fact]
        public async Task AnAlarmBeforeTheJob_ReportsThatItMustBeCleared()
        {
            var machine = CreateMachineWithFile("G21", "G90", "G1 X1 Y1 F100");
            machine.Status = GrblProtocol.StatusAlarm;

            string? reported = await RunAndCaptureErrorAsync(machine);

            Assert.Equal(ErrorAlarmBeforeStart, reported);
        }

        [Fact]
        public async Task ResumeAtADoorHold_IsRefusedNotQueued()
        {
            // GRBL holding at the door takes lines into its planner and runs them when the
            // hold is released, so restarting the stream here fills the buffer and leaves every
            // screen reporting a running job. The refusal is reported, not thrown: the terminal
            // calls Resume from a key press with nothing to catch it.
            using var machine = CreateFastFakeMachine(FileWithoutToolChange);

            machine.IsHomed = true;
            var controller = new MillingController(machine)
            {
                Options = new MillingOptions ()
            };

            var run = controller.StartAsync();
            await AsyncWait.WaitUntilAsync(() => controller.State == ControllerState.Running, "the controller to reach Running",
                CompletionWaitTimeoutMs);

            controller.Pause();
            await AsyncWait.WaitUntilAsync(() => controller.State == ControllerState.Paused, "the controller to reach Paused",
                StateTransitionWaitTimeoutMs);

            machine.SimulateDoorClosedAndHolding();

            ControllerError? refused = null;
            controller.ErrorOccurred += error => refused = error;

            controller.Resume();

            Assert.Equal(ErrorDoorClosedStillHolding, refused?.Message);
            Assert.False(refused?.IsFatal, "a door hold leaves the run paused, not failed");
            Assert.Equal(ControllerState.Paused, controller.State);

            machine.SimulateDoorReleased();
            await controller.StopAsync();
            await AwaitRunOutcomeAsync(run);
        }

        [Fact]
        public async Task DoorOpenedWhileSettling_IsPromptedNotTimedOut()
        {
            // The enclosure prompt is raised once before settling. A door opened during
            // settling prompts again and restarts the settle timeout.
            using var machine = CreateFastFakeMachine(FileWithoutToolChange);

            machine.IsHomed = true;
            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { SettleTimeoutMs = ShortSettleMs }
            };

            var prompts = new PromptRecorder(controller);
            var run = controller.StartAsync();

            await AsyncWait.WaitUntilAsync(() => controller.State == ControllerState.Initializing, "the controller to reach Initializing",
                StateTransitionWaitTimeoutMs);
            machine.SimulateDoorClosedAndHolding();

            var request = await prompts.NextAsync(ShortSettleMs * 4);
            Assert.Equal(DoorHoldingPrompt, request.Message);
            request.OnResponse(OptionContinue);

            await AsyncWait.WaitUntilAsync(() => !MachineWait.IsDoor(machine), "the door hold to clear", StateTransitionWaitTimeoutMs);

            await controller.StopAsync();
            await AwaitRunOutcomeAsync(run);
        }

        [Fact]
        public async Task DoorRelease_RestartsSettleTimeout()
        {
            // The time the operator spends at the enclosure must not count against the settle
            // timeout, or a slow answer fails a machine that is already stopped.
            using var machine = CreateFastFakeMachine(FileWithoutToolChange);

            machine.IsHomed = true;
            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { SettleTimeoutMs = SettleWaitMs }
            };

            var prompts = new PromptRecorder(controller);
            string? reported = null;
            controller.ErrorOccurred += error => reported = error.Message;

            var run = controller.StartAsync();

            await AsyncWait.WaitUntilAsync(() => controller.State == ControllerState.Initializing, "the controller to reach Initializing",
                StateTransitionWaitTimeoutMs);
            machine.SimulateDoorClosedAndHolding();

            var request = await prompts.NextAsync(SettleWaitMs);

            // Answer only after the whole settle timeout would have run out.
            await Task.Delay(SettleWaitMs + SettleMarginMs);
            request.OnResponse(OptionContinue);

            await AsyncWait.WaitUntilAsync(() => controller.State == ControllerState.Running, "the controller to reach Running",
                CompletionWaitTimeoutMs);

            Assert.Equal(ControllerState.Running, controller.State);
            Assert.Null(reported);

            await controller.StopAsync();
            await AwaitRunOutcomeAsync(run);
        }

        [Fact]
        public async Task DoorOpenedMidCut_IsPromptedNotStalled()
        {
            // GRBL holds and the stream stalls. Neither screen's resume releases a door hold,
            // so without the prompt the only exits are Stop or a soft reset.
            using var machine = CreateFastFakeMachine(ALongCut);

            machine.IsHomed = true;
            var controller = new MillingController(machine)
            {
                Options = new MillingOptions ()
            };

            // Open the door off the stream's own progress rather than a delay, so the cut is
            // always still running when it happens.
            machine.FilePositionChanged += () =>
            {
                if (machine.FilePosition == DoorOpensAtLine)
                {
                    machine.SimulateDoorClosedAndHolding();
                }
            };

            var prompts = new PromptRecorder(controller);
            var run = controller.StartAsync();
            try
            {
                var request = await prompts.NextAsync(CompletionWaitTimeoutMs);
                Assert.Equal(DoorHoldingPrompt, request.Message);
                Assert.True(MachineWait.IsDoor(machine));

                request.OnResponse(OptionContinue);

                await AsyncWait.WaitUntilAsync(() => !MachineWait.IsDoor(machine), "the door hold to clear", StateTransitionWaitTimeoutMs);
            }
            finally
            {
                await controller.StopAsync();
                await AwaitRunOutcomeAsync(run);
            }
        }

        /// <summary>Partway through <see cref="ALongCut"/>, so the stream is still running.</summary>
        private const int DoorOpensAtLine = 10;

        /// <summary>
        /// An M0 pause leaves the tool down, so this is where the operator opens the enclosure.
        /// Answering the pause with the door closed is consent to restart, so the hold is
        /// released without a second prompt for the same door.
        /// </summary>
        [Fact]
        public async Task ContinuingPastAnM0_ReleasesTheDoorWithoutAskingAgain()
        {
            using var machine = CreateFastFakeMachine("G21", "G90", "M0", "G1 X1 Y1 F100");

            machine.IsHomed = true;
            var controller = new MillingController(machine)
            {
                Options = new MillingOptions ()
            };

            var prompts = new PromptRecorder(controller);
            var run = controller.StartAsync();
            try
            {
                var pause = await prompts.NextAsync(CompletionWaitTimeoutMs);

                machine.SimulateDoorClosedAndHolding();
                pause.OnResponse(OptionContinue);

                await AsyncWait.WaitUntilAsync(() => controller.HasFinished, "the run to finish", CompletionWaitTimeoutMs);
                Assert.Equal(ControllerState.Completed, controller.State);
                Assert.DoesNotContain(prompts.All, p => p.IsDoorPrompt);
            }
            finally
            {
                await AwaitRunOutcomeAsync(run);
            }
        }

        /// <summary>
        /// A run that ends at the door queues no retract: GRBL keeps the move in its planner
        /// and runs it when the operator clears the hold.
        /// </summary>
        [Fact]
        public async Task AbandoningTheDoorPrompt_QueuesNoRetract()
        {
            using var machine = CreateFastFakeMachine(FileWithoutToolChange);

            machine.IsHomed = true;
            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { SettleTimeoutMs = ShortSettleMs }
            };

            var prompts = new PromptRecorder(controller);
            var run = controller.StartAsync();

            await AsyncWait.WaitUntilAsync(() => controller.State == ControllerState.Initializing, "the controller to reach Initializing",
                StateTransitionWaitTimeoutMs);
            machine.SimulateDoorClosedAndHolding();

            var request = await prompts.NextAsync(ShortSettleMs * 4);
            Assert.Equal(DoorHoldingPrompt, request.Message);

            machine.ClearSentCommands();
            request.OnResponse(OptionAbort);
            await AwaitRunOutcomeAsync(run);

            // G53 G0 Z is the machine-coordinate retract; the work-coordinate form never
            // appears on this path, so matching on it would assert nothing.
            Assert.DoesNotContain(machine.SentCommands,
                c => c.StartsWith(GrblProtocol.CmdMachineCoords, StringComparison.Ordinal)
                    && c.Contains(" Z", StringComparison.Ordinal));
        }

        /// <summary>How long a settle test spends on a machine that never settles.</summary>
        private const int ShortSettleMs = 1_500;

        /// <summary>
        /// Allow at least IdleSettleMs of uninterrupted idle; a shorter timeout would fail
        /// even when the machine settles.
        /// </summary>
        private const int SettleWaitMs = IdleSettleMs + 3_000;

        /// <summary>
        /// Runs a job that is expected to fail and returns the message reported.
        /// </summary>
        private static async Task<string?> RunAndCaptureErrorAsync(MockMachine machine)
        {
            machine.IsHomed = true;
            var controller = new MillingController(machine)
            {
                Options = new MillingOptions { SettleTimeoutMs = ShortSettleMs }
            };

            string? reported = null;
            controller.ErrorOccurred += error => reported = error.Message;

            await AwaitRunOutcomeAsync(controller.StartAsync());
            Assert.Equal(ControllerState.Failed, controller.State);
            return reported;
        }

        private static async Task AwaitRunOutcomeAsync(Task run)
        {
            try
            {
                await run;
            }
            catch (InvalidOperationException)
            {
                // The refusal under test; its text arrives through ErrorOccurred.
            }
            catch (OperationCanceledException)
            {
            }
        }

        [Fact]
        public async Task JobStartAtClosedDoor_PromptsThenReleasesTheHold()
        {
            using var machine = CreateFastFakeMachine(FileWithoutToolChange);
            machine.SimulateDoorClosedAndHolding();

            machine.IsHomed = true;
            var controller = new MillingController(machine)
            {
                Options = new MillingOptions ()
            };

            // The door prompt is the first thing a run raises, so the subscription has to be
            // in place before it starts.
            var prompts = new PromptRecorder(controller);
            var run = controller.StartAsync();

            var request = await prompts.NextAsync(StateTransitionWaitTimeoutMs);
            Assert.Contains(OptionContinue, request.Options);
            Assert.True(MachineWait.IsDoor(machine), "the machine should still be holding while it asks");

            request.OnResponse(OptionContinue);

            await AsyncWait.WaitUntilAsync(() => !MachineWait.IsDoor(machine), "the door hold to clear", StateTransitionWaitTimeoutMs);

            await controller.StopAsync();
            try { await run; } catch (OperationCanceledException) { }
        }

        [Fact]
        public async Task JobStartAtOpenDoor_Waits_ThenPromptsWhenClosed()
        {
            using var machine = CreateFastFakeMachine(FileWithoutToolChange);
            machine.SimulateDoorOpen();

            machine.IsHomed = true;
            var controller = new MillingController(machine)
            {
                Options = new MillingOptions ()
            };

            var prompts = new PromptRecorder(controller);
            var run = controller.StartAsync();

            // An open door gives the operator nothing to answer, so the run waits without
            // prompting. GRBL would ignore a cycle start here anyway.
            await Task.Delay(DoorResumeTimeoutMs + StateTransitionWaitTimeoutMs);
            Assert.True(MachineWait.IsDoor(machine));
            Assert.Empty(prompts.All);

            // The run prompts once the door is closed, because the cycle start restarts the
            // spindle.
            machine.SimulateDoorClosedAndHolding();

            var asked = await prompts.NextAsync(DoorResumeTimeoutMs * 3);
            Assert.Equal(DoorHoldingPrompt, asked.Message);
            asked.OnResponse(OptionContinue);

            await AsyncWait.WaitUntilAsync(() => !MachineWait.IsDoor(machine), "the door hold to clear", StateTransitionWaitTimeoutMs);

            await controller.StopAsync();
            try { await run; } catch (OperationCanceledException) { }
        }

        /// <summary>
        /// The operator has just said the enclosure is clear to start this job, so a hold an
        /// earlier run left closed and parked is released on that answer, not asked about a
        /// second time before anything has moved.
        /// </summary>
        [Fact]
        public async Task JobStartAtClosedDoor_WithTheEnclosureAnswer_ReleasesWithoutAsking()
        {
            using var machine = CreateFastFakeMachine(FileWithoutToolChange);
            machine.SimulateDoorClosedAndHolding();

            machine.IsHomed = true;
            var controller = new MillingController(machine)
            {
                Options = MillingOptions.Create(filePath: null, homeFirst: false, enclosureConfirmed: true)
            };

            var prompts = new PromptRecorder(controller);
            var run = controller.StartAsync();

            await AsyncWait.WaitUntilAsync(() => !MachineWait.IsDoor(machine), "the door hold to clear", StateTransitionWaitTimeoutMs);
            Assert.Empty(prompts.All);

            await controller.StopAsync();
            try { await run; } catch (OperationCanceledException) { }
        }

        /// <summary>
        /// The answer covered the enclosure as it was then. Once the door is seen open, a
        /// hand may be inside, so the hold that follows is put to the operator.
        /// </summary>
        [Fact]
        public async Task ADoorOpenedAfterTheEnclosureAnswer_IsAskedAbout()
        {
            using var machine = CreateFastFakeMachine(FileWithoutToolChange);
            machine.SimulateDoorOpen();

            machine.IsHomed = true;
            var controller = new MillingController(machine)
            {
                Options = MillingOptions.Create(filePath: null, homeFirst: false, enclosureConfirmed: true)
            };

            var doorAnnounced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            controller.ProgressChanged += progress =>
            {
                if (progress.Message == DoorOpenPrompt)
                {
                    doorAnnounced.TrySetResult();
                }
            };

            var prompts = new PromptRecorder(controller);
            var run = controller.StartAsync();

            await doorAnnounced.Task.WaitAsync(TimeSpan.FromMilliseconds(StateTransitionWaitTimeoutMs));
            machine.SimulateDoorClosedAndHolding();

            var asked = await prompts.NextAsync(StateTransitionWaitTimeoutMs);
            Assert.Equal(DoorHoldingPrompt, asked.Message);

            asked.OnResponse(OptionAbort);
            try { await run; } catch (OperationCanceledException) { }
        }

        /// <summary>
        /// Resume releases the feed hold this controller placed. The status it would check is
        /// a report that can predate the hold, so the cycle start goes out regardless.
        /// </summary>
        [Fact]
        public async Task ResumeAfterAPause_SendsTheCycleStart_WhateverTheStatusReads()
        {
            using var machine = CreateFastFakeMachine(ALongCut);

            machine.IsHomed = true;
            var controller = new MillingController(machine)
            {
                Options = new MillingOptions ()
            };

            var run = controller.StartAsync();
            try
            {
                await AsyncWait.WaitUntilAsync(() => controller.State == ControllerState.Running, "the controller to reach Running", CompletionWaitTimeoutMs);
                controller.Pause();
                machine.SimulateStaleStatus(GrblProtocol.StatusRun);

                int before = machine.CycleStartCount;
                controller.Resume();

                Assert.Equal(before + 1, machine.CycleStartCount);
            }
            finally
            {
                await controller.StopAsync();
                try { await run; } catch (OperationCanceledException) { }
            }
        }

        /// <summary>
        /// A move sent while GRBL holds at the door sits in its planner and runs when the hold
        /// is released, so the tool would rise as the operator cleared the door.
        /// </summary>
        [Fact]
        public async Task StopAtDoor_QueuesNoRetract()
        {
            using var machine = CreateFastFakeMachine(FileWithoutToolChange);
            machine.SimulateDoorOpen();

            machine.IsHomed = true;
            var controller = new MillingController(machine)
            {
                Options = new MillingOptions ()
            };

            using var cts = new CancellationTokenSource();
            var run = controller.StartAsync(cts.Token);
            await AsyncWait.WaitUntilAsync(() => controller.IsRunInProgress, "the run to take the machine", StateTransitionWaitTimeoutMs);
            machine.ClearSentCommands();

            // Cancelling is how Stop reaches the run; cleanup runs as it unwinds.
            cts.Cancel();
            try { await run; } catch (OperationCanceledException) { }

            Assert.DoesNotContain(machine.SentCommands,
                c => c.StartsWith(GrblProtocol.CmdMachineCoords));
        }

        [Fact]
        public async Task BareM0MidFile_PromptsOperatorAndCompletesAfterContinue()
        {
            using var machine = CreateFastFakeMachine("G21", "G90", "M0", "G1 X1 Y1 F100");
            machine.IsHomed = true;
            var controller = new MillingController(machine)
            {
                Options = new MillingOptions ()
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(CompletionWaitTimeoutMs));
            var run = controller.StartAsync(cts.Token);

            var request = await WaitForUserInputOrTimeoutAsync(controller, ToolChangeWaitTimeoutMs);
            Assert.Contains(OptionContinue, request.Options);

            request.OnResponse(OptionContinue);
            await run;

            Assert.Equal(ControllerState.Completed, controller.State);
        }

        [Fact]
        public async Task M2NotOnFinalLine_CompletesRatherThanHanging()
        {
            // The M2 sits mid-file with a line after it, so a controller that only finishes at
            // end-of-file waits forever for a line the program never runs.
            using var machine = CreateFastFakeMachine("G21", "G90", "M2", "G1 X1 Y1 F100");
            machine.IsHomed = true;
            var controller = new MillingController(machine)
            {
                Options = new MillingOptions ()
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(CompletionWaitTimeoutMs));
            await controller.StartAsync(cts.Token);

            Assert.Equal(ControllerState.Completed, controller.State);
        }

        [Fact]
        public async Task M6_PausesForToolChangeEvenWhenPauseFileOnHoldIsFalse()
        {
            // PauseFileOnHold is a feed-hold preference. Turning it off must not skip the tool
            // change, or the job carries on cutting with the wrong tool.
            using var machine = CreateFastFakeMachine(FileWithToolChange(SingleRunToolNumber));
            machine.PauseFileOnHold = false;
            machine.IsHomed = true;
            var controller = new MillingController(machine)
            {
                Options = new MillingOptions ()
            };

            using var cts = new CancellationTokenSource();
            var run = controller.StartAsync(cts.Token);

            ToolChangeInfo detected;
            try
            {
                detected = await WaitForToolChangeOrTimeoutAsync(controller, ToolChangeWaitTimeoutMs);
            }
            finally
            {
                cts.Cancel();
                await run;
            }

            Assert.Equal(SingleRunToolNumber, detected.ToolNumber);
        }

        [Fact]
        public void LinesCompleted_ReflectsFilePosition()
        {
            var machine = CreateMachineWithFile("G0 X0", "G0 X10", "G0 X20");
            machine.FilePosition = 2;
            var controller = new MillingController(machine);

            Assert.Equal(2, controller.LinesCompleted);
        }

        [Fact]
        public void TotalLines_ReflectsFileCount()
        {
            var machine = CreateMachineWithFile("G0 X0", "G0 X10", "G0 X20");
            var controller = new MillingController(machine);

            Assert.Equal(3, controller.TotalLines);
        }

        [Fact]
        public async Task StopAsync_WhenIdle_DoesNothing()
        {
            var machine = CreateMachineWithFile("G0 X0");
            var controller = new MillingController(machine);

            await controller.StopAsync();

            Assert.Empty(machine.SentCommands);
        }
    }
}
