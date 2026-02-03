namespace coppercli.Helpers
{
    /// <summary>
    /// Helper methods for path operations.
    /// </summary>
    internal static class PathHelpers
    {
        /// <summary>
        /// Expands ~ at the start of a path to the user's home directory.
        /// </summary>
        public static string ExpandTilde(string path)
        {
            if (path.StartsWith("~"))
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Substring(2));
            }
            return path;
        }
    }
}
