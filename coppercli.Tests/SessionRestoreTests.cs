using System;
using System.Collections.Generic;
using System.Linq;
using coppercli.Core.GCode;
using coppercli.Core.Util;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// Covers SessionRestore.AskPendingSteps, the one place the questions carried over from a
    /// previous session are derived; both interfaces ask them from there. Runs against the
    /// fixture's connected machine because the work-origin question is raised only for a
    /// connected one, and on a disconnected machine two of the four topics never appear.
    /// </summary>
    [Collection(WebServerCollection.Name)]
    public class SessionRestoreTests
    {
        private readonly WebServerFixture _web;

        public SessionRestoreTests(WebServerFixture web)
        {
            _web = web;
        }

        /// <summary>
        /// Declining the stored origin must still leave the map question asked. Tying the two
        /// together leaves a stored map unresolved and later treated as current.
        /// </summary>
        [Fact]
        public void HeightMapQuestion_DoesNotDependOnTheWorkZeroAnswer()
        {
            var grid = new ProbeGrid(5.0, new Vector2(0, 0), new Vector2(10, 10))
            {
                Context = new ProbeContext("/tmp/board.ngc", new Vector3(-1, -2, -3))
            };

            for (int x = 0; x < grid.SizeX; x++)
            {
                for (int y = 0; y < grid.SizeY; y++)
                {
                    grid.AddPoint(x, y, 0.1);
                }
            }

            Assert.True(grid.HasCompleteData);

            Assert.Equal(ProbeApplicability.Applicable,
                grid.GetApplicability("/tmp/board.ngc", new Vector3(-1, -2, -3)));
            Assert.Equal(ProbeApplicability.OriginMoved,
                grid.GetApplicability("/tmp/board.ngc", new Vector3(-9, -2, -3)));
        }

        /// <summary>
        /// Declining to reload the file leaves its height map describing a file that is not
        /// loaded. Asking about that map afterwards offers the operator a yes that can only
        /// fail.
        /// </summary>
        [Fact]
        public void DecliningTheFile_StopsTheMapQuestionBeingAsked()
        {
            var asked = WhileABoardAndItsMapAreRemembered(storedWorkZero: false, yes: false);

            Assert.Contains(SessionRestoreTopic.ReloadFile, asked);
            Assert.DoesNotContain(SessionRestoreTopic.UnsavedHeightMap, asked);
            Assert.DoesNotContain(SessionRestoreTopic.UnfinishedHeightMap, asked);
        }

        /// <summary>
        /// Each answer must change the state its question was derived from, or the sequence
        /// asks the same topic forever. The rule that ends it sits inside the sequence, so no
        /// front end can leave it out.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void TheStartupSequence_RunsOutWhateverTheAnswer(bool yes)
        {
            var asked = WhileABoardAndItsMapAreRemembered(storedWorkZero: true, yes);

            Assert.Equal(asked.Count, asked.Distinct().Count());
            Assert.Contains(SessionRestoreTopic.ReloadFile, asked);
            Assert.Contains(SessionRestoreTopic.SetWorkZeroTrusted, asked);
        }

        /// <summary>
        /// Returns the topics the sequence put to the operator. The stored map has one point
        /// of the grid measured, which is what raises the unfinished-map topic.
        /// </summary>
        private List<SessionRestoreTopic> WhileABoardAndItsMapAreRemembered(
            bool storedWorkZero, bool yes)
        {
            // AppState is process-wide, and another test in this collection may leave a
            // disconnected machine behind, which raises no work-origin question.
            _web.TakeBackAppState();

            string board = System.IO.Path.GetTempFileName();
            var session = AppState.Session;
            string previousFile = session.LastLoadedGCodeFile;
            bool previousWorkZero = session.HasStoredWorkZero;

            try
            {
                System.IO.File.WriteAllLines(board, new[] { "G21", "G90", "G0 X0 Y0 Z5" });
                session.LastLoadedGCodeFile = System.IO.Path.GetFullPath(board);
                session.HasStoredWorkZero = storedWorkZero;
                AppState.DiscardProbeData();

                var stored = new ProbeGrid(5.0, new Vector2(0, 0), new Vector2(10, 10))
                {
                    Context = new ProbeContext(
                        System.IO.Path.GetFullPath(board), AppState.Machine.G54Offset)
                };
                stored.RecordMeasurement(0, 0, 0.1);
                stored.Save(Persistence.GetProbeAutoSavePath());

                var asked = new List<SessionRestoreTopic>();
                int topics = Enum.GetValues<SessionRestoreTopic>().Length;

                Assert.True(SessionRestore.AskPendingSteps(
                    step =>
                    {
                        // Without this bound a sequence that never ends hangs the suite
                        // instead of failing it.
                        Assert.True(
                            asked.Count < topics,
                            "the sequence asked more questions than there are topics: "
                            + string.Join(", ", asked));

                        asked.Add(step.Topic);
                        return yes;
                    },
                    _ => { }));

                return asked;
            }
            finally
            {
                AppState.DiscardProbeData();
                Persistence.ClearProbeAutoSave();
                session.LastLoadedGCodeFile = previousFile;
                session.HasStoredWorkZero = previousWorkZero;
                System.IO.File.Delete(board);
            }
        }
    }
}
