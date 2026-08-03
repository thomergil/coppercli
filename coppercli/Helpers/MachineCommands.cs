// Extracted from Program.cs - Repeated G-code command patterns

using coppercli.Core.Communication;
using coppercli.Core.Controllers;
using coppercli.Core.GCode;
using static coppercli.Core.Util.Constants;
using static coppercli.Core.Util.GrblProtocol;
using static coppercli.Core.Util.GCodeFormat;

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
            machine.SendLine(Inv($"{CmdRapidMove} Z{height:F3}"));
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
            return MachineWait.HomeAsync(machine, timeoutMs).GetAwaiter().GetResult().Success;
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
            machine.SendLine(Inv($"{CmdZeroWorkOffset} {axes}"));
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

            // Zeroing all three axes establishes a full origin worth offering to trust on
            // the next launch. Persisting it here means neither front end has to remember
            // to - both used to do it by hand afterwards.
            string upper = axes.ToUpperInvariant();
            if (upper.Contains('X') && upper.Contains('Y') && upper.Contains('Z'))
            {
                AppState.Session.HasStoredWorkZero = true;
                Persistence.SaveSession();
            }

            Logger.Log($"SetWorkZeroAndWait: IsWorkZeroSet = true (axes={axes})");
        }

        /// <summary>
        /// Rapid move to specified XY position.
        /// </summary>
        public static void RapidMoveXY(Machine machine, double x, double y)
        {
            machine.SendLine(Inv($"{CmdRapidMove} X{x:F3} Y{y:F3}"));
        }

        /// <summary>
        /// Rapid move to an absolute XY target, guarded against moving while the probe
        /// is in contact (which would drag the probe tip sideways across the workpiece).
        /// Returns false without moving if the probe is in contact. Single source of truth
        /// for the guarded "goto XY" workflow shared by the TUI and web front ends.
        /// </summary>
        public static bool GotoAbsoluteXY(Machine machine, double x, double y)
        {
            if (machine.PinStateProbe)
            {
                Logger.Log($"Blocked goto XY ({x:F3},{y:F3}): probe in contact");
                return false;
            }
            SetAbsoluteMode(machine);
            RapidMoveXY(machine, x, y);
            return true;
        }

        /// <summary>
        /// Guarded rapid move to work origin (X0 Y0). Does not change Z.
        /// </summary>
        public static bool GotoWorkOriginXY(Machine machine)
        {
            return GotoAbsoluteXY(machine, 0, 0);
        }

        /// <summary>
        /// Guarded rapid move to the centre of the loaded file. No-op (returns false)
        /// when no file is loaded. Does not change Z.
        /// </summary>
        public static bool GotoFileCenterXY(Machine machine, GCodeFile? file)
        {
            if (file == null)
            {
                return false;
            }
            return GotoAbsoluteXY(machine, file.Center.X, file.Center.Y);
        }

        /// <summary>
        /// Probe toward workpiece on Z axis until contact (no error if no contact).
        /// </summary>
        public static void ProbeZ(Machine machine, double maxDepth, double feed)
        {
            machine.SendLine(Inv($"{CmdProbeToward} Z-{maxDepth:F3} F{feed:F1}"));
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
        /// Prepares machine for an operation by waiting for the machine to settle, waiting for Idle,
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
