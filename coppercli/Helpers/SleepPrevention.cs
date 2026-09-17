using System.Diagnostics;
using System.Runtime.InteropServices;
using coppercli.Core.Settings;
using static coppercli.CliConstants;

namespace coppercli.Helpers
{
    /// <summary>
    /// Keeps the host awake for the length of a mill or probe run: SetThreadExecutionState on
    /// Windows, caffeinate on macOS, systemd-inhibit on Linux, and nothing on any other
    /// platform, where Start returns false and the operator is warned instead.
    /// </summary>
    internal static class SleepPrevention
    {
        private static Process? _caffeinateProcess;
        private static bool _checkedAvailability;
        private static bool _isAvailable;
        private static bool _isActive;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint esFlags);

        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;
        // ES_DISPLAY_REQUIRED is left out on purpose, so the display may still sleep.

        private static ProcessStartInfo CreateProcessStartInfo(string fileName, string arguments, bool redirectStdErr = false)
        {
            return new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = redirectStdErr
            };
        }

        private static bool IsProgramAvailable(string programName)
        {
            try
            {
                var psi = CreateProcessStartInfo(WhichCommand, programName);
                using var process = Process.Start(psi);
                process?.WaitForExit(ProgramCheckTimeoutMs);
                bool found = process?.ExitCode == 0;
                Logger.Log("SleepPrevention: {0} {1} -> {2}",
                    WhichCommand, programName, found ? "found" : "not found");
                return found;
            }
            catch (Exception ex)
            {
                Logger.Log("SleepPrevention: {0} {1} failed: {2}",
                    WhichCommand, programName, ex.Message);
                return false;
            }
        }

        public static bool IsAvailable()
        {
            if (_checkedAvailability)
            {
                return _isAvailable;
            }

            _checkedAvailability = true;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Windows always has SetThreadExecutionState available
                _isAvailable = true;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // caffeinate ships with macOS, so this check almost always passes.
                _isAvailable = IsProgramAvailable(CaffeinateCommand);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // systemd-inhibit is missing on a distro that does not run systemd.
                _isAvailable = IsProgramAvailable(SystemdInhibitCommand);
            }
            else
            {
                _isAvailable = false;
            }

            Logger.Log("SleepPrevention: IsAvailable={0} (Platform={1})",
                _isAvailable, RuntimeInformation.OSDescription);
            return _isAvailable;
        }

        /// <summary>
        /// A second call while already active returns true without starting anything.
        /// </summary>
        public static bool Start()
        {
            Logger.Log("SleepPrevention: Start() called, _isActive={0}", _isActive);

            if (_isActive)
            {
                Logger.Log("SleepPrevention: Already active, returning true");
                return true;
            }

            if (!IsAvailable())
            {
                Logger.Log("SleepPrevention: Not available on this platform");
                return false;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return StartWindows();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return StartMacOS();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return StartLinux();
            }

            Logger.Log("SleepPrevention: Unknown platform, returning false");
            return false;
        }

        private static bool StartWindows()
        {
            try
            {
                uint result = SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
                _isActive = result != 0;
                Logger.Log("SleepPrevention: SetThreadExecutionState result={0}, active={1}", result, _isActive);
                return _isActive;
            }
            catch (Exception ex)
            {
                Logger.Log("SleepPrevention: Failed to set execution state: {0}", ex.Message);
                return false;
            }
        }

        private static bool StartMacOS()
        {
            if (_caffeinateProcess != null)
            {
                Logger.Log("SleepPrevention: caffeinate already running");
                return true;
            }

            Logger.Log("SleepPrevention: Starting {0} {1}", CaffeinateCommand, CaffeinateArgs);
            try
            {
                var psi = CreateProcessStartInfo(CaffeinateCommand, CaffeinateArgs, redirectStdErr: true);
                _caffeinateProcess = Process.Start(psi);
                _isActive = _caffeinateProcess != null;
                if (_isActive)
                {
                    Logger.Log("SleepPrevention: Started {0} (PID={1})", CaffeinateCommand, _caffeinateProcess!.Id);
                }
                else
                {
                    Logger.Log("SleepPrevention: Process.Start returned null");
                }
                return _isActive;
            }
            catch (Exception ex)
            {
                Logger.Log("SleepPrevention: Failed to start {0}: {1}", CaffeinateCommand, ex.Message);
                return false;
            }
        }

        private static bool StartLinux()
        {
            if (_caffeinateProcess != null)
            {
                Logger.Log("SleepPrevention: systemd-inhibit already running");
                return true;
            }

            Logger.Log("SleepPrevention: Starting {0} {1}", SystemdInhibitCommand, SystemdInhibitArgs);
            try
            {
                var psi = CreateProcessStartInfo(SystemdInhibitCommand, SystemdInhibitArgs, redirectStdErr: true);
                _caffeinateProcess = Process.Start(psi);
                _isActive = _caffeinateProcess != null;
                if (_isActive)
                {
                    Logger.Log("SleepPrevention: Started {0} (PID={1})", SystemdInhibitCommand, _caffeinateProcess!.Id);
                }
                else
                {
                    Logger.Log("SleepPrevention: Process.Start returned null");
                }
                return _isActive;
            }
            catch (Exception ex)
            {
                Logger.Log("SleepPrevention: Failed to start {0}: {1}", SystemdInhibitCommand, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Safe to call when nothing was started.
        /// </summary>
        public static void Stop()
        {
            Logger.Log("SleepPrevention: Stop() called, _isActive={0}", _isActive);

            if (!_isActive)
            {
                Logger.Log("SleepPrevention: Not active, nothing to stop");
                return;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                StopWindows();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
                     RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                StopUnixProcess();
            }

            _isActive = false;
            Logger.Log("SleepPrevention: Stopped, _isActive={0}", _isActive);
        }

        private static void StopWindows()
        {
            try
            {
                SetThreadExecutionState(ES_CONTINUOUS);
                Logger.Log("SleepPrevention: Cleared execution state");
            }
            catch (Exception ex)
            {
                Logger.Log("SleepPrevention: Error clearing execution state: {0}", ex.Message);
            }
        }

        private static void StopUnixProcess()
        {
            if (_caffeinateProcess == null)
            {
                Logger.Log("SleepPrevention: No Unix process to stop");
                return;
            }

            try
            {
                int pid = _caffeinateProcess.Id;
                if (!_caffeinateProcess.HasExited)
                {
                    Logger.Log("SleepPrevention: Killing process PID={0}", pid);
                    _caffeinateProcess.Kill();
                    _caffeinateProcess.WaitForExit(ProgramCheckTimeoutMs);
                    Logger.Log("SleepPrevention: Process PID={0} stopped", pid);
                }
                else
                {
                    Logger.Log("SleepPrevention: Process PID={0} already exited", pid);
                }
            }
            catch (Exception ex)
            {
                Logger.Log("SleepPrevention: Error stopping process: {0}", ex.Message);
            }
            finally
            {
                _caffeinateProcess.Dispose();
                _caffeinateProcess = null;
            }
        }

        /// <summary>
        /// Sleep costs more here: losing the TCP connection mid-run leaves the machine in a
        /// state coppercli cannot read back.
        /// </summary>
        public static bool IsNetworkMode()
        {
            return AppState.Settings.ConnectionType == ConnectionType.Ethernet;
        }

        public static bool ShouldWarn()
        {
            return IsNetworkMode() && !IsAvailable();
        }

        public static bool IsActive => _isActive;
    }
}
