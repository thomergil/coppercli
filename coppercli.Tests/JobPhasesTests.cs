#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using coppercli;
using coppercli.Core.Controllers;
using coppercli.Core.GCode;
using coppercli.Core.GCode.GCodeCommands;
using coppercli.Core.Util;
using coppercli.Helpers;
using Xunit;

namespace coppercli.Tests
{
    internal static class PhaseTestSupport
    {
        public const double TraceZ = -0.1;
        public const double SecondPhaseDrillZ = -1.8;
        public const double ThirdPhaseDrillZ = -1;

        /// <summary>
        /// Two phases: a trace outline at Z-0.1, then a tool change to T2 and two drills, at
        /// (5,5) and (15,15), each phase with its own spindle start and dwell.
        /// </summary>
        public static readonly string[] TwoPhaseBoard =
        {
            "G21", "G90", "G0 Z10", "G0 X0 Y0", "S10000", "M3", "G4 P1", "G0 Z1",
            "G0 X2 Y2", "G1 Z-0.1 F200", "G1 X18 Y2 F600", "G1 X18 Y18", "G0 Z1",
            "G0 Z20", "T2", "M5", "M6", "S12000", "M3", "G4 P2",
            "G0 X5 Y5", "G0 Z1", "G1 Z-1.8 F100", "G1 Z1",
            "G0 X15 Y15", "G1 Z-1.8 F100", "G1 Z1",
            "G0 Z20", "M5", "M2"
        };

        /// <summary>The trace: 1.1 mm down at F200, then 32 mm at F600.</summary>
        public static readonly TimeSpan FirstPhaseTime = TimeSpan.FromMinutes(1.1 / 200 + 32.0 / 600);

        /// <summary>Two drills, each 2.8 mm down and 2.8 mm up at F100.</summary>
        public static readonly TimeSpan SecondPhaseTime = TimeSpan.FromMinutes(4 * 2.8 / 100);

        /// <summary>
        /// Three phases: the trace, a drill at (5,5) to Z-1.8 with T2, and a drill at (15,15) to
        /// Z-1 with T3, whose spindle runs counterclockwise.
        /// </summary>
        public static readonly string[] ThreePhaseBoard =
        {
            "G21", "G90", "G0 Z10", "G0 X0 Y0", "S10000", "M3", "G0 Z1",
            "G0 X2 Y2", "G1 Z-0.1 F200", "G1 X18 Y2 F600", "G1 X18 Y18", "G0 Z1",
            "G0 Z20", "T2", "M5", "M6", "S12000", "M3",
            "G0 X5 Y5", "G0 Z1", "G1 Z-1.8 F100", "G1 Z1",
            "G0 Z20", "T3", "M5", "M6", "S9000", "M4",
            "G0 X15 Y15", "G0 Z1", "G1 Z-1 F100", "G1 Z1",
            "G0 Z20", "M5", "M2"
        };

        /// <summary><paramref name="lines"/> with <paramref name="added"/> put right after the first <paramref name="after"/>.</summary>
        public static string[] WithLineAfter(string[] lines, string after, string added)
        {
            var list = lines.ToList();
            list.Insert(list.IndexOf(after) + 1, added);
            return list.ToArray();
        }

        public static ChosenPhases Choose(GCodeFile file, params int[] numbers)
        {
            var (phases, refused) = ChosenPhases.Choose(file, numbers);
            Assert.Null(refused);
            return Assert.IsType<ChosenPhases>(phases);
        }

        public static GCodeFile KeepPhases(GCodeFile file, params int[] numbers) =>
            SectionTestSupport.Keep(file, null, Choose(file, numbers));

        public static int Count(GCodeFile file, int mCode) => file.Toolpath.Count(c => c is MCode m && m.Code == mCode);

        /// <summary>The lowest Z of each cut, to tell the phases' cuts apart by depth.</summary>
        public static HashSet<double> CutDepths(GCodeFile file) =>
            SectionTestSupport.Cuts(file).Select(c => Math.Min(c.Start.Z, c.End.Z)).ToHashSet();

        /// <summary>The last command before <paramref name="index"/> that moves the machine: a move, a block, or a tool change.</summary>
        public static Command LastMachineCommandBefore(List<Command> toolpath, int index) =>
            toolpath.Take(index).Last(c => c is Motion or PassThrough or MCode { IsToolChange: true });
    }

    // How a file divides into phases, the choice of phases, and the toolpath KeepPart builds
    // from it, alone and with sections.
    public class JobPhasesCoreTests
    {
        private const double FirstDwellSeconds = 1;
        private const double SecondDwellSeconds = 2;
        private const int FirstSpindleSpeed = 10000;
        private const int SecondSpindleSpeed = 12000;
        private const int TimeDecimals = 6;

        /// <summary>
        /// Catches the phases or their times being read wrong: each phase's time is how long its
        /// own feed moves take.
        /// </summary>
        [Fact]
        public void AFileWithATwoToolJob_HasTwoPhases_WithTheirTimes()
        {
            var file = SectionTestSupport.Parse(PhaseTestSupport.TwoPhaseBoard);

            Assert.Equal(new[] { 1, 2 }, file.Phases.Select(p => p.Number));
            Assert.Equal(PhaseTestSupport.FirstPhaseTime.TotalSeconds, file.Phases[0].Time.TotalSeconds, TimeDecimals);
            Assert.Equal(PhaseTestSupport.SecondPhaseTime.TotalSeconds, file.Phases[1].Time.TotalSeconds, TimeDecimals);
            Assert.Equal((file.Phases[0].Time + file.Phases[1].Time).TotalSeconds, file.TotalTime.TotalSeconds, TimeDecimals);
            Assert.True(file.OffersAChoiceOfPhases);
        }

