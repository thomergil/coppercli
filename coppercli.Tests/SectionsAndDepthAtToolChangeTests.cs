#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using coppercli;
using coppercli.Core.Controllers;
using coppercli.Core.GCode;
using coppercli.Core.GCode.GCodeCommands;
using coppercli.WebServer;
using Xunit;

namespace coppercli.Tests
{
    // Ending the sections and depth adjustment at a tool change, while a run waits there: the
    // machine's G-code is rebuilt for the rest of the job, the lines already sent stay as they
    // were, and the run's end clears them for the next run.
    [Collection(WebServerCollection.Name)]
    public class SectionsAndDepthAtToolChangeTests : JobFixtureTests
    {
        private const double TestDepth = -0.1;
        private const string ToolChangeLine = "M6";
        private const string DrillOutsideTheSection = "X15 Y15";

        /// <summary>A map high enough to lift every cut of the boards above the surface.</summary>
        private const double MapAboveTheCuts = 0.5;

        public SectionsAndDepthAtToolChangeTests(WebServerFixture web) : base(web)
        {
        }

        private static void ChooseSectionsAndDepth()
        {
            Assert.Null(AppState.SetDepthAdjustment(TestDepth));
            Assert.Null(AppState.ChooseMillSections(2, 2, new[] { new BoardCell(0, 0) }));
        }

        /// <summary>The line after the machine's first tool change, where the stream stops.</summary>
        private static int AfterTheFirstToolChange() => Array.IndexOf(OnTheMachine(), ToolChangeLine) + 1;

        /// <summary>Puts the stream after the machine's first tool change, as if it had stopped there.</summary>
        private static int StopAfterTheFirstToolChange()
        {
            int streamed = AfterTheFirstToolChange();
            AppState.Machine.FileGoto(streamed);
            return streamed;
        }

        /// <summary>
        /// Starts a run of <paramref name="board"/> with a map, the lower left section and a
        /// depth adjustment, runs <paramref name="body"/> while the run asks about them at the
        /// first tool change, then stops the run.
        /// </summary>
        private async Task WhileARunAsksAtTheToolChangeAsync(string[] board, Func<Task> body)
        {
            await _web.LoadWithAMapAppliedAsync(_boards.Write(board));
            ChooseSectionsAndDepth();
            var (code, _) = await _web.PostJsonAsync(WebConstants.ApiMillStart, await _web.MillStartBodyAsync());
            Assert.Equal(HttpStatusCode.OK, code);

            try
            {
                WebServerFixture.WaitUntil(() => PendingPrompt.Current?.Title == ControllerConstants.SectionsAndDepthTitle,
                    "the run to ask about the sections and depth");
                await body();
            }
            finally
            {
                await _web.PostJsonAsync(WebConstants.ApiMillStop);
                WebServerFixture.WaitUntil(() => !AppState.Milling.IsRunInProgress, "the run to end");
            }
        }

        /// <summary>
        /// Catches the lines already sent changing, the stream going back to the start, the rest
        /// of the job still clipped, deepened or without its map after the operator cleared them,
        /// and a stopped run leaving them behind for the next.
        /// </summary>
        [Fact]
        public async Task EndingThem_KeepsTheLinesSent_MillsTheRestOnTheWholeBoard_AndAStopClearsThem()
        {
            await WhileARunAsksAtTheToolChangeAsync(PhaseTestSupport.TwoPhaseBoard, () =>
            {
                var loaded = AppState.CurrentFile!;
                var sections = AppState.MillSections!;
                string[] sent = OnTheMachine();
                int streamed = AppState.Machine.FilePosition;
                Assert.Equal(AfterTheFirstToolChange(), streamed);
                long version = AppState.MachineFileVersion;

                Assert.Null(AppState.EndSectionsAndDepthAtTheToolChange());

                string[] now = OnTheMachine();
                Assert.Equal(sent.Take(streamed), now.Take(streamed));
                Assert.Equal(streamed, AppState.Machine.FilePosition);
                Assert.NotEqual(version, AppState.MachineFileVersion);
                Assert.Equal(loaded.KeepPart(null, sections, 1).File!.OffsetCutDepth(TestDepth, 1)
                    .ApplyProbeGrid(AppState.ProbePoints!).GetGCode(), now);
                Assert.Contains(now.Skip(streamed), line => line.Contains(DrillOutsideTheSection));
                Assert.False(AppState.SectionsOrDepthApplyAfterTheToolChange(), "the run would ask again about sections already ended");
                return Task.CompletedTask;
            });

            WebServerFixture.WaitUntil(() => AppState.MillSections == null, "the stopped run to clear the sections");
            Assert.Equal(0, AppState.DepthAdjustment);
            Assert.True(AppState.AreProbePointsApplied, "clearing the sections took the height map away");
        }

