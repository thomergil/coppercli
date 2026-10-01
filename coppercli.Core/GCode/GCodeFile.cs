#nullable enable

using coppercli.Core.GCode.GCodeCommands;
using coppercli.Core.Util;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;

namespace coppercli.Core.GCode
{
    public class GCodeFile
    {
        public ReadOnlyCollection<Command> Toolpath;
        public string FileName = string.Empty;

        /// <summary>
        /// Full path this file was loaded from, or empty for a generated toolpath. Held on
        /// the file, so nothing keeps a second copy of which board is loaded.
        /// </summary>
        public string FilePath = string.Empty;

        public Vector3 Min { get; private set; }
        public Vector3 Max { get; private set; }
        /// <summary>
        /// The extent of the toolpath, derived from its bounds. Clamped at zero because a
        /// file with no motion leaves the bounds at their empty sentinels, which subtract
        /// to a negative extent.
        /// </summary>
        public Vector3 Size => NonNegativeExtent(Max, Min);

        public Vector3 MinFeed { get; private set; }
        public Vector3 MaxFeed { get; private set; }
        /// <inheritdoc cref="Size"/>
        public Vector3 SizeFeed => NonNegativeExtent(MaxFeed, MinFeed);

        private static Vector3 NonNegativeExtent(Vector3 max, Vector3 min)
        {
            Vector3 extent = max - min;

            for (int i = 0; i < 3; i++)
            {
                if (extent[i] < 0)
                {
                    extent[i] = 0;
                }
            }

            return extent;
        }

        /// <summary>
        /// The area the job cuts: the feed-move bounds when they have width and height, and the
        /// whole toolpath's otherwise. The mill views and the board's sections are laid out on it.
        /// </summary>
        public (Vector2 Min, Vector2 Max) CuttingBounds =>
            HasArea(XY(MinFeed), XY(MaxFeed)) ? (XY(MinFeed), XY(MaxFeed)) : (XY(Min), XY(Max));

        /// <summary>True when <see cref="CuttingBounds"/> has width and height, so it can be divided.</summary>
        public bool CutsAnArea => HasArea(CuttingBounds.Min, CuttingBounds.Max);

        private static bool HasArea(Vector2 min, Vector2 max) =>
            max.X - min.X > Constants.MillMinRangeThreshold && max.Y - min.Y > Constants.MillMinRangeThreshold;

        private static Vector2 XY(Vector3 point) => new(point.X, point.Y);

        public bool ContainsMotion { get; private set; } = false;

        /// <summary>
        /// Midpoint of the toolpath bounds in X and Y, with Z left at 0, which is where the
        /// spindle parks for a reachable tool change. Derived from Min and Max so no caller
        /// recomputes (Min+Max)/2.
        /// </summary>
        public Vector3 Center => new Vector3((Min.X + Max.X) / 2, (Min.Y + Max.Y) / 2, 0);

        public double TravelDistance { get; private set; } = 0;

        /// <summary>How long the feed moves take at the feeds the file gives: every phase's <see cref="JobPhase.Time"/>.</summary>
        public TimeSpan TotalTime { get; }

        /// <summary>The phases of the job, in file order (see <see cref="JobPhase"/>).</summary>
        public IReadOnlyList<JobPhase> Phases { get; }

        private readonly int[] _phaseOf;

        /// <summary>The number, from 1, of the phase that holds the command at <paramref name="index"/> in <see cref="Toolpath"/>.</summary>
        public int PhaseOf(int index) => _phaseOf[index];

        /// <summary>Whether the operator can skip a phase: a job with one phase has nothing to skip.</summary>
        public bool OffersAChoiceOfPhases => Phases.Count > 1;

        /// <summary>What the parser reported, kept so a file derived from this one reports it too.</summary>
        private readonly IReadOnlyList<string> _parseWarnings;

        /// <summary>The warning about where the job lies, if any, then the parser's.</summary>
        public IReadOnlyList<string> Warnings { get; }

        /// <summary>
        /// The warnings the operator confirms before the machine runs this job: the ones that
        /// can put the tool somewhere it should not be.
        /// </summary>
        public IReadOnlyList<string> WarningsToConfirm { get; }

