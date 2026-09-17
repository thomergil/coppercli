using System.Text.RegularExpressions;
using coppercli.Helpers;
using static coppercli.CliConstants;

namespace coppercli.Macro
{
    public record MacroPlaceholder(string Name, int Index);

    internal static class MacroParser
    {
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

        private static readonly Regex PlaceholderRegex = new(@"\[(\w+):file\]", RegexOptions.Compiled);

        /// <summary>
        /// `MacroMenu` prompts for the placeholders in this order.
        /// </summary>
        public static List<MacroPlaceholder> ExtractPlaceholders(List<MacroCommand> commands)
        {
            var seen = new HashSet<string>();
            var placeholders = new List<MacroPlaceholder>();
            int index = 0;

            foreach (var cmd in commands)
            {
                foreach (var arg in cmd.Args)
                {
                    foreach (Match match in PlaceholderRegex.Matches(arg))
                    {
                        var name = match.Groups[1].Value;
                        if (seen.Add(name))
                        {
                            placeholders.Add(new MacroPlaceholder(name, index++));
                        }
                    }
                }
            }

            return placeholders;
        }

        public static List<MacroCommand> SubstitutePlaceholders(
            List<MacroCommand> commands,
            Dictionary<string, string> values)
        {
            var result = new List<MacroCommand>();

            foreach (var cmd in commands)
            {
                var newArgs = new string[cmd.Args.Length];
                for (int i = 0; i < cmd.Args.Length; i++)
                {
                    newArgs[i] = PlaceholderRegex.Replace(cmd.Args[i], match =>
                    {
                        var name = match.Groups[1].Value;
                        return values.TryGetValue(name, out var value) ? value : match.Value;
                    });
                }
                result.Add(new MacroCommand(cmd.Type, newArgs, cmd.LineNumber, cmd.OriginalLine));
            }

            return result;
        }

        private static MacroCommand ParseLine(string line, int lineNumber, string macroDir)
        {
            var (keyword, args) = TokenizeLine(line);

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

            if (type == MacroCommandType.Load && args.Length > 0)
            {
                var filePath = args[0];

                filePath = PathHelpers.ExpandTilde(filePath);

                // A relative path in a macro file is relative to the macro file, not to the
                // working directory coppercli was started from.
                if (!Path.IsPathRooted(filePath))
                {
                    filePath = Path.Combine(macroDir, filePath);
                }

                args[0] = Path.GetFullPath(filePath);
            }

            return new MacroCommand(type, args, lineNumber, line);
        }

        /// <summary>
        /// A closing quote ends the token, so `"a"b` tokenizes as `a` then `b`.
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

    public class MacroParseException : Exception
    {
        public MacroParseException(string message) : base(message) { }
        public MacroParseException(string message, Exception inner) : base(message, inner) { }
    }
}
