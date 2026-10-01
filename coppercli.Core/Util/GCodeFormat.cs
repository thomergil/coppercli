using System;
using System.Collections.Generic;

namespace coppercli.Core.Util
{
    /// <summary>
    /// Formats G-code with a '.' decimal separator regardless of the operator's locale.
    ///
    /// C# interpolated strings format through <see cref="System.Globalization.CultureInfo.CurrentCulture"/>.
    /// On a comma-decimal locale that turns "Z-1.000" into "Z-1,000", which GRBL rejects
    /// as a bad number format - so every number sent to the machine goes through here.
    /// </summary>
    public static class GCodeFormat
    {
        /// <summary>Renders an interpolated string using the invariant culture.</summary>
        public static string Inv(FormattableString line) => FormattableString.Invariant(line);

        /// <summary>
        /// A rapid, or a feed move at <paramref name="feed"/> mm/min, to the axes given; an axis
        /// left null is not written. The one place a single move line, such as a retract, is
        /// formatted; GCodeFile.GetGCode writes the lines of a toolpath.
        /// </summary>
        public static string MoveLine(
            double? x, double? y, double? z, bool inMachineCoordinates = false, double? feed = null)
        {
            var words = new List<string>();
            if (inMachineCoordinates)
            {
                words.Add(GrblProtocol.CmdMachineCoords);
            }

            words.Add(feed == null ? GrblProtocol.CmdRapidMove : GrblProtocol.CmdLinearMove);
            AddAxis(words, 'X', x);
            AddAxis(words, 'Y', y);
            AddAxis(words, 'Z', z);

            if (feed is double f)
            {
                words.Add(Inv($"F{f:F0}"));
            }

            return string.Join(" ", words);
        }

        private static void AddAxis(List<string> words, char axis, double? value)
        {
            if (value is double v)
            {
                words.Add(Inv($"{axis}{v:F3}"));
            }
        }
    }
}
