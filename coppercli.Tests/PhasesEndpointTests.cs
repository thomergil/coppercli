#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mime;
using System.Text;

using System.Threading.Tasks;
using coppercli;
using coppercli.Core.Util;
using coppercli.Helpers;
using coppercli.WebServer;
using Xunit;

namespace coppercli.Tests
{
    // The web side of choosing phases: what can-start reports, what the pre-mill window posts,
    // and the version check that keeps a start from running a job the operator did not see.
    [Collection(WebServerCollection.Name)]
    public class PhasesEndpointTests : JobFixtureTests
    {
        private const string JsonFieldOfferPhases = "offerPhases";
        private const string JsonFieldJobPhases = "jobPhases";
        private const string JsonFieldNumber = "number";
        private const string JsonFieldLabel = "label";
        private const string JsonFieldChosen = "chosen";
        private const string JsonFieldCut = "cut";

        public PhasesEndpointTests(WebServerFixture web) : base(web)
        {
        }

        private static Dictionary<string, object> PhasesBody(params int[] chosen) => new()
        {
            [WebServerFixture.JsonFieldVersion] = AppState.MachineFileVersion,
            [JsonFieldChosen] = chosen
        };

        /// <summary>
        /// Catches can-start offering a choice for a job with one phase, sending no phases, wrong
        /// labels, or every phase as chosen after a choice.
        /// </summary>
        [Fact]
        public async Task CanStart_ListsEachPhase_WithItsLabelAndWhetherItRuns()
        {
            var loaded = _boards.Load(PhaseTestSupport.TwoPhaseBoard);
            Assert.Null(AppState.ChooseMillPhases(new[] { 2 }));

            var (_, body) = await _web.GetJsonAsync(WebConstants.ApiMillCanStart);

            Assert.True(body.GetProperty(JsonFieldOfferPhases).GetBoolean());
            var listed = body.GetProperty(JsonFieldJobPhases).EnumerateArray()
                .Select(p => (p.GetProperty(JsonFieldNumber).GetInt32(), p.GetProperty(JsonFieldLabel).GetString(),
                    p.GetProperty(JsonFieldChosen).GetBoolean()))
                .ToList();
            Assert.Equal(new (int, string?, bool)[]
            {
                (1, DisplayHelpers.GetPhaseLabel(loaded.Phases[0]), false),
                (2, DisplayHelpers.GetPhaseLabel(loaded.Phases[1]), true)
            }, listed);
        }

        /// <summary>Catches a job with one phase offering a choice the operator cannot use.</summary>
        [Fact]
        public async Task CanStart_OffersNoChoice_ForAJobWithOnePhase()
        {
            _boards.Load(SectionTestSupport.Board);

            var (_, body) = await _web.GetJsonAsync(WebConstants.ApiMillCanStart);

            Assert.False(body.GetProperty(JsonFieldOfferPhases).GetBoolean());
        }

        /// <summary>Catches the POST not reaching AppState, or answering with the old version or choice.</summary>
        [Fact]
        public async Task PostingPhases_ChoosesThem_AndAnswersWithTheNewVersionAndTheList()
        {
            _boards.Load(PhaseTestSupport.TwoPhaseBoard);
            long before = AppState.MachineFileVersion;

            var (code, body) = await _web.PostJsonAsync(WebConstants.ApiMillPhases, PhasesBody(2));

            Assert.Equal(HttpStatusCode.OK, code);
            long named = body.GetProperty(WebServerFixture.JsonFieldVersion).GetInt64();
            Assert.NotEqual(before, named);
            Assert.Equal(AppState.MachineFileVersion, named);
            Assert.Equal(new[] { 2 }, AppState.MillPhases!.Numbers);
            Assert.Equal(new[] { false, true },
                body.GetProperty(JsonFieldJobPhases).EnumerateArray().Select(p => p.GetProperty(JsonFieldChosen).GetBoolean()));
        }

        /// <summary>Catches a choice applied to a job that changed since the window checked it.</summary>
        [Fact]
        public async Task PostingPhases_ForAJobThatChangedSinceTheCheck_IsRefused()
        {
            _boards.Load(PhaseTestSupport.TwoPhaseBoard);
            var body = PhasesBody(2);
            body[WebServerFixture.JsonFieldVersion] = AppState.MachineFileVersion - 1;

            var (code, answer) = await _web.PostJsonAsync(WebConstants.ApiMillPhases, body);

            Assert.Equal(HttpStatusCode.Conflict, code);
            Assert.Equal(WebConstants.ErrorJobChangedSinceChecked, WebServerFixture.Error(answer));
            Assert.Null(AppState.MillPhases);
        }

