#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using coppercli;
using coppercli.Core.GCode;
using coppercli.Core.Util;
using coppercli.WebServer;
using Xunit;

namespace coppercli.Tests
{
    // The web side of choosing sections: what can-start reports, what the picker posts, the
    // picture it draws, and the version check that keeps a start from running a job the
    // operator did not see.
    [Collection(WebServerCollection.Name)]
    public class SectionsEndpointTests : JobFixtureTests
    {
        private const string JsonFieldSections = "sections";
        private const string JsonFieldSectionsText = "sectionsText";
        private const string JsonFieldColumns = "columns";
        private const string JsonFieldRows = "rows";
        private const string JsonFieldChosen = "chosen";
        private const string JsonFieldColumn = "column";
        private const string JsonFieldRow = "row";
        private const string JsonFieldWidth = "width";
        private const string JsonFieldHeight = "height";
        private const string JsonFieldCut = "cut";
        private const string JsonFieldSuccess = "success";
        private const int SectionColumns = 2;
        private const int SectionRows = 2;
        private const int TotalSections = SectionColumns * SectionRows;
        private const int WideBoardHeightCells = SectionsPictureHalf;
        private const int SectionsPictureHalf = WebConstants.SectionsPictureCells / 2;

        public SectionsEndpointTests(WebServerFixture web) : base(web)
        {
        }

        private Task<(HttpStatusCode Code, JsonElement Body)> Get(string path) => _web.GetJsonAsync(path);

        private Task<(HttpStatusCode Code, JsonElement Body)> Post(string path, object body) => _web.PostJsonAsync(path, body);

        private GCodeFile GivenTheBoardIsLoaded(string[]? lines = null) => _boards.Load(lines ?? SectionTestSupport.Board);

        private static object[] Chosen(params (int Column, int Row)[] sections) =>
            sections.Select(s => (object)new Dictionary<string, int> { [JsonFieldColumn] = s.Column, [JsonFieldRow] = s.Row }).ToArray();

        private static Dictionary<string, object> SectionsBody(
            int columns = SectionColumns, int rows = SectionRows, params (int Column, int Row)[] chosen) =>
            new()
            {
                [WebServerFixture.JsonFieldVersion] = AppState.MachineFileVersion,
                [JsonFieldColumns] = columns,
                [JsonFieldRows] = rows,
                [JsonFieldChosen] = Chosen(chosen.Length == 0 ? new[] { (0, 0) } : chosen)
            };

        /// <summary>
        /// Catches can-start reporting a stale or invented choice when none was made.
        /// </summary>
        [Fact]
        public async Task CanStart_ReportsNoSections_AndTheWholeBoardText_WhenNoneAreChosen()
        {
            GivenTheBoardIsLoaded();

            var (code, body) = await Get(WebConstants.ApiMillCanStart);

            Assert.Equal(HttpStatusCode.OK, code);
            Assert.Equal(JsonValueKind.Null, body.GetProperty(JsonFieldSections).ValueKind);
            Assert.Equal(CliConstants.SectionsWholeBoard, body.GetProperty(JsonFieldSectionsText).GetString());
        }

        /// <summary>
        /// Catches can-start dropping the choice the picker must show again, or its words.
        /// </summary>
        [Fact]
        public async Task CanStart_ReportsTheChosenSections_AfterAChoice()
        {
            GivenTheBoardIsLoaded();
            Assert.Null(AppState.ChooseMillSections(SectionColumns, SectionRows,
                new[] { new BoardCell(0, 0), new BoardCell(1, 1) }));

            var (_, body) = await Get(WebConstants.ApiMillCanStart);

            var sections = body.GetProperty(JsonFieldSections);
            Assert.Equal(SectionColumns, sections.GetProperty(JsonFieldColumns).GetInt32());
            Assert.Equal(SectionRows, sections.GetProperty(JsonFieldRows).GetInt32());
            var chosen = sections.GetProperty(JsonFieldChosen).EnumerateArray()
                .Select(c => (c.GetProperty(JsonFieldColumn).GetInt32(), c.GetProperty(JsonFieldRow).GetInt32()))
                .OrderBy(c => c.Item1).ToArray();
            Assert.Equal(new[] { (0, 0), (1, 1) }, chosen);
            Assert.Equal(string.Format(CliConstants.SectionsChosenFormat, 2, TotalSections),
                body.GetProperty(JsonFieldSectionsText).GetString());
        }

