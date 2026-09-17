namespace coppercli.Helpers
{
    /// <summary>
    /// Helper methods for path operations.
    /// </summary>
    internal static class PathHelpers
    {
        /// <summary>
        /// The name with one of the given extensions on it. One rule, so a name the operator
        /// gives the terminal and the same name given to the browser save one file, not two.
        /// </summary>
        public static string EnsureExtension(string filename, string[]? extensions)
        {
            if (string.IsNullOrWhiteSpace(filename) || extensions == null || extensions.Length == 0)
            {
                return filename;
            }

            var ext = Path.GetExtension(filename).ToLower();
            if (extensions.Contains(ext))
            {
                return filename;
            }

            // Append the first valid extension
            return filename + extensions[0];
        }

        /// <summary>
        /// Expands a leading ~ to the home directory. Only "~", "~/" and "~\" are a home
        /// path; "~name" is another user's, and coppercli cannot resolve it.
        /// </summary>
        public static string ExpandTilde(string path)
        {
            if (path != "~"
                && !path.StartsWith("~/", StringComparison.Ordinal)
                && !path.StartsWith(@"~\", StringComparison.Ordinal))
            {
                return path;
            }

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string rest = path.Substring(1).TrimStart('/', '\\');
            return rest.Length == 0 ? home : Path.Combine(home, rest);
        }
    }
}
