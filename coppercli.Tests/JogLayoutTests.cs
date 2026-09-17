#nullable enable
using coppercli.Core.Util;
using coppercli.Helpers;
using coppercli.Menus;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// The jog screen pads each row out to the width of the box. A padding computed from a
    /// hand-counted width leaves the row short or overruns the border, and nothing on screen
    /// says which.
    /// </summary>
    public class JogLayoutTests
    {
        /// <summary>Coordinates at their widest, which is what the field is sized for.</summary>
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
        /// Both rows are drawn from the same builder, so one cannot grow without the other.
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
