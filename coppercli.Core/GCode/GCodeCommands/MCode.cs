namespace coppercli.Core.GCode.GCodeCommands
{
    public class MCode : Command
    {
        public int Code;

        public bool IsToolChange => GCodeNumbers.ClassifyPauseMCode(Code) == GCodeNumbers.PauseMCode.ToolChange;

        /// <summary>M0 or M1: the program waits for the operator.</summary>
        public bool IsPause =>
            GCodeNumbers.ClassifyPauseMCode(Code) is GCodeNumbers.PauseMCode.ProgramStop or GCodeNumbers.PauseMCode.OptionalStop;

        public bool StartsTheSpindle =>
            Code is GCodeNumbers.MCodeSpindleClockwise or GCodeNumbers.MCodeSpindleCounterclockwise;

        public bool StopsTheSpindle => Code == GCodeNumbers.MCodeSpindleStop;
    }
}
