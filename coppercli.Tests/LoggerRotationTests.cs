using System.IO;
using coppercli.Helpers;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// Checks that Logger.Clear keeps the session before it; the reason is on Logger.Clear.
    ///
    /// In the web-server collection because Logger is process-wide and writes to one file:
    /// run in parallel with anything else that logs, these assertions race a writer.
    /// </summary>
    [Collection(WebServerCollection.Name)]
    public class LoggerRotationTests
    {
        private static string PreviousLogPath => Logger.PreviousLogFilePath;

        /// <summary>Both files, so neither test can pass on what an earlier run left.</summary>
        private static void StartFromNoLogs()
        {
            File.Delete(Logger.LogFilePath);
            File.Delete(PreviousLogPath);
        }

        [Fact]
        public void ClearingTheLog_KeepsTheRunBeforeIt()
        {
            bool wasEnabled = Logger.Enabled;
            Logger.Enabled = true;
            try
            {
                StartFromNoLogs();
                Logger.Log(FirstRunMarker);
                Assert.Contains(FirstRunMarker, File.ReadAllText(Logger.LogFilePath));

                Logger.Clear();
                Logger.Log(SecondRunMarker);

                Assert.DoesNotContain(FirstRunMarker, File.ReadAllText(Logger.LogFilePath));
                Assert.Contains(SecondRunMarker, File.ReadAllText(Logger.LogFilePath));
                Assert.Contains(FirstRunMarker, File.ReadAllText(PreviousLogPath));
            }
            finally
            {
                Logger.Enabled = wasEnabled;
            }
        }

        /// <summary>Clearing twice must overwrite the copy already kept, not throw on it.</summary>
        [Fact]
        public void ClearingTwice_OverwritesTheKeptCopy()
        {
            bool wasEnabled = Logger.Enabled;
            Logger.Enabled = true;
            try
            {
                StartFromNoLogs();
                Logger.Log(FirstRunMarker);
                Logger.Clear();
                Logger.Log(SecondRunMarker);
                Logger.Clear();

                Assert.False(File.Exists(Logger.LogFilePath), "Clear left the old log in place");
                Assert.Contains(SecondRunMarker, File.ReadAllText(PreviousLogPath));
            }
            finally
            {
                Logger.Enabled = wasEnabled;
            }
        }

        private const string FirstRunMarker = "rotation-test-first-run";
        private const string SecondRunMarker = "rotation-test-second-run";
    }
}
