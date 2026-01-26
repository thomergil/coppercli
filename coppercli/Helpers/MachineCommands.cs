// Extracted from Program.cs - Repeated G-code command patterns

using coppercli.Core.Communication;
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
            machine.SendLine(CmdAbsolute);
            machine.SendLine($"{CmdRapidMove} Z{height:F3}");
        }

        /// <summary>
        /// Performs a relative move on a single axis, then returns to absolute mode.
        /// </summary>
        public static void RelativeMove(Machine machine, string axis, double distance)
        {
            machine.SendLine(CmdRelative);
            machine.SendLine($"{CmdRapidMove} {axis}{distance:F3}");
            machine.SendLine(CmdAbsolute);
        }

        /// <summary>
        /// Sends the home command ($H).
        /// </summary>
        public static void Home(Machine machine)
        {
            machine.SendLine(CmdHome);
        }

        /// <summary>
        /// Sends the unlock command ($X).
        /// </summary>
        public static void Unlock(Machine machine)
        {
            machine.SendLine(CmdUnlock);
        }

        /// <summary>
        /// Sends the spindle off command (M5).
        /// </summary>
        public static void StopSpindle(Machine machine)
        {
            machine.SendLine(CmdSpindleOff);
        }

        /// <summary>
        /// Sends the cycle start command (~).
        /// </summary>
        public static void CycleStartCmd(Machine machine)
        {
            machine.SendLine(CycleStart.ToString());
        }

        /// <summary>
        /// Zeros the work offset for the specified axes (e.g., "X0 Y0 Z0").
        /// </summary>
        public static void ZeroWorkOffset(Machine machine, string axes)
        {
            machine.SendLine($"{CmdZeroWorkOffset} {axes}");
        }

        /// <summary>
        /// Rapid move to specified XY position.
        /// </summary>
        public static void RapidMoveXY(Machine machine, double x, double y)
        {
            machine.SendLine($"{CmdRapidMove} X{x:F3} Y{y:F3}");
        }

        /// <summary>
        /// Rapid move to specified Z position.
        /// </summary>
        public static void RapidMoveZ(Machine machine, double z)
        {
            machine.SendLine($"{CmdRapidMove} Z{z:F3}");
        }

        /// <summary>
        /// Linear move to specified XY position at given feed rate.
        /// </summary>
        public static void LinearMoveXY(Machine machine, double x, double y, double feed)
        {
            machine.SendLine($"{CmdLinearMove} X{x:F3} Y{y:F3} F{feed:F0}");
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
        /// Sets incremental distance mode (G91).
        /// </summary>
        public static void SetRelativeMode(Machine machine)
        {
            machine.SendLine(CmdRelative);
        }

        /// <summary>
        /// Always sends soft reset and unlocks if needed. Use to stop active operations.
        /// </summary>
        public static void ForceResetAndUnlock(Machine machine)
        {
            machine.SoftReset();
            Thread.Sleep(CliConstants.ResetWaitMs);

            if (machine.Status.StartsWith(StatusAlarm))
            {
                machine.SendLine(CmdUnlock);
                Thread.Sleep(CliConstants.CommandDelayMs);
            }
        }

        /// <summary>
        /// Clears Door or Alarm state if present. Returns true if reset was performed.
        /// </summary>
        public static bool ClearDoorOrAlarm(Machine machine)
        {
            if (!machine.Status.StartsWith(StatusDoor) && !machine.Status.StartsWith(StatusAlarm))
            {
                return false;
            }

            ForceResetAndUnlock(machine);
            return true;
        }
    }
}
