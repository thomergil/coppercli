namespace coppercli.Helpers
{
    internal static class PathHelpers
    {
        /// <summary>
        /// Both the terminal and the browser call this, so one typed name produces one file
        /// rather than two.
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

            return filename + extensions[0];
        }

        /// <summary>
        /// Only "~", "~/" and "~\" are a home path; "~name" is another user's, and coppercli
        /// cannot resolve it.
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

            // GetFullPath, because on Windows "~/pcb/board.nc" would otherwise keep its forward
            // slashes after the home folder, and one file would have two spellings that path
            // comparisons treat as different files.
            return rest.Length == 0 ? home : Path.GetFullPath(Path.Combine(home, rest));
        }
    }
}
