#nullable enable
using System;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using coppercli;
using coppercli.Core.Controllers;
using coppercli.Helpers;
using coppercli.Tests.Fakes;
using coppercli.WebServer;
using Xunit;

namespace coppercli.Tests
{
    // The milling run's estimate as the browser gets it, from a run on the real Machine.
    [Collection(WebServerCollection.Name)]
    public class MillEstimateEndpointTests : JobFixtureTests
    {
        private const string JsonFieldFile = "file";
        private const string JsonFieldProgress = "progress";
        private const string JsonFieldTimeLeft = "timeLeft";

        public MillEstimateEndpointTests(WebServerFixture web) : base(web)
        {
        }

        /// <summary>
        /// Catches GRBL's top speeds not read at the start of a run, or the browser sent
        /// something other than the run's own estimate: at the tool change the status carries
        /// its progress and time left.
        /// </summary>
        [Fact]
        public async Task ARun_ReadsGrblsTopSpeeds_AndTheStatusCarriesItsEstimate()
        {
            await _web.LoadWithAMapAppliedAsync(_boards.Write(PhaseTestSupport.TwoPhaseBoard));
            var (code, _) = await _web.PostJsonAsync(WebConstants.ApiMillStart, await _web.MillStartBodyAsync());
            Assert.Equal(HttpStatusCode.OK, code);

            JobEstimate? estimateThen = null;
            JsonElement statusThen = default;
            await AnswerPromptsUntilTheRunEndsAsync(async prompt =>
            {
                if (estimateThen == null)
                {
                    estimateThen = AppState.MillEstimate;
                    statusThen = (await _web.GetJsonAsync(WebConstants.ApiStatus)).Body.GetProperty(JsonFieldFile);
                }
                return ControllerConstants.OptionContinue;
            });

            Assert.Equal(FakeGrbl.TopSpeeds, AppState.Machine.TopSpeeds);
            Assert.NotNull(estimateThen);
            Assert.True(estimateThen!.FractionDone is > 0 and < 1, $"progress at the tool change was {estimateThen.FractionDone}");
            Assert.Equal(estimateThen.FractionDone, statusThen.GetProperty(JsonFieldProgress).GetDouble());
            Assert.Equal(DisplayHelpers.FormatTimeSpan(estimateThen.TimeLeft), statusThen.GetProperty(JsonFieldTimeLeft).GetString());
        }

        /// <summary>
        /// Catches the time left wrapping at a day, as a long job at a low feed override would:
        /// both screens write it this way.
        /// </summary>
        [Fact]
        public void TheTimeLeft_CountsHoursPastADay()
        {
            Assert.Equal("25:30:05", DisplayHelpers.FormatTimeSpan(new TimeSpan(25, 30, 5)));
        }

        /// <summary>Catches a finished run's estimate sent between runs, under a file it may not describe.</summary>
        [Fact]
        public async Task WithNoRunInProgress_TheStatusCarriesNoEstimate()
        {
            await _web.LoadWithAMapAppliedAsync(_boards.Write(SectionTestSupport.Board));
            var (code, _) = await _web.PostJsonAsync(WebConstants.ApiMillStart, await _web.MillStartBodyAsync());
            Assert.Equal(HttpStatusCode.OK, code);

            JsonElement file = default;
            var ended = await AnswerPromptsUntilTheRunEndsAsync(
                _ => Task.FromResult(ControllerConstants.OptionContinue),
                async () => file = (await _web.GetJsonAsync(WebConstants.ApiStatus)).Body.GetProperty(JsonFieldFile));

            Assert.Equal(ControllerState.Completed, ended);
            Assert.Equal(0, file.GetProperty(JsonFieldProgress).GetDouble());
            Assert.Equal(JsonValueKind.Null, file.GetProperty(JsonFieldTimeLeft).ValueKind);
        }
    }
}