        public static IEnumerable<object[]> ToolChangesWithNoCutOnOneSide()
        {
            yield return new object[] { new[] { "G21", "G90", "T1", "M6", "S1000", "M3", "G0 X0 Y0 Z5", "G1 Z-0.1 F100", "G1 X10 F300", "G0 Z5", "M5", "M30" } };
            yield return new object[] { new[] { "G21", "G90", "G0 X0 Y0 Z5", "G1 Z-0.1 F100", "G1 X10 F300", "G0 Z5", "T2", "M6", "M2" } };
        }

        /// <summary>
        /// Catches a tool change written before the first cut, as Fusion writes "T1 M6", making
        /// a phase with no work, and one after the last cut doing the same.
        /// </summary>
        [Theory]
        [MemberData(nameof(ToolChangesWithNoCutOnOneSide))]
        public void AToolChangeWithNoCutOnOneSide_StartsNoPhase(string[] lines)
        {
            var file = SectionTestSupport.Parse(lines);

            var only = Assert.Single(file.Phases);
            Assert.True(only.Time > TimeSpan.Zero);
            Assert.False(file.OffersAChoiceOfPhases);
            Assert.All(Enumerable.Range(0, file.Toolpath.Count), i => Assert.Equal(1, file.PhaseOf(i)));
        }

        /// <summary>
        /// Catches two tool changes between the same two cuts making a phase with no work: the
        /// next phase starts at the last of them.
        /// </summary>
        [Fact]
        public void TwoToolChangesBetweenCuts_StartOnePhase_AtTheLast()
        {
            var file = SectionTestSupport.Parse(
                "G21", "G90", "G0 X0 Y0 Z5", "G1 Z-0.1 F100", "G1 X10 F300", "G0 Z5",
                "M6", "M6", "G1 Z-0.1 F100", "G0 Z5", "M2");

            var changes = Enumerable.Range(0, file.Toolpath.Count)
                .Where(i => file.Toolpath[i] is MCode { IsToolChange: true }).ToList();

            Assert.Equal(2, file.Phases.Count);
            Assert.Equal(1, file.PhaseOf(changes[0]));
            Assert.Equal(2, file.PhaseOf(changes[1]));
        }

        /// <summary>
        /// Catches a phase whose only cut starts where the file has not said, after a move in
        /// machine coordinates, being folded into the phase before it.
        /// </summary>
        [Fact]
        public void APhaseWhoseCutHasAnUnknownStart_IsStillAPhase()
        {
            var file = SectionTestSupport.Parse(
                "G21", "G90", "G0 X0 Y0 Z5", "G1 Z-0.1 F100", "G1 X10 F300", "G0 Z5",
                "M6", "G53 G0 Z-1", "G0 X5 Y5", "G1 Z-1 F100", "G0 Z5", "M2");

            Assert.Equal(2, file.Phases.Count);
        }

        /// <summary>Catches a choice of no phases, or of a phase the file does not have, being taken.</summary>
        [Fact]
        public void ChoosingNoPhases_OrOneNotInTheFile_IsRefused_AndChoosingAll_IsTheWholeJob()
        {
            var file = SectionTestSupport.Parse(PhaseTestSupport.TwoPhaseBoard);

            Assert.Equal((null, Constants.ErrorNoPhaseChosen), ChosenPhases.Choose(file, Array.Empty<int>()));
            Assert.Equal((null, Constants.ErrorPhaseNotInFile), ChosenPhases.Choose(file, new[] { 0 }));
            Assert.Equal((null, Constants.ErrorPhaseNotInFile), ChosenPhases.Choose(file, new[] { 2, 3 }));
            Assert.Equal((null, null), ChosenPhases.Choose(file, new[] { 2, 1 }));
            Assert.Equal(new[] { 2 }, PhaseTestSupport.Choose(file, 2).Numbers);
            Assert.True(ChosenPhases.Runs(null, 1));
            Assert.False(ChosenPhases.Runs(PhaseTestSupport.Choose(file, 2), 1));
        }

