#nullable enable
using System;
using System.Collections.Generic;
using coppercli.Core.GCode.GCodeCommands;
using coppercli.Core.Util;

namespace coppercli.Core.GCode
{
    /// <summary>A place in the G-code a machine streams: the line the tool is on, and how far along that line's move.</summary>
    public readonly record struct JobPosition(int Line, double Ratio);

    /// <summary>
    /// How long each line of the G-code a machine streams takes: a feed move or an arc its
    /// length at its feed, a rapid as long as the axis that takes longest at its own top speed,
    /// a dwell its time. The sum is close for a machine that holds the feeds the file gives, as a
    /// Nomad 3 does (see the history entry eta-from-where-the-tool-is); one whose acceleration
    /// keeps it below them on short moves finishes later. The tool's place, not the last line
    /// sent, sets how much is left, because the lines sent run ahead of the tool by the moves
    /// GRBL holds in its buffers.
    /// </summary>
    public sealed class JobTimeline
    {
        /// <summary>The move each line makes, or null, to find the tool on.</summary>
        private readonly Motion?[] _moves;

        // The seconds of each kind of motion before each line, one entry past the last line.
        private readonly double[] _feedBefore;
        private readonly double[] _rapidBefore;
        private readonly double[] _dwellBefore;

        private JobTimeline(Motion?[] moves, double[] feed, double[] rapid, double[] dwell)
        {
            _moves = moves;
            _feedBefore = RunningTotal(feed);
            _rapidBefore = RunningTotal(rapid);
            _dwellBefore = RunningTotal(dwell);
        }

        /// <summary>
        /// Rapids take <paramref name="topSpeeds"/>, and no time without them. A move the file
        /// does not place, as after a tool change, is timed from where the last move left the
        /// tool, with the axes it does not name staying there. A block the parser keeps as
        /// written, such as a move in machine coordinates or a probe, takes no time, and leaves
        /// the moves after it untimed until one names every axis.
        /// </summary>
        /// <param name="lines">The G-code the machine streams.</param>
        /// <param name="topSpeeds">GRBL's top speed on each axis in mm/min, or null.</param>
        /// <exception cref="ParseException">The lines are not G-code coppercli reads.</exception>
        public static JobTimeline Of(IReadOnlyList<string> lines, Vector3? topSpeeds)
        {
            var moves = new Motion?[lines.Count];
            var feed = new double[lines.Count];
            var rapid = new double[lines.Count];
            var dwell = new double[lines.Count];
            Vector3? toolAt = null;

            foreach (Command command in GCodeFile.FromList(lines).Toolpath)
            {
                int line = command.LineNumber - 1;
                bool onALine = line >= 0 && line < lines.Count;

                switch (command)
                {
                    case Motion move:
                        var placed = Placed(move, toolAt);
                        if (onALine)
                        {
                            moves[line] = move;
                            if (placed is Line { Rapid: true })
                            {
                                rapid[line] += topSpeeds is Vector3 top ? RapidSeconds(placed.Delta, top) : 0;
                            }
                            else
                            {
                                feed[line] += placed?.FeedTime.TotalSeconds ?? 0;
                            }
                        }
                        toolAt = placed?.End ?? move.KnownEnd;
                        break;
                    case PassThrough:
                        toolAt = null;
                        break;
                    case Dwell pause when onALine:
                        dwell[line] += pause.Seconds;
                        break;
                }
            }

            return new JobTimeline(moves, feed, rapid, dwell);
        }

        /// <summary>
        /// <paramref name="move"/> with both ends known: as the file gives it, or from where the
        /// last move left the tool, the axes it does not name staying there. Null when neither
        /// says, as after a move in machine coordinates.
        /// </summary>
        private static Motion? Placed(Motion move, Vector3? toolAt)
        {
            if (move.FullyKnown)
            {
                return move;
            }

            if (move is not Line line || toolAt is not Vector3 start)
            {
                return null;
            }

            // The parser leaves an axis the move does not name where the last move put it.
            var placed = (Line)line.Copy();
            placed.Start = start;
            placed.StartValid = true;
            placed.PositionValid = new[] { true, true, true };
            return placed;
        }