        /// <summary>
        /// Catches a rebuild made where the stream did not stop at a tool change, including at
        /// the file's first line, before any tool change, whose lines a rebuild leaves the same.
        /// </summary>
        [Fact]
        public async Task EndingThem_WhereTheStreamDidNotStopAtAToolChange_IsRefused_AndChangesNothing()
        {
            await WhileARunAsksAtTheToolChangeAsync(PhaseTestSupport.TwoPhaseBoard, () =>
            {
                string[] sent = OnTheMachine();
                long version = AppState.MachineFileVersion;

                foreach (int streamed in new[] { AppState.Machine.FilePosition - 1, 1 })
                {
                    AppState.Machine.FileGoto(streamed);
                    Assert.Equal(ControllerConstants.ErrorSectionsAndDepthNotEnded, AppState.EndSectionsAndDepthAtTheToolChange());
                }

                Assert.Equal(sent, OnTheMachine());
                Assert.Equal(version, AppState.MachineFileVersion);
                return Task.CompletedTask;
            });
        }

        /// <summary>
        /// Catches a rebuild that changes a line the machine has already run, which would put
        /// the rest of the job out of step with where the tool is.
        /// </summary>
        [Fact]
        public async Task EndingThem_WhenTheLinesSentWouldChange_IsRefused_AndChangesNothing()
        {
            await WhileARunAsksAtTheToolChangeAsync(PhaseTestSupport.TwoPhaseBoard, () =>
            {
                int streamed = AppState.Machine.FilePosition;
                var ranOtherLines = OnTheMachine().Select((line, i) => i == 0 ? line + " " : line).ToList();
                Assert.True(AppState.Machine.SetFile(ranOtherLines, streamed));
                long version = AppState.MachineFileVersion;

                Assert.Equal(ControllerConstants.ErrorSectionsAndDepthNotEnded, AppState.EndSectionsAndDepthAtTheToolChange());

                Assert.Equal(ranOtherLines, OnTheMachine());
                Assert.Equal(version, AppState.MachineFileVersion);
                Assert.NotNull(AppState.MillSections);
                return Task.CompletedTask;
            });
        }

        /// <summary>
        /// Catches the job rewritten for a milling run that is not waiting at a tool change,
        /// which no run's end would clear.
        /// </summary>
        [Fact]
        public void EndingThem_WithNoRunAtAToolChange_IsRefused_AndChangesNothing()
        {
            _boards.Load(PhaseTestSupport.TwoPhaseBoard);
            ChooseSectionsAndDepth();
            StopAfterTheFirstToolChange();
            string[] sent = OnTheMachine();

            Assert.Equal(ControllerConstants.ErrorSectionsAndDepthNotEnded, AppState.EndSectionsAndDepthAtTheToolChange());

            Assert.Equal(sent, OnTheMachine());
            Assert.NotNull(AppState.MillSections);
        }

        /// <summary>
        /// Catches the job rewritten for a run that has ended while it waited at its tool change,
        /// which no run's end would clear.
        /// </summary>
        [Fact]
        public async Task EndingThem_AfterTheRunAtTheToolChangeEnded_IsRefused_AndChangesNothing()
        {
            await WhileARunAsksAtTheToolChangeAsync(PhaseTestSupport.TwoPhaseBoard, () => Task.CompletedTask);
            StopAfterTheFirstToolChange();
            string[] sent = OnTheMachine();

            Assert.Equal(ControllerConstants.ErrorSectionsAndDepthNotEnded, AppState.EndSectionsAndDepthAtTheToolChange());

            Assert.Equal(sent, OnTheMachine());
            Assert.NotNull(AppState.MillSections);
        }

        /// <summary>Catches the job rewritten for a milling run that waits anywhere but at its tool change.</summary>
        [Fact]
        public async Task EndingThem_WhileARunWaitsElsewhere_IsRefused_AndChangesNothing()
        {
            await _web.LoadWithAMapAppliedAsync(_boards.Write(PhaseTestSupport.TwoPhaseBoard));
            ChooseSectionsAndDepth();

            await _web.WhileAMillRunHoldsAtTheDoorAsync(() =>
            {
                StopAfterTheFirstToolChange();
                string[] sent = OnTheMachine();

                Assert.Equal(ControllerConstants.ErrorSectionsAndDepthNotEnded, AppState.EndSectionsAndDepthAtTheToolChange());

                Assert.Equal(sent, OnTheMachine());
                return Task.CompletedTask;
            });
        }

