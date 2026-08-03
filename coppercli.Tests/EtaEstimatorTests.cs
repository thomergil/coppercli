using System;
using coppercli.Core.Controllers;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// The ETA must start from an up-front guess, stay near it early, and ease toward the
    /// measured pace - not swing wildly the way the old elapsed / lines-done estimate did.
    /// </summary>
    public class EtaEstimatorTests
    {
        [Fact]
        public void BeforeAnyProgress_ReportsTheModelGuess()
        {
            var eta = new EtaEstimator(TimeSpan.FromMinutes(10), totalLines: 1000);

            // No lines done, no time elapsed: the whole model estimate remains.
            var remaining = eta.Update(linesCompleted: 0, elapsed: TimeSpan.Zero);

            Assert.Equal(TimeSpan.FromMinutes(10), remaining);
        }

        [Fact]
        public void WithNoModelAndNoProgress_ReportsNothing()
        {
            var eta = new EtaEstimator(TimeSpan.Zero, totalLines: 1000);

            Assert.Null(eta.Update(linesCompleted: 0, elapsed: TimeSpan.Zero));
        }

        /// <summary>
        /// The first real measurement, if it disagrees with the guess, moves the estimate
        /// only a little - it does not jump straight to the measured projection.
        /// </summary>
        [Fact]
        public void EarlyMeasurement_NudgesRatherThanJumps()
        {
            // Guess: 10 min total over 1000 lines. Reality so far: running at half pace
            // (100 lines took 2 min, which projects to 20 min total).
            var eta = new EtaEstimator(TimeSpan.FromMinutes(10), totalLines: 1000);

            var remaining = eta.Update(linesCompleted: 100, elapsed: TimeSpan.FromMinutes(2));

            // A raw elapsed/lines estimate would project 20 min total -> 18 min remaining.
            // The smoothed estimate must sit far closer to the guess than to that.
            double minutes = remaining!.Value.TotalMinutes;
            Assert.InRange(minutes, 8.0, 11.0);
        }

        /// <summary>
        /// The guess dominates early and the measured pace dominates late, so the total-
        /// duration estimate moves monotonically from the guess toward the truth and
        /// lands on it by the end.
        /// </summary>
        [Fact]
        public void EstimateShiftsFromGuessTowardTruthAsTheJobProgresses()
        {
            // Model guessed 150 s; the job actually runs at 120 s (a realistic ~25% miss).
            const double trueTotal = 120.0;
            var eta = new EtaEstimator(TimeSpan.FromSeconds(150), totalLines: 1000);

            double EstimatedTotalAt(int line)
            {
                var elapsed = TimeSpan.FromSeconds(line * trueTotal / 1000.0);
                var remaining = eta.Update(line, elapsed);
                return remaining!.Value.TotalSeconds + elapsed.TotalSeconds;   // reconstruct the total
            }

            double early = EstimatedTotalAt(50);    // 5% in
            double mid = EstimatedTotalAt(500);      // halfway
            double late = EstimatedTotalAt(990);     // nearly done

            // Early: close to the 150 s guess. Late: close to the 120 s truth.
            Assert.InRange(early, 147.0, 150.0);
            Assert.InRange(late, 120.0, 122.0);

            // Monotonic march from guess toward truth.
            Assert.True(early > mid && mid > late);
        }

        [Fact]
        public void NeverReportsNegativeTime()
        {
            var eta = new EtaEstimator(TimeSpan.FromSeconds(10), totalLines: 100);

            // Elapsed already past the whole guess.
            var remaining = eta.Update(linesCompleted: 50, elapsed: TimeSpan.FromSeconds(60));

            Assert.True(remaining!.Value >= TimeSpan.Zero);
        }

        /// <summary>
        /// Dwelling on the same line means running slower than expected, so the estimate
        /// drifts up - but gradually, a few seconds over several frames, never a jump.
        /// </summary>
        [Fact]
        public void DwellingOnOneLine_DriftsGraduallyNotAbruptly()
        {
            var eta = new EtaEstimator(TimeSpan.FromMinutes(10), totalLines: 1000);

            var first = eta.Update(200, TimeSpan.FromMinutes(4));
            var second = eta.Update(200, TimeSpan.FromMinutes(4.1));    // same line, 6 s later
            var third = eta.Update(200, TimeSpan.FromMinutes(4.2));     // and 6 s more

            // Monotonic and gentle: each 6 s of dwelling nudges the estimate a little.
            Assert.True(third >= second && second >= first);
            Assert.InRange((third!.Value - first!.Value).TotalSeconds, 0, 30);
        }

        [Fact]
        public void WithoutModel_TheFirstMeasurementSeedsIt()
        {
            var eta = new EtaEstimator(TimeSpan.Zero, totalLines: 1000);

            var remaining = eta.Update(linesCompleted: 100, elapsed: TimeSpan.FromMinutes(1));

            // 100 lines in 1 min projects 10 min total -> ~9 min remaining.
            Assert.NotNull(remaining);
            Assert.InRange(remaining!.Value.TotalMinutes, 8.0, 10.0);
        }
    }
}
