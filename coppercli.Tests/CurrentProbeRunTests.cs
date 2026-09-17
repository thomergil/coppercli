#nullable enable
using System;
using System.Linq;
using System.Threading;
using coppercli.WebServer;
using Xunit;

namespace coppercli.Tests
{
    // Requests are served concurrently, so two probe starts can arrive together. Both then
    // subscribe their handlers to one controller, and the cleanup of the request that did not
    // start the run releases the run that did, which leaves the stop endpoint unable to find
    // it.
    //
    // In the web collection because the slot is process-wide: run beside a test that starts a
    // real probe, both would claim the same slot.
    [Collection(WebServerCollection.Name)]
    public class CurrentProbeRunTests
    {
        /// <summary>Enough rounds to hit a window a few instructions wide.</summary>
        private const int Rounds = 2000;

        /// <summary>More threads than a machine has cores, so they interleave.</summary>
        private const int Racers = 8;

        [Fact]
        public void RacingProbeStarts_GiveTheMachineToExactlyOne()
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

                        if (CncWebServer.TryClaimProbeRun(cts))
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
        /// The other half of the slot: a run that outlives the next one's start must release
        /// nothing. The stop endpoint finds the machine by this slot, so a late release would
        /// free the machine while the new run is still using it.
        /// </summary>
        [Fact]
        public void ALateReleaseFromAFinishedRun_LeavesTheNewRunOwningTheMachine()
        {
            CncWebServer.ClearCurrentProbeRunForTest();

            try
            {
                using var first = new CancellationTokenSource();
                using var second = new CancellationTokenSource();
                using var third = new CancellationTokenSource();

                Assert.True(CncWebServer.TryClaimProbeRun(first));

                // first's run ends and the next one takes the machine.
                Assert.True(CncWebServer.ClearCurrentProbeRun(first));
                Assert.True(CncWebServer.TryClaimProbeRun(second));

                // first's finally, arriving late.
                Assert.False(
                    CncWebServer.ClearCurrentProbeRun(first),
                    "a finished run released a slot a newer run owns");

                Assert.False(
                    CncWebServer.TryClaimProbeRun(third),
                    "the machine was handed away from the run that owns it");
            }
            finally
            {
                CncWebServer.ClearCurrentProbeRunForTest();
            }
        }
    }
}