        /// <summary>
        /// Catches a run of the second phase alone moving the tool, starting the spindle or
        /// waiting before its tool change, or losing a cut of its own. The output starts at the
        /// tool change, then rises to the machine's safe height before it moves across.
        /// </summary>
        [Fact]
        public void SkippingTheFirstPhase_StartsTheRunAtTheToolChange()
        {
            var source = SectionTestSupport.Parse(PhaseTestSupport.TwoPhaseBoard);

            var output = PhaseTestSupport.KeepPhases(source, 2);

            var toolpath = output.Toolpath.ToList();
            int change = toolpath.FindIndex(c => c is MCode { IsToolChange: true });
            int firstMove = toolpath.FindIndex(c => c is Motion);
            Assert.True(change >= 0, "the tool change was dropped");
            Assert.True(firstMove > change, "the tool moved before the tool change");
            Assert.True(SectionTestSupport.IsTheClipsRetract(PhaseTestSupport.LastMachineCommandBefore(toolpath, firstMove)),
                "the tool moved across after the tool change without rising to the machine's safe height");

            Assert.Equal(1, PhaseTestSupport.Count(output, GCodeNumbers.MCodeToolChange));
            Assert.Equal(1, PhaseTestSupport.Count(output, GCodeNumbers.MCodeSpindleClockwise));
            Assert.True(toolpath.FindIndex(c => c is MCode { StartsTheSpindle: true }) > change,
                "the first phase's spindle start was kept");
            Assert.Equal(new[] { SecondDwellSeconds }, output.Toolpath.OfType<Dwell>().Select(d => d.Seconds));
            Assert.Equal(new[] { FirstSpindleSpeed, SecondSpindleSpeed },
                output.Toolpath.OfType<Spindle>().Select(s => (int)s.Speed));
            Assert.Contains(output.Toolpath, c => c is TCode);
            Assert.Equal(2, PhaseTestSupport.Count(output, GCodeNumbers.MCodeSpindleStop));
            Assert.Equal(1, PhaseTestSupport.Count(output, GCodeNumbers.MCodeProgramEnd));

            var drills = SectionTestSupport.Cuts(source).Where(c => c.End.Z == PhaseTestSupport.SecondPhaseDrillZ);
            Assert.Equal(drills.Select(d => (d.Start, d.End)), SectionTestSupport.Cuts(output).Select(c => (c.Start, c.End)));
        }

        /// <summary>
        /// Catches a run of the first phase alone changing the tool, starting the spindle or
        /// waiting for the second, cutting any of its drills, or ending in the copper.
        /// </summary>
        [Fact]
        public void SkippingTheLastPhase_DropsItsToolChangeAndWork_AndKeepsTheStops()
        {
            var source = SectionTestSupport.Parse(PhaseTestSupport.TwoPhaseBoard);

            var output = PhaseTestSupport.KeepPhases(source, 1);

            Assert.Equal(0, PhaseTestSupport.Count(output, GCodeNumbers.MCodeToolChange));
            Assert.Equal(1, PhaseTestSupport.Count(output, GCodeNumbers.MCodeSpindleClockwise));
            Assert.Equal(new[] { FirstDwellSeconds }, output.Toolpath.OfType<Dwell>().Select(d => d.Seconds));
            Assert.Equal(2, PhaseTestSupport.Count(output, GCodeNumbers.MCodeSpindleStop));
            Assert.Equal(1, PhaseTestSupport.Count(output, GCodeNumbers.MCodeProgramEnd));
            Assert.Equal(new[] { PhaseTestSupport.TraceZ }.ToHashSet(), PhaseTestSupport.CutDepths(output));
            Assert.True(output.Toolpath.OfType<Motion>().Last().End.Z >= 0, "the run ends with the tool in the copper");
        }

        /// <summary>
        /// Catches the middle phase's work, tool change or spindle start surviving a run of the
        /// other two, or the last phase's counterclockwise start surviving a run of the first two.
        /// </summary>
        [Fact]
        public void SkippingOneOfThreePhases_LeavesOutOnlyItsWork()
        {
            var source = SectionTestSupport.Parse(PhaseTestSupport.ThreePhaseBoard);

            var firstAndLast = PhaseTestSupport.KeepPhases(source, 1, 3);
            var firstTwo = PhaseTestSupport.KeepPhases(source, 1, 2);

            Assert.Equal(3, source.Phases.Count);
            Assert.Equal(1, PhaseTestSupport.Count(firstAndLast, GCodeNumbers.MCodeToolChange));
            Assert.Equal(1, PhaseTestSupport.Count(firstAndLast, GCodeNumbers.MCodeSpindleClockwise));
            Assert.Equal(1, PhaseTestSupport.Count(firstAndLast, GCodeNumbers.MCodeSpindleCounterclockwise));
            Assert.Equal(new[] { PhaseTestSupport.TraceZ, PhaseTestSupport.ThirdPhaseDrillZ }.ToHashSet(),
                PhaseTestSupport.CutDepths(firstAndLast));

            Assert.Equal(0, PhaseTestSupport.Count(firstTwo, GCodeNumbers.MCodeSpindleCounterclockwise));
            Assert.Equal(new[] { PhaseTestSupport.TraceZ, PhaseTestSupport.SecondPhaseDrillZ }.ToHashSet(),
                PhaseTestSupport.CutDepths(firstTwo));
        }

        /// <summary>
        /// Catches a pause right after a tool change, as pcb2gcode writes one, being kept for a
        /// phase that is skipped, or dropped for one that runs.
        /// </summary>
        [Theory]
        [InlineData(GCodeNumbers.MCodeProgramStop)]
        [InlineData(GCodeNumbers.MCodeProgramOptionalStop)]
        public void APauseAfterAToolChange_GoesWithItsPhase(int pause)
        {
            var source = SectionTestSupport.Parse(
                PhaseTestSupport.WithLineAfter(PhaseTestSupport.TwoPhaseBoard, "M6", FormattableString.Invariant($"M{pause}")));

            Assert.Equal(0, PhaseTestSupport.Count(PhaseTestSupport.KeepPhases(source, 1), pause));
            Assert.Equal(1, PhaseTestSupport.Count(PhaseTestSupport.KeepPhases(source, 2), pause));
        }

