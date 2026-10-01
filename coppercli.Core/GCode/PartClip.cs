#nullable enable

using coppercli.Core.GCode.GCodeCommands;
using coppercli.Core.Util;
using System;
using System.Collections.Generic;
using System.Linq;

namespace coppercli.Core.GCode
{
    /// <summary>
    /// Builds the toolpath of <see cref="GCodeFile.KeepPart"/>: the chosen phases of the file,
    /// and within them the cuts that lie in the chosen sections, split exactly at the section
    /// lines, with travel between them. The tool comes down only over a cut it is about to make.
    /// </summary>
    /// <remarks>
    /// While _following is true the output copies the file. The file's travel is held back until
    /// the next command shows whether it is needed: it is written as the file wrote it if
    /// _following is still true then, and dropped otherwise, so the tool never goes to a cut that
    /// is left out. Once _following is false, or where the output does not know where the tool
    /// is, <see cref="TravelTo"/> takes the tool to the next kept stretch.
    /// </remarks>
    internal sealed class PartClip
    {
        private readonly GCodeFile _file;
        private readonly ChosenPhases? _phases;
        private readonly BoardSections? _sections;
        private readonly List<Command> _toolpath = new();

        /// <summary>The file's travel since the output last wrote a move.</summary>
        private readonly List<Motion> _heldTravel = new();

        /// <summary>Where the written toolpath leaves the tool, or null where it does not say.</summary>
        private Vector3? _toolAt;

        /// <summary>Where the file leaves the tool, or null where it does not say.</summary>
        private Vector3? _fileAt;

        /// <summary>True when writing the held travel from _toolAt would bring the tool to _fileAt.</summary>
        private bool _following = true;

        /// <summary>The height the output crosses the board at, for each stage of the file (see ClearancePerStage).</summary>
        private readonly List<double?> _stageClearances;

        /// <summary>
        /// Which stage of the file the walk is in: how many tool changes and probe,
        /// machine-coordinate or offset blocks it has passed (see EndsAStage).
        /// </summary>
        private int _stage;

        /// <summary>Which phase of the file the walk is in, from 1.</summary>
        private int _phase = 1;

        /// <summary>
        /// Where the file's last rapid down in this stage ended, above the surface: the output
        /// comes down this far at rapid speed and feeds the rest of the way.
        /// </summary>
        private double? _approach;

        /// <summary>The feed of the file's last cut that went down, used for a plunge into a kept stretch.</summary>
        private double? _plungeFeed;

        /// <summary>
        /// The file's feed at its last move. The rapids the output adds carry it, and a plunge
        /// uses it when the file has not plunged yet.
        /// </summary>
        private double _fileFeed;

        /// <summary>
        /// Whether the file has the spindle on, and whether the output does: they differ after a
        /// skipped phase starts it, and a cut in a phase that runs would then be made with the
        /// spindle stopped.
        /// </summary>
        private bool _fileSpindleOn;
        private bool _outputSpindleOn;

        /// <summary>A skipped phase holds a probe or offset block, which any later phase that runs may depend on.</summary>
        private bool _skippedABlock;

        /// <summary>True when the output stopped following the file because of a skipped phase, not a section.</summary>
        private bool _leftByASkippedPhase;

        /// <summary>Whether any cut has been written.</summary>
        private bool _keptACut;

        /// <summary>Why the toolpath cannot be built, once known; the walk stops there.</summary>
        private string? _refused;

        /// <summary>The line that takes the tool to the machine's safe height, the retract every run starts with.</summary>
        internal static readonly string MachineSafeHeightLine =
                        GCodeFormat.MoveLine(x: null, y: null, z: Constants.SafeClearanceZ, inMachineCoordinates: true);

        /// <summary>The rise to the machine's safe height, as the clip writes it.</summary>
        private static PassThrough MachineSafeHeightBlock() => new() { Line = MachineSafeHeightLine, MovesOnly = true };

