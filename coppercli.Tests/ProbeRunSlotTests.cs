#nullable enable
using System;
using System.Linq;
using System.Threading;
using coppercli.WebServer;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// Requests are served concurrently, so two probe starts can arrive together. Both would
    /// subscribe their handlers to one controller, and the cleanup of the request that did
    /// not start the run would release the run that did, leaving the stop endpoint unable to
    /// find it. The window is a few instructions wide, so this races it.
    ///
    /// In the web collection because the slot is process-wide: run beside a test that starts
    /// a real probe, both would be claiming the same one.
    /// </summary>
    [Collection(WebServerCollection.Name)]
    public class ProbeRunSlotTests
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
                    CncWebServer.ReleaseProbeRunSlotForTest();

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
                CncWebServer.ReleaseProbeRunSlotForTest();
            }
        }

        /// <summary>
        /// The other half of the slot: a run that outlives the next one's start must release
        /// nothing. The stop endpoint finds the machine by this slot, so a late release would
        /// take it from the run that owns it.
        /// </summary>
        [Fact]
        public void ALateReleaseFromAFinishedRun_LeavesTheNewRunOwningTheMachine()
        {
            CncWebServer.ReleaseProbeRunSlotForTest();

            try
            {
                using var first = new CancellationTokenSource();
                using var second = new CancellationTokenSource();
                using var third = new CancellationTokenSource();

                Assert.True(CncWebServer.TryClaimProbeRun(first));

                // first's run ends and the next one takes the machine.
                Assert.True(CncWebServer.ReleaseProbeRunSlot(first));
                Assert.True(CncWebServer.TryClaimProbeRun(second));

                // first's finally, arriving late.
                Assert.False(
                    CncWebServer.ReleaseProbeRunSlot(first),
                    "a finished run released a slot a newer run owns");

                Assert.False(
                    CncWebServer.TryClaimProbeRun(third),
                    "the machine was handed away from the run that owns it");
            }
            finally
            {
                CncWebServer.ReleaseProbeRunSlotForTest();
            }
        }
    }
}