        /// <summary>Catches a request missing a field being read as an empty or zero choice.</summary>
        [Theory]
        [InlineData(WebServerFixture.JsonFieldVersion)]
        [InlineData(JsonFieldChosen)]
        public async Task PostingPhases_WithAFieldMissing_IsABadRequest(string missing)
        {
            _boards.Load(PhaseTestSupport.TwoPhaseBoard);
            var body = PhasesBody(2);
            body.Remove(missing);

            var (code, answer) = await _web.PostJsonAsync(WebConstants.ApiMillPhases, body);

            Assert.Equal(HttpStatusCode.BadRequest, code);
            Assert.Equal(WebConstants.ErrorInvalidRequest, WebServerFixture.Error(answer));
            Assert.Null(AppState.MillPhases);
        }

        /// <summary>Catches a body that is not JSON being read as an empty choice.</summary>
        [Fact]
        public async Task PostingPhases_WithABodyThatIsNotJson_IsABadRequest()
        {
            _boards.Load(PhaseTestSupport.TwoPhaseBoard);

            var response = await _web.Client.PostAsync(WebConstants.ApiMillPhases,
                new StringContent(NotJson, Encoding.UTF8, MediaTypeNames.Application.Json));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Null(AppState.MillPhases);
        }

        private const string NotJson = "{chosen: [2";

        /// <summary>Catches a phase the file does not have reaching AppState over HTTP.</summary>
        [Fact]
        public async Task PostingAPhaseNotInTheFile_IsRefusedWithTheChoosesText()
        {
            _boards.Load(PhaseTestSupport.TwoPhaseBoard);

            var (code, answer) = await _web.PostJsonAsync(WebConstants.ApiMillPhases, PhasesBody(3));

            Assert.Equal(HttpStatusCode.Conflict, code);
            Assert.Equal(Constants.ErrorPhaseNotInFile, WebServerFixture.Error(answer));
        }

        /// <summary>Catches the refusal of no phases not reaching the browser in the terminal's words.</summary>
        [Fact]
        public async Task PostingNoPhases_IsRefusedWithTheChoosesText()
        {
            _boards.Load(PhaseTestSupport.TwoPhaseBoard);

            var (code, answer) = await _web.PostJsonAsync(WebConstants.ApiMillPhases, PhasesBody());

            Assert.Equal(HttpStatusCode.Conflict, code);
            Assert.Equal(Constants.ErrorNoPhaseChosen, WebServerFixture.Error(answer));
        }

        /// <summary>Catches a choice being accepted while a run streams the file.</summary>
        [Fact]
        public async Task PostingPhases_DuringARun_IsRefusedWith409()
        {
            await _web.LoadWithAMapAppliedAsync(_boards.Write(PhaseTestSupport.TwoPhaseBoard));

            await _web.WhileAMillRunHoldsAtTheDoorAsync(async () =>
            {
                long version = AppState.MachineFileVersion;

                var (code, answer) = await _web.PostJsonAsync(WebConstants.ApiMillPhases, PhasesBody(2));

                Assert.Equal(HttpStatusCode.Conflict, code);
                Assert.Equal(CliConstants.ErrorFileChangeDuringRun, WebServerFixture.Error(answer));
                Assert.Null(AppState.MillPhases);
                Assert.Equal(version, AppState.MachineFileVersion);
            });
        }

        /// <summary>Catches the browser's sections picker drawing cuts of a phase the run skips.</summary>
        [Fact]
        public async Task TheSectionsPicture_ShowsOnlyTheChosenPhasesCuts()
        {
            _boards.Load(PhaseTestSupport.TwoPhaseBoard);
            var (_, whole) = await _web.GetJsonAsync(WebConstants.ApiMillSections);
            Assert.Null(AppState.ChooseMillPhases(new[] { 2 }));

            var (code, second) = await _web.GetJsonAsync(WebConstants.ApiMillSections);

            Assert.Equal(HttpStatusCode.OK, code);
            Assert.InRange(second.GetProperty(JsonFieldCut).GetArrayLength(), 1, whole.GetProperty(JsonFieldCut).GetArrayLength() - 1);
        }
    }
}