        private PartClip(GCodeFile file, ChosenPhases? phases, BoardSections? sections)
        {
            _file = file;
            _phases = phases;
            _sections = sections;
            _stageClearances = ClearancePerStage(file.Toolpath);
        }

        /// <returns>The toolpath, or null with the reason it cannot be built.</returns>
        public static (List<Command>? Toolpath, string? Refused) Keep(GCodeFile file, ChosenPhases? phases, BoardSections? sections)
        {
            var clip = new PartClip(file, phases, sections);

            for (int i = 0; i < file.Toolpath.Count; i++)
            {
                Command command = file.Toolpath[i];
                clip._phase = file.PhaseOf(i);
                clip.Take(command);
                if (clip._refused != null)
                {
                    return (null, clip._refused);
                }

                if (EndsAStage(command))
                {
                    clip._stage++;
                    clip._approach = null;
                }
            }

            clip.CatchUp();
            if (clip._refused == null && !clip._keptACut)
            {
                clip._refused = Constants.ErrorNothingToCut;
            }
            return clip._refused != null ? (null, clip._refused) : (clip._toolpath, null);
        }

        private bool PhaseRuns => ChosenPhases.Runs(_phases, _phase);

        /// <summary>
        /// A tool change starts work with another tool, a move in machine coordinates leaves the
        /// tool where the file's heights do not say, and a probe or offset block can move work
        /// zero, so the heights noted before any of them do not carry past it.
        /// </summary>
        private static bool EndsAStage(Command command) =>
            command is PassThrough or MCode { IsToolChange: true };

        /// <summary>
        /// The height the output crosses the board at in each stage: the highest Z at which the
        /// stage moves level across the board without cutting, or, if it never does, the highest
        /// Z it reaches. Taken over the whole stage, so no crossing is lower than one the file
        /// makes later with the same tool.
        /// </summary>
        private static List<double?> ClearancePerStage(IReadOnlyList<Command> toolpath)
        {
            var clearances = new List<double?>();
            double? crossing = null;
            double? highest = null;

            foreach (Command command in toolpath)
            {
                if (command is Motion move)
                {
                    if (move is { FullyKnown: true, IsCut: false, MovesAcrossTheBoard: true } && move.Start.Z == move.End.Z)
                    {
                        crossing = Higher(crossing, move.End.Z);
                    }

                    highest = Higher(highest,
                        move.FullyKnown ? Math.Max(move.Start.Z, move.End.Z)
                        : move is Line { ZKnown: true } ? move.End.Z
                        : null);
                }

                if (EndsAStage(command))
                {
                    clearances.Add(crossing ?? highest);
                    crossing = null;
                    highest = null;
                }
            }

            clearances.Add(crossing ?? highest);
            return clearances;
        }

        private static double? Higher(double? a, double? b) =>
            a is not double first ? b : b is not double second ? a : Math.Max(first, second);

        private void Take(Command command)
        {
            if (command is MCode spindle && (spindle.StartsTheSpindle || spindle.StopsTheSpindle))
            {
                _fileSpindleOn = spindle.StartsTheSpindle;
                if (PhaseRuns || spindle.StopsTheSpindle)
                {
                    _outputSpindleOn = spindle.StartsTheSpindle;
                }
            }

            if (!PhaseRuns)
            {
                TakeSkipped(command);
                return;
            }

            if (_skippedABlock)
            {
                _refused = Constants.ErrorPhaseSkipsABlock;
                return;
            }


            switch (command)
            {
                case Motion { FullyKnown: false } unplaced:
                    TakeUnplaced(unplaced);
                    break;

                case Motion { IsCut: true } cut:
                    TakeCut(cut);
                    break;

                case Motion travel:
                    TakeTravel(travel);
                    break;

                case PassThrough block:
                    TakePassThrough(block);
                    break;

                // The wait belongs to a cut that is left out.
                case Dwell when !_following && _fileAt is { Z: < 0 }:
                    break;

                default:
                    TakeCommand(command);
                    break;
            }
        }