        /// <summary>
        /// Catches the sections and depth left ending at a tool change after a run stopped during
        /// that tool change, while the tool change's own run was still stopping: the next run
        /// would mill the sections again up to that tool change.
        /// </summary>
        [Fact]
        public async Task AStop_DuringTheToolChangeAfterClear_StillLeavesTheWholeBoardForTheNextRun()
        {
            await WhileARunAsksAtTheToolChangeAsync(PhaseTestSupport.TwoPhaseBoard, async () =>
            {
                var question = PendingPrompt.Current!;
                var (code, _) = await _web.PostJsonAsync(WebConstants.ApiMillToolChangeUserInput,
                    new { id = question.Id, response = ControllerConstants.OptionClear });
                Assert.Equal(HttpStatusCode.OK, code);
                WebServerFixture.WaitUntil(() => AppState.ToolChange.IsRunInProgress, "the tool change to start");

                Assert.Equal(HttpStatusCode.OK, (await _web.PostJsonAsync(WebConstants.ApiMillToolChangeAbort)).Code);
            });

            WebServerFixture.WaitUntil(() => AppState.MillSections == null, "the stopped run to clear the sections");
            Assert.Equal(0, AppState.DepthAdjustment);
        }

        /// <summary>
        /// Catches the sections and depth left ending at a tool change when the milling run ends
        /// before the tool change's own run: the file cannot change until that run ends too, and
        /// the next run would mill the sections again up to that tool change.
        /// </summary>
        [Fact]
        public async Task AMillingRunEnded_BeforeItsToolChangeRun_StillLeavesTheWholeBoardForTheNextRun()
        {
            await WhileARunAsksAtTheToolChangeAsync(PhaseTestSupport.TwoPhaseBoard, async () =>
            {
                var question = PendingPrompt.Current!;
                var (code, _) = await _web.PostJsonAsync(WebConstants.ApiMillToolChangeUserInput,
                    new { id = question.Id, response = ControllerConstants.OptionClear });
                Assert.Equal(HttpStatusCode.OK, code);
                WebServerFixture.WaitUntil(() => AppState.ToolChange.IsRunInProgress, "the tool change to start");

                await AppState.Milling.StopAsync();
                Assert.True(AppState.ToolChange.IsRunInProgress, "the tool change's run ended with the milling run");
                Assert.NotNull(AppState.MillSections);
            });

            WebServerFixture.WaitUntil(() => AppState.MillSections == null, "the tool change's end to clear the sections");
            Assert.Equal(0, AppState.DepthAdjustment);
        }

        /// <summary>
        /// Catches the question asked at a tool change with nothing to keep or clear, or with no
        /// milling before it, such as Fusion's T1 M6 at the start of a file.
        /// </summary>
        [Fact]
        public void TheQuestion_IsOnlyForSectionsOrDepth_AfterACut()
        {
            _boards.Load(PhaseTestSupport.TwoPhaseBoard);
            StopAfterTheFirstToolChange();
            Assert.False(AppState.SectionsOrDepthApplyAfterTheToolChange());

            _boards.Load(new[] { "G21", "G90", "T1", "M6", "G0 X0 Y0 Z5", "G1 Z-0.1 F100", "G1 X10 F300", "G0 Z5", "M2" });
            Assert.Null(AppState.SetDepthAdjustment(TestDepth));
            StopAfterTheFirstToolChange();
            Assert.False(AppState.SectionsOrDepthApplyAfterTheToolChange());
        }

        /// <summary>Catches the question asked only when both sections and a depth adjustment are chosen.</summary>
        [Fact]
        public void TheQuestion_IsAskedForSectionsAlone_AndForADepthAdjustmentAlone()
        {
            _boards.Load(PhaseTestSupport.TwoPhaseBoard);
            Assert.Null(AppState.ChooseMillSections(2, 2, new[] { new BoardCell(0, 0) }));
            StopAfterTheFirstToolChange();
            Assert.True(AppState.SectionsOrDepthApplyAfterTheToolChange());

            _boards.Load(PhaseTestSupport.TwoPhaseBoard);
            Assert.Null(AppState.SetDepthAdjustment(TestDepth));
            StopAfterTheFirstToolChange();
            Assert.True(AppState.SectionsOrDepthApplyAfterTheToolChange());
        }

        /// <summary>
        /// Catches the question skipped on a board higher than its zero point: the map lifts
        /// every cut of the machine's G-code above the surface, and the milling is still done.
        /// </summary>
        [Fact]
        public void TheQuestion_IsAsked_WhenTheMapLiftsTheCutsAboveTheSurface()
        {
            _boards.Load(PhaseTestSupport.TwoPhaseBoard);
            Assert.Null(AppState.AdoptProbeGrid(WebServerFixture.CompleteMapForThisJob(MapAboveTheCuts)));
            Assert.Null(AppState.ApplyProbeData());
            ChooseSectionsAndDepth();
            Assert.DoesNotContain(AppState.MachineFile!.Toolpath.TakeWhile(command => command is not MCode { IsToolChange: true }),
                command => command is Motion { CutsTheBoard: true });
            StopAfterTheFirstToolChange();

            Assert.True(AppState.SectionsOrDepthApplyAfterTheToolChange());
        }