        public static bool GCodeIncludeMEnd { get; set; } = true;
        public static bool GCodeIncludeSpindle { get; set; } = true;
        public static bool GCodeIncludeDwell { get; set; } = true;

        private GCodeFile(List<Command> toolpath, IReadOnlyList<string> parseWarnings)
        {
            _parseWarnings = parseWarnings;

            for (int i = 0; i < toolpath.Count; i++)
            {
                Command c = toolpath[i];

                if (c is Motion)
                {
                    Motion m = (Motion)c;

                    // An arc that ends where it starts is a full circle, which is a real cut:
                    // drilled holes and circular isolation contours are written that way.
                    // Only a straight move does nothing at zero length, and only when its
                    // start is known on every axis: after a G53 or G92 block, Start holds
                    // values from before it.
                    if (m is Line { StartValid: true } && m.Start == m.End)
                    {
                        // Common in CAM output, and nothing the operator can act on, so
                        // this raises no warning.
                        toolpath.RemoveAt(i--);
                    }
                }
            }

            Toolpath = new ReadOnlyCollection<Command>(toolpath);

            Vector3 min = Vector3.MaxValue, max = Vector3.MinValue;
            Vector3 minfeed = Vector3.MaxValue, maxfeed = Vector3.MinValue;

            (Phases, _phaseOf) = JobPhase.Of(Toolpath);
            TotalTime = Phases.Aggregate(TimeSpan.Zero, (sum, phase) => sum + phase.Time);

            foreach (Command c in Toolpath)
            {
                if (c is not Motion { FullyKnown: true } m)
                {
                    continue;
                }

                ContainsMotion = true;
                TravelDistance += m.Length;

                bool isFeed = m is not Line { Rapid: true };

                foreach (Vector3 point in m.ExtremePoints)
                {
                    min = Vector3.ElementwiseMin(min, point);
                    max = Vector3.ElementwiseMax(max, point);

                    if (isFeed)
                    {
                        minfeed = Vector3.ElementwiseMin(minfeed, point);
                        maxfeed = Vector3.ElementwiseMax(maxfeed, point);
                    }
                }
            }

            Max = max;
            Min = min;
            MaxFeed = maxfeed;
            MinFeed = minfeed;

            Warnings = ReachesPastOrigin
                ? parseWarnings.Prepend(string.Format(Constants.WarningJobOriginFormat, Min.X, Min.Y, -Min.X, -Min.Y)).ToList()
                : parseWarnings;
            WarningsToConfirm = Warnings
                .Where(w => w.StartsWith(Constants.WarningPrefixDanger, StringComparison.Ordinal)
                    || w.StartsWith(Constants.WarningPrefixInches, StringComparison.Ordinal))
                .ToList();
        }

        /// <summary>
        /// The job reaches more than JobOriginToleranceMm left of or below work zero, so zero is
        /// not at its lower-left corner. A file with no motion leaves Min at its empty sentinel,
        /// so it never warns.
        /// </summary>
        private bool ReachesPastOrigin =>
            Min.X < -Constants.JobOriginToleranceMm || Min.Y < -Constants.JobOriginToleranceMm;

        public static GCodeFile Load(string path)
        {
            lock (GCodeParser.ParseLock)
            {
                GCodeParser.Reset();
                GCodeParser.ParseFile(path);

                string fileName = Path.GetFileName(path);
                return new GCodeFile(GCodeParser.Commands, GCodeParser.Warnings.ToList())
                {
                    FileName = fileName,
                    FilePath = Path.GetFullPath(path)
                };
            }
        }

        public static GCodeFile FromList(IEnumerable<string> file)
        {
            lock (GCodeParser.ParseLock)
            {
                GCodeParser.Reset();
                GCodeParser.Parse(file);

                return new GCodeFile(GCodeParser.Commands, GCodeParser.Warnings.ToList()) { FileName = "output.nc" };
            }
        }

        public static GCodeFile Empty
        {
            get { return new GCodeFile(new List<Command>(), Array.Empty<string>()); }
        }

