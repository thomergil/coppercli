using coppercli.Core.Util;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// The terminal and the browser both draw the height map and must match. These tests
    /// cover the band boundaries and both ends of the range.
    /// </summary>
    public class HeightGradientTests
    {
        [Theory]
        [InlineData(0.00, 0, 0, 255)]      // lowest: blue
        [InlineData(0.25, 0, 255, 255)]    // cyan
        [InlineData(0.50, 0, 255, 0)]      // green
        [InlineData(0.75, 255, 255, 0)]    // yellow
        [InlineData(1.00, 255, 0, 0)]      // highest: red
        public void TheBandBoundaries_AreTheNamedColours(
            double fraction, int r, int g, int b)
        {
            Assert.Equal((r, g, b), HeightGradient.Colour(fraction));
        }

        [Theory]
        [InlineData(-5.0, 0, 0, 255)]
        [InlineData(5.0, 255, 0, 0)]
        public void AFractionOutsideTheRange_ClampsToAnEnd(double fraction, int r, int g, int b)
        {
            Assert.Equal((r, g, b), HeightGradient.Colour(fraction));
        }

        [Fact]
        public void EveryChannelStaysWithinRange()
        {
            for (int i = 0; i <= 100; i++)
            {
                var (r, g, b) = HeightGradient.Colour(i / 100.0);

                Assert.InRange(r, 0, HeightGradient.MaxChannel);
                Assert.InRange(g, 0, HeightGradient.MaxChannel);
                Assert.InRange(b, 0, HeightGradient.MaxChannel);
            }
        }
    }
}
