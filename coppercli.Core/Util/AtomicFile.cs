using System;
using System.IO;

namespace coppercli.Core.Util
{
    /// <summary>
    /// Writes a file so that readers only ever see the old contents or the new ones.
    ///
    /// A plain write truncates the file first, so anything that interrupts it - a crash,
    /// a power cut, the machine being switched off at the wall - leaves a half-written
    /// file behind. That matters most for the probe autosave, which is rewritten after
    /// every probed point and holds the height map a job will be cut to.
    /// </summary>
    public static class AtomicFile
    {
        private const string TempSuffix = ".tmp";

        /// <summary>Writes text, replacing the destination only once it is complete.</summary>
        public static void WriteAllText(string path, string contents)
        {
            Write(path, temp => File.WriteAllText(temp, contents));
        }

        /// <summary>
        /// Runs <paramref name="writeToTempPath"/> against a temporary file, then moves it
        /// over the destination. The destination is untouched if the write throws.
        /// </summary>
        public static void Write(string path, Action<string> writeToTempPath)
        {
            string temp = path + TempSuffix;

            try
            {
                writeToTempPath(temp);
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                try
                {
                    if (File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }
                catch
                {
                    // Leaving a stray temp file behind is not worth masking the real error.
                }

                throw;
            }
        }
    }
}