        public void Save(string path)
        {
            File.WriteAllLines(path, GetGCode());
        }

        public List<string> GetGCode()
        {
            // G90 absolute, G91.1 incremental arc centers, G21 mm, G17 XY plane.
            List<string> GCode = new List<string>(Toolpath.Count + 5) { "G90", "G91.1", "G21", "G17" };

            NumberFormatInfo nfi = new NumberFormatInfo();
            nfi.NumberDecimalSeparator = ".";

            // Find the first feed rate and output it early, so no move runs before one is set.
            // Without this, standalone F values (e.g., "G01 F600" with no coords) are lost
            // during regeneration, causing GRBL errors for commands before any motion.
            double firstFeed = 0;
            foreach (Command c in Toolpath)
            {
                if (c is Motion m && m.Feed > 0)
                {
                    firstFeed = m.Feed;
                    break;
                }
            }
            if (firstFeed > 0)
            {
                GCode.Add(string.Format(nfi, "F{0:0.###}", firstFeed));
            }

            ParserState State = new ParserState();
            State.Feed = firstFeed;  // So the first motion does not repeat this F
            var xyz = "XYZ";

            foreach (Command c in Toolpath)
            {
                // Blocks the parser could not model as geometry are re-emitted exactly
                // as the file wrote them - see PassThrough.
                // A block can set the feed itself (G38.2 F50), so the next feed move writes its own.
                if (c is PassThrough)
                {
                    GCode.Add(((PassThrough)c).Line);
                    State.Feed = double.NaN;
                    continue;
                }

                // A rapid ignores the feed, so only a feed move writes one.
                if (c is Motion m && m is not Line { Rapid: true } && m.Feed != State.Feed)
                {
                    GCode.Add(string.Format(nfi, "F{0:0.###}", m.Feed));
                    State.Feed = m.Feed;
                }

                if (c is Line l)
                {
                    string code = l.Rapid ? "G0" : "G1";

                    for (int i = 0; i < 3; i++)
                    {
                        if (!l.PositionValid[i])
                        {
                            continue;
                        }
                        if (!l.StartValid || State.Position[i] != l.End[i])
                        {
                            code += string.Format(nfi, " {0}{1:0.###}", xyz[i], l.End[i]);
                        }
                    }

                    GCode.Add(code);
                    State.Position = l.End;
                    continue;
                }

                if (c is Arc a)
                {
                    if (State.Plane != a.Plane)
                    {
                        switch (a.Plane)
                        {
                            case ArcPlane.XY:
                                GCode.Add("G17");
                                break;
                            case ArcPlane.YZ:
                                GCode.Add("G19");
                                break;
                            case ArcPlane.ZX:
                                GCode.Add("G18");
                                break;
                        }
                        State.Plane = a.Plane;
                    }

                    string code = a.Direction == ArcDirection.CW ? "G2" : "G3";

                    // GRBL needs explicit endpoint coordinates for a full-circle arc, where
                    // start and end match in X and Y; without them it returns error:33 for a
                    // helical full circle.
                    code += string.Format(nfi, " X{0:0.###}", a.End.X);
                    code += string.Format(nfi, " Y{0:0.###}", a.End.Y);
                    if (State.Position.Z != a.End.Z)
                    {
                        code += string.Format(nfi, " Z{0:0.###}", a.End.Z);
                    }

                    Vector3 Center = new Vector3(a.U, a.V, 0).RollComponents((int)a.Plane) - State.Position;

                    if (Center.X != 0 && a.Plane != ArcPlane.YZ)
                    {
                        code += string.Format(nfi, " I{0:0.###}", Center.X);
                    }
                    if (Center.Y != 0 && a.Plane != ArcPlane.ZX)
                    {
                        code += string.Format(nfi, " J{0:0.###}", Center.Y);
                    }
                    if (Center.Z != 0 && a.Plane != ArcPlane.XY)
                    {
                        code += string.Format(nfi, " K{0:0.###}", Center.Z);
                    }

                    GCode.Add(code);
                    State.Position = a.End;
                    continue;
                }

                if (c is TCode)
                {
                    TCode t = (TCode)c;
                    string tcode = $"T{t.ToolNumber}";
                    if (!string.IsNullOrEmpty(t.Comment))
                    {
                        tcode += $" ({t.Comment})";
                    }
                    GCode.Add(tcode);
                    continue;
                }

                if (c is MCode)
                {
                    int code = ((MCode)c).Code;
                    if (!GCodeIncludeMEnd)
                    {
                        if (code == GCodeNumbers.MCodeProgramEnd || code == GCodeNumbers.MCodeProgramEndReset)
                        {
                            continue;
                        }
                    }
                    GCode.Add($"M{code}");
                    continue;
                }

                if (c is Spindle)
                {
                    if (!GCodeIncludeSpindle)
                    {
                        continue;
                    }

                    GCode.Add(string.Format(nfi, "S{0}", ((Spindle)c).Speed));
                    continue;
                }

                if (c is Dwell)
                {
                    if (!GCodeIncludeDwell)
                    {
                        continue;
                    }

                    GCode.Add(string.Format(nfi, "G4 P{0}", ((Dwell)c).Seconds));
                    continue;
                }
            }

            return GCode;
        }

