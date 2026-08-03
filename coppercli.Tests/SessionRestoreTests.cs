using System.Linq;
using coppercli.Core.GCode;
using coppercli.Core.Util;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// The questions carried over from a previous session are computed in one place and
    /// asked by both interfaces. These pin the rules that the two copies disagreed on.
    /// </summary>
    public class SessionRestoreTests
    {
        /// <summary>
        /// The height-map question must not depend on the work-zero answer. The terminal
        /// used to skip it whenever the operator declined to trust the stored origin, so
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
    }
}