        /// <summary>
        /// Catches a retract written between a tool change and the pause right after it: the run
        /// skips that pause only when it comes straight after the change, so the operator would
        /// be stopped a second time with words that no longer hold.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void APauseRightAfterAToolChange_StaysRightAfterIt(bool bySections)
        {
            var source = SectionTestSupport.Parse(PhaseTestSupport.WithLineAfter(PhaseTestSupport.TwoPhaseBoard, "M6", "M0"));

            var output = bySections
                ? SectionTestSupport.Keep(source, SectionTestSupport.Choose(source, 2, 2, (0, 0)))
                : PhaseTestSupport.KeepPhases(source, 2);

            var toolpath = output.Toolpath.ToList();
            int change = toolpath.FindIndex(c => c is MCode { IsToolChange: true });
            Assert.True(change >= 0, "the tool change was dropped");
            Assert.True(toolpath[change + 1] is MCode { IsPause: true }, "the pause no longer comes right after the tool change");
            var lines = output.GetGCode();
            Assert.Equal(PauseLine, lines[lines.IndexOf(ToolChangeLine) + 1]);
        }

        private const string ToolChangeLine = "M6";
        private const string PauseLine = "M0";

        /// <summary>
        /// Catches a probe or offset block in a skipped phase being dropped: a later phase may
        /// cut at the work zero it sets. In a phase that runs, it is kept.
        /// </summary>
        [Theory]
        [InlineData("G92 Z0")]
        [InlineData("G10 L20 P1 Z0")]
        [InlineData("G38.2 Z-5 F50")]
        [InlineData("G43.1 Z1")]
        public void AnOffsetBlockInASkippedPhase_IsRefused_AndInAPhaseThatRuns_IsKept(string block)
        {
            var source = SectionTestSupport.Parse(PhaseTestSupport.WithLineAfter(PhaseTestSupport.TwoPhaseBoard, "G0 Z1", block));

            Assert.Equal((null, Constants.ErrorPhaseSkipsABlock), source.KeepPart(PhaseTestSupport.Choose(source, 2), null));
            var firstOnly = PhaseTestSupport.KeepPhases(source, 1);
            Assert.Contains(firstOnly.Toolpath, c => c is PassThrough { Line: var line } && line == block);
        }

        /// <summary>
        /// Catches an offset block in the last phase stopping that phase from being skipped: no
        /// phase after it runs, so none can depend on it.
        /// </summary>
        [Fact]
        public void AnOffsetBlockInTheLastPhase_DoesNotStopItBeingSkipped()
        {
            var source = SectionTestSupport.Parse(PhaseTestSupport.WithLineAfter(PhaseTestSupport.TwoPhaseBoard, "G0 X5 Y5", "G92 Z0"));

            var firstOnly = PhaseTestSupport.KeepPhases(source, 1);

            Assert.DoesNotContain(firstOnly.Toolpath, c => c is PassThrough && !SectionTestSupport.IsTheClipsRetract(c));
        }

        /// <summary>
        /// Catches a skipped arc, such as a milled hole of the phase left out, reaching the output.
        /// </summary>
        [Fact]
        public void ASkippedPhasesArcs_AreLeftOut()
        {
            var source = SectionTestSupport.Parse(PhaseTestSupport.WithLineAfter(PhaseTestSupport.TwoPhaseBoard, "G1 X18 Y18", "G2 X2 Y18 I-8 J0"));

            Assert.DoesNotContain(PhaseTestSupport.KeepPhases(source, 2).Toolpath, c => c is Arc);
            Assert.Contains(PhaseTestSupport.KeepPhases(source, 1).Toolpath, c => c is Arc);
        }

        private const string MachineCoordinateRetract = "G53 G0 Z-2";

        /// <summary>
        /// Catches a move in machine coordinates, which pcb2gcode can write before a tool change,
        /// stopping the phase it is in from being skipped: it sets no offset, so it is left out
        /// like any other move.
        /// </summary>
        [Fact]
        public void AMoveInMachineCoordinatesInASkippedPhase_IsLeftOut()
        {
            var source = SectionTestSupport.Parse(PhaseTestSupport.WithLineAfter(PhaseTestSupport.TwoPhaseBoard, "G0 Z20", MachineCoordinateRetract));

            var output = PhaseTestSupport.KeepPhases(source, 2);

            Assert.DoesNotContain(output.Toolpath, c => c is PassThrough { Line: MachineCoordinateRetract });
            Assert.Equal(1, PhaseTestSupport.Count(output, GCodeNumbers.MCodeToolChange));
        }

        /// <summary>
        /// The spindle starts once, before the first phase, and the second phase cuts with it
        /// still running.
        /// </summary>
        private static readonly string[] SpindleStartsOnce =
        {
            "G21", "G90", "G0 Z10", "G0 X0 Y0", "S10000", "M3", "G0 Z1",
            "G0 X2 Y2", "G1 Z-0.1 F200", "G1 X18 Y2 F600", "G0 Z20", "M6",
            "G0 X5 Y5", "G0 Z1", "G1 Z-1.8 F100", "G1 Z1", "G0 Z20", "M5", "M2"
        };

