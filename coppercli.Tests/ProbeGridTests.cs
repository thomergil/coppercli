using System;
using coppercli.Core.GCode;
using coppercli.Core.Util;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// Every cutting move has this grid's interpolated height added to its commanded Z, so a
    /// wrong height here is a wrong cut depth. Covers the grid geometry, interpolation inside
    /// and outside the probed area, save and load, the queue of points still to measure, and
    /// the neighbor deviation check that separates a bad reading from a good one.
    /// </summary>
    public class ProbeGridTests
    {
        private static ProbeGrid FullyProbed(double height = 0.0)
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
        /// Progress counts points taken off the queue and a skipped probe comes off without a
        /// height, so Progress reaching TotalPoints does not mean the map is usable.
        /// </summary>
        [Fact]
        public void SkippedPoint_LeavesGridIncompleteEvenWhenProgressLooksDone()
        {
            var grid = new ProbeGrid(5.0, new Vector2(0, 0), new Vector2(10, 10));

            for (int x = 0; x < grid.SizeX; x++)
            {
                for (int y = 0; y < grid.SizeY; y++)
                {
                    if (!(x == 1 && y == 1))
                    {
                        grid.RecordMeasurement(x, y, 0.1);
                    }
                }
            }

            // What ProbeController does with a failed probe when Options.AbortOnFail is off.
            grid.SkipPoint(1, 1);

            Assert.Equal(grid.TotalPoints, grid.Progress);
            Assert.False(grid.HasCompleteData);
        }

        [Fact]
        public void InterpolateZ_RefusesAnIncompleteGridInsteadOfThrowingNullRef()
        {
            var grid = new ProbeGrid(5.0, new Vector2(0, 0), new Vector2(10, 10));
            grid.AddPoint(0, 0, 0.1);

            var ex = Assert.Throws<InvalidOperationException>(() => grid.InterpolateZ(5, 5));
            Assert.Contains("re-probe", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A toolpath point exactly on the far edge must not index one past the last node.
        /// 20.3mm at 3mm spacing rounds to 7.000000000000001, so Ceiling gives 8 on an
        /// 8-node axis.
        /// </summary>
        [Fact]
        public void InterpolateZ_AtExactUpperEdge_DoesNotGoOutOfBounds()
        {
            var grid = new ProbeGrid(3.0, new Vector2(0, 0), new Vector2(20.3, 20.3));
            for (int x = 0; x < grid.SizeX; x++)
            {
                for (int y = 0; y < grid.SizeY; y++)
                {
                    grid.AddPoint(x, y, 0.25);
                }
            }

            double z = grid.InterpolateZ(20.3, 20.3);
            Assert.Equal(0.25, z, precision: 6);
        }

        /// <summary>
        /// Outside the probed area, interpolation uses the nearest edge height. Using
        /// the board's maximum height would add a step where outer traces are cut.
        /// </summary>
        [Fact]
        public void InterpolateZ_OutsideGrid_UsesNearestEdge()
        {
            var grid = new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20));

            for (int x = 0; x < grid.SizeX; x++)
            {
                for (int y = 0; y < grid.SizeY; y++)
                {
                    // Height varies along X only, so each edge has a single value.
                    grid.AddPoint(x, y, grid.GetCoordinates(x, y).X / 100.0);
                }
            }

            Assert.Equal(0.2, grid.MaxHeight, precision: 6);

            Assert.Equal(0.0, grid.InterpolateZ(-0.001, 10), precision: 3);
            Assert.Equal(0.0, grid.InterpolateZ(-5, 10), precision: 6);

            Assert.Equal(0.2, grid.InterpolateZ(25, 10), precision: 6);

            // No step in the height as a move crosses the edge of the probed area.
            Assert.Equal(
                grid.InterpolateZ(20, 7), grid.InterpolateZ(20.0001, 7), precision: 6);
        }

        [Fact]
        public void InterpolateZ_ReturnsProbedHeightOnAFlatBoard()
        {
            var grid = FullyProbed(-0.35);
            Assert.Equal(-0.35, grid.InterpolateZ(4.2, 7.9), precision: 6);
        }

        [Fact]
        public void SaveLoadRoundTrip_PreservesHeightsAndCompleteness()
        {
            var grid = FullyProbed(0.42);
            string path = System.IO.Path.GetTempFileName();
            try
            {
                grid.Save(path);
                var loaded = ProbeGrid.Load(path);

                Assert.True(loaded.HasCompleteData);
                Assert.Equal(grid.SizeX, loaded.SizeX);
                Assert.Equal(grid.SizeY, loaded.SizeY);
                Assert.Equal(0.42, loaded.InterpolateZ(5, 5), precision: 6);
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }
    
        /// <summary>
        /// A skipped probe comes off the queue without measuring the node, so the queue empties
        /// while the map stays incomplete. Without a requeue that node can never be measured
        /// again, leaving a map that can neither be applied nor finished.
        /// </summary>
        [Fact]
        public void SkippedPoints_AreRequeuedSoTheyCanBeProbedAgain()
        {
            var grid = new ProbeGrid(5.0, new Vector2(0, 0), new Vector2(10, 10));

            for (int x = 0; x < grid.SizeX; x++)
            {
                for (int y = 0; y < grid.SizeY; y++)
                {
                    if (!(x == 1 && y == 1))
                    {
                        grid.RecordMeasurement(x, y, 0.1);
                    }
                }
            }
            grid.SkipPoint(1, 1);

            Assert.Equal(0, grid.RemainingCount);
            Assert.False(grid.HasCompleteData);

            grid.RequeueUnmeasuredPoints();

            Assert.Equal(1, grid.RemainingCount);
            Assert.Equal((1, 1), grid.SnapshotRemaining()[0]);
        }

        [Fact]
        public void RequeueUnmeasuredPoints_OnACompleteGrid_LeavesItEmpty()
        {
            var grid = FullyProbed();
            grid.RequeueUnmeasuredPoints();

            Assert.Equal(0, grid.RemainingCount);
            Assert.True(grid.HasCompleteData);
        }
    
        /// <summary>
        /// The display reads the remaining points on the UI thread while the probe loop removes
        /// them on another. Enumerating the live list rather than a snapshot throws "Collection
        /// was modified; enumeration operation may not execute" and ends the run.
        /// </summary>
        [Fact]
        public void RemainingPointsCanBeReadWhileProbingRemovesThem()
        {
            var grid = new ProbeGrid(1.0, new Vector2(0, 0), new Vector2(30, 30));
            int total = grid.TotalPoints;

            Exception? readerFailure = null;

            var reader = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    // What DrawProbeMatrix does on each redraw, as fast as it can.
                    while (grid.RemainingCount > 0)
                    {
                        var snapshot = grid.SnapshotRemaining();
                        var seen = new HashSet<(int, int)>(snapshot);
                        _ = seen.Count;
                    }
                }
                catch (Exception ex)
                {
                    readerFailure = ex;
                }
            });

            // What the probe loop does for each point.
            for (int i = 0; i < total; i++)
            {
                grid.OrderRemainingBy(pt => pt.X * 1.0 + pt.Y);

                if (grid.TryPeekNext(out var next))
                {
                    grid.RecordMeasurement(next.X, next.Y, 0.05);
                }
            }

            reader.Wait(System.TimeSpan.FromSeconds(10));

            Assert.Null(readerFailure);
            Assert.Equal(0, grid.RemainingCount);
            Assert.True(grid.HasCompleteData);
        }

        [Fact]
        public void NeighborDeviation_WithNoMeasuredNeighbors_IsNull()
        {
            var grid = new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20));

            Assert.Null(grid.GetNeighborDeviation(0, 0, -0.5));
        }

        [Fact]
        public void NeighborDeviation_ExcludesCurrentNode()
        {
            var grid = new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20));

            // The controller records the height before it checks, so a node that counted its
            // own height would always read as within tolerance.
            grid.RecordMeasurement(0, 0, -5.0);

            Assert.Null(grid.GetNeighborDeviation(0, 0, -5.0));
        }

        [Fact]
        public void NeighborDeviation_MeasuresAgainstTheMeanOfMeasuredNeighbors()
        {
            var grid = new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20));

            grid.RecordMeasurement(0, 1, -0.10);
            grid.RecordMeasurement(1, 0, -0.30);

            // Mean of the two measured neighbors is -0.20, so -0.25 deviates by 0.05.
            double? deviation = grid.GetNeighborDeviation(0, 0, -0.25);

            Assert.NotNull(deviation);
            Assert.Equal(0.05, deviation!.Value, 6);
        }

        [Fact]
        public void NeighborDeviation_SkipsUnmeasuredNeighbors()
        {
            var grid = new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20));

            // An unmeasured neighbor is a hole, and must not count as a height of zero.
            grid.RecordMeasurement(1, 1, -0.40);

            double? deviation = grid.GetNeighborDeviation(1, 2, -0.40);

            Assert.NotNull(deviation);
            Assert.Equal(0.0, deviation!.Value, 6);
        }

        [Fact]
        public void NeighborDeviation_ExcludesDiagonalNodes()
        {
            var grid = new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20));

            // A diagonal is further than one grid step away, so it does not count as a
            // neighbor of this node.
            grid.RecordMeasurement(1, 1, -0.40);

            Assert.Null(grid.GetNeighborDeviation(0, 0, -0.40));
        }

        [Theory]
        [InlineData(-3.0, 2.5)]   // pushed past the surface
        [InlineData(2.0, 2.5)]    // stopped short, on debris or a shorted clip
        public void NeighborDeviation_IsUnsignedInBothDirections(
            double measured, double expectedDeviation)
        {
            var grid = new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20));

            grid.RecordMeasurement(0, 1, -0.50);
            grid.RecordMeasurement(1, 0, -0.50);

            double? deviation = grid.GetNeighborDeviation(0, 0, measured);

            Assert.NotNull(deviation);
            Assert.Equal(expectedDeviation, deviation!.Value, 6);
        }

        [Fact]
        public void NeighborDeviation_OnAWarpedBoardStaysSmallBetweenAdjacentNodes()
        {
            // A bowed board spans millimeters end to end while staying flat between any two
            // adjacent nodes, so comparing against the neighbors rather than the overall
            // range keeps a real warp from reading as a fault.
            var grid = new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(40, 40));

            for (int x = 0; x < grid.SizeX; x++)
            {
                for (int y = 0; y < grid.SizeY; y++)
                {
                    grid.AddPoint(x, y, -0.2 * x);
                }
            }

            // Ends of the board differ by 0.8mm; adjacent nodes by 0.2mm.
            double? deviation = grid.GetNeighborDeviation(2, 2, -0.4);

            Assert.NotNull(deviation);
            Assert.Equal(0.0, deviation!.Value, 6);
        }

        /// <summary>
        /// A skipped point leaves the map partial even when Progress reaches TotalPoints.
        /// Save and Apply must not be offered for that map.
        /// </summary>
        [Fact]
        public void AMapWithASkippedPoint_IsPartialHoweverFarTheQueueGot()
        {
            var grid = new ProbeGrid(5.0, new Vector2(0, 0), new Vector2(10, 10));

            for (int x = 0; x < grid.SizeX; x++)
            {
                for (int y = 0; y < grid.SizeY; y++)
                {
                    if (x != 1 || y != 1)
                    {
                        grid.RecordMeasurement(x, y, 0.1);
                    }
                }
            }

            grid.SkipPoint(1, 1);

            Assert.Equal(grid.TotalPoints, grid.Progress);
            Assert.Equal(ProbeDataState.Partial, grid.State);
            Assert.Equal(ProbeDataState.Partial, ProbeGrid.StateOf(grid));
        }

        [Fact]
        public void TheStateOfNoMapAtAll_IsNone() =>
            Assert.Equal(ProbeDataState.None, ProbeGrid.StateOf(null));

        [Fact]
        public void AMapWithNothingMeasured_IsReady() =>
            Assert.Equal(
                ProbeDataState.Ready,
                new ProbeGrid(5.0, new Vector2(0, 0), new Vector2(10, 10)).State);
    }
}