        /// <summary>The seconds every line takes at the feeds the file gives and the machine's top speeds.</summary>
        public double TotalSeconds => SecondsBefore(new JobPosition(_moves.Length, 0));

        /// <summary>The share of <see cref="TotalSeconds"/> before <paramref name="at"/>, 0 for a job that takes none.</summary>
        public double FractionDone(JobPosition at) =>
            TotalSeconds > 0 ? SecondsBefore(at) / TotalSeconds : 0;

        /// <summary>
        /// The time the lines from <paramref name="at"/> on take, with feed moves at
        /// <paramref name="feedPercent"/> of their feed and rapids at <paramref name="rapidPercent"/>
        /// of the top speeds, as GRBL's overrides run them.
        /// </summary>
        public TimeSpan TimeLeft(JobPosition at, int feedPercent, int rapidPercent)
        {
            double feed = _feedBefore[^1] - Before(_feedBefore, at);
            double rapid = _rapidBefore[^1] - Before(_rapidBefore, at);
            double dwell = _dwellBefore[^1] - Before(_dwellBefore, at);
            return TimeSpan.FromSeconds(
                feed * Constants.OverrideDefaultPercent / Math.Max(1, feedPercent)
                + rapid * Constants.OverrideDefaultPercent / Math.Max(1, rapidPercent)
                + dwell);
        }

        /// <summary>
        /// The place the tool at <paramref name="tool"/>, in work coordinates, has reached: the
        /// first move of the <paramref name="linesSent"/> lines sent, at or past
        /// <paramref name="from"/>, whose path passes within ToolOnPathToleranceMm of it. When no
        /// move is near, as while the tool is away for a tool change, the place stays at
        /// <paramref name="from"/>, or moves up to the lines GRBL can still hold
        /// (GrblLinesHeldMax), all before which have run.
        /// </summary>
        /// <remarks>
        /// The search runs forward only: a tool retracting from a hole passes the points of the
        /// plunge before it, and a backward search would place it on the plunge.
        /// </remarks>
        public JobPosition Locate(Vector3 tool, JobPosition from, int linesSent)
        {
            int last = Math.Min(linesSent, _moves.Length);
            int firstUnrun = Math.Max(0, last - Constants.GrblLinesHeldMax);
            if (from.Line < firstUnrun)
            {
                from = new JobPosition(firstUnrun, 0);
            }

            for (int line = from.Line; line < last; line++)
            {
                if (_moves[line] is not Motion move)
                {
                    continue;
                }

                var (distance, ratio) = move.Nearest(tool);
                if (distance <= Constants.ToolOnPathToleranceMm && (line > from.Line || ratio >= from.Ratio))
                {
                    return new JobPosition(line, ratio);
                }
            }

            return from;
        }

        private double SecondsBefore(JobPosition at) =>
            Before(_feedBefore, at) + Before(_rapidBefore, at) + Before(_dwellBefore, at);

        private static double Before(double[] secondsBefore, JobPosition at)
        {
            int line = Math.Clamp(at.Line, 0, secondsBefore.Length - 1);
            double onTheLine = line + 1 < secondsBefore.Length ? secondsBefore[line + 1] - secondsBefore[line] : 0;
            return secondsBefore[line] + at.Ratio * onTheLine;
        }

        /// <summary>A rapid lasts as long as the axis that takes longest at its own top speed.</summary>
        private static double RapidSeconds(Vector3 delta, Vector3 topSpeeds) =>
            TimeSpan.FromMinutes(Math.Max(
                Math.Abs(delta.X) / topSpeeds.X,
                Math.Max(Math.Abs(delta.Y) / topSpeeds.Y, Math.Abs(delta.Z) / topSpeeds.Z))).TotalSeconds;

        private static double[] RunningTotal(double[] perLine)
        {
            var before = new double[perLine.Length + 1];
            for (int line = 0; line < perLine.Length; line++)
            {
                before[line + 1] = before[line] + perLine[line];
            }
            return before;
        }
    }
}
