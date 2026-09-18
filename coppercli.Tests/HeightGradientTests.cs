using coppercli.Core.Util;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// The terminal and the browser both take their height-map colors from HeightGradient,
    /// so these values are what each of them draws. The ramp runs blue (lowest) through
    /// cyan, green and yellow to red (highest).
    /// </summary>
    public class HeightGradientTests
    {
        [Theory]
        [InlineData(0.00, 0, 0, 255)]
        [InlineData(0.25, 0, 255, 255)]
        [InlineData(0.50, 0, 255, 0)]
        [InlineData(0.75, 255, 255, 0)]
        [InlineData(1.00, 255, 0, 0)]
        public void TheBandBoundaries_AreTheNamedColors(
            double fraction, int r, int g, int b)
        {
            Assert.Equal((r, g, b), HeightGradient.Color(fraction));
        }

        [Theory]
        [InlineData(-5.0, 0, 0, 255)]
        [InlineData(5.0, 255, 0, 0)]
        public void AFractionOutsideTheRange_ClampsToAnEnd(double fraction, int r, int g, int b)
        {
            Assert.Equal((r, g, b), HeightGradient.Color(fraction));
        }

        [Fact]
        public void GradientChannels_RemainWithinRange()
        {
            for (int i = 0; i <= 100; i++)
            {
                var (r, g, b) = HeightGradient.Color(i / 100.0);

                Assert.InRange(r, 0, HeightGradient.MaxChannel);
                Assert.InRange(g, 0, HeightGradient.MaxChannel);
                Assert.InRange(b, 0, HeightGradient.MaxChannel);
            }
        }
    }
}