        /// <summary>
        /// Catches a skipped phase's spindle start being dropped when a later phase relies on it:
        /// that phase would cut with the spindle stopped.
        /// </summary>
        [Fact]
        public void ASpindleStartALaterPhaseReliesOn_CannotBeSkipped()
        {
            var source = SectionTestSupport.Parse(SpindleStartsOnce);

            Assert.Equal((null, Constants.ErrorPhaseSkipsTheSpindleStart), source.KeepPart(PhaseTestSupport.Choose(source, 2), null));
            Assert.Equal(1, PhaseTestSupport.Count(PhaseTestSupport.KeepPhases(source, 1), GCodeNumbers.MCodeSpindleClockwise));
        }

        /// <summary>
        /// The spindle starts in the first phase and again in the second, and the third cuts with
        /// it still running; nothing stops it between the phases.
        /// </summary>
        private static readonly string[] SpindleRunsThroughThreePhases =
        {
            "G21", "G90", "G0 Z10", "G0 X0 Y0", "S10000", "M3", "G0 Z1",
            "G0 X2 Y2", "G1 Z-0.1 F200", "G1 X18 Y2 F600", "G0 Z20", "M6", "M3",
            "G0 X5 Y5", "G0 Z1", "G1 Z-1.8 F100", "G1 Z1", "G0 Z20", "M6",
            "G0 X15 Y15", "G0 Z1", "G1 Z-1 F100", "G1 Z1", "G0 Z20", "M5", "M2"
        };

        /// <summary>
        /// Catches a skipped spindle start refusing the choice when the spindle is still on from
        /// a phase that runs.
        /// </summary>
        [Fact]
        public void ASkippedSpindleStart_IsNoReasonToRefuse_WhenTheSpindleIsStillOn()
        {
            var source = SectionTestSupport.Parse(SpindleRunsThroughThreePhases);

            var firstAndLast = PhaseTestSupport.KeepPhases(source, 1, 3);

            Assert.Equal(new[] { PhaseTestSupport.TraceZ, PhaseTestSupport.ThirdPhaseDrillZ }.ToHashSet(),
                PhaseTestSupport.CutDepths(firstAndLast));
        }

        /// <summary>
        /// Catches a choice that keeps nothing to cut: a run would change the tool and start the
        /// spindle, then stop.
        /// </summary>
        [Fact]
        public void AChoiceWithNoCutInIt_IsRefused()
        {
            var source = SectionTestSupport.Parse(PhaseTestSupport.TwoPhaseBoard);
            var traceOnly = SectionTestSupport.Choose(source, 2, 2, (1, 0));

            Assert.Equal((null, Constants.ErrorNothingToCut), source.KeepPart(PhaseTestSupport.Choose(source, 2), traceOnly));
        }

        /// <summary>
        /// Catches a refusal for a choice of phases alone naming sections the operator never chose.
        /// </summary>
        [Fact]
        public void AChoiceOfPhasesAlone_IsRefusedInTheWordsOfPhases()
        {
            var source = SectionTestSupport.Parse(
                "G21", "G90", "G0 X0 Y0 Z0", "G1 Z-0.1 F100", "G1 X10 F300", "G0 Z0",
                "M6", "G0 X20 Y0", "G1 Z-0.1 F100", "G1 X30 F300", "G0 Z0", "M2");

            Assert.Equal((null, Constants.ErrorPhaseNoTravelHeight), source.KeepPart(PhaseTestSupport.Choose(source, 2), null));
        }

        /// <summary>
        /// Catches a refusal caused by a skipped phase naming the sections, because sections are
        /// chosen too.
        /// </summary>
        [Fact]
        public void ARefusalASkippedPhaseCaused_NamesThePhases_WhenSectionsAreChosenToo()
        {
            var source = SectionTestSupport.Parse(
                "G21", "G90", "G0 X0 Y0 Z0", "G1 Z-0.1 F100", "G1 X10 F300", "G0 Z0",
                "M6", "G0 X20 Y0", "G1 Z-0.1 F100", "G1 X30 Y10 F300", "G0 Z0", "M2");
            var rightHalf = SectionTestSupport.Choose(source, 2, 1, (1, 0));

            Assert.Equal((null, Constants.ErrorPhaseNoTravelHeight), source.KeepPart(PhaseTestSupport.Choose(source, 2), rightHalf));
        }

        /// <summary>
        /// Catches a refusal a section caused naming the phases, because a phase was skipped
        /// earlier: the second phase probes inside its trace where the right half is left out.
        /// </summary>
        [Fact]
        public void ARefusalASectionCaused_NamesTheSections_AfterASkippedPhase()
        {
            var source = SectionTestSupport.Parse(
                "G21", "G90", "G0 Z10", "G0 X0 Y0", "G0 Z1",
                "G0 X2 Y10", "G1 Z-0.1 F200", "G1 X18 Y10 F600", "G0 Z1", "G0 Z20", "M6",
                "G0 X2 Y2", "G0 Z1", "G1 Z-0.1 F200", "G1 X18 Y2 F600", "G38.2 Z-10 F50", "G1 X18 Y18", "G0 Z1",
                "G0 Z10", "M2");
            var leftHalf = SectionTestSupport.Choose(source, 2, 1, (0, 0));

            Assert.Equal((null, Constants.ErrorSectionsBlockInLeftOutCut), source.KeepPart(PhaseTestSupport.Choose(source, 2), leftHalf));
        }

