using System;
using System.Collections.Generic;
using System.Linq;
using coppercli.Core.GCode;
using coppercli.Core.Util;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// The questions carried over from a previous session are computed in one place and
    /// asked by both interfaces. These pin the rules that the two copies disagreed on.
    ///
    /// Driven through the fixture's connected machine, because the work-origin question is
    /// only raised for one: on a disconnected machine two of the four topics never appear
    /// and the sequence is only half tested.
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
        /// The height-map question must not depend on the work-zero answer. The terminal
        /// must not be skipped when the operator declines to trust the stored origin, or
        /// the data was never resolved - and later announced itself as current.
        /// </summary>
        [Fact]
        public void HeightMapQuestion_DoesNotDependOnTheWorkZeroAnswer()
        {
            // Expressed against the grid model the sequence is built on: a stored map is
            // a question in its own right, not a consequence of trusting an origin.
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

            // Whatever was decided about the origin, the map still describes the board it
            // names - that is what makes it a separate question.
            Assert.Equal(ProbeApplicability.Applicable,
                grid.GetApplicability("/tmp/board.ngc", new Vector3(-1, -2, -3)));
            Assert.Equal(ProbeApplicability.OriginMoved,
                grid.GetApplicability("/tmp/board.ngc", new Vector3(-9, -2, -3)));
        }

        /// <summary>
        /// Answering one question changes which of the rest apply. Declining to reload the
        /// file leaves a map measured for it describing nothing, so asking about that map
        /// afterwards offers the operator a yes that can only fail.
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
        /// The sequence must run out whatever the operator answers, and the rule that ends
        /// it lives inside the sequence so no front end can leave it out. Every answer used
        /// to leave the state its question was derived from unchanged.
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
        /// Runs the whole sequence against a remembered board with a part-measured map, and
        /// returns the topics it put to the operator.
        /// </summary>
        private List<SessionRestoreTopic> WhileABoardAndItsMapAreRemembered(
            bool storedWorkZero, bool yes)
        {
            // AppState is process-wide and another test in this collection may have left a
            // disconnected machine behind, which raises no work-origin question at all.
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
                        // Bounded: the defect is a sequence that never ends, and an
                        // unbounded walk would hang the suite rather than fail it.
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
