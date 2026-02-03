// Extracted from Program.cs - Repeated G-code command patterns

using coppercli.Core.Communication;
using coppercli.Core.Controllers;
using static coppercli.Core.Util.Constants;
using static coppercli.Core.Util.GrblProtocol;

namespace coppercli.Helpers
{
    /// <summary>
    /// Helper methods for common machine command patterns.
    /// These consolidate repeated G-code sequences into reusable methods.
    /// </summary>
    internal static class MachineCommands
    {
        /// <summary>
        /// Moves to a safe Z height in absolute mode.
        /// </summary>
        public static void MoveToSafeHeight(Machine machine, double height)
        {
            Logger.Log($"MoveToSafeHeight: sending {CmdAbsolute} then {CmdRapidMove} Z{height:F3}");
            machine.SendLine(CmdAbsolute);
            machine.SendLine($"{CmdRapidMove} Z{height:F3}");
        }

        /// <summary>
        /// Sends the home command ($H) without waiting.
        /// Prefer HomeAndWait() which also waits for completion and sets IsHomed.
        /// </summary>
        public static void Home(Machine machine)
        {
            machine.SendLine(CmdHome);
        }

        /// <summary>
        /// Homes the machine and waits for completion.
        /// Calls MachineWait.HomeAsync which is the single source of truth.
        /// Sets machine.IsHomed = true on success.
        /// Returns true if homing succeeded, false on timeout or alarm.
        /// </summary>
        public static bool HomeAndWait(Machine machine, int timeoutMs = HomingTimeoutMs)
        {
            Logger.Log("HomeAndWait: calling MachineWait.HomeAsync");
            return MachineWait.HomeAsync(machine, timeoutMs).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Sends the unlock command ($X).
        /// </summary>
        public static void Unlock(Machine machine)
        {
            machine.SendLine(CmdUnlock);
        }

        /// <summary>
        /// Zeros the work offset for the specified axes (e.g., "X0 Y0 Z0").
        /// </summary>
        public static void ZeroWorkOffset(Machine machine, string axes)
        {
            Logger.Log($"ZeroWorkOffset: sending {CmdZeroWorkOffset} {axes}");
            machine.SendLine($"{CmdZeroWorkOffset} {axes}");
        }

        /// <summary>
        /// Zeros the work offset (async), waits for completion, sets IsWorkZeroSet flag,
        /// and handles probe grid state (re-applies if Z-only, discards if XY).
        /// This is the single source of truth for setting work zero - all UI code should use this.
        /// </summary>
        public static void SetWorkZeroAndWait(Machine machine, string axes)
        {
            MachineWait.ZeroWorkOffsetAsync(machine, axes).GetAwaiter().GetResult();
            AppState.IsWorkZeroSet = true;
            AppState.HandleWorkZeroChange(axes);
            Logger.Log($"SetWorkZeroAndWait: IsWorkZeroSet = true (axes={axes})");
        }

        /// <summary>
        /// Rapid move to specified XY position.
        /// </summary>
        public static void RapidMoveXY(Machine machine, double x, double y)
        {
            machine.SendLine($"{CmdRapidMove} X{x:F3} Y{y:F3}");
        }

        /// <summary>
        /// Probe toward workpiece on Z axis until contact (no error if no contact).
        /// </summary>
        public static void ProbeZ(Machine machine, double maxDepth, double feed)
        {
            machine.SendLine($"{CmdProbeToward} Z-{maxDepth:F3} F{feed:F1}");
        }

        /// <summary>
        /// Sets absolute distance mode (G90).
        /// </summary>
        public static void SetAbsoluteMode(Machine machine)
        {
            machine.SendLine(CmdAbsolute);
        }

        /// <summary>
        /// Clears Door state if present by sending CycleStart.
        /// Returns true if Door was cleared, false if no action needed.
        /// Does NOT handle Alarm state - caller should check for Alarm separately.
        /// Wraps MachineWait.ClearDoorStateAsync for sync callers.
        /// </summary>
        public static bool ClearDoorState(Machine machine)
        {
            return MachineWait.ClearDoorStateAsync(machine).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Prepares machine for an operation by clearing Door state, waiting for Idle,
        /// and checking for Alarm. Returns true if machine is ready, false if in Alarm state.
        /// Use at the start of milling, probing, or other operations that require a clean state.
        /// Wraps MachineWait.EnsureMachineReadyAsync for sync callers.
        /// </summary>
        public static bool EnsureMachineReady(Machine machine, int idleTimeoutMs = 0)
        {
            if (idleTimeoutMs <= 0)
            {
                idleTimeoutMs = IdleWaitTimeoutMs;
            }
            return MachineWait.EnsureMachineReadyAsync(machine, idleTimeoutMs).GetAwaiter().GetResult();
        }

    }
}