        /// <summary>
        /// Leaves out a skipped phase's moves, including a move in machine coordinates, its tool
        /// change, and what starts the spindle, dwells or pauses (M0, M1) for its work; keeps what
        /// stops the spindle or ends the program, and settings such as the spindle speed, which a
        /// later phase and the end of the job need. A probe or offset block refuses the choice
        /// once a later phase runs, because that phase may depend on it.
        /// </summary>
        private void TakeSkipped(Command command)
        {
            switch (command)
            {
                case Motion move:
                    NoteMove(move);
                    SkipAMove(move.KnownEnd);
                    break;

                case PassThrough { MovesOnly: true }:
                    SkipAMove(null);
                    break;

                case PassThrough:
                    _skippedABlock = true;
                    break;

                case Dwell:
                case MCode { IsToolChange: true }:
                case MCode { StartsTheSpindle: true }:
                case MCode { IsPause: true }:
                    break;

                default:
                    TakeCommand(command);
                    break;
            }
        }

        private void SkipAMove(Vector3? fileAt)
        {
            _fileAt = fileAt;
            _following = false;
            _leftByASkippedPhase = true;
            _heldTravel.Clear();
        }

        /// <summary>
        /// A command that does not depend on where the tool is (a spindle start or speed, a tool
        /// number, a dwell) is written once the tool is out of the copper, ahead of the rest of
        /// any held travel, which the next cut may still drop; any other command waits until the
        /// tool is where the file puts it (<see cref="CatchUp"/>). A tool change moves the tool,
        /// so after one the output no longer knows where the tool is.
        /// </summary>
        private void TakeCommand(Command command)
        {
            bool needsTheToolInPlace = command is not (Spindle or TCode or Dwell or MCode { StartsTheSpindle: true });
            if (needsTheToolInPlace)
            {
                CatchUp();
            }
            else if (_toolAt is { Z: < 0 })
            {
                LeaveTheCopper();
            }

            _toolpath.Add(command);
            if (command is MCode { IsToolChange: true })
            {
                _toolAt = null;
            }
        }

        private void TakeCut(Motion cut)
        {
            if (RefusesACutWithTheSpindleStopped())
            {
                return;
            }

            NoteMove(cut);
            if (cut is not Line { Rapid: true } && cut.End.Z < cut.Start.Z)
            {
                _plungeFeed = cut.Feed;
            }

            var stretches = (_sections?.KeptStretches(cut) ?? new[] { (From: 0.0, To: 1.0) }).ToList();
            foreach (var (from, to) in stretches)
            {
                Motion part = from == 0 && to == 1 ? cut : cut.Slice(from, to);

                if (from == 0 && CanCopyTheFile)
                {
                    WriteHeldTravel();
                }
                else
                {
                    _heldTravel.Clear();
                    if (!TravelTo(part.Start))
                    {
                        return;
                    }
                }

                Write(part);
            }

            _keptACut |= stretches.Count > 0;
            _fileAt = cut.End;
            _following = stretches.Count > 0 && stretches[^1].To == 1;
            if (!_following)
            {
                _leftByASkippedPhase = false;
                _heldTravel.Clear();
            }
        }

        private void TakeTravel(Motion travel)
        {
            NoteMove(travel);
            _fileAt = travel.KnownEnd;

            if (_following)
            {
                _heldTravel.Add(travel);
            }
            else if (_toolAt is Vector3 at && _fileAt is Vector3 fileAt && at == fileAt)
            {
                _following = true;
            }
        }

        /// <summary>
        /// A move whose start is not known, such as the file's first move or one after a probe,
        /// machine-coordinate or offset block, is travel like any other: held, and dropped if it
        /// leads to a cut that is left out, so the tool never comes down over that cut. One that
        /// ends below the surface is a cut whose path is not known: written as the file wrote it
        /// where the output copies the file, and refused otherwise.
        /// </summary>
        private void TakeUnplaced(Motion move)
        {
            if (move is not Line { ZKnown: true } || move.End.Z >= 0)
            {
                TakeTravel(move);
                return;
            }

            if (RefusesACutWithTheSpindleStopped())
            {
                return;
            }

            if (!CanCopyTheFile)
            {
                Refuse(Constants.ErrorSectionsCutWithUnknownStart, Constants.ErrorPhaseToolPlaceUnknown);
                return;
            }

            NoteMove(move);
            WriteHeldTravel();
            Write(move);
            _keptACut = true;
            _fileAt = move.KnownEnd;
        }

