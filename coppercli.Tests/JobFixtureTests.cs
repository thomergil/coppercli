#nullable enable
using System;
using System.Linq;
using coppercli;
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
    }
}
