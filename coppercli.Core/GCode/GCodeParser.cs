#nullable enable
using coppercli.Core.GCode.GCodeCommands;
using coppercli.Core.Util;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace coppercli.Core.GCode
{
    public enum ParseDistanceMode
    {
        Absolute,
        Incremental
    }

    public enum ParseUnit
    {
        Metric,
        Imperial
    }

    public class ParserState
    {
        public Vector3 Position;
        public bool[] PositionValid;
        public ArcPlane Plane;
        public double Feed;
        public ParseDistanceMode DistanceMode;
        public ParseDistanceMode ArcDistanceMode;
        public ParseUnit Unit;
        public int LastMotionMode;

        public ParserState()
        {
            Position = Vector3.MinValue;
            PositionValid = new bool[] { false, false, false };
            Plane = ArcPlane.XY;
            Feed = 0;
            DistanceMode = ParseDistanceMode.Absolute;
            ArcDistanceMode = ParseDistanceMode.Incremental;
            Unit = ParseUnit.Metric;
            LastMotionMode = -1;
        }
    }

    struct Word
    {
        public char Command;
        public double Parameter;

        public override string ToString()
        {
            return $"{Command}{Parameter}";
        }
    }

    public static class GCodeParser
    {
        public static ParserState State = null!;

        public static Regex GCodeSplitter = new Regex(@"([A-Z])\s*(\-?\d+\.?\d*)", RegexOptions.Compiled);
        private static double[] MotionCommands = new double[] { 0, 1, 2, 3 };
        private static string ValidWords = "GMXYZIJKFRSP";
        private static string IgnoreAxes = "ABC";
        public static List<Command> Commands = null!;
        public static List<string> Warnings = null!;

        /// <summary>
        /// When true, A, B, C axes are ignored during parsing
        /// </summary>
        public static bool IgnoreAdditionalAxes { get; set; } = true;

        public static void Reset()
        {
            State = new ParserState();
            Commands = new List<Command>();
            Warnings = new List<string>();
        }

        static GCodeParser()
        {
            Reset();
        }

        public static void ParseFile(string path)
        {
            Parse(File.ReadLines(path));
        }

        // =========================================================================
        // M-code detection utilities (for tool change, pause, etc.)
        // =========================================================================

        private static readonly Regex M6Pattern = new Regex(@"\bM0*6\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex M0Pattern = new Regex(@"\bM0+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ToolNumberPattern = new Regex(@"\bT(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ToolNamePattern = new Regex(@"\(([^)]+)\)", RegexOptions.Compiled);

        /// <summary>
        /// Returns true if the line contains an M6 (tool change) command.
        /// </summary>
        public static bool IsM6Line(string line)
        {
            return M6Pattern.IsMatch(line);
        }

        /// <summary>
        /// Returns true if the line contains an M0 (program pause) command.
        /// </summary>
        public static bool IsM0Line(string line)
        {
            return M0Pattern.IsMatch(line);
        }

        /// <summary>
        /// Extracts the tool number from a line (e.g., "T1" returns 1).
        /// Returns null if no tool number found.
        /// </summary>
        public static int? ExtractToolNumber(string line)
        {
            var match = ToolNumberPattern.Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int toolNumber))
            {
                return toolNumber;
            }
            return null;
        }

        /// <summary>
        /// Extracts the tool name from a comment in the line.
        /// Returns null if no comment found.
        /// </summary>
        public static string? ExtractToolName(string line)
        {
            var match = ToolNamePattern.Match(line);
            if (match.Success)
            {
                return match.Groups[1].Value.Trim();
            }
            return null;
        }

        /// <summary>
        /// Finds tool info by searching the given line and preceding lines.
        /// Returns (toolNumber, toolName) tuple.
        /// </summary>
        public static (int? ToolNumber, string? ToolName) FindToolInfo(IList<string> lines, int lineIndex)
        {
            int? toolNumber = null;
            string? toolName = null;

            // First check the current line
            if (lineIndex >= 0 && lineIndex < lines.Count)
            {
                string line = lines[lineIndex];
                toolNumber = ExtractToolNumber(line);
                toolName = ExtractToolName(line);
            }

            // Search backwards for missing info (tool number and/or name)
            for (int i = lineIndex - 1; i >= 0 && i >= lineIndex - Constants.ToolInfoSearchLines; i--)
            {
                if (i >= lines.Count)
                {
                    continue;
                }

                // Stop if we have both number and name
                if (toolNumber != null && toolName != null)
                {
                    break;
                }

                string prevLine = lines[i];

                // Look for tool number if we don't have one
                if (toolNumber == null)
                {
                    toolNumber = ExtractToolNumber(prevLine);
                }

                // Look for tool name if we don't have one
                if (toolName == null)
                {
                    toolName = ExtractToolName(prevLine);
                }
            }

            return (toolNumber, toolName);
        }

        public static void Parse(IEnumerable<string> file)
        {
            int i = 0;

            foreach (string linei in file)
            {
                i++;

                // Extract T code before cleanup strips comments
                ExtractTCode(linei, i);

                string line = CleanupLine(linei, i);

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                Parse(line.ToUpper(), i);
            }
        }

        private static readonly Regex TCodePattern = new Regex(@"T(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex CommentPattern = new Regex(@"\(([^)]+)\)", RegexOptions.Compiled);

        static void ExtractTCode(string line, int lineNumber)
        {
            var tMatch = TCodePattern.Match(line);
            if (!tMatch.Success)
            {
                return;
            }

            int toolNumber = int.Parse(tMatch.Groups[1].Value);
            string comment = "";

            var commentMatch = CommentPattern.Match(line);
            if (commentMatch.Success)
            {
                comment = commentMatch.Groups[1].Value.Trim();
            }

            Commands.Add(new TCode { ToolNumber = toolNumber, Comment = string.IsNullOrEmpty(comment) ? null : comment, LineNumber = lineNumber });
        }

        static string CleanupLine(string line, int lineNumber)
        {
            int commentIndex = line.IndexOf(';');

            if (commentIndex > -1)
            {
                line = line.Remove(commentIndex);
            }

            int start = -1;

            while ((start = line.IndexOf('(')) != -1)
            {
                int end = line.IndexOf(')');

                if (end < start)
                {
                    throw new ParseException("mismatched parentheses", lineNumber);
                }

                line = line.Remove(start, end - start);
            }

            return line;
        }

        static void Parse(string line, int lineNumber)
        {
            MatchCollection matches = GCodeSplitter.Matches(line);

            List<Word> Words = new List<Word>(matches.Count);

            foreach (Match match in matches)
            {
                Words.Add(new Word() { Command = match.Groups[1].Value[0], Parameter = double.Parse(match.Groups[2].Value, Constants.DecimalParseFormat) });
            }

            for (int i = 0; i < Words.Count; i++)
            {
                if (Words[i].Command == 'N')
                {
                    Words.RemoveAt(i--);
                    continue;
                }

                // T codes are handled in ExtractTCode before cleanup
                if (Words[i].Command == 'T')
                {
                    Words.RemoveAt(i--);
                    continue;
                }

                if (IgnoreAxes.Contains(Words[i].Command) && IgnoreAdditionalAxes)
                {
                    Words.RemoveAt(i--);
                    continue;
                }

                if (!ValidWords.Contains(Words[i].Command))
                {
                    Warnings.Add($"ignoring unknown word (letter): \"{Words[i]}\". (line {lineNumber})");
                    Words.RemoveAt(i--);
                    continue;
                }

                if (Words[i].Command != 'F')
                {
                    continue;
                }

                State.Feed = Words[i].Parameter;
                if (State.Unit == ParseUnit.Imperial)
                {
                    State.Feed *= 25.4;
                }
                Words.RemoveAt(i--);
                continue;
            }

            for (int i = 0; i < Words.Count; i++)
            {
                if (Words[i].Command == 'M')
                {
                    int param = (int)Words[i].Parameter;

                    if (param != Words[i].Parameter || param < 0)
                    {
                        throw new ParseException("M code can only have positive integer parameters", lineNumber);
                    }

                    Commands.Add(new MCode() { Code = param, LineNumber = lineNumber });

                    Words.RemoveAt(i);
                    i--;
                    continue;
                }

                if (Words[i].Command == 'S')
                {
                    double param = Words[i].Parameter;

                    if (param < 0)
                    {
                        Warnings.Add($"spindle speed must be positive. (line {lineNumber})");
                    }

                    Commands.Add(new Spindle() { Speed = Math.Abs(param), LineNumber = lineNumber });

                    Words.RemoveAt(i);
                    i--;
                    continue;
                }

                if (Words[i].Command == 'G' && !MotionCommands.Contains(Words[i].Parameter))
                {
                    double param = Words[i].Parameter;

                    if (param == GCodeNumbers.DistanceAbsolute)
                    {
                        State.DistanceMode = ParseDistanceMode.Absolute;
                        Words.RemoveAt(i);
                        i--;
                        continue;
                    }
                    if (param == GCodeNumbers.DistanceIncremental)
                    {
                        State.DistanceMode = ParseDistanceMode.Incremental;
                        Words.RemoveAt(i);
                        i--;
                        continue;
                    }
                    if (param == GCodeNumbers.ArcDistanceAbsolute)
                    {
                        State.ArcDistanceMode = ParseDistanceMode.Absolute;
                        Words.RemoveAt(i);
                        continue;
                    }
                    if (param == GCodeNumbers.ArcDistanceIncremental)
                    {
                        State.ArcDistanceMode = ParseDistanceMode.Incremental;
                        Words.RemoveAt(i);
                        i--;
                        continue;
                    }
                    if (param == GCodeNumbers.UnitsMillimeters)
                    {
                        State.Unit = ParseUnit.Metric;
                        Words.RemoveAt(i);
                        i--;
                        continue;
                    }
                    if (param == GCodeNumbers.UnitsInches)
                    {
                        State.Unit = ParseUnit.Imperial;
                        Warnings.Add($"{Constants.WarningPrefixInches}: File uses inches (G{GCodeNumbers.UnitsInches}) - coordinates may be incorrect if machine expects mm. (line {lineNumber})");
                        Words.RemoveAt(i);
                        i--;
                        continue;
                    }
                    if (param == GCodeNumbers.Home)
                    {
                        Warnings.Add($"{Constants.WarningPrefixDanger}: G{GCodeNumbers.Home} (Home) command found - may crash into workpiece! (line {lineNumber})");
                        Words.RemoveAt(i);
                        i--;
                        continue;
                    }
                    if (param == GCodeNumbers.HomeSecondary)
                    {
                        Warnings.Add($"{Constants.WarningPrefixDanger}: G{GCodeNumbers.HomeSecondary} (Secondary home) command found - may crash into workpiece! (line {lineNumber})");
                        Words.RemoveAt(i);
                        i--;
                        continue;
                    }
                    if (param == GCodeNumbers.PlaneXY)
                    {
                        State.Plane = ArcPlane.XY;
                        Words.RemoveAt(i);
                        i--;
                        continue;
                    }
                    if (param == GCodeNumbers.PlaneYZ)
                    {
                        State.Plane = ArcPlane.ZX;
                        Words.RemoveAt(i);
                        i--;
                        continue;
                    }
                    if (param == GCodeNumbers.PlaneZX)
                    {
                        State.Plane = ArcPlane.YZ;
                        Words.RemoveAt(i);
                        i--;
                        continue;
                    }
                    if (param == GCodeNumbers.Dwell)
                    {
                        if (Words.Count >= 2 && Words[i + 1].Command == 'P')
                        {
                            if (Words[i + 1].Parameter < 0)
                            {
                                Warnings.Add($"dwell time must be positive. (line {lineNumber})");
                            }

                            Commands.Add(new Dwell() { Seconds = Math.Abs(Words[i + 1].Parameter), LineNumber = lineNumber });
                            Words.RemoveAt(i + 1);
                            Words.RemoveAt(i);
                            i--;
                            continue;
                        }
                    }
                    // G94 = units per minute feed rate (default) - safe to ignore
                    if (param == GCodeNumbers.FeedRateUnitsPerMinute)
                    {
                        Words.RemoveAt(i);
                        i--;
                        continue;
                    }
                    // G93 = inverse time feed rate - warn because height map and time
                    // calculations assume G94. Feed values would be misinterpreted.
                    if (param == GCodeNumbers.FeedRateInverseTime)
                    {
                        Warnings.Add($"WARNING: G93 (inverse time feed) not fully supported - height map correction and time estimates will be incorrect. (line {lineNumber})");
                        Words.RemoveAt(i);
                        i--;
                        continue;
                    }
                    // These are valid GRBL commands - pass through without warning
                    if (param == GCodeNumbers.MachineCoordinates ||
                        param == GCodeNumbers.SetWorkOffset ||
                        param == GCodeNumbers.Home ||
                        param == GCodeNumbers.HomeSecondary ||
                        (param >= GCodeNumbers.ProbeToward && param <= GCodeNumbers.ProbeAwayNoError) ||
                        param == GCodeNumbers.ToolLengthOffsetDynamic)
                    {
                        Words.RemoveAt(i);
                        i--;
                        continue;
                    }

                    Warnings.Add($"ignoring unknown command G{param}. (line {lineNumber})");
                    Words.RemoveAt(i--);
                }
            }

            if (Words.Count == 0)
            {
                return;
            }

            int MotionMode = State.LastMotionMode;

            if (Words.First().Command == 'G')
            {
                MotionMode = (int)Words.First().Parameter;
                State.LastMotionMode = MotionMode;
                Words.RemoveAt(0);
            }

            if (MotionMode < 0)
            {
                throw new ParseException("no motion mode active", lineNumber);
            }

            double UnitMultiplier = (State.Unit == ParseUnit.Metric) ? 1 : 25.4;

            Vector3 EndPos = State.Position;

            var StartValid = State.PositionValid.All(isValid => isValid);

            if (State.DistanceMode == ParseDistanceMode.Incremental && !StartValid)
            {
                throw new ParseException("incremental motion is only allowed after an absolute position has been established (eg. with \"G90 G0 X0 Y0 Z5\")", lineNumber);
            }

            if ((MotionMode == 2 || MotionMode == 3) && !StartValid)
            {
                throw new ParseException("arcs (G2/G3) are only allowed after an absolute position has been established (eg. with \"G90 G0 X0 Y0 Z5\")", lineNumber);
            }

            // Find EndPos
            {
                int Incremental = (State.DistanceMode == ParseDistanceMode.Incremental) ? 1 : 0;

                for (int i = 0; i < Words.Count; i++)
                {
                    if (Words[i].Command != 'X')
                    {
                        continue;
                    }
                    EndPos.X = Words[i].Parameter * UnitMultiplier + Incremental * EndPos.X;
                    Words.RemoveAt(i);
                    State.PositionValid[0] = true;
                    break;
                }

                for (int i = 0; i < Words.Count; i++)
                {
                    if (Words[i].Command != 'Y')
                    {
                        continue;
                    }
                    EndPos.Y = Words[i].Parameter * UnitMultiplier + Incremental * EndPos.Y;
                    Words.RemoveAt(i);
                    State.PositionValid[1] = true;
                    break;
                }

                for (int i = 0; i < Words.Count; i++)
                {
                    if (Words[i].Command != 'Z')
                    {
                        continue;
                    }
                    EndPos.Z = Words[i].Parameter * UnitMultiplier + Incremental * EndPos.Z;
                    Words.RemoveAt(i);
                    State.PositionValid[2] = true;
                    break;
                }
            }

            if (MotionMode != 0 && State.Feed <= 0)
            {
                throw new ParseException("feed rate undefined", lineNumber);
            }

            // Note: Feed moves before absolute position is established won't have
            // height maps applied, but this is common in G-code preambles and not
            // worth warning about.

            if (MotionMode <= 1)
            {
                if (Words.Count > 0)
                {
                    Warnings.Add($"motion command must be last in line (ignoring unused words {string.Join(" ", Words)} in block). (line {lineNumber})");
                }

                Line motion = new Line();
                motion.Start = State.Position;
                motion.End = EndPos;
                motion.Feed = State.Feed;
                motion.Rapid = MotionMode == 0;
                motion.LineNumber = lineNumber;
                motion.StartValid = StartValid;
                State.PositionValid.CopyTo(motion.PositionValid, 0);

                Commands.Add(motion);
                State.Position = EndPos;
                return;
            }

            double U, V;

            bool IJKused = false;

            switch (State.Plane)
            {
                default:
                    U = State.Position.X;
                    V = State.Position.Y;
                    break;
                case ArcPlane.YZ:
                    U = State.Position.Y;
                    V = State.Position.Z;
                    break;
                case ArcPlane.ZX:
                    U = State.Position.Z;
                    V = State.Position.X;
                    break;
            }

            // Find IJK
            {
                int ArcIncremental = (State.ArcDistanceMode == ParseDistanceMode.Incremental) ? 1 : 0;

                for (int i = 0; i < Words.Count; i++)
                {
                    if (Words[i].Command != 'I')
                    {
                        continue;
                    }

                    switch (State.Plane)
                    {
                        case ArcPlane.XY:
                            U = Words[i].Parameter * UnitMultiplier + ArcIncremental * State.Position.X;
                            break;
                        case ArcPlane.YZ:
                            throw new ParseException("current plane is YZ, I word is invalid", lineNumber);
                        case ArcPlane.ZX:
                            V = Words[i].Parameter * UnitMultiplier + ArcIncremental * State.Position.X;
                            break;
                    }

                    IJKused = true;
                    Words.RemoveAt(i);
                    break;
                }

                for (int i = 0; i < Words.Count; i++)
                {
                    if (Words[i].Command != 'J')
                    {
                        continue;
                    }

                    switch (State.Plane)
                    {
                        case ArcPlane.XY:
                            V = Words[i].Parameter * UnitMultiplier + ArcIncremental * State.Position.Y;
                            break;
                        case ArcPlane.YZ:
                            U = Words[i].Parameter * UnitMultiplier + ArcIncremental * State.Position.Y;
                            break;
                        case ArcPlane.ZX:
                            throw new ParseException("current plane is ZX, J word is invalid", lineNumber);
                    }

                    IJKused = true;
                    Words.RemoveAt(i);
                    break;
                }

                for (int i = 0; i < Words.Count; i++)
                {
                    if (Words[i].Command != 'K')
                    {
                        continue;
                    }

                    switch (State.Plane)
                    {
                        case ArcPlane.XY:
                            throw new ParseException("current plane is XY, K word is invalid", lineNumber);
                        case ArcPlane.YZ:
                            V = Words[i].Parameter * UnitMultiplier + ArcIncremental * State.Position.Z;
                            break;
                        case ArcPlane.ZX:
                            U = Words[i].Parameter * UnitMultiplier + ArcIncremental * State.Position.Z;
                            break;
                    }

                    IJKused = true;
                    Words.RemoveAt(i);
                    break;
                }
            }

            // Resolve Radius
            for (int i = 0; i < Words.Count; i++)
            {
                if (Words[i].Command != 'R')
                {
                    continue;
                }

                if (IJKused)
                {
                    throw new ParseException("both IJK and R notation used", lineNumber);
                }

                if (State.Position == EndPos)
                {
                    throw new ParseException("arcs in R-notation must have non-coincident start and end points", lineNumber);
                }

                double Radius = Words[i].Parameter * UnitMultiplier;

                if (Radius == 0)
                {
                    throw new ParseException("radius can't be zero", lineNumber);
                }

                double A, B;

                switch (State.Plane)
                {
                    default:
                        A = EndPos.X;
                        B = EndPos.Y;
                        break;
                    case ArcPlane.YZ:
                        A = EndPos.Y;
                        B = EndPos.Z;
                        break;
                    case ArcPlane.ZX:
                        A = EndPos.Z;
                        B = EndPos.X;
                        break;
                }

                A -= U;
                B -= V;

                double h_x2_div_d = 4.0 * (Radius * Radius) - (A * A + B * B);
                if (h_x2_div_d < 0)
                {
                    throw new ParseException("arc radius too small to reach both ends", lineNumber);
                }

                h_x2_div_d = -Math.Sqrt(h_x2_div_d) / Math.Sqrt(A * A + B * B);

                if (MotionMode == 3 ^ Radius < 0)
                {
                    h_x2_div_d = -h_x2_div_d;
                }

                U += 0.5 * (A - (B * h_x2_div_d));
                V += 0.5 * (B + (A * h_x2_div_d));

                Words.RemoveAt(i);
                break;
            }

            if (Words.Count > 0)
            {
                Warnings.Add($"motion command must be last in line (ignoring unused words {string.Join(" ", Words)} in block). (line {lineNumber})");
            }

            Arc arc = new Arc();
            arc.Start = State.Position;
            arc.End = EndPos;
            arc.Feed = State.Feed;
            arc.Direction = (MotionMode == 2) ? ArcDirection.CW : ArcDirection.CCW;
            arc.U = U;
            arc.V = V;
            arc.LineNumber = lineNumber;
            arc.Plane = State.Plane;

            Commands.Add(arc);
            State.Position = EndPos;
            return;
        }
    }
}
