#nullable enable
using System;
using System.Collections.Generic;
using coppercli.Core.GCode;
using coppercli.Core.GCode.GCodeCommands;
using coppercli.Core.Util;
using coppercli.Tests.Fakes;
using Xunit;

namespace coppercli.Tests
{
    // How long each line of the machine's G-code takes, and where on it the tool is.
    public class JobTimelineTests
    {
        private const int Precision = 4;
        private const int NoOverride = Constants.OverrideDefaultPercent;

        private static readonly Vector3 TopSpeeds = FakeGrbl.TopSpeeds;

        /// <summary>G-code as coppercli writes it for the machine, one move per line.</summary>
        private static readonly string[] Job =
        {
            "G90", "G21", "F600",
            "G0 X0 Y0 Z1",          // 3: the start is not known, so no time
            "G1 Z-1",               // 4: 2 mm at 600 mm/min, 0.2 s
            "G1 X10",               // 5: 10 mm, 1 s
            "G2 X10 Y0 I-5 J0",     // 6: a full circle of radius 5, 10 pi mm, pi s
            "G4 P2",                // 7: 2 s
            "G0 X0 Y0 Z1",          // 8: X's 10 mm at its top speed outlasts Z's 2 mm at its own
        };

        private const double PlungeSeconds = 0.2;
        private const double CutSeconds = 1;
        private const double CircleSeconds = Math.PI;
        private const double DwellSeconds = 2;
        private static readonly double RapidSeconds = 60 * 10 / TopSpeeds.X;
        private static readonly double FeedSeconds = PlungeSeconds + CutSeconds + CircleSeconds;
        private const int CutLine = 5;
        private const int CircleLine = 6;
        private const int DwellLine = 7;

        private static JobPosition StartOf(int line) => new(line, 0);

        /// <summary>
        /// Catches lines counted by number rather than by how long each takes: a feed move takes
        /// its length at its feed, an arc its length along the arc, a dwell its time, and a rapid
        /// as long as the axis that takes longest at its own top speed.
        /// </summary>
        [Fact]
        public void EachLine_TakesTheTimeItsMoveOrDwellTakes()
        {
            var timeline = JobTimeline.Of(Job, TopSpeeds);

            Assert.Equal(FeedSeconds + DwellSeconds + RapidSeconds, timeline.TotalSeconds, Precision);
            Assert.Equal(CircleSeconds + DwellSeconds + RapidSeconds,
                timeline.TimeLeft(StartOf(CircleLine), NoOverride, NoOverride).TotalSeconds, Precision);
            Assert.Equal(DwellSeconds + RapidSeconds,
                timeline.TimeLeft(StartOf(DwellLine), NoOverride, NoOverride).TotalSeconds, Precision);
        }

        /// <summary>
        /// Catches the moves after a tool change taking no time because the file does not place
        /// them: each runs from where the last move left the tool, the axes it does not name
        /// staying there, as pcb2gcode's retract after the M6 does.
        /// </summary>
        [Fact]
        public void TheMovesAfterAToolChange_RunFromWhereTheToolWas()
        {
            string[] job = { "G90", "F600", "G0 X0 Y0 Z1", "G0 X10 Y0 Z1", "M6", "G0 Z35", "G1 X20 Y0 Z35" };
            const int toolChange = 4;
            double retract = 60 * 34 / TopSpeeds.Z;
            double cut = 60 * 10 / 600.0;

            var timeline = JobTimeline.Of(job, TopSpeeds);

            Assert.Equal(retract + cut, timeline.TimeLeft(StartOf(toolChange), NoOverride, NoOverride).TotalSeconds, Precision);
        }

        /// <summary>Catches rapids given a speed GRBL never reported.</summary>
        [Fact]
        public void ARapid_TakesNoTime_WhenGrblHasNotListedItsTopSpeeds()
        {
            Assert.Equal(FeedSeconds + DwellSeconds, JobTimeline.Of(Job, null).TotalSeconds, Precision);
        }

        /// <summary>
        /// Catches the time left ignoring GRBL's overrides: at half the feed the feed moves take
        /// twice as long, and at a quarter of the top speeds the rapids four times as long.
        /// </summary>
        [Fact]
        public void TheTimeLeft_FollowsTheFeedAndRapidOverrides()
        {
            var timeline = JobTimeline.Of(Job, TopSpeeds);

            var left = timeline.TimeLeft(StartOf(0), feedPercent: 50, rapidPercent: 25);

            Assert.Equal(2 * FeedSeconds + DwellSeconds + 4 * RapidSeconds, left.TotalSeconds, Precision);
        }

        /// <summary>Catches progress counted in lines, or a line's time counted all or nothing.</summary>
        [Fact]
        public void TheShareDone_CountsTheTimeBefore_AndThePartOfTheLineTheToolIsOn()
        {
            var timeline = JobTimeline.Of(Job, TopSpeeds);

            double halfwayAlongTheCut = PlungeSeconds + CutSeconds / 2;

            Assert.Equal(halfwayAlongTheCut / timeline.TotalSeconds,
                timeline.FractionDone(new JobPosition(CutLine, 0.5)), Precision);
            Assert.Equal(1, timeline.FractionDone(StartOf(Job.Length)), Precision);
        }

