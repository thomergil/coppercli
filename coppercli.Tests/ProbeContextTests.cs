using System.IO;
using coppercli.Core.GCode;
using coppercli.Core.Util;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// A height map is Z heights indexed by X/Y in work coordinates. It describes one
    /// board, measured from one origin. These pin that the map itself can say so.
    ///
    /// The regression behind this: validity was inferred from scattered session state -
    /// chiefly "does an autosave file exist on disk" - so a leftover map from a previous
    /// session was announced as the operator's current data, offered for application to
    /// a different board, and warned about when zeroing.
    /// </summary>
    public class ProbeContextTests
    {
        private static ProbeGrid GridFor(string sourceFile, Vector3 origin)
        {
            var grid = new ProbeGrid(5.0, new Vector2(0, 0), new Vector2(10, 10))
            {
                Context = new ProbeContext(sourceFile, origin)
            };

            for (int x = 0; x < grid.SizeX; x++)
            {
                for (int y = 0; y < grid.SizeY; y++)
                {
                    grid.AddPoint(x, y, 0.1);
                }
            }

            return grid;
        }

        [Fact]
        public void MapIsApplicable_ToTheBoardAndOriginItWasMeasuredIn()
        {
            var grid = GridFor("/tmp/board-a.ngc", new Vector3(-10, -20, -5));

            Assert.Equal(ProbeApplicability.Applicable,
                grid.GetApplicability("/tmp/board-a.ngc", new Vector3(-10, -20, -5)));
        }

        /// <summary>The heights are indexed against a different toolpath.</summary>
        [Fact]
        public void MapIsNotApplicable_ToADifferentFile()
        {
            var grid = GridFor("/tmp/board-a.ngc", new Vector3(-10, -20, -5));

            Assert.Equal(ProbeApplicability.DifferentFile,
                grid.GetApplicability("/tmp/board-b.ngc", new Vector3(-10, -20, -5)));
        }

        /// <summary>
        /// The origin check is the one that catches a work zero moved by any route -
        /// jogging and re-zeroing, another client, a G10 in a macro. Only the X/Y zero
        /// path used to consider this at all.
        /// </summary>
        [Fact]
        public void MapIsNotApplicable_WhenTheWorkOriginHasMoved()
        {
            var grid = GridFor("/tmp/board-a.ngc", new Vector3(-10, -20, -5));

            Assert.Equal(ProbeApplicability.OriginMoved,
                grid.GetApplicability("/tmp/board-a.ngc", new Vector3(-15, -20, -5)));
        }

        /// <summary>A Z-only change does not move where the heights land in X/Y.</summary>
        [Fact]
        public void MapStaysApplicable_WhenOnlyZChanges()
        {
            var grid = GridFor("/tmp/board-a.ngc", new Vector3(-10, -20, -5));

            Assert.Equal(ProbeApplicability.Applicable,
                grid.GetApplicability("/tmp/board-a.ngc", new Vector3(-10, -20, -9.4)));
        }

        /// <summary>A map from before this was recorded is questioned, not assumed good.</summary>
        [Fact]
        public void MapWithNoRecordedSetup_IsUnknownRatherThanUsable()
        {
            var grid = GridFor("/tmp/board-a.ngc", new Vector3(0, 0, 0));
            grid.Context = ProbeContext.Unknown;

            Assert.Equal(ProbeApplicability.Unknown,
                grid.GetApplicability("/tmp/board-a.ngc", new Vector3(0, 0, 0)));
        }

        /// <summary>The binding has to survive the round trip, or it protects nothing.</summary>
        [Fact]
        public void SetupSurvivesSaveAndLoad()
        {
            var grid = GridFor("/tmp/board-a.ngc", new Vector3(-10.5, -20.25, -5.125));
            string path = Path.GetTempFileName();

            try
            {
                grid.Save(path);
                var loaded = ProbeGrid.Load(path);

                Assert.True(loaded.Context.IsKnown);
                Assert.Equal("/tmp/board-a.ngc", loaded.Context.SourceFile);
                Assert.Equal(-10.5, loaded.Context.WorkOrigin.X, precision: 3);
                Assert.Equal(-20.25, loaded.Context.WorkOrigin.Y, precision: 3);

                Assert.Equal(ProbeApplicability.Applicable,
                    loaded.GetApplicability("/tmp/board-a.ngc", new Vector3(-10.5, -20.25, -5.125)));
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>A map saved by an older version has no setup and must not claim one.</summary>
        [Fact]
        public void MapWithoutSetup_RoundTripsAsUnknown()
        {
            var grid = GridFor("/tmp/board-a.ngc", new Vector3(0, 0, 0));
            grid.Context = ProbeContext.Unknown;

            string path = Path.GetTempFileName();
            try
            {
                grid.Save(path);
                var loaded = ProbeGrid.Load(path);

                Assert.False(loaded.Context.IsKnown);
                Assert.Equal(ProbeApplicability.Unknown,
                    loaded.GetApplicability("/tmp/board-a.ngc", new Vector3(0, 0, 0)));
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
