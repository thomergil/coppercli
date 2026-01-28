// Extracted from Program.cs - Status checking and position waiting

using coppercli.Core.Communication;
using static coppercli.CliConstants;
using static coppercli.Core.Util.GrblProtocol;

namespace coppercli.Helpers
{
    /// <summary>
    /// Helper methods for checking machine status and waiting for positions.
    /// </summary>
    internal static class StatusHelpers
    {
        /// <summary>
        /// Checks if the machine is in Idle state.
        /// </summary>
        public static bool IsIdle(Machine machine)
        {
            return machine.Status == StatusIdle;
        }

        /// <summary>
        /// Checks if the machine is in Alarm state.
        /// </summary>
        public static bool IsAlarm(Machine machine)
        {
            return machine.Status.StartsWith(StatusAlarm);
        }

        /// <summary>
        /// Checks if the machine is in Hold state.
        /// </summary>
        public static bool IsHold(Machine machine)
        {
            return machine.Status.StartsWith(StatusHold);
        }

        /// <summary>
        /// Checks if the machine is in Door state.
        /// </summary>
        public static bool IsDoor(Machine machine)
        {
            return machine.Status.StartsWith(StatusDoor);
        }

        /// <summary>
        /// Checks if the machine is in any problematic state (Alarm or Door).
        /// </summary>
        public static bool IsProblematicState(Machine machine)
        {
            return IsAlarm(machine) || IsDoor(machine);
        }

        /// <summary>
        /// Waits for work Z to reach the target height (within tolerance).
        /// </summary>
        public static void WaitForZHeight(Machine machine, double targetZ, int timeoutMs = 0)
        {
            if (timeoutMs <= 0)
            {
                timeoutMs = ZHeightWaitTimeoutMs;
            }

            var startTime = DateTime.Now;

            while ((DateTime.Now - startTime).TotalMilliseconds < timeoutMs)
            {
                double dz = Math.Abs(machine.WorkPosition.Z - targetZ);
                if (dz < PositionToleranceMm)
                {
                    return;  // Reached target
                }
                Thread.Sleep(StatusPollIntervalMs);
            }
        }

        /// <summary>
        /// Waits for machine Z to reach the target height (within tolerance).
        /// Use for G53 moves where we're targeting machine coordinates.
        /// </summary>
        public static void WaitForMachineZHeight(Machine machine, double targetZ, int timeoutMs = 0)
        {
            if (timeoutMs <= 0)
            {
                timeoutMs = ZHeightWaitTimeoutMs;
            }

            var startTime = DateTime.Now;

            while ((DateTime.Now - startTime).TotalMilliseconds < timeoutMs)
            {
                double dz = Math.Abs(machine.MachinePosition.Z - targetZ);
                if (dz < PositionToleranceMm)
                {
                    return;  // Reached target
                }
                Thread.Sleep(StatusPollIntervalMs);
            }
        }

        /// <summary>
        /// Waits for the machine to reach the target XY position (within tolerance).
        /// Returns true if reached, false if cancelled by the checkCancel function.
        /// </summary>
        public static bool WaitForMoveComplete(Machine machine, double targetX, double targetY,
            Func<bool>? checkCancel = null, int timeoutMs = 0)
        {
            if (timeoutMs <= 0)
            {
                timeoutMs = MoveCompleteTimeoutMs;
            }

            var startTime = DateTime.Now;

            while ((DateTime.Now - startTime).TotalMilliseconds < timeoutMs)
            {
                // Check for cancellation
                if (checkCancel != null && checkCancel())
                {
                    return false;  // Cancelled
                }

                var pos = machine.WorkPosition;
                double dx = Math.Abs(pos.X - targetX);
                double dy = Math.Abs(pos.Y - targetY);

                if (dx < PositionToleranceMm && dy < PositionToleranceMm)
                {
                    return true;  // Reached target
                }

                Thread.Sleep(StatusPollIntervalMs);
            }

            return true;  // Timeout, but don't treat as cancel
        }

        /// <summary>
        /// Waits for the machine to reach Idle state.
        /// </summary>
        public static bool WaitForIdle(Machine machine, int timeoutMs)
        {
            var start = DateTime.Now;
            while ((DateTime.Now - start).TotalMilliseconds < timeoutMs)
            {
                if (machine.Status == StatusIdle)
                {
                    return true;
                }
                Thread.Sleep(StatusPollIntervalMs);
            }
            return false;
        }

        /// <summary>
        /// Number of consecutive idle checks required for stable idle.
        /// </summary>
        public static int StableIdleRequiredCount => IdleSettleMs / StatusPollIntervalMs;

        /// <summary>
        /// Async (non-blocking) check for stable idle state. Call repeatedly in a loop.
        /// Returns true when machine has been idle for the required settle period.
        /// Caller must maintain stableCount between calls; reset to 0 to restart.
        /// For blocking behavior, use WaitForStableIdleBlocking instead.
        /// </summary>
        public static bool WaitForStableIdleAsync(Machine machine, ref int stableCount)
        {
            if (machine.Status == StatusIdle)
            {
                stableCount++;
                return stableCount >= StableIdleRequiredCount;
            }
            stableCount = 0;
            return false;
        }

        /// <summary>
        /// Blocking wait for stable idle. Waits for the machine to reach Idle state
        /// AND stay Idle for the settling period. Handles buffered commands that may
        /// start executing immediately after Idle is first seen.
        /// For non-blocking behavior, use WaitForStableIdleAsync instead.
        /// </summary>
        public static bool WaitForStableIdleBlocking(Machine machine, int timeoutMs)
        {
            var start = DateTime.Now;
            int stableCount = 0;
            while ((DateTime.Now - start).TotalMilliseconds < timeoutMs)
            {
                if (WaitForStableIdleAsync(machine, ref stableCount))
                {
                    return true;
                }
                Thread.Sleep(StatusPollIntervalMs);
            }
            return false;
        }

        /// <summary>
        /// Waits for GRBL to respond with a valid status (not "Disconnected").
        /// Returns the status string if successful, null if timeout.
        /// </summary>
        public static string? WaitForGrblResponse(Machine machine, int timeoutMs)
        {
            var timeout = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < timeout)
            {
                if (machine.Connected && machine.Status != StatusDisconnected)
                {
                    return machine.Status;
                }
                Thread.Sleep(StatusPollIntervalMs);
            }
            return null;
        }

        /// <summary>
        /// Waits for GRBL status to change from the current status.
        /// Returns the new status string if successful, null if timeout.
        /// </summary>
        public static string? WaitForStatusChange(Machine machine, string currentStatus, int timeoutMs)
        {
            var timeout = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < timeout)
            {
                if (machine.Connected && machine.Status != StatusDisconnected && machine.Status != currentStatus)
                {
                    return machine.Status;
                }
                Thread.Sleep(StatusPollIntervalMs);
            }
            return null;
        }
    }
}
