namespace coppercli.Macro
{
    /// <summary>
    /// Every operation the TUI offers has a keyword here, and `MacroParser` maps the keyword to it.
    /// </summary>
    public enum MacroCommandType
    {
        Load,

        Jog,
        Home,
        Safe,
        Zero,
        Unlock,

        ProbeZ,
        ProbeGrid,
        ProbeApply,

        Mill,

        Prompt,
        Confirm,
        Echo,

        Wait
    }

    public record MacroCommand(
        MacroCommandType Type,
        string[] Args,
        int LineNumber,
        string OriginalLine
    )
    {
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