        /// <summary>Catches the tool placed on the line last sent rather than the move it is making.</summary>
        [Fact]
        public void TheTool_IsFoundOnTheMoveItIsMaking_AndHowFarAlong()
        {
            var timeline = JobTimeline.Of(Job, TopSpeeds);

            var at = timeline.Locate(new Vector3(5, 0, -1), StartOf(0), linesSent: Job.Length);

            Assert.Equal(new JobPosition(CutLine, 0.5), at);
        }

        /// <summary>Catches the tool placed on a line GRBL has not been sent, and so cannot be running.</summary>
        [Fact]
        public void TheTool_IsNotFoundOnALineNotYetSent()
        {
            var timeline = JobTimeline.Of(Job, TopSpeeds);
            var from = StartOf(0);

            Assert.Equal(from, timeline.Locate(new Vector3(5, 0, -1), from, linesSent: CutLine));
        }

        /// <summary>
        /// Catches the place moved while the tool is away from the path, as for a tool change,
        /// or back to a point of the plunge it already made: it stays where it was.
        /// </summary>
        [Fact]
        public void TheTool_AwayFromThePath_LeavesThePlaceWhereItWas()
        {
            var timeline = JobTimeline.Of(Job, TopSpeeds);
            var from = new JobPosition(CutLine, 0.5);

            Assert.Equal(from, timeline.Locate(new Vector3(-100, 50, 30), from, linesSent: Job.Length));
            Assert.Equal(from, timeline.Locate(new Vector3(0, 0, 0), from, linesSent: Job.Length));
        }

        /// <summary>
        /// Catches a retracting tool placed on the plunge it came down, which passes the same
        /// points: the place only moves forward.
        /// </summary>
        [Fact]
        public void ATool_ComingBackUpAHole_IsOnTheRetract()
        {
            string[] drill = { "G90", "G0 X0 Y0 Z1", "G0 X5 Y5 Z1", "F100", "G1 Z-2", "G1 Z1" };
            var timeline = JobTimeline.Of(drill, TopSpeeds);
            const int plunge = 4;
            const int retract = 5;

            var bottom = timeline.Locate(new Vector3(5, 5, -2), StartOf(0), drill.Length);
            var comingUp = timeline.Locate(new Vector3(5, 5, -0.5), bottom, drill.Length);

            Assert.Equal(plunge, bottom.Line);
            Assert.Equal(new JobPosition(retract, 0.5), comingUp);
        }

        /// <summary>
        /// Catches the tool placed at the start of a helical full circle after it has gone round
        /// to the end, which lies over the same point: the height tells them apart.
        /// </summary>
        [Fact]
        public void AHelicalFullCircle_TellsItsStartFromItsEnd_ByHeight()
        {
            string[] helix = { "G90", "G0 X0 Y0 Z1", "G0 X10 Y0 Z0", "F100", "G2 X10 Y0 Z-1 I-5 J0" };
            var timeline = JobTimeline.Of(helix, TopSpeeds);
            const int circle = 4;

            Assert.Equal(new JobPosition(circle, 1), timeline.Locate(new Vector3(10, 0, -1), StartOf(circle), helix.Length));
            Assert.Equal(new JobPosition(circle, 0.5), timeline.Locate(new Vector3(0, 0, -0.5), StartOf(circle), helix.Length));
        }

        /// <summary>Catches a point past a move's end placed beyond it, or nowhere.</summary>
        [Fact]
        public void APointPastAMove_IsNearestItsEnd()
        {
            var file = GCodeFile.FromList(new[] { "G90", "G0 X0 Y0 Z0", "G1 X10 F100", "G3 X15 Y5 I0 J5" });
            var line = (Motion)file.Toolpath[0 + LineAfterTheRapid];
            var quarterTurn = (Motion)file.Toolpath[1 + LineAfterTheRapid];

            Assert.Equal((2.0, 1.0), line.Nearest(new Vector3(12, 0, 0)));
            var (distance, ratio) = quarterTurn.Nearest(new Vector3(15, 7, 0));
            Assert.Equal(1, ratio);
            Assert.Equal(2, distance, Precision);
        }

        /// <summary>The toolpath index of the first move after a file's opening rapid.</summary>
        private const int LineAfterTheRapid = 1;

        /// <summary>
        /// Catches a place stuck behind the lines GRBL can still hold, as when the tool's
        /// position never matches the path: every line before those has run.
        /// </summary>
        [Fact]
        public void APlaceBehindTheLinesGrblCanHold_MovesUpToThem()
        {
            var job = new List<string> { "G90", "G0 X0 Y0 Z1" };
            for (int cut = 0; cut < 2 * Constants.GrblLinesHeldMax; cut++)
            {
                job.Add(GCodeFormat.Inv($"G1 X{cut + 1} F600"));
            }
            var timeline = JobTimeline.Of(job, TopSpeeds);

            var at = timeline.Locate(new Vector3(-100, -100, 50), StartOf(0), job.Count);

            Assert.Equal(StartOf(job.Count - Constants.GrblLinesHeldMax), at);
        }

        /// <summary>
        /// Catches a move whose start is not known, such as the first after a tool change, never
        /// being reached: the tool is on it at its end.
        /// </summary>
        [Fact]
        public void AMoveWithAnUnknownStart_IsReachedAtItsEnd()
        {
            var timeline = JobTimeline.Of(Job, TopSpeeds);
            const int firstMove = 3;

            Assert.Equal(new JobPosition(firstMove, 1), timeline.Locate(new Vector3(0, 0, 1), StartOf(0), Job.Length));
        }
    }
}
