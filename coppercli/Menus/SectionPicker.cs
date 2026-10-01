using coppercli.Core.GCode;
using coppercli.Helpers;
using static coppercli.CliConstants;
using static coppercli.Core.Util.Constants;
using static coppercli.Helpers.DisplayHelpers;

namespace coppercli.Menus
{
    /// <summary>
    /// The screen where the operator divides the board into sections and chooses the ones a run
    /// mills. First the arrows add and remove lines; Enter then switches to choosing, where the
    /// arrows move between sections and Space chooses or clears one, and Esc goes back to
    /// dividing. Enter again hands the choice to AppState.ChooseMillSections.
    /// </summary>
    internal static class SectionPicker
    {
        /// <summary>
        /// The cells the loaded file cuts through in the phases a run mills, drawn at the size the
        /// screen last had. Kept because finding the cells walks every move in the file.
        /// </summary>
        private static (GCodeFile File, ChosenPhases? Phases, int Wide, int Tall, IReadOnlySet<BoardCell> Cut)? _picture;

        public static void Show()
        {
            if (AppState.CurrentFile is not GCodeFile file)
            {
                return;
            }

            var (_, refused) = BoardDivision.Of(file, 1, 1);
            if (refused != null)
            {
                ShowWarningOverlay(refused);
                return;
            }

            var current = AppState.MillSections;
            int columns = current?.Division.Columns ?? 1;
            int rows = current?.Division.Rows ?? 1;
            var chosen = current?.Chosen.ToHashSet() ?? new HashSet<BoardCell>();
            BoardCell? cursor = null;

            Console.Clear();

            while (true)
            {
                Draw(file, columns, rows, chosen, cursor);

                if (InputHelpers.ReadKeyPolling() is not ConsoleKeyInfo key)
                {
                    continue;
                }

                if (cursor is not BoardCell at)
                {
                    int newColumns = Step(columns, key, ConsoleKey.RightArrow, ConsoleKey.LeftArrow, 1, MaxSectionsPerAxis);
                    int newRows = Step(rows, key, ConsoleKey.UpArrow, ConsoleKey.DownArrow, 1, MaxSectionsPerAxis);
                    if (newColumns != columns || newRows != rows)
                    {
                        columns = newColumns;
                        rows = newRows;
                        chosen.Clear();
                        Console.Clear();
                    }
                    else if (InputHelpers.IsEnterKey(key))
                    {
                        cursor = new BoardCell(0, rows - 1);
                    }
                    else if (InputHelpers.IsExitKey(key))
                    {
                        return;
                    }
                    continue;
                }

                if (InputHelpers.IsKey(key, ConsoleKey.Spacebar))
                {
                    if (!chosen.Remove(at))
                    {
                        chosen.Add(at);
                    }
                }
                else if (InputHelpers.IsEnterKey(key))
                {
                    string? notChosen = AppState.ChooseMillSections(columns, rows, chosen);
                    if (notChosen == null)
                    {
                        return;
                    }
                    ShowWarningOverlay(notChosen);
                    Console.Clear();
                }
                else if (InputHelpers.IsExitKey(key))
                {
                    cursor = null;
                }
                else
                {
                    cursor = new BoardCell(
                        Step(at.Column, key, ConsoleKey.RightArrow, ConsoleKey.LeftArrow, 0, columns - 1),
                        Step(at.Row, key, ConsoleKey.UpArrow, ConsoleKey.DownArrow, 0, rows - 1));
                }
            }
        }

        private static int Step(int value, ConsoleKeyInfo key, ConsoleKey up, ConsoleKey down, int lowest, int highest) =>
            InputHelpers.IsKey(key, up) ? Math.Min(value + 1, highest)
            : InputHelpers.IsKey(key, down) ? Math.Max(value - 1, lowest)
            : value;