        /// <summary>
        /// Catches the POST not reaching AppState, or answering with the old version or
        /// choice.
        /// </summary>
        [Fact]
        public async Task PostingSections_ChoosesThem_AndAnswersWithTheNewVersionAndTheChoice()
        {
            GivenTheBoardIsLoaded();
            long before = AppState.MachineFileVersion;

            var (code, body) = await Post(WebConstants.ApiMillSections,
                SectionsBody(SectionColumns, SectionRows, (1, 0)));

            Assert.Equal(HttpStatusCode.OK, code);
            Assert.True(body.GetProperty(JsonFieldSuccess).GetBoolean());
            long named = body.GetProperty(WebServerFixture.JsonFieldVersion).GetInt64();
            Assert.NotEqual(before, named);
            Assert.Equal(AppState.MachineFileVersion, named);
            Assert.Equal(new[] { new BoardCell(1, 0) }, AppState.MillSections!.Chosen);
            var sections = body.GetProperty(JsonFieldSections);
            Assert.Equal(SectionColumns, sections.GetProperty(JsonFieldColumns).GetInt32());
            Assert.Equal(1, sections.GetProperty(JsonFieldChosen).GetArrayLength());
            Assert.Equal(string.Format(CliConstants.SectionsChosenFormat, 1, TotalSections),
                body.GetProperty(JsonFieldSectionsText).GetString());
        }

        private const int WideColumns = 3;
        private const int WideRows = 2;

        /// <summary>
        /// Catches columns and rows swapped between the request, the choice and the reply. A
        /// square division with a symmetric choice cannot tell; three columns by two rows, with
        /// a section only the third column has, can.
        /// </summary>
        [Fact]
        public async Task PostingSections_OnADivisionWiderThanTall_KeepsColumnsAndRowsApart()
        {
            GivenTheBoardIsLoaded();

            var (code, body) = await Post(WebConstants.ApiMillSections, SectionsBody(WideColumns, WideRows, (2, 1)));

            Assert.Equal(HttpStatusCode.OK, code);
            Assert.Equal(WideColumns, AppState.MillSections!.Division.Columns);
            Assert.Equal(WideRows, AppState.MillSections.Division.Rows);
            Assert.Equal(new[] { new BoardCell(2, 1) }, AppState.MillSections.Chosen);
            var sections = body.GetProperty(JsonFieldSections);
            Assert.Equal(WideColumns, sections.GetProperty(JsonFieldColumns).GetInt32());
            Assert.Equal(WideRows, sections.GetProperty(JsonFieldRows).GetInt32());
            var only = Assert.Single(sections.GetProperty(JsonFieldChosen).EnumerateArray().ToList());
            Assert.Equal(2, only.GetProperty(JsonFieldColumn).GetInt32());
            Assert.Equal(1, only.GetProperty(JsonFieldRow).GetInt32());
        }

        /// <summary>
        /// Catches a choice applied to a job that changed since the window checked it.
        /// </summary>
        [Fact]
        public async Task PostingSections_ForAJobThatChangedSinceTheCheck_IsRefused_AndChangesNothing()
        {
            GivenTheBoardIsLoaded();
            var body = SectionsBody(SectionColumns, SectionRows, (0, 0));
            body[WebServerFixture.JsonFieldVersion] = AppState.MachineFileVersion - 1;
            long version = AppState.MachineFileVersion;

            var (code, answer) = await Post(WebConstants.ApiMillSections, body);

            Assert.Equal(HttpStatusCode.Conflict, code);
            Assert.Equal(WebConstants.ErrorJobChangedSinceChecked, WebServerFixture.Error(answer));
            Assert.Null(AppState.MillSections);
            Assert.Equal(version, AppState.MachineFileVersion);
        }

        public static IEnumerable<object[]> BodiesMissingSomething()
        {
            yield return new object[] { WebServerFixture.JsonFieldVersion };
            yield return new object[] { JsonFieldColumns };
            yield return new object[] { JsonFieldRows };
            yield return new object[] { JsonFieldChosen };
        }

