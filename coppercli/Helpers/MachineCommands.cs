using coppercli.Core.Communication;
using coppercli.Core.Controllers;
using coppercli.Core.GCode;
using static coppercli.Core.Util.Constants;
using static coppercli.Core.Util.GrblProtocol;
using static coppercli.Core.Util.GCodeFormat;

namespace coppercli.Helpers
{
    internal static class MachineCommands
    {
        public static void MoveToSafeHeight(Machine machine, double height)
        {
            Logger.Log($"MoveToSafeHeight: sending {CmdAbsolute} then {CmdRapidMove} Z{height:F3}");
            machine.SendLine(CmdAbsolute);
            machine.SendLine(Inv($"{CmdRapidMove} Z{height:F3}"));
        }


        /// <summary>
        /// MachineWait.HomeAsync is the only code that sets Machine.IsHomed, so every sync
        /// caller goes through here rather than sending $H itself.
        /// </summary>
        public static bool HomeAndWait(Machine machine, int timeoutMs = HomingTimeoutMs)
        {
            Logger.Log("HomeAndWait: calling MachineWait.HomeAsync");
            return MachineWait.HomeAsync(machine, timeoutMs).GetAwaiter().GetResult().Success;
        }

        public static void Unlock(Machine machine)
        {
            machine.SendLine(CmdUnlock);
        }

        /// <summary>
        /// Front ends set work zero here so every origin change calls
        /// AppState.HandleWorkZeroChange to update the height map.
        /// </summary>
        /// <param name="axes">The axes string, such as "X0 Y0 Z0" or "Z0".</param>
        /// <returns>Why nothing was sent, and what became of the height map.</returns>
        public static WorkZeroResult SetWorkZeroAndWait(Machine machine, string axes)
        {
            // Checked before the offset is written: moving the datum invalidates the height
            // map whose corrections are already in the file the run is streaming.
            if (AppState.IsRunInProgress && AppState.ZeroTouchesXY(axes))
            {
                Logger.Log("SetWorkZeroAndWait: refused, a run owns the machine (axes={0})", axes);
                return new WorkZeroResult(
                    CliConstants.ErrorZeroXYDuringRun, WorkZeroOutcome.NothingToDo);
            }

            // Nothing is recorded until GRBL has taken the offset. Marking an origin the
            // machine does not have would also discard the height map on an X/Y zero.
            string? notWritten = MachineWait.ZeroWorkOffsetAsync(machine, axes)
                .GetAwaiter().GetResult();

            if (notWritten != null)
            {
                Logger.Log("SetWorkZeroAndWait: {0} (axes={1})", notWritten, axes);
                return new WorkZeroResult(notWritten, WorkZeroOutcome.NothingToDo);
            }

            AppState.MarkWorkZeroSet();
            var outcome = AppState.HandleWorkZeroChange(axes);

            // Only all three axes make a full origin, which the next launch offers to trust.
            // Saved here so neither front end has to.
            if (AppState.ZeroIsFullOrigin(axes))
            {
                AppState.Session.HasStoredWorkZero = true;
                Persistence.SaveSession();
            }

            Logger.Log($"SetWorkZeroAndWait: work zero set (axes={axes})");
            return new WorkZeroResult(null, outcome);
        }

        public static void RapidMoveXY(Machine machine, double x, double y)
        {
            machine.SendLine(Inv($"{CmdRapidMove} X{x:F3} Y{y:F3}"));
        }

        /// <summary>
        /// Returns false without moving while the probe is in contact, because an X/Y move
        /// would drag the tip sideways across the workpiece. Z is left where it is.
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

        public static bool GotoWorkOriginXY(Machine machine)
        {
            return GotoAbsoluteXY(machine, 0, 0);
        }

        public static bool GotoFileCenterXY(Machine machine, GCodeFile? file)
        {
            if (file == null)
            {
                return false;
            }
            return GotoAbsoluteXY(machine, file.Center.X, file.Center.Y);
        }

        public static void SetAbsoluteMode(Machine machine)
        {
            machine.SendLine(CmdAbsolute);
        }


    }
}
