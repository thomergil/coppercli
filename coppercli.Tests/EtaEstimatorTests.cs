using System;
using coppercli.Core.Controllers;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// The ETA has to track the pace the machine is actually keeping, in both directions.
    /// An estimate that can only count down tells the operator a job is nearly finished
    /// while it is in fact running late, which is worse than no estimate at all.
    /// </summary>
    public class EtaEstimatorTests
    {
        /// <summary>Feeds a steady pace and returns the estimate at each sampled line.</summary>
        private static double[] RunAtSteadyPace(EtaEstimator eta, int totalLines,
                                                double secondsPerLine, int step)
        {
            var series = new System.Collections.Generic.List<double>();
            for (int line = step; line <= totalLines; line += step)
            {
                var remaining = eta.Update(line, TimeSpan.FromSeconds(line * secondsPerLine));
                series.Add(remaining!.Value.TotalSeconds);
            }
            return series.ToArray();
        }

        [Fact]
        public void BeforeAnyProgress_ReportsTheModelGuess()
        {
            var eta = new EtaEstimator(TimeSpan.FromMinutes(10), totalLines: 1000);

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
        /// The regression this pins. A machine running steadily slower than the model must
        /// produce an estimate that reflects the real finish time, not the guess. The old
        /// blend answered with the model guess here and never recovered.
        /// </summary>
        [Theory]
        [InlineData(1.5)]
        [InlineData(2.0)]
        [InlineData(3.0)]
        public void RunningSlowerThanTheModel_EstimateReflectsRealityNotTheGuess(double slowdown)
        {
            const int totalLines = 1000;
            var modelTotal = TimeSpan.FromSeconds(600);
            double secondsPerLine = 600.0 / totalLines * slowdown;
            double trueTotal = totalLines * secondsPerLine;

            var eta = new EtaEstimator(modelTotal, totalLines);
            var series = RunAtSteadyPace(eta, totalLines, secondsPerLine, step: 10);

            // A tenth of the way in, the estimate must already be near the truth.
            double atTenPercent = series[9];
            double trueRemainingAtTenPercent = trueTotal * 0.9;
            Assert.InRange(atTenPercent, trueRemainingAtTenPercent * 0.85, trueRemainingAtTenPercent * 1.15);

            // And it must land on zero by the end rather than still promising time.
            Assert.InRange(series[^1], 0.0, secondsPerLine * 2);
        }

        /// <summary>
        /// The complaint that prompted the rewrite: the estimate was only ever willing to
        /// go down. When the machine slows mid-job the time remaining must go UP.
        /// </summary>
        [Fact]
        public void WhenTheMachineSlowsMidJob_TheEstimateRises()
        {
            const int totalLines = 1000;
            var eta = new EtaEstimator(TimeSpan.FromSeconds(600), totalLines);

            double elapsed = 0;
            double? beforeSlowdown = null;
            double peakAfter = 0;

            for (int line = 10; line <= totalLines; line += 10)
            {
                // Second half runs at half speed.
                double secondsPerLine = line <= totalLines / 2 ? 0.6 : 1.2;
                elapsed += 10 * secondsPerLine;
                double remaining = eta.Update(line, TimeSpan.FromSeconds(elapsed))!.Value.TotalSeconds;

                if (line == totalLines / 2)
                {
                    beforeSlowdown = remaining;
                }
                else if (beforeSlowdown != null && line <= totalLines * 0.8)
                {
                    peakAfter = Math.Max(peakAfter, remaining);
                }
            }

            Assert.NotNull(beforeSlowdown);
            Assert.True(peakAfter > beforeSlowdown!.Value,
                $"estimate must rise when the machine slows: was {beforeSlowdown:F0}s, peaked at {peakAfter:F0}s");
        }

        /// <summary>
        /// Steady pace matching the model: the estimate should simply count down, without
        /// the measurement introducing wobble.
        /// </summary>
        [Fact]
        public void AtTheModelledPace_TheEstimateCountsDownSmoothly()
        {
            const int totalLines = 1000;
            var eta = new EtaEstimator(TimeSpan.FromSeconds(600), totalLines);

            var series = RunAtSteadyPace(eta, totalLines, secondsPerLine: 0.6, step: 10);

            for (int i = 1; i < series.Length; i++)
            {
                Assert.True(series[i] <= series[i - 1] + 1.0,
                    $"estimate jumped up at sample {i}: {series[i - 1]:F1} -> {series[i]:F1}");
            }
            Assert.InRange(series[0], 570.0, 600.0);
        }

        [Fact]
        public void NeverReportsNegativeTime()
        {
            var eta = new EtaEstimator(TimeSpan.FromSeconds(10), totalLines: 100);

            var remaining = eta.Update(linesCompleted: 50, elapsed: TimeSpan.FromSeconds(60));

            Assert.True(remaining!.Value >= TimeSpan.Zero);
        }

        /// <summary>
        /// Sitting on one line means the job is running behind, so the estimate drifts up -
        /// but by roughly the time actually spent waiting, not by that time multiplied
        /// across every line still to come.
        /// </summary>
        [Fact]
        public void DwellingOnOneLine_DriftsUpByTheTimeSpentWaiting()
        {
            var eta = new EtaEstimator(TimeSpan.FromMinutes(10), totalLines: 1000);

            var first = eta.Update(200, TimeSpan.FromMinutes(4));
            var second = eta.Update(200, TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(6));
            var third = eta.Update(200, TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(12));

            Assert.True(third >= second && second >= first);
            // 12 s of dwelling must not balloon the estimate across the remaining 800 lines.
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

        /// <summary>
        /// Reaction speed must come from progress through the job, not from how often the
        /// caller happens to redraw. Polling ten times more often must not change the curve.
        /// </summary>
        [Fact]
        public void ReactionSpeedDoesNotDependOnPollRate()
        {
            const int totalLines = 1000;
            const double secondsPerLine = 1.2;

            var coarse = new EtaEstimator(TimeSpan.FromSeconds(600), totalLines);
            var fine = new EtaEstimator(TimeSpan.FromSeconds(600), totalLines);

            RunAtSteadyPace(coarse, totalLines, secondsPerLine, step: 50);
            RunAtSteadyPace(fine, totalLines, secondsPerLine, step: 5);

            var atCoarse = coarse.Update(500, TimeSpan.FromSeconds(500 * secondsPerLine))!.Value.TotalSeconds;
            var atFine = fine.Update(500, TimeSpan.FromSeconds(500 * secondsPerLine))!.Value.TotalSeconds;

            Assert.InRange(Math.Abs(atCoarse - atFine), 0.0, 30.0);
        }
    }
}