        /// <param name="cursor">The section the arrows are on while choosing, or null while dividing.</param>
        private static void Draw(GCodeFile file, int columns, int rows, IReadOnlySet<BoardCell> chosen, BoardCell? cursor)
        {
            Console.SetCursorPosition(0, 0);
            var (winWidth, winHeight) = GetSafeWindowSize();

            string header = $"{AnsiPrompt}{SectionsTitle}{AnsiReset}";
            WriteLineTruncated(CenteredHeader(header, winWidth), winWidth);
            WriteLineTruncated("", winWidth);
            WriteLineTruncated("  " + string.Format(SectionsDivisionFormat, columns, rows,
                GetSectionsText(chosen.Count, columns * rows)), winWidth);
            WriteLineTruncated($"  {AnsiInfo}{(cursor == null ? SectionsDivideHint : SectionsChooseHint)}{AnsiReset}", winWidth);
            WriteLineTruncated("", winWidth);

            // The picture cells that fit inside the borders, one border character between
            // sections and at each side. A cell is MillGridCharsPerCell characters wide, so it
            // looks about square on screen.
            int fitColumns = (winWidth - MillGridHorizontalPadding - columns - 1) / MillGridCharsPerCell;
            int fitRows = winHeight - SectionPickerTextLines - rows - 1;
            if (fitColumns < columns || fitRows < rows)
            {
                WriteLineTruncated($"  {WindowTooSmallForBoard}", winWidth);
                return;
            }

            var (fitting, _) = BoardDivision.Fitting(file, fitColumns, fitRows);
            int perColumn = Math.Max(1, fitting!.Columns / columns);
            int perRow = Math.Max(1, fitting.Rows / rows);
            var cut = CellsCut(file, columns * perColumn, rows * perRow);

            int sectionWidth = perColumn * MillGridCharsPerCell;
            int innerWidth = columns * (sectionWidth + 1) - 1;
            string pad = new string(' ', Math.Max(0, (winWidth - innerWidth - MillBorderPadding) / 2));
            string Border(char left, char joint, char right) =>
                pad + BoxDivider(left, right, innerWidth, '─',
                    Enumerable.Range(1, columns - 1).Select(c => (c * (sectionWidth + 1) - 1, joint)).ToArray());

            WriteLineTruncated(Border('┌', '┬', '┐'), winWidth);
            for (int row = rows - 1; row >= 0; row--)
            {
                for (int line = perRow - 1; line >= 0; line--)
                {
                    var text = new System.Text.StringBuilder(pad).Append('│');
                    for (int column = 0; column < columns; column++)
                    {
                        var section = new BoardCell(column, row);
                        text.Append(SectionColor(chosen.Contains(section), section == cursor));
                        for (int cell = 0; cell < perColumn; cell++)
                        {
                            bool isCut = cut.Contains(new BoardCell(column * perColumn + cell, row * perRow + line));
                            text.Append(isCut ? MillVisitedMarker : MillEmptyMarker);
                        }
                        text.Append(AnsiReset).Append('│');
                    }
                    WriteLineTruncated(text.ToString(), winWidth);
                }

                if (row > 0)
                {
                    WriteLineTruncated(Border('├', '┼', '┤'), winWidth);
                }
            }
            WriteLineTruncated(Border('└', '┴', '┘'), winWidth);

            var (min, max) = file.CuttingBounds;
            WriteLineTruncated($"{pad}  " + string.Format(BoardBoundsFormat, min.X, max.X, min.Y, max.Y), winWidth);
        }

        private static string SectionColor(bool isChosen, bool underCursor) =>
            (isChosen, underCursor) switch
            {
                (true, true) => AnsiSuccessBold,
                (true, false) => AnsiSuccess,
                (false, true) => AnsiWarning,
                _ => AnsiDim
            };

        /// <summary>The cells of a <paramref name="wide"/> by <paramref name="tall"/> grid over the board that a cut passes through.</summary>
        private static IReadOnlySet<BoardCell> CellsCut(GCodeFile file, int wide, int tall)
        {
            var phases = AppState.MillPhases;
            if (_picture is { } picture && picture.File == file && picture.Phases == phases
                && picture.Wide == wide && picture.Tall == tall)
            {
                return picture.Cut;
            }

            var (division, _) = BoardDivision.Of(file, wide, tall);
            var cut = division == null ? new HashSet<BoardCell>() : AppState.CellsCut(division);
            _picture = (file, phases, wide, tall, cut);
            return cut;
        }
    }
}
