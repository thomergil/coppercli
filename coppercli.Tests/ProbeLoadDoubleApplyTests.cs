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
    /// G-code, stacks both corrections and cuts at the wrong depth. The web path reloaded the
    /// original first; the TUI did not. AppState.LoadProbeGridFromFile is now the single source
    /// that reloads the original for both. This pins that behaviour.
    /// </summary>
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
                AppState.ProbePoints = ProbeGrid.Load(grid1Path);
                Assert.True(AppState.ApplyProbeData());
                Assert.NotEqual(originalGCode, string.Join("\n", AppState.CurrentFile!.GetGCode()));

                // Load a second grid. The fix must reload the original before this grid can be
                // applied; otherwise grid2 would stack on top of grid1's already-baked-in Z.
                AppState.LoadProbeGridFromFile(grid2Path);

                Assert.False(AppState.AreProbePointsApplied);
                Assert.Equal(originalGCode, string.Join("\n", AppState.CurrentFile!.GetGCode()));
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
