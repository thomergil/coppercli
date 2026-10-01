#nullable enable

using coppercli.Core.GCode.GCodeCommands;
using coppercli.Core.Util;
using System;
using System.Collections.Generic;
using System.Linq;

namespace coppercli.Core.GCode
{
    /// <summary>
    /// The sections of the board a run mills: the file's cuts inside them are kept and the rest
    /// are left out (<see cref="GCodeFile.KeepPart"/>).
    /// </summary>
    public sealed class BoardSections
    {
        private BoardSections(BoardDivision division, IReadOnlySet<BoardCell> chosen)
        {
            Division = division;
            Chosen = chosen;
        }

        public BoardDivision Division { get; }
        public IReadOnlySet<BoardCell> Chosen { get; }

        /// <summary>
        /// The operator's choice of sections of <paramref name="file"/>, divided into
        /// <paramref name="columns"/> by <paramref name="rows"/>. Choosing no sections, or all
        /// of them, chooses the whole board, which comes back as null with no refusal.
        /// </summary>
        /// <returns>The sections, or null with the reason the choice was refused.</returns>
        public static (BoardSections? Sections, string? Refused) Choose(
            GCodeFile file, int columns, int rows, IEnumerable<BoardCell> chosen)
        {
            if (columns < 1 || columns > Constants.MaxSectionsPerAxis
                || rows < 1 || rows > Constants.MaxSectionsPerAxis)
            {
                return (null, string.Format(Constants.ErrorSectionCountFormat, Constants.MaxSectionsPerAxis));
            }

            // With nothing to place on the board, the file need not have an area to divide.
            var set = chosen.ToHashSet();
            if (set.Count == 0)
            {
                return (null, null);
            }

            var (division, refused) = BoardDivision.Of(file, columns, rows);
            if (division == null)
            {
                return (null, refused);
            }

            if (!set.All(division.Contains))
            {
                return (null, Constants.ErrorSectionOutsideBoard);
            }

            return IsWholeBoard(set.Count, columns * rows)
                ? (null, null)
                : (new BoardSections(division, set), null);
        }

        /// <summary>
        /// The division and each chosen section with the area it covers in work coordinates, for
        /// the log.
        /// </summary>
        public override string ToString() =>
            FormattableString.Invariant($"{Division.Columns}x{Division.Rows}: ") + string.Join("; ",
                Chosen.OrderBy(cell => cell.Row).ThenBy(cell => cell.Column).Select(cell =>
                {
                    var (min, max) = Division.BoundsOf(cell);
                    return FormattableString.Invariant(
                        $"column {cell.Column} row {cell.Row} X {min.X:F2}..{max.X:F2} Y {min.Y:F2}..{max.Y:F2}");
                }));

        /// <summary>Choosing none of the sections, or all of them, mills the whole board.</summary>
        public static bool IsWholeBoard(int chosen, int total) => chosen == 0 || chosen == total;

        /// <summary>
        /// The stretches of <paramref name="motion"/> that lie in chosen sections, in order along
        /// it, as ratios of the whole move.
        /// </summary>
        internal IEnumerable<(double From, double To)> KeptStretches(Motion motion)
        {
            (double From, double To)? open = null;

            foreach (var piece in Division.Pieces(motion))
            {
                if (!Chosen.Contains(piece.Cell))
                {
                    if (open is { } finished)
                    {
                        yield return finished;
                        open = null;
                    }
                    continue;
                }

                open = open is { } stretch ? (stretch.From, piece.To) : (piece.From, piece.To);
            }

            if (open is { } last)
            {
                yield return last;
            }
        }
    }
}