        /// <summary>
        /// Catches a request with a field missing being read as zero or empty and choosing
        /// something the operator never picked.
        /// </summary>
        [Theory]
        [MemberData(nameof(BodiesMissingSomething))]
        public async Task PostingSections_WithAFieldMissing_IsABadRequest_AndChangesNothing(string missing)
        {
            GivenTheBoardIsLoaded();
            long version = AppState.MachineFileVersion;
            var body = SectionsBody(SectionColumns, SectionRows, (0, 0));
            body.Remove(missing);

            var (code, answer) = await Post(WebConstants.ApiMillSections, body);

            Assert.Equal(HttpStatusCode.BadRequest, code);
            Assert.Equal(WebConstants.ErrorInvalidRequest, WebServerFixture.Error(answer));
            Assert.Null(AppState.MillSections);
            Assert.Equal(version, AppState.MachineFileVersion);
        }

        /// <summary>
        /// Catches a chosen entry without a column or a row being read as section 0.
        /// </summary>
        [Theory]
        [InlineData(JsonFieldColumn)]
        [InlineData(JsonFieldRow)]
        public async Task PostingSections_WithAChosenEntryMissingAField_IsABadRequest(string present)
        {
            GivenTheBoardIsLoaded();
            var body = SectionsBody(SectionColumns, SectionRows, (0, 0));
            body[JsonFieldChosen] = new object[] { new Dictionary<string, int> { [present] = 1 } };

            var (code, answer) = await Post(WebConstants.ApiMillSections, body);

            Assert.Equal(HttpStatusCode.BadRequest, code);
            Assert.Equal(WebConstants.ErrorInvalidRequest, WebServerFixture.Error(answer));
            Assert.Null(AppState.MillSections);
        }

        /// <summary>
        /// Catches the Choose refusal not reaching the browser in the terminal's words.
        /// </summary>
        [Fact]
        public async Task PostingSections_WithTooManyColumns_IsRefusedWithTheChoosesText()
        {
            GivenTheBoardIsLoaded();

            var (code, answer) = await Post(WebConstants.ApiMillSections,
                SectionsBody(Constants.MaxSectionsPerAxis + 1, SectionRows, (0, 0)));

            Assert.Equal(HttpStatusCode.Conflict, code);
            Assert.Equal(string.Format(Constants.ErrorSectionCountFormat, Constants.MaxSectionsPerAxis), WebServerFixture.Error(answer));
            Assert.Null(AppState.MillSections);
        }

        /// <summary>
        /// Catches a section off the board being accepted over HTTP.
        /// </summary>
        [Fact]
        public async Task PostingSections_OffTheBoard_IsRefused()
        {
            GivenTheBoardIsLoaded();

            var (code, answer) = await Post(WebConstants.ApiMillSections,
                SectionsBody(SectionColumns, SectionRows, (SectionColumns, 0)));

            Assert.Equal(HttpStatusCode.Conflict, code);
            Assert.Equal(Constants.ErrorSectionOutsideBoard, WebServerFixture.Error(answer));
        }

        /// <summary>
        /// Catches a choice being accepted while a run streams the file.
        /// </summary>
        [Fact]
        public async Task PostingSections_DuringARun_IsRefusedWith409()
        {
            await _web.LoadWithAMapAppliedAsync(_boards.Write(SectionTestSupport.Board));

            await _web.WhileAMillRunHoldsAtTheDoorAsync(async () =>
            {
                long version = AppState.MachineFileVersion;

                var (code, answer) = await Post(WebConstants.ApiMillSections, SectionsBody(SectionColumns, SectionRows, (0, 0)));

                Assert.Equal(HttpStatusCode.Conflict, code);
                Assert.Equal(CliConstants.ErrorFileChangeDuringRun, WebServerFixture.Error(answer));
                Assert.Null(AppState.MillSections);
                Assert.Equal(version, AppState.MachineFileVersion);
            });
        }

        /// <summary>
        /// Catches a picture whose size does not follow the board: the longer side is
        /// SectionsPictureCells and the shorter follows the aspect.
        /// </summary>
        [Fact]
        public async Task ThePicture_IsSectionsPictureCellsLong_AndFollowsTheBoardsAspect()
        {
            GivenTheBoardIsLoaded(SectionTestSupport.WideBoard);

            var (code, wide) = await Get(WebConstants.ApiMillSections);

            Assert.Equal(HttpStatusCode.OK, code);
            Assert.Equal(WebConstants.SectionsPictureCells, wide.GetProperty(JsonFieldWidth).GetInt32());
            Assert.Equal(WideBoardHeightCells, wide.GetProperty(JsonFieldHeight).GetInt32());

            GivenTheBoardIsLoaded(SectionTestSupport.TallBoard);
            var (_, tall) = await Get(WebConstants.ApiMillSections);

            Assert.Equal(SectionsPictureHalf, tall.GetProperty(JsonFieldWidth).GetInt32());
            Assert.Equal(WebConstants.SectionsPictureCells, tall.GetProperty(JsonFieldHeight).GetInt32());
        }