        /// <summary>
        /// Catches the output planning its first move after a tool change from where the tool
        /// was before it: the change moves the tool, so it rises to the machine's safe height
        /// before it moves across, whether or not the output was following the file.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AfterAToolChange_TheToolRisesToTheMachinesSafeHeight_BeforeMovingAcross(bool followingTheFile)
        {
            var source = SectionTestSupport.Parse(PhaseTestSupport.TwoPhaseBoard);
            var sections = followingTheFile
                ? SectionTestSupport.Choose(source, 2, 2, (0, 0), (1, 1))
                : SectionTestSupport.Choose(source, 2, 2, (0, 0));

            var toolpath = SectionTestSupport.Keep(source, sections).Toolpath.ToList();

            int change = toolpath.FindIndex(c => c is MCode { IsToolChange: true });
            int firstMove = toolpath.FindIndex(change, c => c is Motion);
            Assert.True(change >= 0 && firstMove > change, "the second phase is missing");
            Assert.True(SectionTestSupport.IsTheClipsRetract(PhaseTestSupport.LastMachineCommandBefore(toolpath, firstMove)),
                "the tool moved across after the tool change without rising to the machine's safe height");
        }

        /// <summary>
        /// Catches sections applied to a phase that is skipped, or a phase that runs ignoring
        /// them: with the second phase and the lower left section, only the drill at (5,5) is cut.
        /// </summary>
        [Fact]
        public void PhasesAndSections_Combine()
        {
            var source = SectionTestSupport.Parse(PhaseTestSupport.TwoPhaseBoard);
            var sections = SectionTestSupport.Choose(source, 2, 2, (0, 0));

            var output = SectionTestSupport.Keep(source, sections, PhaseTestSupport.Choose(source, 2));

            var cuts = SectionTestSupport.Cuts(output).ToList();
            Assert.NotEmpty(cuts);
            Assert.All(cuts, c =>
            {
                Assert.Equal(5, c.End.X, SectionTestSupport.Tolerance);
                Assert.Equal(5, c.End.Y, SectionTestSupport.Tolerance);
            });
        }

        /// <summary>Catches the board picture showing cuts of a phase the run skips.</summary>
        [Fact]
        public void TheCellsCut_AreThoseOfTheChosenPhases()
        {
            var source = SectionTestSupport.Parse(PhaseTestSupport.TwoPhaseBoard);
            var (division, _) = BoardDivision.Of(source, 2, 2);

            var second = source.CellsCut(division!, PhaseTestSupport.Choose(source, 2));
            var all = source.CellsCut(division!, null);

            Assert.Equal(new[] { new BoardCell(0, 0), new BoardCell(1, 1) }.ToHashSet(), second.ToHashSet());
            Assert.Contains(new BoardCell(1, 0), all);
        }

        /// <summary>
        /// Catches a spindle start or a dwell between the file's travel and a cut that is left
        /// out taking the tool to that cut: they are written where the tool is.
        /// </summary>
        [Fact]
        public void ACommandBetweenTravelAndALeftOutCut_DoesNotTakeTheToolThere()
        {
            var source = SectionTestSupport.Parse(
                "G21", "G90", "G0 Z10", "G0 X0 Y0", "G0 Z1",
                "G0 X2 Y2", "G1 Z-0.1 F200", "G1 X8 Y2 F600", "G0 Z1",
                "G0 X15 Y5", "M3", "G4 P2", "G1 Z-1 F100", "G1 Z1",
                "G0 X4 Y5", "G1 Z-1 F100", "G1 Z1",
                "M5", "M2");
            var sections = SectionTestSupport.Choose(source, 2, 1, (0, 0));

            var output = SectionTestSupport.Keep(source, sections);

            Assert.DoesNotContain(output.Toolpath.OfType<Motion>(), m => m.End.X == 15 && m.End.Y == 5);
            Assert.Equal(1, PhaseTestSupport.Count(output, GCodeNumbers.MCodeSpindleClockwise));
            Assert.Equal(new[] { SecondDwellSeconds }, output.Toolpath.OfType<Dwell>().Select(d => d.Seconds));
        }

        /// <summary>
        /// Catches the clip's rise to the machine's safe height drifting from the safety retract
        /// every run starts with.
        /// </summary>
        [Fact]
        public void TheClipsRetract_IsTheRunsSafetyRetract()
        {
            Assert.Equal(new MoveTarget(Z: Constants.SafeClearanceZ, InMachineCoordinates: true).ToGCode(),
                PartClip.MachineSafeHeightLine);
        }

