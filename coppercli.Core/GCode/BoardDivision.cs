#nullable enable

using coppercli.Core.GCode.GCodeCommands;
using coppercli.Core.Util;
using System;
using System.Collections.Generic;
using System.Linq;

namespace coppercli.Core.GCode
{
    /// <summary>
    /// A cell of a <see cref="BoardDivision"/>: its column counted from the left and its row
    /// from the bottom, both from 0.
    /// </summary>
    public readonly record struct BoardCell(int Column, int Row);

    /// <summary>
    /// The area a file cuts (<see cref="GCodeFile.CuttingBounds"/>) divided into equal columns
    /// and rows of cells: the operator's sections, or the cells of a picture of the board. A
    /// point on the line between two cells belongs to the one right of it or above it, and a
    /// point outside the area to the nearest cell.
    /// </summary>
    public sealed class BoardDivision
    {
        private BoardDivision(Vector2 min, Vector2 max, int columns, int rows)
        {
            Min = min;
            Max = max;
            Columns = columns;
            Rows = rows;
        }

        public Vector2 Min { get; }
        public Vector2 Max { get; }
        public int Columns { get; }
        public int Rows { get; }

        private double CellWidth => (Max.X - Min.X) / Columns;
        private double CellHeight => (Max.Y - Min.Y) / Rows;

        /// <summary>
        /// Divides the area <paramref name="file"/> cuts. Refused for a file that cuts no area,
        /// or one with <see cref="GCodeFile.HasArcsOutsideXYPlane"/>.
        /// </summary>
        /// <returns>The division, or null with the reason it was refused.</returns>
        public static (BoardDivision? Division, string? Refused) Of(GCodeFile file, int columns, int rows)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);

            if (file.HasArcsOutsideXYPlane)
            {
                return (null, Constants.ErrorArcsOutsideXYPlane);
            }

            if (!file.CutsAnArea)
            {
                return (null, Constants.ErrorNoAreaToDivide);
            }

            var (min, max) = file.CuttingBounds;
            return (new BoardDivision(min, max, columns, rows), null);
        }

        /// <summary>
        /// Divides the area <paramref name="file"/> cuts into cells as near square as possible,
        /// as many as fit <paramref name="mostColumns"/> across and <paramref name="mostRows"/>
        /// down, for a picture of the board. Refused as <see cref="Of"/> is.
        /// </summary>
        public static (BoardDivision? Division, string? Refused) Fitting(GCodeFile file, int mostColumns, int mostRows)
        {
            var (whole, refused) = Of(file, 1, 1);
            if (whole == null)
            {
                return (null, refused);
            }

            double aspect = (whole.Max.X - whole.Min.X) / (whole.Max.Y - whole.Min.Y);
            double columns = Math.Min(mostColumns, mostRows * aspect);
            return Of(file,
                Math.Max(1, (int)Math.Round(columns)),
                Math.Max(1, (int)Math.Round(columns / aspect)));
        }

        public bool Contains(BoardCell cell) =>
            cell.Column >= 0 && cell.Column < Columns && cell.Row >= 0 && cell.Row < Rows;

        public BoardCell CellAt(double x, double y) =>
            new(Index(x, Min.X, CellWidth, Columns), Index(y, Min.Y, CellHeight, Rows));

        /// <summary>The corners of <paramref name="cell"/> in work coordinates.</summary>
        public (Vector2 Min, Vector2 Max) BoundsOf(BoardCell cell) =>
            (new Vector2(Min.X + cell.Column * CellWidth, Min.Y + cell.Row * CellHeight),
                new Vector2(Min.X + (cell.Column + 1) * CellWidth, Min.Y + (cell.Row + 1) * CellHeight));

        private static int Index(double value, double min, double size, int count) =>
            Math.Clamp((int)Math.Floor((value - min) / size), 0, count - 1);

        /// <summary>
        /// The pieces of <paramref name="motion"/> that each lie in one cell, in order along it,
        /// as ratios of the whole move. A piece shorter than SectionEdgeToleranceMm joins its
        /// neighbor, so a move that grazes a line or a corner is not cut into slivers.
        /// </summary>
        internal IEnumerable<(double From, double To, BoardCell Cell)> Pieces(Motion motion)
        {
            var reach = motion.ExtremePoints.ToList();
            var crossings = LinesBetween(reach.Min(p => p.X), reach.Max(p => p.X), Min.X, CellWidth, Columns)
                    .SelectMany(motion.RatiosWhereXIs)
                .Concat(LinesBetween(reach.Min(p => p.Y), reach.Max(p => p.Y), Min.Y, CellHeight, Rows)
                    .SelectMany(motion.RatiosWhereYIs))
                .OrderBy(ratio => ratio);

            double shortest = Constants.SectionEdgeToleranceMm / motion.Length;
            var edges = new List<double> { 0 };
            foreach (double ratio in crossings)
            {
                if (ratio - edges[^1] >= shortest && 1 - ratio >= shortest)
                {
                    edges.Add(ratio);
                }
            }
            edges.Add(1);

            (double From, double To, BoardCell Cell)? open = null;
            for (int i = 1; i < edges.Count; i++)
            {
                Vector3 middle = motion.Interpolate((edges[i - 1] + edges[i]) / 2);
                BoardCell cell = CellAt(middle.X, middle.Y);

                if (open is { } piece && piece.Cell == cell)
                {
                    open = (piece.From, edges[i], cell);
                    continue;
                }

                if (open is { } finished)
                {
                    yield return finished;
                }
                open = (edges[i - 1], edges[i], cell);
            }

            yield return open!.Value;
        }

        /// <summary>
        /// The lines between cells that lie from <paramref name="from"/> to <paramref name="to"/>,
        /// the only ones a move spanning that range can cross.
        /// </summary>
        private static IEnumerable<double> LinesBetween(double from, double to, double min, double size, int count)
        {
            int first = Math.Max(1, (int)Math.Ceiling((from - min) / size));
            int last = Math.Min(count - 1, (int)Math.Floor((to - min) / size));
            return Enumerable.Range(first, Math.Max(0, last - first + 1)).Select(line => min + line * size);
        }
    }
}