        /// <summary>
        /// A copy fitted to the probed surface: feed moves follow the map's height under them,
        /// and rapids rise by the map's highest point, or stay as written when that point is
        /// below zero, so travel clears the highest copper by at least the height the file
        /// wrote. A rapid that
        /// has to rise from where the last move left the tool rises in Z alone before it moves
        /// in X or Y, so it cannot cross copper on the way up.
        /// </summary>
        public GCodeFile ApplyProbeGrid(ProbeGrid map)
        {
            ThrowUnlessEveryArcIsInTheXYPlane();

            double segmentLength = Math.Min(map.GridX, map.GridY);
            double travelLift = Math.Max(0, map.MaxHeight);

            List<Command> toolpath = new List<Command>();

            // Where the last move left the tool in the fitted file, or null where the file
            // does not say: a block the parser could not model, or a move with an axis missing.
            Vector3? toolAt = null;

            foreach (Command command in Toolpath)
            {
                if (command is not Motion motion)
                {
                    toolpath.Add(command);
                    if (command is PassThrough)
                    {
                        toolAt = null;
                    }
                    continue;
                }

                if (motion is Line line && line.Rapid)
                {
                    toolAt = AddTravel(toolpath, line, travelLift, toolAt);
                    continue;
                }

                if (motion is Line { FullyKnown: false } unknown)
                {
                    toolpath.Add(unknown);
                    toolAt = unknown.KnownEnd;
                    continue;
                }

                foreach (Motion segment in motion.Split(segmentLength))
                {
                    segment.Start.Z += map.InterpolateZ(segment.Start.X, segment.Start.Y);
                    segment.End.Z += map.InterpolateZ(segment.End.X, segment.End.Y);

                    toolpath.Add(segment);
                    toolAt = segment.End;
                }
            }

            return Derive(toolpath);
        }

        /// <summary>
        /// Adds a rapid raised by <paramref name="lift"/>, climbing first where it would
        /// otherwise rise on the diagonal from <paramref name="toolAt"/>.
        /// </summary>
        /// <returns>Where the rapid leaves the tool, or null where the file does not say.</returns>
        private static Vector3? AddTravel(List<Command> toolpath, Line rapid, double lift, Vector3? toolAt)
        {
            Line raised = (Line)rapid.Copy();
            raised.End.Z += lift;

            if (raised.KnownEnd is null || toolAt is not Vector3 from)
            {
                toolpath.Add(raised);
                return raised.KnownEnd;
            }

            raised.Start = from;
            raised.StartValid = true;

            if (raised.MovesAcrossTheBoard && raised.End.Z > from.Z)
            {
                Line climb = (Line)raised.Copy();
                climb.End = new Vector3(from.X, from.Y, raised.End.Z);
                toolpath.Add(climb);

                raised.Start = climb.End;
            }

            toolpath.Add(raised);
            return raised.End;
        }

