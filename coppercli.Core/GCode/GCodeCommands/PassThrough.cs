namespace coppercli.Core.GCode.GCodeCommands
{
    /// <summary>
    /// A block the parser cannot model as toolpath geometry but must not alter: G53 (machine
    /// coordinates), G10 and G92 (set an offset), G43.1 (tool length offset) and G38.x
    /// (probe). Each contains axis words, and reading those as ordinary work-coordinate
    /// motion would move the tool somewhere the file never asked for, so the block is
    /// re-emitted verbatim.
    /// </summary>
    public class PassThrough : Command
    {
        public string Line = string.Empty;

        /// <summary>True for a move in machine coordinates (G53) that probes nothing and sets no offset.</summary>
        public bool MovesOnly;
    }
}