        /// <summary>
        /// A probe, a machine-coordinate move or an offset change acts where the tool is, so the
        /// tool first goes to where the file puts it, as a run of the whole file does. Refused
        /// when that point is not known while the output no longer follows the file, or is
        /// below the surface, which the tool could reach only by cutting.
        /// </summary>
        private void TakePassThrough(PassThrough block)
        {
            if (CanCopyTheFile || (_following && _fileAt == null))
            {
                WriteHeldTravel();
            }
            else if (_fileAt is not { Z: >= 0 } fileAt)
            {
                Refuse(Constants.ErrorSectionsBlockInLeftOutCut, Constants.ErrorPhaseToolPlaceUnknown);
                return;
            }
            else
            {
                _heldTravel.Clear();
                if (!TravelTo(fileAt))
                {
                    return;
                }
            }

            _toolpath.Add(block);
            _toolAt = null;
            _fileAt = null;
            _following = true;
        }

        /// <summary>
        /// Whether the held travel can be written as the file wrote it: the output follows the
        /// file, and either knows where the tool is or keeps the whole board, so the file's
        /// travel cannot bring the tool down over a section that is left out.
        /// </summary>
        private bool CanCopyTheFile => _following && (_toolAt != null || _sections == null);

        /// <summary>
        /// Takes the tool out of the copper: by the file's own held travel, only as far as the
        /// first move that clears the surface, while the output follows the file; by rising in
        /// place otherwise (<see cref="CatchUp"/>).
        /// </summary>
        private void LeaveTheCopper()
        {
            if (!_following)
            {
                CatchUp();
                return;
            }

            int written = 0;
            while (_toolAt is { Z: < 0 } && written < _heldTravel.Count)
            {
                Write(_heldTravel[written++]);
            }
            _heldTravel.RemoveRange(0, written);
        }

        /// <summary>
        /// Writes the held travel while _following is true. Otherwise the file's last cut ended
        /// in a section that is left out, or its last move was in a skipped phase, so the tool
        /// stays where it is in X and Y and rises: to the file's Z if that is at or above the
        /// surface, to the clearance if not, and to the machine's safe height where the output
        /// does not know where the tool is. Nothing has moved the tool since a tool change, so
        /// after one it stays where the change left it until a move rises first (TravelTo): a
        /// pause the file makes right after the change stays right after it.
        /// </summary>
        private void CatchUp()
        {
            if (_following)
            {
                WriteHeldTravel();
                return;
            }

            if (LastMachineCommand() is MCode { IsToolChange: true })
            {
                return;
            }

            if (_toolAt == null)
            {
                RiseToMachineSafeHeight();
            }
            else if (_fileAt is { Z: >= 0 } fileAt)
            {
                RiseTo(fileAt.Z);
            }
            else if (Clearance() is double clearance)
            {
                RiseTo(clearance);
            }
        }

