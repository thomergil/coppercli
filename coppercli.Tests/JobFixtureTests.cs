#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using coppercli;
using coppercli.Core.Controllers;
using coppercli.WebServer;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// A test of the job the web server's machine holds: the shared fixture restored before
    /// each test, boards written for it and deleted after, and the probe data it leaves cleared.
    /// </summary>
    public abstract class JobFixtureTests : IDisposable
    {
        private protected readonly WebServerFixture _web;
        private protected readonly TestBoards _boards = new();

        private protected JobFixtureTests(WebServerFixture web)
        {
            _web = web;
            _web.RestoreFixtureState();
        }

        public void Dispose()
        {
            AppState.DiscardProbeData();
            Persistence.ClearProbeAutoSave();
            _boards.Dispose();
        }

        /// <summary>The lines the machine holds to stream.</summary>
        private protected static string[] OnTheMachine() => AppState.Machine.File.ToArray();

        private const int RunTimeoutMs = 60_000;
        private const int PromptPollMs = 20;

        /// <summary>
        /// Answers each prompt the milling run started over the web raises with what
        /// <paramref name="answer"/> gives for it, until the run ends; then stops the run, so a
        /// failed assertion still leaves the machine free for the next test.
        /// </summary>
        /// <param name="whenEnded">Runs once the run has ended, before the stop releases it.</param>
        /// <returns>The state the run ended in.</returns>
        private protected async Task<ControllerState> AnswerPromptsUntilTheRunEndsAsync(
            Func<UserInputRequest, Task<string>> answer, Func<Task>? whenEnded = null)
        {
            try
            {
                long deadline = Environment.TickCount64 + RunTimeoutMs;
                UserInputRequest? answered = null;
                while (!AppState.Milling.HasFinished)
                {
                    Assert.True(Environment.TickCount64 < deadline, "the run did not finish");
                    var prompt = PendingPrompt.Current;
                    if (prompt != null && prompt != answered)
                    {
                        var (code, _) = await _web.PostJsonAsync(WebConstants.ApiMillToolChangeUserInput,
                            new { id = prompt.Id, response = await answer(prompt) });
                        Assert.Equal(HttpStatusCode.OK, code);
                        answered = prompt;
                    }
                    await Task.Delay(PromptPollMs);
                }

                var ended = AppState.Milling.State;
                if (whenEnded != null)
                {
                    await whenEnded();
                }
                return ended;
            }
            finally
            {
                await _web.PostJsonAsync(WebConstants.ApiMillStop);
                WebServerFixture.WaitUntil(() => !AppState.Milling.IsRunInProgress, "the run to end");
            }
        }
    }
}
