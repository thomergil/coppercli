using System.Globalization;

namespace coppercli.Tests.Fakes
{
    /// <summary>Reads the words the fakes act on out of a G-code line.</summary>
    internal static class GCodeWords
    {
        /// <summary>The value after <paramref name="axis"/>, or null when the line has none.</summary>
        public static double? Axis(string line, char axis)
        {
            int i = line.IndexOf(axis);
            if (i < 0) { return null; }

            int end = i + 1;
            while (end < line.Length && (char.IsDigit(line[end]) || line[end] == '.' || line[end] == '-'))
            {
                end++;
            }

            string word = line.Substring(i + 1, end - i - 1);
            return double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                ? v
                : null;
        }
    }
}
