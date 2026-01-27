// Macro command types and data structures

namespace coppercli.Macro
{
    /// <summary>
    /// All available macro commands. Every TUI operation has a corresponding keyword.
    /// </summary>
    public enum MacroCommandType
    {
        // File
        Load,

        // Movement
        Jog,
        Home,
        Safe,
        Zero,
        Unlock,

        // Probing
        ProbeZ,     // "probe z"
        ProbeGrid,  // "probe grid"
        ProbeApply, // "probe apply"

        // Execution
        Mill,

        // User interaction
        Prompt,
        Confirm,
        Echo,

        // Flow control
        Wait
    }

    /// <summary>
    /// A parsed macro command with its arguments and source location.
    /// </summary>
    public record MacroCommand(
        MacroCommandType Type,
        string[] Args,
        int LineNumber,
        string OriginalLine
    )
    {
        /// <summary>
        /// Gets a display-friendly representation of this command.
        /// </summary>
        public string DisplayText
        {
            get
            {
                return Type switch
                {
                    MacroCommandType.Prompt => Args.Length > 0 ? $"prompt \"{Args[0]}\"" : "prompt",
                    MacroCommandType.Confirm => Args.Length > 0 ? $"confirm \"{Args[0]}\"" : "confirm",
                    MacroCommandType.Echo => Args.Length > 0 ? $"echo \"{Args[0]}\"" : "echo",
                    MacroCommandType.Load => Args.Length > 0 ? $"load {Args[0]}" : "load",
                    MacroCommandType.Zero => Args.Length > 0 ? $"zero {Args[0]}" : "zero xyz",
                    MacroCommandType.Wait => "wait",
                    MacroCommandType.ProbeZ => "probe z",
                    MacroCommandType.ProbeGrid => "probe grid",
                    MacroCommandType.ProbeApply => "probe apply",
                    _ => Type.ToString().ToLower()
                };
            }
        }
    }

}
