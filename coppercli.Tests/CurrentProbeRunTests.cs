#nullable enable
using System;
using System.Linq;
using System.Threading;
using coppercli.WebServer;
using Xunit;

namespace coppercli.Tests
{
    // Concurrent requests can reach the same probe controller, so only one may store its token;
    // otherwise both subscribe handlers, and cleanup from one can release the other's run.
    // This class shares the web collection because the current probe token is process-wide.
    [Collection(WebServerCollection.Name)]
    public class CurrentProbeRunTests
    {
        /// <summary>Repeat enough times to exercise concurrent token assignments.</summary>
        private const int Rounds = 2000;

        /// <summary>Use more threads than cores to increase interleaving.</summary>
        private const int Racers = 8;

        [Fact]
        public void ConcurrentProbeStarts_SetExactlyOneCurrentToken()
        {
            try
            {
                for (int round = 0; round < Rounds; round++)
                {
                    CncWebServer.ClearCurrentProbeRunForTest();

                    int winners = 0;
                    using var gate = new Barrier(Racers);

                    var racers = Enumerable.Range(0, Racers).Select(_ => new Thread(() =>
                    {
                        var cts = new CancellationTokenSource();
                        gate.SignalAndWait();

                        if (CncWebServer.TrySetCurrentProbeRun(cts))
                        {
                            Interlocked.Increment(ref winners);
                        }
                        else
                        {
                            cts.Dispose();
                        }
                    })).ToArray();

                    foreach (var racer in racers) { racer.Start(); }
                    foreach (var racer in racers) { racer.Join(); }

                    Assert.Equal(1, winners);
                }
            }
            finally
            {
                CncWebServer.ClearCurrentProbeRunForTest();
            }
        }

        /// <summary>
        /// Cleanup from a completed run must not clear the token of a newer run. The stop
        /// endpoint uses that token to find the run it must stop.
        /// </summary>
        [Fact]
        public void CompletedRunCleanup_DoesNotClearNewerProbeRun()
        {
            CncWebServer.ClearCurrentProbeRunForTest();

            try
            {
                using var first = new CancellationTokenSource();
                using var second = new CancellationTokenSource();
                using var third = new CancellationTokenSource();

                Assert.True(CncWebServer.TrySetCurrentProbeRun(first));

                // The first run ends before the second stores its token.
                Assert.True(CncWebServer.ClearCurrentProbeRun(first));
                Assert.True(CncWebServer.TrySetCurrentProbeRun(second));

                // The first run's cleanup arrives after the second starts.
                Assert.False(
                    CncWebServer.ClearCurrentProbeRun(first),
                    "cleanup cleared the token of a newer probe run");

                Assert.False(
                    CncWebServer.TrySetCurrentProbeRun(third),
                    "a third probe run started while the second was active");
            }
            finally
            {
                CncWebServer.ClearCurrentProbeRunForTest();
            }
        }
    }
}