        /// <summary>
        /// Takes the tool from where the output left it to <paramref name="target"/>: up to the
        /// clearance, or to its current Z or the target's if either is higher, or to the
        /// machine's safe height where the output does not know where the tool is (see RiseTo);
        /// then across, then down to the approach height at rapid speed. The descent below that
        /// is a feed plunge.
        /// </summary>
        /// <returns>False when the file gives no clearance above the surface.</returns>
        private bool TravelTo(Vector3 target)
        {
            if (_toolAt == target)
            {
                return true;
            }

            if (Clearance() is not double clearance)
            {
                return false;
            }

            double across = Math.Max(Math.Max(_toolAt?.Z ?? clearance, clearance), target.Z);
            RiseTo(across);

            if (_toolAt is not Vector3 above)
            {
                // Over the target at whatever height RiseTo left the tool.
                Write(new Line
                {
                    Rapid = true,
                    End = new Vector3(target.X, target.Y, 0),
                    Feed = _fileFeed,
                    PositionValid = new[] { true, true, false }
                });
            }
            else if (above.X != target.X || above.Y != target.Y)
            {
                Write(Move(rapid: true, new Vector3(target.X, target.Y, across)));
            }

            double approach = Math.Min(_approach ?? clearance, across);
            double rapidTo = Math.Max(target.Z, approach);
            if (_toolAt is not Vector3 over || over.Z != rapidTo)
            {
                Write(Move(rapid: true, new Vector3(target.X, target.Y, rapidTo)));
            }

            if (target.Z < rapidTo)
            {
                Write(Move(rapid: false, target, _plungeFeed ?? _fileFeed));
            }
            return true;
        }

        /// <summary>
        /// Raises the tool straight up to <paramref name="height"/> if it is lower. Where the
        /// output does not know where the tool is, it rises to the machine's safe height instead.
        /// </summary>
        private void RiseTo(double height)
        {
            if (_toolAt is not Vector3 at)
            {
                RiseToMachineSafeHeight();
            }
            else if (at.Z < height)
            {
                Write(Move(rapid: true, new Vector3(at.X, at.Y, height)));
            }
        }

        /// <summary>Writes the rise to the machine's safe height, unless nothing has moved the machine since the last one.</summary>
        private void RiseToMachineSafeHeight()
        {
            if (!IsAtMachineSafeHeight())
            {
                _toolpath.Add(MachineSafeHeightBlock());
            }
        }

        private bool IsAtMachineSafeHeight() =>
            LastMachineCommand() is PassThrough { Line: var line } && line == MachineSafeHeightLine;

        /// <summary>The last command written that moves the machine: a move, a block, or a tool change.</summary>
        private Command? LastMachineCommand() =>
            _toolpath.LastOrDefault(command => command is Motion or PassThrough or MCode { IsToolChange: true });

        /// <summary>A straight move from where the tool is to <paramref name="to"/>, a rapid unless a feed is given.</summary>
        private Line Move(bool rapid, Vector3 to, double? feed = null) => new()
        {
            Rapid = rapid,
            Start = _toolAt ?? to,
            End = to,
            Feed = feed ?? _fileFeed,
            PositionValid = new[] { true, true, true },
            StartValid = _toolAt != null
        };

        /// <returns>The height to cross the board at, or null, with the refusal set, when the file gives none above the surface.</returns>
        private double? Clearance()
        {
            double? height = _stageClearances[_stage];
            if (height is not > 0)
            {
                Refuse(Constants.ErrorSectionsNoTravelHeight, Constants.ErrorPhaseNoTravelHeight);
                return null;
            }
            return height;
        }

        /// <summary>The refusal names what made the output leave the file: a skipped phase, or a section.</summary>
        private void Refuse(string forSections, string forPhases) =>
            _refused = _sections == null || _leftByASkippedPhase ? forPhases : forSections;

        private bool RefusesACutWithTheSpindleStopped()
        {
            bool stopped = _fileSpindleOn && !_outputSpindleOn;
            if (stopped)
            {
                _refused = Constants.ErrorPhaseSkipsTheSpindleStart;
            }
            return stopped;
        }

        private void NoteMove(Motion move)
        {
            _fileFeed = move.Feed;

            // Z alone is enough: a file that has not said where X and Y are yet, at its start or
            // after a block, still says how low it rapids.
            if (move is Line { Rapid: true, ZKnown: true } rapid
                && rapid.End.Z < rapid.Start.Z && rapid.End.Z > 0)
            {
                _approach = rapid.End.Z;
            }
        }

        private void WriteHeldTravel()
        {
            foreach (Motion travel in _heldTravel)
            {
                Write(travel);
            }
            _heldTravel.Clear();
        }

        private void Write(Motion move)
        {
            _toolpath.Add(move);
            _toolAt = move.KnownEnd;
        }
    }
}