        /// <summary>
        /// Catches the whole path failing together: a run with sections and depth asks at the
        /// first tool change and not again, Clear drills outside the section, and the run's end
        /// leaves the whole board at the file's depth, with the map, for the next run.
        /// </summary>
        [Fact]
        public async Task ARunCleared_AtTheFirstToolChange_DrillsTheWholeBoard_AndLeavesNoSectionsBehind()
        {
            var (titles, ended, sent) = await RunAnsweringAsync(PhaseTestSupport.ThreePhaseBoard, ControllerConstants.OptionClear);

            Assert.Equal(new[] { ControllerConstants.SectionsAndDepthTitle },
                titles.Where(title => title == ControllerConstants.SectionsAndDepthTitle));
            Assert.Equal(ControllerState.Completed, ended);
            Assert.Contains(sent, line => line.Contains(DrillOutsideTheSection));
            WebServerFixture.WaitUntil(() => AppState.MillSections == null, "the run's end to clear the sections");
            Assert.Equal(0, AppState.DepthAdjustment);
            Assert.True(AppState.AreProbePointsApplied, "clearing the sections took the height map away");
        }

        /// <summary>
        /// Catches Keep at the first tool change ending the depth there, or Clear at the second
        /// leaving it on the last tool's work: the second drill keeps the adjusted depth, the
        /// third has the file's depth, both with the map.
        /// </summary>
        [Fact]
        public async Task ARunKeptAtTheFirstToolChange_AndClearedAtTheSecond_DrillsTheLastToolAtTheFilesDepth()
        {
            var (titles, ended, sent) = await RunAnsweringAsync(PhaseTestSupport.ThreePhaseBoard,
                ControllerConstants.OptionKeep, ControllerConstants.OptionClear);

            Assert.Equal(2, titles.Count(title => title == ControllerConstants.SectionsAndDepthTitle));
            Assert.Equal(ControllerState.Completed, ended);
            Assert.Contains(sent, line => line.Contains(SecondDrillWithTheDepthAndMap));
            Assert.Contains(sent, line => line.Contains(ThirdDrillWithTheMapOnly));
            Assert.DoesNotContain(sent, line => line.Contains(ThirdDrillWithTheDepthAndMap));
        }

        // The drills' depths in ThreePhaseBoard with TestDepth and the fixture's map, -0.1 everywhere.
        private const string SecondDrillWithTheDepthAndMap = "Z-2";
        private const string ThirdDrillWithTheMapOnly = "Z-1.1";
        private const string ThirdDrillWithTheDepthAndMap = "Z-1.2";

        /// <summary>Catches Keep clearing the sections, or drilling outside them.</summary>
        [Fact]
        public async Task ARunKept_AtTheToolChange_DrillsOnlyInTheSections_AndKeepsThemForTheNextRun()
        {
            var (_, ended, sent) = await RunAnsweringAsync(PhaseTestSupport.TwoPhaseBoard, ControllerConstants.OptionKeep);

            Assert.Equal(ControllerState.Completed, ended);
            Assert.DoesNotContain(sent, line => line.Contains(DrillOutsideTheSection));
            Assert.NotNull(AppState.MillSections);
            Assert.Equal(TestDepth, AppState.DepthAdjustment);
        }

        /// <summary>
        /// Runs <paramref name="board"/> with a map, the lower left section and a depth
        /// adjustment, answering each question about them with the next of
        /// <paramref name="answers"/> and every other prompt with Continue, until the run ends.
        /// </summary>
        /// <returns>The prompts' titles, the state the run ended in, and the lines GRBL received.</returns>
        private async Task<(List<string> Titles, ControllerState Ended, string[] Sent)> RunAnsweringAsync(
            string[] board, params string[] answers)
        {
            var answersLeft = new Queue<string>(answers);
            await _web.LoadWithAMapAppliedAsync(_boards.Write(board));
            ChooseSectionsAndDepth();
            int receivedBefore = _web.Grbl.Received.Count;
            var (code, _) = await _web.PostJsonAsync(WebConstants.ApiMillStart, await _web.MillStartBodyAsync());
            Assert.Equal(HttpStatusCode.OK, code);

            var titles = new List<string>();
            ControllerState ended = await AnswerPromptsUntilTheRunEndsAsync(prompt =>
            {
                titles.Add(prompt.Title);
                return Task.FromResult(prompt.Title == ControllerConstants.SectionsAndDepthTitle
                    ? answersLeft.Dequeue()
                    : ControllerConstants.OptionContinue);
            });

            return (titles, ended, _web.Grbl.Received.Skip(receivedBefore).ToArray());
        }
    }
}