        /// <summary>
        /// A copy with <paramref name="offset"/> added to the Z of every feed-move point below
        /// work zero, or this file when the offset is zero. Work zero is the copper surface in
        /// the file's frame, so a point below it is a cut; rapids, and feed moves above it such
        /// as a drill's G1 retract, keep their height.
        /// </summary>
        /// <param name="offset">Added to Z, so a negative offset cuts deeper.</param>
        public GCodeFile OffsetCutDepth(double offset)
        {
            if (offset == 0)
            {
                return this;
            }

            ThrowUnlessEveryArcIsInTheXYPlane();

            List<Command> toolpath = new List<Command>(Toolpath.Count);

            foreach (Command command in Toolpath)
            {
                if (command is not Motion motion || motion is Line { Rapid: true })
                {
                    toolpath.Add(command);
                    continue;
                }

                Motion cut = motion.Copy();
                bool startKnown = cut is not Line line || line.StartValid;
                bool endKnown = cut is not Line { ZKnown: false };

                if (startKnown && cut.Start.Z < 0)
                {
                    cut.Start.Z += offset;
                }
                if (endKnown && cut.End.Z < 0)
                {
                    cut.End.Z += offset;
                }

                toolpath.Add(cut);
            }

            return Derive(toolpath);
        }

        /// <summary>
        /// A copy with only the chosen phases and, within them, only the chosen sections (see
        /// PartClip). Null phases means every phase, and null sections the whole board.
        /// </summary>
        /// <returns>The copy, or null with the reason it cannot be made.</returns>
        public (GCodeFile? File, string? Refused) KeepPart(ChosenPhases? phases, BoardSections? sections)
        {
            if (sections != null)
            {
                ThrowUnlessEveryArcIsInTheXYPlane();
            }

            var (toolpath, refused) = PartClip.Keep(this, phases, sections);
            return toolpath == null ? (null, refused) : (Derive(toolpath), null);
        }

        /// <summary>
        /// The cells of <paramref name="division"/> that a cut in the chosen phases passes
        /// through; null phases means every phase.
        /// </summary>
        public IReadOnlySet<BoardCell> CellsCut(BoardDivision division, ChosenPhases? phases) =>
            Toolpath
                .Where((command, index) => command is Motion { FullyKnown: true, IsCut: true }
                    && ChosenPhases.Runs(phases, PhaseOf(index)))
                .Cast<Motion>()
                .SelectMany(division.Pieces)
                .Select(piece => piece.Cell)
                .ToHashSet();

        /// <summary>
        /// An arc in the XZ or YZ plane carries Z in its center, which neither a height map
        /// nor a depth adjustment can move with it, and Arc.RatiosWhereXIs and RatiosWhereYIs
        /// cannot find where it crosses a section line.
        /// </summary>
        public bool HasArcsOutsideXYPlane =>
            Toolpath.Any(command => command is Arc arc && arc.Plane != ArcPlane.XY);

        private void ThrowUnlessEveryArcIsInTheXYPlane()
        {
            if (HasArcsOutsideXYPlane)
            {
                throw new InvalidOperationException(Constants.ErrorArcsOutsideXYPlane);
            }
        }

        /// <summary>A file built from this one's commands, keeping its name, path and parse warnings.</summary>
        private GCodeFile Derive(List<Command> toolpath) =>
            new GCodeFile(toolpath, _parseWarnings) { FileName = FileName, FilePath = FilePath };

        public string GetInfo()
        {
            return $"File: {FileName}\n" +
                   $"Bounds: X[{Min.X:F3} to {Max.X:F3}] Y[{Min.Y:F3} to {Max.Y:F3}] Z[{Min.Z:F3} to {Max.Z:F3}]\n" +
                   $"Size: {Size.X:F3} x {Size.Y:F3} x {Size.Z:F3}\n" +
                   $"Travel Distance: {TravelDistance:F3} mm\n" +
                   $"Estimated Time: {TotalTime:hh\\:mm\\:ss}\n" +
                   $"Commands: {Toolpath.Count}";
        }
    }
}
