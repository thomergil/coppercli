using System;

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
    }
}
