using System;
using coppercli.Core.GCode;
using coppercli.Core.Util;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// The probe grid decides the commanded Z of every cutting move, so a wrong or
    /// guessed height here is a wrong cut depth on copper.
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
        /// Progress counts removals from NotProbed, and a skipped probe removes without
        /// measuring - so "progress complete" must not be mistaken for "usable map".
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

            // The one that failed comes off the queue without a height, exactly as
            // ProbeController does when told not to abort on a failed probe.
            grid.SkipPoint(1, 1);

            Assert.Equal(grid.TotalPoints, grid.Progress);   // looks finished...
            Assert.False(grid.HasCompleteData);              // ...but is not usable
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
        /// 20.3mm at 3mm spacing rounds to 7.000000000000001 -> Ceiling 8 on a 8-node axis.
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
        /// Outside the probed area the map takes the nearest edge. Taking the board's highest
        /// point instead stepped by the whole warp range at the boundary, which is where the
        /// outermost traces are cut.
        /// </summary>
        [Fact]
        public void InterpolateZ_OutsideTheGrid_TakesTheNearestEdge()
        {
            var grid = new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20));

            for (int x = 0; x < grid.SizeX; x++)
            {
                for (int y = 0; y < grid.SizeY; y++)
                {
                    // Rises along X only, so the edge value is unambiguous.
                    grid.AddPoint(x, y, grid.GetCoordinates(x, y).X / 100.0);
                }
            }

            Assert.Equal(0.2, grid.MaxHeight, precision: 6);

            // A hair past the low edge takes that edge, not the far corner.
            Assert.Equal(0.0, grid.InterpolateZ(-0.001, 10), precision: 3);
            Assert.Equal(0.0, grid.InterpolateZ(-5, 10), precision: 6);

            // And past the high edge it stays at the high edge.
            Assert.Equal(0.2, grid.InterpolateZ(25, 10), precision: 6);

            // Continuous across the boundary: the step is what the old answer introduced.
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
        /// A skipped probe empties the work queue without measuring the node, so the
        /// queue and the map disagree. Starting another run must put those nodes back -
        /// otherwise the loop indexes an empty queue and the operator is left with a map
        /// that can never be applied and never re-probed.
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
            grid.SkipPoint(1, 1);   // what the skip path leaves behind

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
        /// Reproduces the reported crash: the display reads the remaining points on the
        /// UI thread while probing removes them on another. Enumerating the live list
        /// threw "Collection was modified; enumeration operation may not execute" partway
        /// through a run - after about twenty points, whenever a redraw happened to
        /// coincide with a removal - and lost the job.
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
                    // What DrawProbeMatrix does, as fast as it can.
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

            // What the probe loop does: reorder, take the next, record it.
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

        // =========================================================================
        // Neighbour deviation
        //
        // This is the check that separates a bad reading from a good one: a height outside
        // tolerance of the nodes around it.
        // =========================================================================

        [Fact]
        public void NeighbourDeviation_WithNoMeasuredNeighbours_IsNull()
        {
            var grid = new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20));

            Assert.Null(grid.GetNeighbourDeviation(0, 0, -0.5));
        }

        [Fact]
        public void NeighbourDeviation_IgnoresTheNodesOwnRecordedHeight()
        {
            var grid = new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20));

            // The controller records the height before checking, so a node that counted
            // itself would always be within tolerance and the check would never fire.
            grid.RecordMeasurement(0, 0, -5.0);

            Assert.Null(grid.GetNeighbourDeviation(0, 0, -5.0));
        }

        [Fact]
        public void NeighbourDeviation_MeasuresAgainstTheMeanOfMeasuredNeighbours()
        {
            var grid = new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20));

            grid.RecordMeasurement(0, 1, -0.10);
            grid.RecordMeasurement(1, 0, -0.30);

            // Mean of the two orthogonal neighbors is -0.20.
            double? deviation = grid.GetNeighbourDeviation(0, 0, -0.25);

            Assert.NotNull(deviation);
            Assert.Equal(0.05, deviation!.Value, 6);
        }

        [Fact]
        public void NeighbourDeviation_SkipsUnmeasuredNeighbours()
        {
            var grid = new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20));

            // A skipped point leaves a hole. It must not count as a height of zero.
            grid.RecordMeasurement(1, 1, -0.40);

            double? deviation = grid.GetNeighbourDeviation(1, 2, -0.40);

            Assert.NotNull(deviation);
            Assert.Equal(0.0, deviation!.Value, 6);
        }

        [Fact]
        public void NeighbourDeviation_ExcludesDiagonalNodes()
        {
            var grid = new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20));

            // Only a diagonal is measured. It is further than one grid step away, so it is
            // not used as the expected height for this node.
            grid.RecordMeasurement(1, 1, -0.40);

            Assert.Null(grid.GetNeighbourDeviation(0, 0, -0.40));
        }

        [Theory]
        [InlineData(-3.0, 2.5)]   // pushed past the surface
        [InlineData(2.0, 2.5)]    // stopped short, on debris or a shorted clip
        public void NeighbourDeviation_IsUnsignedInBothDirections(
            double measured, double expectedDeviation)
        {
            var grid = new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(20, 20));

            grid.RecordMeasurement(0, 1, -0.50);
            grid.RecordMeasurement(1, 0, -0.50);

            double? deviation = grid.GetNeighbourDeviation(0, 0, measured);

            Assert.NotNull(deviation);
            Assert.Equal(expectedDeviation, deviation!.Value, 6);
        }

        [Fact]
        public void NeighbourDeviation_OnAWarpedBoardStaysSmallBetweenAdjacentNodes()
        {
            // A bowed board spans millimetres end to end while staying flat between any two
            // adjacent nodes. Comparing against neighbours rather than the overall range is
            // what keeps a real warp from reading as a fault.
            var grid = new ProbeGrid(10.0, new Vector2(0, 0), new Vector2(40, 40));

            for (int x = 0; x < grid.SizeX; x++)
            {
                for (int y = 0; y < grid.SizeY; y++)
                {
                    grid.AddPoint(x, y, -0.2 * x);
                }
            }

            // Ends of the board differ by 0.8mm; adjacent nodes by 0.2mm.
            double? deviation = grid.GetNeighbourDeviation(2, 2, -0.4);

            Assert.NotNull(deviation);
            Assert.Equal(0.0, deviation!.Value, 6);
        }

        /// <summary>
        /// Every screen and gate reads one answer for how much of a map is measured.
        /// Derived from the counters, a skipped point reads as a finished map: Save and Apply
        /// are offered for a map that cannot be applied, and Continue Probing disappears.
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

            Assert.Equal(grid.TotalPoints, grid.Progress);       // looks finished...
            Assert.Equal(ProbeDataState.Partial, grid.State);    // ...and is not
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
