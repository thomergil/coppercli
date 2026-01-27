// Macro file parser

using static coppercli.CliConstants;

namespace coppercli.Macro
{
    /// <summary>
    /// Parses .cmacro files into a list of MacroCommands.
    /// </summary>
    internal static class MacroParser
    {
        /// <summary>
        /// Parses a macro file and returns the list of commands.
        /// </summary>
        /// <param name="filePath">Path to the .cmacro file</param>
        /// <returns>List of parsed commands</returns>
        /// <exception cref="MacroParseException">Thrown when parsing fails</exception>
        public static List<MacroCommand> Parse(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new MacroParseException($"File not found: {filePath}");
            }

            var commands = new List<MacroCommand>();
            var lines = File.ReadAllLines(filePath);
            var macroDir = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".";

            for (int i = 0; i < lines.Length; i++)
            {
                int lineNumber = i + 1;
                var line = lines[i].Trim();

                // Skip empty lines and comments
                if (string.IsNullOrEmpty(line) || line[0] == MacroCommentChar)
                {
                    continue;
                }

                try
                {
                    var command = ParseLine(line, lineNumber, macroDir);
                    commands.Add(command);
                }
                catch (Exception ex) when (ex is not MacroParseException)
                {
                    throw new MacroParseException($"Line {lineNumber}: {ex.Message}", ex);
                }
            }

            return commands;
        }

        /// <summary>
        /// Parses a single line into a MacroCommand.
        /// </summary>
        private static MacroCommand ParseLine(string line, int lineNumber, string macroDir)
        {
            var (keyword, args) = TokenizeLine(line);

            // Handle two-word commands like "probe z", "probe grid", "probe apply"
            string fullCommand = keyword.ToLower();
            if (fullCommand == "probe" && args.Length > 0)
            {
                string subCmd = args[0].ToLower();
                if (subCmd == "z" || subCmd == "grid" || subCmd == "apply")
                {
                    fullCommand = $"probe {subCmd}";
                    args = args.Skip(1).ToArray();
                }
            }

            var type = fullCommand switch
            {
                "load" => MacroCommandType.Load,
                "jog" => MacroCommandType.Jog,
                "home" => MacroCommandType.Home,
                "safe" => MacroCommandType.Safe,
                "zero" => MacroCommandType.Zero,
                "unlock" => MacroCommandType.Unlock,
                "probe z" => MacroCommandType.ProbeZ,
                "probe grid" => MacroCommandType.ProbeGrid,
                "probe apply" => MacroCommandType.ProbeApply,
                "mill" => MacroCommandType.Mill,
                "prompt" => MacroCommandType.Prompt,
                "confirm" => MacroCommandType.Confirm,
                "echo" => MacroCommandType.Echo,
                "wait" => MacroCommandType.Wait,
                _ => throw new MacroParseException($"Line {lineNumber}: Unknown command '{keyword}'")
            };

            // Resolve file paths for load command
            if (type == MacroCommandType.Load && args.Length > 0)
            {
                var filePath = args[0];

                // Expand ~ for home directory
                if (filePath.StartsWith("~"))
                {
                    filePath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        filePath.Substring(2));
                }
                // Resolve relative paths against macro directory
                else if (!Path.IsPathRooted(filePath))
                {
                    filePath = Path.Combine(macroDir, filePath);
                }

                args[0] = Path.GetFullPath(filePath);
            }

            return new MacroCommand(type, args, lineNumber, line);
        }

        /// <summary>
        /// Tokenizes a line into keyword and arguments, handling quoted strings.
        /// </summary>
        private static (string Keyword, string[] Args) TokenizeLine(string line)
        {
            var tokens = new List<string>();
            var current = new System.Text.StringBuilder();
            bool inQuotes = false;
            char quoteChar = '"';

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (inQuotes)
                {
                    if (c == quoteChar)
                    {
                        inQuotes = false;
                        tokens.Add(current.ToString());
                        current.Clear();
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
                else if (c == '"' || c == '\'')
                {
                    inQuotes = true;
                    quoteChar = c;
                }
                else if (char.IsWhiteSpace(c))
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }
                }
                else
                {
                    current.Append(c);
                }
            }

            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
            }

            if (tokens.Count == 0)
            {
                throw new MacroParseException("Empty command");
            }

            string keyword = tokens[0];
            string[] args = tokens.Count > 1 ? tokens.Skip(1).ToArray() : Array.Empty<string>();

            return (keyword, args);
        }
    }

    /// <summary>
    /// Exception thrown when macro parsing fails.
    /// </summary>
    public class MacroParseException : Exception
    {
        public MacroParseException(string message) : base(message) { }
        public MacroParseException(string message, Exception inner) : base(message, inner) { }
    }
}
