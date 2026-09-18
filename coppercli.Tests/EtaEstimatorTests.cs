using System;
using coppercli.Core.Controllers;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// Covers EtaEstimator, which must follow the pace the machine keeps in both directions.
    /// An estimate that only counts down reports a job as nearly finished while it runs late.
    /// </summary>
    public class EtaEstimatorTests
    {
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
        /// A run steadily slower than the model must produce an estimate near the real finish
        /// time. A blend weighted toward the model guess stays there and never recovers.
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

            // Index 9 is line 100 of 1000, a tenth of the way in.
            double atTenPercent = series[9];
            double trueRemainingAtTenPercent = trueTotal * 0.9;
            Assert.InRange(atTenPercent, trueRemainingAtTenPercent * 0.85, trueRemainingAtTenPercent * 1.15);

            Assert.InRange(series[^1], 0.0, secondsPerLine * 2);
        }

        /// <summary>
        /// An estimate that can only decrease hides a mid-job slowdown until the job overruns.
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
        /// At the model's own pace the measurement must not push the estimate back up between
        /// samples.
        /// </summary>
        [Fact]
        public void AtModeledPace_EstimateDecreasesSmoothly()
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
        /// Repeated updates on the same line must raise the estimate by about the time spent
        /// waiting, not by that time scaled across every line still to come.
        /// </summary>
        [Fact]
        public void RepeatedUpdatesOnSameLine_IncreaseEtaWithElapsedTime()
        {
            var eta = new EtaEstimator(TimeSpan.FromMinutes(10), totalLines: 1000);

            var first = eta.Update(200, TimeSpan.FromMinutes(4));
            var second = eta.Update(200, TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(6));
            var third = eta.Update(200, TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(12));

            Assert.True(third >= second && second >= first);
            Assert.InRange((third!.Value - first!.Value).TotalSeconds, 0, 30);
        }

        [Fact]
        public void WithoutModel_FirstMeasurementStartsEstimate()
        {
            var eta = new EtaEstimator(TimeSpan.Zero, totalLines: 1000);

            var remaining = eta.Update(linesCompleted: 100, elapsed: TimeSpan.FromMinutes(1));

            // 100 lines in 1 min projects 10 min total -> ~9 min remaining.
            Assert.NotNull(remaining);
            Assert.InRange(remaining!.Value.TotalMinutes, 8.0, 10.0);
        }

        /// <summary>
        /// Reaction speed must follow progress through the job, not the rate Update is called
        /// at. The fine run here polls ten times as often and must reach the same estimate.
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