        /// <summary>
        /// Catches a feed written for a rapid: a rapid read before any F carries a feed of 0,
        /// which reached the machine as "F0".
        /// </summary>
        [Fact]
        public void TheGCode_WritesAFeedOnlyBeforeAFeedMove()
        {
            var file = SectionTestSupport.Parse("G21", "G90", "G0 X1 Y1 Z1", "G1 Z-0.1 F300", "G0 Z1", "G0 X5 Y5", "G1 Z-0.1 F300");

            var lines = file.GetGCode();

            Assert.DoesNotContain("F0", lines);
            // The feed written ahead of every move, so no move runs before one is set, comes first.
            int firstMove = lines.FindIndex(l => l.StartsWith("G0 ") || l.StartsWith("G1 "));
            for (int i = firstMove; i + 1 < lines.Count; i++)
            {
                Assert.False(lines[i].StartsWith('F') && lines[i + 1].StartsWith("G0 "),
                    $"a feed is written before the rapid \"{lines[i + 1]}\"");
            }
            Assert.Equal(new[] { 300.0, 300.0 }, GCodeFile.FromList(lines).Toolpath.OfType<Line>().Where(l => !l.Rapid).Select(l => l.Feed));
        }

        private const string ProbeAtItsOwnFeed = "G38.2 Z-5 F200";
        private const double CutFeed = 100;

        /// <summary>
        /// Catches the feed a block sets for itself being taken for the next cut's: the cut must
        /// name its own feed again, or the machine runs it at the probe's.
        /// </summary>
        [Fact]
        public void TheGCode_WritesTheFeedAgain_AfterABlockThatSetsItsOwn()
        {
            var lines = SectionTestSupport.Parse(
                "G21", "G90", "G0 X0 Y0 Z5", "G1 Z1 F100", ProbeAtItsOwnFeed,
                "G92 Z0", "G0 Z5", "G0 X0 Y0", "G1 Z-0.1 F100", "G1 X10").GetGCode();

            int probe = lines.IndexOf(ProbeAtItsOwnFeed);
            int cut = lines.FindIndex(probe, l => l.StartsWith("G1 "));
            Assert.Contains(FormattableString.Invariant($"F{CutFeed}"), lines.Skip(probe).Take(cut - probe));
        }

        /// <summary>Catches the words for a phase, or for the choice, being built twice.</summary>
        [Fact]
        public void ThePhaseWords_NameTheNumberAndTime_AndTheChoice()
        {
            var phase = new JobPhase(2, TimeSpan.FromMinutes(5.5));
            var file = SectionTestSupport.Parse(PhaseTestSupport.TwoPhaseBoard);

            Assert.Equal(string.Format(CliConstants.PhaseLabelFormat, 2, DisplayHelpers.FormatDuration(phase.Time)),
                DisplayHelpers.GetPhaseLabel(phase));
            Assert.Equal(CliConstants.PhasesAll, DisplayHelpers.GetPhasesText(null));
            Assert.Equal(string.Format(CliConstants.PhasesChosenFormat, "2"),
                DisplayHelpers.GetPhasesText(PhaseTestSupport.Choose(file, 2)));
        }
    }

    // What AppState does with a choice of phases: the machine's G-code, the version, the reset
    // rules, and the picture of the board both UIs draw.
    [Collection(WebServerCollection.Name)]
    public class JobPhasesAppStateTests : JobFixtureTests
    {
        private const double TestDepth = -0.1;

        public JobPhasesAppStateTests(WebServerFixture web) : base(web)
        {
        }

        private GCodeFile GivenTheJobIsLoaded(string[]? lines = null)
        {
            var loaded = _boards.Load(lines ?? PhaseTestSupport.TwoPhaseBoard);
            Assert.Null(AppState.MillPhases);
            return loaded;
        }

        /// <summary>Catches a choice that changes the version and not the lines, or the lines and not the version.</summary>
        [Fact]
        public void ChoosingPhases_RebuildsTheMachinesGCode()
        {
            var loaded = GivenTheJobIsLoaded();
            long before = AppState.MachineFileVersion;

            Assert.Null(AppState.ChooseMillPhases(new[] { 2 }));

            Assert.Equal(new[] { 2 }, AppState.MillPhases!.Numbers);
            Assert.NotEqual(before, AppState.MachineFileVersion);
            Assert.Equal(SectionTestSupport.Keep(loaded, null, AppState.MillPhases).GetGCode(), OnTheMachine());
            Assert.Same(loaded, AppState.CurrentFile);
        }

        /// <summary>Catches choosing every phase leaving a rebuilt file on the machine instead of the file's own.</summary>
        [Fact]
        public void ChoosingEveryPhase_MillsTheWholeJob()
        {
            var loaded = GivenTheJobIsLoaded();
            Assert.Null(AppState.ChooseMillPhases(new[] { 2 }));

            Assert.Null(AppState.ChooseMillPhases(new[] { 1, 2 }));

            Assert.Null(AppState.MillPhases);
            Assert.Equal(loaded.GetGCode(), OnTheMachine());
        }

        /// <summary>
        /// Catches a refused choice, whether the choice itself or the G-code it would build is
        /// refused, changing the version or the machine's lines.
        /// </summary>
        [Fact]
        public void ARefusedChoice_ChangesNothing()
        {
            GivenTheJobIsLoaded(PhaseTestSupport.WithLineAfter(PhaseTestSupport.TwoPhaseBoard, "G0 Z1", "G92 Z0"));
            Assert.Null(AppState.ChooseMillPhases(new[] { 1 }));
            long version = AppState.MachineFileVersion;
            string[] lines = OnTheMachine();

            Assert.Equal(Constants.ErrorNoPhaseChosen, AppState.ChooseMillPhases(Array.Empty<int>()));
            Assert.Equal(Constants.ErrorPhaseNotInFile, AppState.ChooseMillPhases(new[] { 3 }));
            Assert.Equal(Constants.ErrorPhaseSkipsABlock, AppState.ChooseMillPhases(new[] { 2 }));

            Assert.Equal(version, AppState.MachineFileVersion);
            Assert.Equal(lines, OnTheMachine());
            Assert.Equal(new[] { 1 }, AppState.MillPhases!.Numbers);
        }

