namespace coppercli.Core.Util
{
    /// <summary>
    /// The color a probed height is drawn in, from blue at the lowest point through cyan,
    /// green and yellow to red at the highest.
    ///
    /// Computed here so both views of the height map show the same board. The terminal
    /// encodes the answer as an ANSI color and the browser as CSS; neither works out the
    /// gradient itself.
    /// </summary>
    public static class HeightGradient
    {
        /// <summary>Highest value a color channel takes.</summary>
        public const int MaxChannel = 255;

        /// <summary>Width of each of the four bands the gradient is built from.</summary>
        public const double BandWidth = 0.25;

        /// <summary>
        /// The color for a height expressed as a fraction of the measured range, where 0
        /// is the lowest point on the board and 1 the highest. Values outside that are
        /// clamped, so a point off the end of the range still draws.
        /// </summary>
        public static (int R, int G, int B) Colour(double fraction)
        {
            double t = System.Math.Clamp(fraction, 0, 1);
            double s = (t % BandWidth) / BandWidth;
            int rising = Scale(s);
            int falling = Scale(1 - s);

            if (t < BandWidth)
            {
                return (0, rising, MaxChannel);            // blue to cyan
            }

            if (t < BandWidth * 2)
            {
                return (0, MaxChannel, falling);           // cyan to green
            }

            if (t < BandWidth * 3)
            {
                return (rising, MaxChannel, 0);            // green to yellow
            }

            return (MaxChannel, t >= 1 ? 0 : falling, 0);  // yellow to red
        }

        private static int Scale(double s) => (int)(MaxChannel * s);
    }
}