        /// <summary>
        /// Catches cells outside the grid, the origin at the wrong corner, or cells for
        /// copper-free space: the trace is an outline, so its corners are cut and its
        /// middle is not.
        /// </summary>
        [Fact]
        public async Task ThePicture_ListsTheCellsTheBoardCuts_WithinRange_RowZeroAtTheBottom()
        {
            GivenTheBoardIsLoaded(SectionTestSupport.WideBoard);

            var (_, picture) = await Get(WebConstants.ApiMillSections);

            int width = picture.GetProperty(JsonFieldWidth).GetInt32();
            int height = picture.GetProperty(JsonFieldHeight).GetInt32();
            var cut = CutCells(picture);
            Assert.NotEmpty(cut);
            Assert.All(cut, cell =>
            {
                Assert.InRange(cell.Column, 0, width - 1);
                Assert.InRange(cell.Row, 0, height - 1);
            });

            Assert.Contains(new BoardCell(0, 0), cut);
            Assert.Contains(new BoardCell(width - 1, height - 1), cut);
            Assert.DoesNotContain(new BoardCell(width / 2, height / 2), cut);
        }

        /// <summary>A trace along the bottom edge and up the right edge only, so the top-left corner is not cut.</summary>
        private static readonly string[] BottomAndRightEdges =
        {
            "G21", "G90", "G0 Z10", "G0 X0 Y0", "G0 Z1", "G0 X2 Y2", "G1 Z-0.1 F200",
            "G1 X18 Y2 F600", "G1 X18 Y10", "G0 Z1", "G0 Z10", "M2"
        };

        /// <summary>
        /// Catches the picture sent upside down or mirrored. The outline board is symmetric, so
        /// it cannot tell; this board is cut only along its bottom and right edges.
        /// </summary>
        [Fact]
        public async Task ThePicture_PutsRowZeroAtTheBottom_OnABoardThatIsNotSymmetric()
        {
            GivenTheBoardIsLoaded(BottomAndRightEdges);

            var (_, picture) = await Get(WebConstants.ApiMillSections);

            int width = picture.GetProperty(JsonFieldWidth).GetInt32();
            int height = picture.GetProperty(JsonFieldHeight).GetInt32();
            var cut = CutCells(picture);
            Assert.Contains(new BoardCell(0, 0), cut);
            Assert.Contains(new BoardCell(width - 1, height - 1), cut);
            Assert.DoesNotContain(new BoardCell(0, height - 1), cut);
        }

        private static HashSet<BoardCell> CutCells(JsonElement picture) =>
            picture.GetProperty(JsonFieldCut).EnumerateArray()
                .Select(c => new BoardCell(c.GetProperty(JsonFieldColumn).GetInt32(), c.GetProperty(JsonFieldRow).GetInt32()))
                .ToHashSet();

        /// <summary>
        /// Catches a picture of nothing being served when no file is loaded.
        /// </summary>
        [Fact]
        public async Task ThePicture_WithNoFileLoaded_Is409WithAnError()
        {
            GivenTheBoardIsLoaded();
            AppState.UnloadFileForTest();

            var (code, body) = await Get(WebConstants.ApiMillSections);

            Assert.Equal(HttpStatusCode.Conflict, code);
            Assert.False(string.IsNullOrEmpty(WebServerFixture.Error(body)));
        }

        /// <summary>
        /// Catches a start confirmed before a sections choice running the new choice.
        /// </summary>
        [Fact]
        public async Task AStartAfterTheSectionsChanged_IsRefused_AndNoRunStarts()
        {
            GivenTheBoardIsLoaded();
            var checkedBody = await _web.MillStartBodyAsync();
            Assert.Null(AppState.ChooseMillSections(SectionColumns, SectionRows, new[] { new BoardCell(0, 0) }));

            var (code, answer) = await Post(WebConstants.ApiMillStart, checkedBody);

            Assert.Equal(HttpStatusCode.Conflict, code);
            Assert.Equal(WebConstants.ErrorJobChangedSinceChecked, WebServerFixture.Error(answer));
            Assert.False(AppState.Milling.IsRunInProgress);
        }
    }
}
