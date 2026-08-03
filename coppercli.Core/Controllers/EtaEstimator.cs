using System;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Estimates the time remaining in a job from the pace the machine is actually keeping,
    /// using the toolpath's own duration estimate only as a starting guess.
    ///
    /// The estimate has to be free to move in BOTH directions. An earlier version weighted
    /// the guess by (1 - fraction-complete), which made the remaining time proportional to
    /// (1-f)(1 + f(k-1)) for a machine running k times slower than the model. The slope of
    /// that at the start is (k-2), so unless the machine was more than twice as slow as
    /// predicted the displayed figure could only ever fall: a job running 50% late counted
    /// calmly down to zero and then carried on cutting.
    ///
    /// Instead, measure the recent pace in seconds per line as an exponential moving average
    /// and project it across the lines still to run. Two details matter:
    ///
    ///   - the average is smoothed over a share of the job rather than a number of samples,
    ///     so how quickly it reacts does not change when the caller's redraw rate does;
    ///   - time already spent sitting on the current line is added as its own term rather
    ///     than folded into the pace, so a single long cut does not get extrapolated across
    ///     every remaining line - but a genuine stall still pushes the estimate up.
    ///
    /// The model guess only covers the warmup, before enough lines have run to measure
    /// anything. The clock this is fed must measure milling only, not the setup before it.
    /// </summary>
    public sealed class EtaEstimator
    {
        /// <summary>Share of the job over which the measured pace displaces the model guess.</summary>
        private const double WarmupFraction = 0.02;

        /// <summary>Share of the job the moving average remembers. Larger reacts more slowly.</summary>
        private const double SmoothingFraction = 0.10;

        private readonly int _totalLines;
        private readonly double _modelSecondsPerLine;
        private readonly bool _haveModel;
        private readonly double _warmupLines;
        private readonly double _smoothingLines;

        private int _lastLines;
        private double _lastAdvanceSeconds;
        private double _measuredLines;
        private double _pace;
        private bool _havePace;

        /// <param name="modelEstimate">Up-front guess for the whole job, from the toolpath
        /// model. Zero or negative if the model could not estimate (e.g. no feed moves), in
        /// which case only the measured pace is used once it exists.</param>
        /// <param name="totalLines">Number of lines in the job.</param>
        public EtaEstimator(TimeSpan modelEstimate, int totalLines)
        {
            _totalLines = Math.Max(1, totalLines);
            _haveModel = modelEstimate > TimeSpan.Zero;
            _modelSecondsPerLine = _haveModel ? modelEstimate.TotalSeconds / _totalLines : 0.0;
            _warmupLines = Math.Max(1.0, WarmupFraction * _totalLines);
            _smoothingLines = Math.Max(1.0, SmoothingFraction * _totalLines);
        }

        /// <summary>
        /// Returns the time still to go. Null while there is nothing to base an estimate on -
        /// no model guess and no measured progress yet.
        /// </summary>
        public TimeSpan? Update(int linesCompleted, TimeSpan elapsed)
        {
            double elapsedSeconds = elapsed.TotalSeconds;

            // Rewinding (a restart, or a rewound abort) invalidates the baseline the deltas
            // are measured from, but not the pace already learned.
            if (linesCompleted < _lastLines || elapsedSeconds < _lastAdvanceSeconds)
            {
                _lastLines = linesCompleted;
                _lastAdvanceSeconds = elapsedSeconds;
            }

            int deltaLines = linesCompleted - _lastLines;
            double deltaSeconds = elapsedSeconds - _lastAdvanceSeconds;

            if (deltaLines > 0 && deltaSeconds > 0)
            {
                double instantPace = deltaSeconds / deltaLines;
                double weight = Math.Clamp(deltaLines / _smoothingLines, 0.0, 1.0);
                _pace = _havePace ? weight * instantPace + (1 - weight) * _pace : instantPace;
                _havePace = true;
                _measuredLines += deltaLines;
                _lastLines = linesCompleted;
                _lastAdvanceSeconds = elapsedSeconds;
                deltaSeconds = 0;
            }

            if (!_havePace)
            {
                if (!_haveModel)
                {
                    return null;
                }
                double guessRemaining = _modelSecondsPerLine * _totalLines - elapsedSeconds;
                return TimeSpan.FromSeconds(Math.Max(0, guessRemaining));
            }

            // Until enough lines have run to measure a pace worth trusting, lean on the guess.
            double rate = _pace;
            if (_haveModel)
            {
                double trust = Math.Clamp(_measuredLines / _warmupLines, 0.0, 1.0);
                rate = (1 - trust) * _modelSecondsPerLine + trust * _pace;
            }

            // Time already spent on the line in progress beyond what its pace predicted.
            // Counted once, for this line only - not extrapolated across the rest.
            double currentLineOverrun = Math.Max(0, deltaSeconds - rate);

            double remaining = rate * (_totalLines - linesCompleted) + currentLineOverrun;
            return TimeSpan.FromSeconds(Math.Max(0, remaining));
        }
    }
}
