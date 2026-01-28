using System.Diagnostics;
using System.Runtime.InteropServices;
using coppercli.Core.Settings;
using static coppercli.CliConstants;

namespace coppercli.Helpers
{
    /// <summary>
    /// Prevents system sleep during long-running operations (milling, probing).
    /// Uses caffeinate on macOS, SetThreadExecutionState on Windows.
    /// </summary>
    internal static class SleepPrevention
    {
        private static Process? _caffeinateProcess;
        private static bool _checkedAvailability;
        private static bool _isAvailable;
        private static bool _isActive;

        // Windows API for preventing sleep
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint esFlags);

        // Execution state flags
        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;
        // ES_DISPLAY_REQUIRED = 0x00000002 - not needed, allow display to sleep

        /// <summary>
        /// Check if a program exists on Unix-like systems using 'which'.
        /// </summary>
        private static bool IsProgramAvailable(string programName)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = WhichCommand,
                    Arguments = programName,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
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

        /// <summary>
        /// Check if sleep prevention is available on this platform.
        /// </summary>
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
                // Check if caffeinate exists (should always be present on macOS)
                _isAvailable = IsProgramAvailable(CaffeinateCommand);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Check if systemd-inhibit exists (available on most modern Linux distros)
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
        /// Start preventing sleep. Safe to call multiple times.
        /// Returns true if sleep prevention is active, false if not available.
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
                // Request system stay awake (but allow display to sleep)
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
                var psi = new ProcessStartInfo
                {
                    FileName = CaffeinateCommand,
                    Arguments = CaffeinateArgs,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
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
                var psi = new ProcessStartInfo
                {
                    FileName = SystemdInhibitCommand,
                    Arguments = SystemdInhibitArgs,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
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
        /// Stop preventing sleep. Safe to call multiple times or when not started.
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
                // Clear the execution state flags
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
        /// Check if we're in network mode (Ethernet connection).
        /// Sleep is more dangerous in network mode because disconnection can leave machine in unknown state.
        /// </summary>
        public static bool IsNetworkMode()
        {
            return AppState.Settings.ConnectionType == ConnectionType.Ethernet;
        }

        /// <summary>
        /// Check if we should warn the user about sleep prevention.
        /// Returns true if in network mode AND sleep prevention is not available.
        /// </summary>
        public static bool ShouldWarn()
        {
            return IsNetworkMode() && !IsAvailable();
        }

        /// <summary>
        /// Returns true if sleep prevention is currently active.
        /// </summary>
        public static bool IsActive => _isActive;
    }
}
