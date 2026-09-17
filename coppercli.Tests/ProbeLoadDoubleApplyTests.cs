using System.IO;
using coppercli;
using coppercli.Core.Communication;
using coppercli.Core.GCode;
using coppercli.Core.Settings;
using coppercli.Core.Util;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// ApplyProbeGrid is additive (commanded Z += interpolated height). So loading a second
    /// height map over one that was already applied, without first restoring the un-corrected
    /// G-code, stacks both corrections and cuts at the wrong depth.
    /// AppState.LoadProbeGridFromFile reloads the original before adopting a new map, for
    /// both front ends.
    /// </summary>
    [Collection(WebServerCollection.Name)]
    public class ProbeLoadDoubleApplyTests
    {
        private static ProbeGrid ConstantHeightGrid(double height)
        {
            var grid = new ProbeGrid(5.0, new Vector2(0, 0), new Vector2(10, 10));
            for (int x = 0; x < grid.SizeX; x++)
            {
                for (int y = 0; y < grid.SizeY; y++)
                {
                    grid.RecordMeasurement(x, y, height);
                }
            }
            return grid;
        }

        /// <summary>
        /// A zero discards the map, and the operator is warned first. A screen that asks
        /// ProbePoints alone deletes an autosaved map with no warning.
        /// </summary>
        [Fact]
        public void AMapThatLivesOnlyInTheAutosave_IsStillWarnedAboutBeforeAZero()
        {
            string board = Path.GetTempFileName();
            try
            {
                AppState.Machine = new Machine();
                AppState.Session = new SessionState { LastLoadedGCodeFile = Path.GetFullPath(board) };
                AppState.DiscardProbeData();

                var stored = ConstantHeightGrid(0.1);
                stored.Context = new ProbeContext(
                    Path.GetFullPath(board), AppState.Machine.G54Offset);
                stored.Save(Persistence.GetProbeAutoSavePath());

                Assert.Null(AppState.ProbePoints);
                Assert.NotNull(Menus.JogMenu.MapAZeroWouldDiscard());
            }
            finally
            {
                AppState.DiscardProbeData();
                Persistence.ClearProbeAutoSave();
                File.Delete(board);
            }
        }

        /// <summary>
        /// Loading a board drops a height map measured for another one, and says why. Both
        /// front ends report that, so neither has to work out for itself what the load did to
        /// the map.
        /// </summary>
        [Fact]
        public void LoadingAnotherBoard_ReportsTheMapItDropped()
        {
            string first = Path.GetTempFileName();
            string second = Path.GetTempFileName();
            try
            {
                File.WriteAllLines(first, new[] { "G21", "G90", "G0 X0 Y0 Z5", "G1 X10 Y10 Z-1 F100" });
                File.WriteAllLines(second, new[] { "G21", "G90", "G0 X0 Y0 Z5", "G1 X5 Y5 Z-1 F100" });

                AppState.Machine = new Machine();
                AppState.Session = new SessionState { LastLoadedGCodeFile = Path.GetFullPath(first) };
                Assert.Null(AppState.LoadGCodeIntoMachine(GCodeFile.Load(first)).Refused);

                var map = ConstantHeightGrid(1.0);
                map.Context = new ProbeContext(Path.GetFullPath(first), AppState.Machine.G54Offset);
                Assert.Null(AppState.AdoptProbeGrid(map));
                Assert.Equal(ProbeApplicability.Applicable, AppState.DescribeApplicability(map));

                var loaded = AppState.LoadGCodeIntoMachine(GCodeFile.Load(second));

                Assert.Null(loaded.Refused);
                Assert.Null(AppState.ProbePoints);
                Assert.Equal(
                    AppState.GetInapplicableReason(ProbeApplicability.DifferentFile, first),
                    loaded.MapDiscardedBecause);
            }
            finally
            {
                File.Delete(first);
                File.Delete(second);
            }
        }

        /// <summary>
        /// Loading a partly measured map from a file clears the autosave, so a menu that asked
        /// the autosave offered no way to resume it. Both front ends judge the map the resume
        /// would act on, which is the one in memory.
        /// </summary>
        [Fact]
        public void APartlyMeasuredMapLoadedFromAFile_CanStillBeResumed()
        {
            string board = Path.GetTempFileName();
            string map = Path.GetTempFileName();
            try
            {
                AppState.Machine = new Machine();
                AppState.Session = new SessionState { LastLoadedGCodeFile = Path.GetFullPath(board) };
                AppState.DiscardProbeData();
                Persistence.ClearProbeAutoSave();

                var partial = new ProbeGrid(5.0, new Vector2(0, 0), new Vector2(10, 10));
                partial.RecordMeasurement(0, 0, 1.0);
                partial.Save(map);

                Assert.Null(AppState.LoadProbeGridFromFile(map).Refused);

                // Loading a grid from a file clears the autosave, so the only map is in memory.
                Assert.Null(Persistence.ReadProbeAutoSave());
                Assert.NotNull(AppState.CurrentProbeGrid);
                Assert.False(AppState.CurrentProbeGrid!.HasCompleteData);

                // The screen itself, not just the helper behind it: asked of the autosave
                // alone, the probe menu offered no way to resume this map.
                Assert.True(
                    Menus.ProbeMenu.HasIncompleteProbeData(),
                    "Continue probing was not offered for the map the screen would act on");
            }
            finally
            {
                AppState.DiscardProbeData();
                Persistence.ClearProbeAutoSave();
                File.Delete(board);
                File.Delete(map);
            }
        }

        /// <summary>
        /// "There is no saved map" and "the saved map is for another board" are different
        /// things to the operator, and both callers now relay whichever comes back rather
        /// than asking first. Interchangeable arms would read as one message.
        /// </summary>
        [Fact]
        public void RecoveringWithNoAutosave_AndOneThatDoesNotFit_SaySoDifferently()
        {
            string board = Path.GetTempFileName();
            try
            {
                AppState.Machine = new Machine();
                AppState.Session = new SessionState { LastLoadedGCodeFile = Path.GetFullPath(board) };
                Persistence.ClearProbeAutoSave();

                // Whatever ran before this left its own map behind; a refusal must adopt
                // nothing, which only means something from a known starting point.
                AppState.DiscardProbeData();
                Assert.Null(AppState.ProbePoints);

                Assert.Equal(
                    CliConstants.ProbeErrorNoAutosave,
                    AppState.ForceLoadProbeFromAutosave().Refused);

                // Present, and measured for a different board.
                var elsewhere = ConstantHeightGrid(1.0);
                elsewhere.Context = new ProbeContext("/some/other/board.ngc", AppState.Machine.G54Offset);
                elsewhere.Save(Persistence.GetProbeAutoSavePath());

                Assert.Equal(
                    CliConstants.ProbeAutosaveNotApplicable,
                    AppState.ForceLoadProbeFromAutosave().Refused);
                Assert.Null(AppState.ProbePoints);
            }
            finally
            {
                // Process-wide, so what this test adopted must not reach the next one.
                AppState.DiscardProbeData();
                Persistence.ClearProbeAutoSave();
                File.Delete(board);
            }
        }

        /// <summary>
        /// Loading a grid from a file clears the autosave, so there is nothing to move. The
        /// menu offers Save from the map in memory, so Save writes that.
        /// </summary>
        [Fact]
        public void SavingAMapThatCameFromAFile_WritesIt()
        {
            string loaded = Path.GetTempFileName();
            string saved = Path.GetTempFileName();
            try
            {
                AppState.Machine = new Machine();
                AppState.Session = new SessionState();
                ConstantHeightGrid(0.4).Save(loaded);

                Assert.Null(AppState.LoadProbeGridFromFile(loaded).Refused);
                Assert.Null(Persistence.ReadProbeAutoSave());
                Assert.NotNull(AppState.ProbePoints);

                Assert.True(Persistence.SaveProbeToFile(saved),
                    "Save was offered for a map in memory and then refused");

                var written = ProbeGrid.Load(saved);

                Assert.Equal(AppState.ProbePoints!.TotalPoints, written.TotalPoints);
                Assert.True(written.HasCompleteData, "the saved map has unmeasured points");
            }
            finally
            {
                File.Delete(loaded);
                File.Delete(saved);
            }
        }

        [Fact]
        public void LoadingSecondGridOverAppliedGrid_RestoresOriginalGCode_SoCorrectionsDoNotStack()
        {
            string nc = Path.GetTempFileName();
            string grid1Path = Path.GetTempFileName();
            string grid2Path = Path.GetTempFileName();
            try
            {
                // A single cutting move inside the grid bounds, so ApplyProbeGrid actually shifts it.
                File.WriteAllLines(nc, new[] { "G21", "G90", "G0 X0 Y0 Z5", "G1 X10 Y10 Z-1 F100" });
                ConstantHeightGrid(1.0).Save(grid1Path);
                ConstantHeightGrid(0.2).Save(grid2Path);

                AppState.Machine = new Machine();
                AppState.Session = new SessionState { LastLoadedGCodeFile = Path.GetFullPath(nc) };
                AppState.LoadGCodeIntoMachine(GCodeFile.Load(nc));

                string originalGCode = string.Join("\n", GCodeFile.Load(nc).GetGCode());

                // Apply the first grid: its correction is now baked into CurrentFile.
                Assert.Null(AppState.AdoptProbeGrid(ProbeGrid.Load(grid1Path)));
                Assert.Null(AppState.ApplyProbeData());
                Assert.NotEqual(originalGCode, string.Join("\n", AppState.CurrentFile!.GetGCode()));

                // Load a second grid. The fix must reload the original before this grid can be
                // applied; otherwise grid2 would stack on top of grid1's already-baked-in Z.
                Assert.Null(AppState.LoadProbeGridFromFile(grid2Path).Refused);

                Assert.False(AppState.AreProbePointsApplied);
                Assert.Equal(originalGCode, string.Join("\n", AppState.CurrentFile!.GetGCode()));

                // A grid from a file is already saved, so the autosave goes: left behind, it
                // is offered later as unsaved work belonging to a map nobody has.
                Assert.Null(Persistence.ReadProbeAutoSave());
            }
            finally
            {
                File.Delete(nc);
                File.Delete(grid1Path);
                File.Delete(grid2Path);
            }
        }
    }
}
