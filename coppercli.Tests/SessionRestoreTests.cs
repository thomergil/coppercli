using System;
using System.Collections.Generic;
using System.Linq;
using coppercli.Core.GCode;
using coppercli.Core.Util;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// Tests the session questions used by both interfaces. The work-origin question
    /// requires a connected machine; disconnecting would omit two of the four topics.
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
            WithStoredBoardAndMap(storedWorkZero: true, completeMap: false, () =>
            {
                Assert.Null(AnswerAsShown(SessionRestoreTopic.SetWorkZeroTrusted, false));
                Assert.Contains(SessionRestoreTopic.UnfinishedHeightMap, PendingTopics());
            });
        }

        /// <summary>
        /// Declining to reload the file leaves its height map describing a file that is not
        /// loaded. Asking about that map afterwards offers the operator a yes that can only
        /// fail.
        /// </summary>
        [Fact]
        public void DecliningTheFile_StopsTheMapQuestionBeingAsked()
        {
            var asked = RunRestoreWithStoredBoardAndPartialMap(storedWorkZero: false, yes: false);

            Assert.Contains(SessionRestoreTopic.ReloadFile, asked);
            Assert.DoesNotContain(SessionRestoreTopic.UnsavedHeightMap, asked);
            Assert.DoesNotContain(SessionRestoreTopic.UnfinishedHeightMap, asked);
        }

        /// <summary>
        /// Each answer, yes or no, must make its question's condition false, or the pass asks it
        /// again.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void RestoreQuestions_EndAfterEachTopicForEitherAnswer(bool yes)
        {
            var asked = RunRestoreWithStoredBoardAndPartialMap(storedWorkZero: true, yes);

            Assert.Equal(asked.Count, asked.Distinct().Count());
            Assert.Contains(SessionRestoreTopic.ReloadFile, asked);
            Assert.Contains(SessionRestoreTopic.SetWorkZeroTrusted, asked);
        }

        /// <summary>
        /// Saving deletes the autosave, so after a restart only the saved file holds the map;
        /// unless it is offered, the reloaded job mills without height correction.
        /// </summary>
        [Fact]
        public void ASavedMap_IsOfferedWhenItsFileIsLoadedAgain_AndAppliedOnYes()
        {
            WithBoardAndSavedMap(measuredForBoard: true, () =>
            {
                Assert.Contains(SessionRestoreTopic.SavedHeightMap, PendingTopics());

                Assert.Null(AnswerAsShown(SessionRestoreTopic.SavedHeightMap, true));
                Assert.True(AppState.AreProbePointsApplied);
                Assert.DoesNotContain(SessionRestoreTopic.SavedHeightMap, PendingTopics());
                Assert.False(AppState.HasCompleteMapNotApplied,
                    "the File menu would ask again whether to apply the map it just applied");
            });
        }

        /// <summary>A map measured for another board must not be offered for this one.</summary>
        [Fact]
        public void ASavedMapForAnotherFile_IsNotOffered()
        {
            WithBoardAndSavedMap(measuredForBoard: false, () =>
                Assert.DoesNotContain(SessionRestoreTopic.SavedHeightMap, PendingTopics()));
        }

        /// <summary>
        /// Saves a complete map as the Probe menu does, forgets it as a restart does, and loads
        /// the board again.
        /// </summary>
        private void WithBoardAndSavedMap(bool measuredForBoard, Action check)
        {
            _web.RestoreFixtureState();

            string board = System.IO.Path.GetTempFileName();
            string mapFile = System.IO.Path.GetTempFileName();
            var session = AppState.Session;
            string previousFile = session.LastLoadedGCodeFile;
            string previousMap = session.LastProbeFile;

            try
            {
                System.IO.File.WriteAllLines(board, new[] { "G21", "G90", "G0 X0 Y0 Z5" });
                string boardPath = System.IO.Path.GetFullPath(board);
                Assert.Null(AppState.LoadGCodeIntoMachine(GCodeFile.Load(boardPath)).Refused);

                var grid = new ProbeGrid(5.0, new Vector2(0, 0), new Vector2(10, 10))
                {
                    Context = new ProbeContext(
                        measuredForBoard ? boardPath : boardPath + ".other",
                        AppState.Machine.G54Offset)
                };
                for (int x = 0; x < grid.SizeX; x++)
                {
                    for (int y = 0; y < grid.SizeY; y++)
                    {
                        grid.AddPoint(x, y, 0.1);
                    }
                }

                Assert.Null(AppState.AdoptProbeGrid(grid));
                Assert.True(Persistence.SaveProbeToFile(mapFile));
                Assert.Equal(System.IO.Path.GetFullPath(mapFile), session.LastProbeFile);

                // What a restart leaves: the G-code loaded again, no map in memory, no autosave.
                AppState.DiscardProbeData();
                Persistence.ClearProbeAutoSave();
                Assert.Null(AppState.LoadGCodeIntoMachine(GCodeFile.Load(boardPath)).Refused);

                check();
            }
            finally
            {
                AppState.DiscardProbeData();
                Persistence.ClearProbeAutoSave();
                session.LastLoadedGCodeFile = previousFile;
                session.LastProbeFile = previousMap;
                Persistence.SaveSession();
                System.IO.File.Delete(board);
                System.IO.File.Delete(mapFile);
            }
        }

        /// <summary>
        /// Either answer must clear the question: nothing else stops the browser, which re-reads
        /// the list after every answer and page load, asking it again.
        /// </summary>
        [Theory]
        [InlineData(nameof(SessionRestoreTopic.ReloadFile), true)]
        [InlineData(nameof(SessionRestoreTopic.ReloadFile), false)]
        [InlineData(nameof(SessionRestoreTopic.SetWorkZeroTrusted), true)]
        [InlineData(nameof(SessionRestoreTopic.SetWorkZeroTrusted), false)]
        [InlineData(nameof(SessionRestoreTopic.UnfinishedHeightMap), true)]
        [InlineData(nameof(SessionRestoreTopic.UnfinishedHeightMap), false)]
        [InlineData(nameof(SessionRestoreTopic.UnsavedHeightMap), true)]
        [InlineData(nameof(SessionRestoreTopic.UnsavedHeightMap), false)]
        public void EveryAnswer_ClearsItsOwnQuestion(string topicName, bool yes)
        {
            var topic = Enum.Parse<SessionRestoreTopic>(topicName);
            bool completeMap = topic == SessionRestoreTopic.UnsavedHeightMap;
            WithStoredBoardAndMap(storedWorkZero: true, completeMap, () =>
            {
                Assert.Contains(topic, PendingTopics());
                Assert.Null(AnswerAsShown(topic, yes));
                Assert.DoesNotContain(topic, PendingTopics());
            });
        }

        [Fact]
        public void DecliningTheSavedMap_ClearsItsQuestion()
        {
            WithBoardAndSavedMap(measuredForBoard: true, () =>
            {
                Assert.Contains(SessionRestoreTopic.SavedHeightMap, PendingTopics());
                Assert.Null(AnswerAsShown(SessionRestoreTopic.SavedHeightMap, false));
                Assert.DoesNotContain(SessionRestoreTopic.SavedHeightMap, PendingTopics());
            });
        }

        /// <summary>
        /// Of two screens showing one question, the second to answer must not undo the first: a
        /// "no" after a "keep" would delete the kept map.
        /// </summary>
        [Fact]
        public void AnAnswerToAQuestionAlreadySettled_IsRefused()
        {
            WithStoredBoardAndMap(storedWorkZero: false, completeMap: false, () =>
            {
                string shown = DetailOf(SessionRestoreTopic.UnfinishedHeightMap);
                Assert.Null(SessionRestore.Answer(SessionRestoreTopic.UnfinishedHeightMap, shown, true));
                var kept = AppState.ProbePoints;

                Assert.Equal(CliConstants.ErrorQuestionAlreadyAnswered,
                    SessionRestore.Answer(SessionRestoreTopic.UnfinishedHeightMap, shown, false));
                Assert.Same(kept, AppState.ProbePoints);
                Assert.NotNull(Persistence.ReadProbeAutoSave());
            });
        }

        /// <summary>
        /// An answer to a question that has changed since it was shown, here a different map,
        /// must not delete the map now on disk.
        /// </summary>
        [Fact]
        public void AnAnswerToAQuestionThatChanged_IsRefused()
        {
            WithStoredBoardAndMap(storedWorkZero: false, completeMap: false, () =>
            {
                Assert.Equal(CliConstants.ErrorQuestionAlreadyAnswered,
                    SessionRestore.Answer(SessionRestoreTopic.UnfinishedHeightMap, "another map", false));
                Assert.NotNull(Persistence.ReadProbeAutoSave());
                Assert.Contains(SessionRestoreTopic.UnfinishedHeightMap, PendingTopics());
            });
        }

        /// <summary>
        /// A failed question stays pending but is skipped for the rest of the pass: asked again
        /// it repeats the failure, and ending the pass on it holds back the rest.
        /// </summary>
        [Fact]
        public void AFailedAnswer_IsSkippedForThePass_AndTheOthersAreAsked()
        {
            WithStoredBoardAndMap(storedWorkZero: false, completeMap: false, () =>
            {
                var asked = new List<SessionRestoreTopic>();
                var failures = new List<string>();

                Assert.True(SessionRestore.AskPendingSteps(
                    step =>
                    {
                        asked.Add(step.Topic);

                        // The file goes between the question and the answer, so the answer
                        // is refused.
                        if (step.Topic == SessionRestoreTopic.ReloadFile)
                        {
                            System.IO.File.Delete(AppState.Session.LastLoadedGCodeFile);
                        }
                        return true;
                    },
                    failures.Add));

                Assert.Equal(
                    new[] { SessionRestoreTopic.ReloadFile, SessionRestoreTopic.UnfinishedHeightMap },
                    asked);
                Assert.Single(failures);
            });
        }

        private static List<SessionRestoreTopic> PendingTopics() =>
            SessionRestore.GetPendingSteps().Select(step => step.Topic).ToList();

        private static string DetailOf(SessionRestoreTopic topic) =>
            SessionRestore.GetPendingSteps().Single(step => step.Topic == topic).Detail;

        /// <summary>Answers the pending question on <paramref name="topic"/> as shown.</summary>
        private static string? AnswerAsShown(SessionRestoreTopic topic, bool yes) =>
            SessionRestore.Answer(topic, DetailOf(topic), yes);

        /// <summary>
        /// Return the topics asked with a stored board and a map with one measured point.
        /// </summary>
        private List<SessionRestoreTopic> RunRestoreWithStoredBoardAndPartialMap(
            bool storedWorkZero, bool yes)
        {
            var asked = new List<SessionRestoreTopic>();
            int topics = Enum.GetValues<SessionRestoreTopic>().Length;

            WithStoredBoardAndMap(storedWorkZero, completeMap: false, () =>
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
                    _ => { })));

            return asked;
        }

        /// <summary>
        /// What a restart leaves: last time's file recorded but not loaded, the origin not yet
        /// trusted, and an autosave for that file with one measured point, or all of them when
        /// <paramref name="completeMap"/>.
        /// </summary>
        private void WithStoredBoardAndMap(bool storedWorkZero, bool completeMap, Action check)
        {
            // AppState is process-wide, and another test in this collection may leave a
            // disconnected machine behind, which raises no work-origin question.
            _web.RestoreFixtureState();

            string board = System.IO.Path.GetTempFileName();
            var session = AppState.Session;
            string previousFile = session.LastLoadedGCodeFile;
            bool previousWorkZero = session.HasStoredWorkZero;
            var previousGCode = AppState.CurrentFile;
            bool previousZeroSet = AppState.IsWorkZeroSet;

            try
            {
                System.IO.File.WriteAllLines(board, new[] { "G21", "G90", "G0 X0 Y0 Z5" });
                session.LastLoadedGCodeFile = System.IO.Path.GetFullPath(board);
                session.HasStoredWorkZero = storedWorkZero;
                AppState.CurrentFile = null;
                AppState.SetWorkZeroTrusted(false);
                AppState.DiscardProbeData();

                var stored = new ProbeGrid(5.0, new Vector2(0, 0), new Vector2(10, 10))
                {
                    Context = new ProbeContext(
                        System.IO.Path.GetFullPath(board), AppState.Machine.G54Offset)
                };
                for (int x = 0; x < (completeMap ? stored.SizeX : 1); x++)
                {
                    for (int y = 0; y < (completeMap ? stored.SizeY : 1); y++)
                    {
                        stored.RecordMeasurement(x, y, 0.1);
                    }
                }
                stored.Save(Persistence.GetProbeAutoSavePath());

                check();
            }
            finally
            {
                AppState.DiscardProbeData();
                Persistence.ClearProbeAutoSave();
                AppState.CurrentFile = previousGCode;
                AppState.SetWorkZeroTrusted(previousZeroSet);
                session.LastLoadedGCodeFile = previousFile;
                session.HasStoredWorkZero = previousWorkZero;
                Persistence.SaveSession();
                System.IO.File.Delete(board);
            }
        }
    }
}
