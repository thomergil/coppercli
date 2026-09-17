#nullable enable
using coppercli.Core.Util;
using coppercli.Helpers;
using coppercli.Menus;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// The jog screen pads each position row out to the width of the box. A padding computed
    /// from a hand-counted width leaves the row short or pushes the border off the end of the
    /// line.
    /// </summary>
    public class JogLayoutTests
    {
        /// <summary>The widest a coordinate formats, which is what the field is sized for.</summary>
        private static readonly Vector3 Widest = new(-999.999, -999.999, -999.999);

        [Fact]
        public void APositionRow_DrawsTheWidthItsPaddingAssumes()
        {
            string row = JogMenu.PositionRow(JogMenu.WorkLabel, Widest, DisplayHelpers.AnsiWarning);

            Assert.Equal(
                JogMenu.PositionContentWidth,
                DisplayHelpers.CalculateDisplayLength(row));
        }

        /// <summary>
        /// PositionContentWidth is derived from WorkLabel alone, so MachineLabel has to be
        /// padded to the same length.
        /// </summary>
        [Fact]
        public void BothPositionRows_DrawTheSameWidth()
        {
            Assert.Equal(
                DisplayHelpers.CalculateDisplayLength(
                    JogMenu.PositionRow(JogMenu.WorkLabel, Widest, DisplayHelpers.AnsiWarning)),
                DisplayHelpers.CalculateDisplayLength(
                    JogMenu.PositionRow(JogMenu.MachineLabel, Widest, DisplayHelpers.AnsiDim)));
        }
    }
}