        /// <summary>
        /// Catches a choice of phases surviving a new file, which may have other phases, or a
        /// new height map, which resets the depth and sections too.
        /// </summary>
        [Fact]
        public void LoadingAFile_OrAdoptingAMap_ResetsThePhases()
        {
            var loaded = GivenTheJobIsLoaded();
            Assert.Null(AppState.ChooseMillPhases(new[] { 2 }));

            Assert.Null(AppState.AdoptProbeGrid(WebServerFixture.CompleteMapForThisJob()));
            Assert.Null(AppState.MillPhases);
            Assert.Equal(loaded.GetGCode(), OnTheMachine());

            Assert.Null(AppState.ChooseMillPhases(new[] { 2 }));
            var next = GCodeFile.Load(_boards.Write(PhaseTestSupport.TwoPhaseBoard));
            Assert.Null(AppState.LoadGCodeIntoMachine(next).Refused);
            Assert.Null(AppState.MillPhases);
        }

        /// <summary>
        /// Catches the phases, sections and depth not composing, or one change dropping another.
        /// </summary>
        [Fact]
        public void PhasesSectionsAndDepth_Combine_AndEachChangeKeepsTheOthers()
        {
            var loaded = GivenTheJobIsLoaded();
            Assert.Null(AppState.SetDepthAdjustment(TestDepth));
            Assert.Null(AppState.ChooseMillSections(2, 2, new[] { new BoardCell(0, 0) }));

            Assert.Null(AppState.ChooseMillPhases(new[] { 2 }));

            var sections = AppState.MillSections!;
            var phases = AppState.MillPhases!;
            Assert.Equal(TestDepth, AppState.DepthAdjustment, SectionTestSupport.Tolerance);
            Assert.Equal(SectionTestSupport.Keep(loaded, sections, phases).OffsetCutDepth(TestDepth).GetGCode(), OnTheMachine());

            Assert.Null(AppState.ChooseMillSections(2, 2, new[] { new BoardCell(1, 1) }));
            Assert.Same(phases, AppState.MillPhases);
        }

        private const double MapBaseHeight = 0.05;
        private const double MapRisePerColumn = 0.1;

        /// <summary>
        /// Catches applying the height map dropping the chosen phases, or applying it to the
        /// whole file instead of the phases that run.
        /// </summary>
        [Fact]
        public void ApplyingTheMap_KeepsThePhases_AndAppliesTheMapOverThePhasesThatRun()
        {
            var loaded = GivenTheJobIsLoaded();
            Persistence.ClearProbeAutoSave();
            Assert.Null(AppState.AdoptProbeGrid(WebServerFixture.CompleteMapForThisJob(MapBaseHeight, MapRisePerColumn)));
            Assert.Null(AppState.ChooseMillPhases(new[] { 2 }));
            var phases = AppState.MillPhases!;

            Assert.Null(AppState.ApplyProbeData());

            Assert.Same(phases, AppState.MillPhases);
            Assert.Equal(SectionTestSupport.Keep(loaded, null, phases).ApplyProbeGrid(AppState.ProbePoints!).GetGCode(), OnTheMachine());
        }

        /// <summary>
        /// Catches the picture both UIs draw to choose sections on showing the cuts of a phase
        /// the run skips.
        /// </summary>
        [Fact]
        public void ThePictureOfTheBoard_FollowsTheChosenPhases()
        {
            var loaded = GivenTheJobIsLoaded();
            var (division, _) = BoardDivision.Of(loaded, 2, 2);
            Assert.Equal(loaded.CellsCut(division!, null), AppState.CellsCut(division!));

            Assert.Null(AppState.ChooseMillPhases(new[] { 2 }));

            Assert.Equal(loaded.CellsCut(division!, AppState.MillPhases), AppState.CellsCut(division!));
            Assert.NotEqual(loaded.CellsCut(division!, null).Count, AppState.CellsCut(division!).Count);
        }

        /// <summary>Catches a choice during a run swapping the file under it.</summary>
        [Fact]
        public async Task ChoosingPhases_DuringARun_IsRefused_AndChangesNothing()
        {
            await _web.LoadWithAMapAppliedAsync(_boards.Write(PhaseTestSupport.TwoPhaseBoard));

            await _web.WhileAMillRunHoldsAtTheDoorAsync(() =>
            {
                long version = AppState.MachineFileVersion;
                string[] lines = OnTheMachine();

                Assert.Equal(CliConstants.ErrorFileChangeDuringRun, AppState.ChooseMillPhases(new[] { 2 }));

                Assert.Null(AppState.MillPhases);
                Assert.Equal(version, AppState.MachineFileVersion);
                Assert.Equal(lines, OnTheMachine());
                return Task.CompletedTask;
            });
        }
    }
}
