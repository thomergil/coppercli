namespace coppercli.Core.GCode.GCodeCommands
{
    /// <summary>
    /// A block the parser cannot model as toolpath geometry but must not alter: G53 (machine
    /// coordinates), G10 (set offset), G43.1 (tool length offset), G38.x (probe) and G28/G30
    /// (home). Each carries its own axis words, and reading those as ordinary work-coordinate
    /// motion would move the tool somewhere the file never asked for, so the block is
    /// re-emitted verbatim.
    /// </summary>
    public class PassThrough : Command
    {
        public string Line = string.Empty;
    }
}
